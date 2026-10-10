using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CSM.API.Commands;
using CSM.BaseGame.Commands.Data.Trees;
using CSM.BaseGame.Commands.Handler.Trees;
using CSM.BaseGame.Helpers;
using CSM.BaseGame.Injections;
using CSM.Commands.Data.Internal;
using HarmonyLib;
using ProtoBuf.Meta;

internal static class WildfireSyncTests
{
    private const uint Id = 262143;
    private static readonly string Host = new string('f', 32);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static TreeInstance Tree()
    {
        return new TreeInstance
        {
            m_flags = (ushort)(TreeInstance.Flags.Created | TreeInstance.Flags.Single | TreeInstance.Flags.FixedHeight),
            m_infoIndex = 3,
            m_posX = -120,
            m_posY = 200,
            m_posZ = -240,
            m_nextGridTree = 81
        };
    }

    private static TreeFireState Fire(ref TreeInstance tree, byte intensity = 200, byte damage = 25)
    {
        TreeFireState state = TreeFireState.Capture(Id, 7, ref tree, true, new TreeManager.BurningTree
        {
            m_treeIndex = Id,
            m_fireIntensity = intensity,
            m_fireDamage = damage
        });
        state.FireFlags = (ushort)(TreeInstance.Flags.FireDamage | (intensity != 0 ? TreeInstance.Flags.Burning : 0));
        return state;
    }

    private static WildfireStatusCommand Delta(ulong revision = 2)
    {
        return new WildfireStatusCommand { SenderId = -1, HostSession = Host, Revision = revision };
    }

    private static WildfireStatusCommand Part(WildfireCache cache, ulong revision = 1, int index = 0, int count = 1)
    {
        return new WildfireStatusCommand
        {
            SenderId = -1,
            HostSession = Host,
            Revision = revision,
            RequestSession = cache.Session,
            SnapshotToken = cache.PendingToken,
            PartIndex = index,
            PartCount = count
        };
    }

    private static void Bind(WildfireCache cache, ref TreeInstance tree)
    {
        cache.Request(0, 0, 100, 10);
        WildfireStatusCommand command = Part(cache);
        Check(cache.BeginPart(command, 0), "baseline part rejected");
        Check(cache.AcceptTree(Fire(ref tree), command, 0, ref tree), "baseline state rejected");
        cache.FinishSnapshot();
    }

    private static RuntimeTypeModel Model()
    {
        RuntimeTypeModel model = RuntimeTypeModel.Create();
        model.Add(typeof(CommandBase), true);
        model[typeof(CommandBase)].AddSubType(100, typeof(WildfireStatusCommand));
        model[typeof(CommandBase)].AddSubType(101, typeof(RequestWildfireStatusCommand));
        model[typeof(CommandBase)].AddSubType(102, typeof(ConnectionRequestCommand));
        model[typeof(CommandBase)].AddSubType(103, typeof(ConnectionResultCommand));
        model.Add(typeof(WildfireStatusCommand), true);
        model.Add(typeof(RequestWildfireStatusCommand), true);
        model.Add(typeof(ConnectionRequestCommand), true);
        model.Add(typeof(ConnectionResultCommand), true);
        model.CompileInPlace();
        return model;
    }

    private static T RoundTrip<T>(T value) where T : CommandBase
    {
        using (MemoryStream stream = new MemoryStream())
        {
            RuntimeTypeModel model = Model();
            model.Serialize(stream, value);
            stream.Position = 0;
            return (T)model.Deserialize(stream, null, typeof(CommandBase));
        }
    }

    private static void PacketRoundTrip()
    {
        TreeInstance tree = Tree();
        TreeFireState state = Fire(ref tree, 0, 120);
        state.Lifetime = ulong.MaxValue;
        WildfireStatusCommand command = Delta();
        command.Trees = new List<TreeFireState> { state, state.WithoutFire() };
        command.Ground = new List<BurnedGroundState> { new BurnedGroundState { Cell = 0, Burned = 0 } };
        WildfireStatusCommand decoded = RoundTrip(command);
        Check(decoded.SenderId == -1 && decoded.Trees[0].SameAs(state), "tree identity or recovery changed over wire");
        Check(decoded.Trees[0].TreeId > ushort.MaxValue && decoded.Trees[0].Present && decoded.Trees[0].Intensity == 0 &&
            !decoded.Trees[1].Present && decoded.Ground[0].Cell == 0 && decoded.Ground[0].Burned == 0, "zero clears or uint ID lost");
        RequestWildfireStatusCommand request = RoundTrip(new RequestWildfireStatusCommand { Session = Host, Token = ulong.MaxValue });
        Check(request.Session == Host && request.Token == ulong.MaxValue, "snapshot identity lost");
    }

    private static void PreserveTreeFields()
    {
        TreeInstance tree = Tree();
        tree.m_flags |= (ushort)(TreeInstance.Flags.Hidden | TreeInstance.Flags.GrowState);
        ushort flags = tree.m_flags;
        TreeFireState state = Fire(ref tree);
        state.ApplyFlags(ref tree);
        Check((tree.m_flags & ~TreeFireState.FireMask) == flags && tree.m_nextGridTree == 81 &&
            tree.m_infoIndex == 3 && tree.m_posX == -120 && tree.m_posY == 200 && tree.m_posZ == -240, "unrelated tree fields changed");
        state.WithoutFire().ApplyFlags(ref tree);
        Check(tree.m_flags == flags, "recovery retained a fire flag");
    }

    private static void RecoveryAndRemoval()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        Bind(cache, ref tree);
        Check(cache.AcceptTree(Fire(ref tree, 0, 100), Delta(), 0, ref tree) && cache.TreeCount == 1,
            "an extinguished, damaged tree was removed too early");
        Check(cache.Get(Id, 0, ref tree).Damage == 100, "recovery damage lost");
        Check(cache.AcceptTree(Fire(ref tree).WithoutFire(), Delta(3), 0, ref tree) && cache.TreeCount == 0 &&
            !cache.Get(Id, 0, ref tree).Present, "finished recovery retained an entry");
    }

    private static void BaselineAuthority()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        Check(!cache.AcceptTree(Fire(ref tree), Delta(), 0, ref tree), "unsolicited delta bound a tree");
        Bind(cache, ref tree);
        WildfireStatusCommand foreign = Delta();
        foreign.HostSession = new string('b', 32);
        Check(!cache.AcceptTree(Fire(ref tree), foreign, 0, ref tree), "another host session overwrote a binding");
        TreeFireState stale = Fire(ref tree);
        stale.Lifetime++;
        Check(!cache.AcceptTree(stale, Delta(), 0, ref tree), "another host lifetime overwrote a binding");
    }

    private static void ReusedTreeId()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        cache.Request(0, 0, 100, 10);
        WildfireStatusCommand old = Part(cache);
        cache.Invalidate(Id);
        Check(cache.BeginPart(old, 1) && !cache.AcceptTree(Fire(ref tree), old, 1, ref tree),
            "a reply attached to a tree recreated at the same position");
        Check(!cache.ShouldClear(Id, 1, ref tree), "an old snapshot cleared a newer tree lifetime");
        cache.FinishSnapshot();
        cache.Request(1, 2, 100, 10);
        WildfireStatusCommand next = Part(cache, 2);
        Check(cache.BeginPart(next, 2) && cache.AcceptTree(Fire(ref tree), next, 1, ref tree), "new tree lifetime failed to bind");
        cache.FinishSnapshot();
        Check(!cache.AcceptTree(Fire(ref tree), Delta(3), 0, ref tree), "old local lifetime accepted");
    }

    private static void ReloadSession()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        cache.Request(0, 0, 100, 10);
        WildfireStatusCommand old = Part(cache);
        cache.Reset();
        cache.Request(0, 0, 100, 10);
        Check(old.SnapshotToken == cache.PendingToken && old.RequestSession != cache.Session && !cache.BeginPart(old, 0),
            "late pre-reload snapshot attached to restarted token");
    }

    private static void MultipartSnapshot()
    {
        WildfireCache cache = new WildfireCache();
        cache.Request(0, 0, 100, 10);
        WildfireStatusCommand first = Part(cache, 1, 0, 2);
        Check(cache.BeginPart(first, 0) && !cache.SnapshotComplete, "partial snapshot considered complete");
        Check(!cache.BeginPart(first, 1), "duplicate part counted twice");
        Check(!cache.BeginPart(Part(cache, 2, 1, 2), 2), "different revision joined snapshot");
        Check(!cache.BeginPart(Part(cache, 1, 1, 3), 2), "different part count joined snapshot");
        Check(cache.BeginPart(Part(cache, 1, 1, 2), 3) && cache.SnapshotComplete, "final part did not complete snapshot");
        cache.FinishSnapshot();
        Check(cache.GroundReady && cache.PendingToken == 0, "empty complete baseline did not establish ground authority");
    }

    private static void DeferredDelta()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        Check(!cache.AcceptTree(Fire(ref tree, 240, 80), Delta(3), 0, ref tree), "unbound delta applied before baseline");
        cache.Request(0, 0, 100, 10);
        WildfireStatusCommand part = Part(cache);
        Check(cache.BeginPart(part, 0) && cache.AcceptTree(Fire(ref tree), part, 0, ref tree), "baseline failed to bind");
        Check(cache.Get(Id, 0, ref tree).Intensity == 240 && cache.Get(Id, 0, ref tree).Damage == 80,
            "baseline regressed newer deferred fire state");
        cache.FinishSnapshot();
        cache.Invalidate(Id);
        TreeFireState clear = Fire(ref tree).WithoutFire();
        Check(!cache.AcceptTree(clear, Delta(5), 1, ref tree), "clear applied before rebind");
        cache.Request(1, 1, 100, 10);
        part = Part(cache, 4);
        Check(cache.BeginPart(part, 1) && cache.AcceptTree(Fire(ref tree), part, 1, ref tree), "second baseline failed");
        Check(!cache.Get(Id, 1, ref tree).Present, "baseline resurrected a newer deferred clear");
        cache.FinishSnapshot();
    }

    private static void DeferredGround()
    {
        WildfireCache cache = new WildfireCache();
        Check(!cache.AcceptGround(new BurnedGroundState { Cell = 12, Burned = 80 }, Delta(3)), "unbound ground applied");
        cache.Request(0, 0, 100, 10);
        WildfireStatusCommand part = Part(cache);
        Check(cache.BeginPart(part, 0), "baseline failed");
        Check(!cache.AcceptGround(new BurnedGroundState { Cell = 12, Burned = 10 }, part), "old ground snapshot overwrote delta");
        cache.FinishSnapshot();
        Check(cache.Ground(12) == 80, "initial baseline lost newer ground state");
        cache.Reset();
        WildfireStatusCommand foreign = Delta(100);
        foreign.HostSession = new string('b', 32);
        cache.AcceptGround(new BurnedGroundState { Cell = 12, Burned = 90 }, foreign);
        cache.Request(0, 0, 100, 10);
        Check(cache.BeginPart(Part(cache), 0), "new session baseline failed");
        cache.FinishSnapshot();
        Check(cache.Ground(12) == 0, "foreign deferred ground attached to current host");
    }

    private static void PreserveNewerDeltas()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        Bind(cache, ref tree);
        cache.Request(0, 100, 100, 10);
        Check(cache.BeginPart(Part(cache, 2, 0, 2), 100), "snapshot did not start");
        Check(cache.AcceptTree(Fire(ref tree, 240, 80), Delta(3), 0, ref tree), "newer tree delta rejected");
        Check(cache.AcceptGround(new BurnedGroundState { Cell = 12, Burned = 50 }, Delta(3)), "newer ground delta rejected");
        Check(cache.BeginPart(Part(cache, 2, 1, 2), 101) && !cache.ShouldClear(Id, 0, ref tree),
            "older absence erased a newer fire");
        cache.FinishSnapshot();
        Check(cache.Get(Id, 0, ref tree).Intensity == 240 && cache.Ground(12) == 50, "snapshot erased interleaved deltas");
    }

    private static void PreserveNewerClears()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        Bind(cache, ref tree);
        cache.Request(0, 100, 100, 10);
        WildfireStatusCommand first = Part(cache, 2, 0, 2);
        Check(cache.BeginPart(first, 100), "snapshot did not start");
        Check(cache.AcceptTree(Fire(ref tree).WithoutFire(), Delta(3), 0, ref tree), "tree clear rejected");
        Check(cache.AcceptGround(new BurnedGroundState { Cell = 12 }, Delta(3)), "ground clear rejected");
        WildfireStatusCommand last = Part(cache, 2, 1, 2);
        Check(cache.BeginPart(last, 101), "last part rejected");
        Check(!cache.AcceptTree(Fire(ref tree), last, 0, ref tree) &&
            !cache.AcceptGround(new BurnedGroundState { Cell = 12, Burned = 200 }, last), "old snapshot resurrected cleared fire/ground");
        cache.FinishSnapshot();
        Check(cache.TreeCount == 0 && cache.Ground(12) == 0, "snapshot restored cleared state");
    }

    private static void ClearMissingState()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        Bind(cache, ref tree);
        cache.AcceptGround(new BurnedGroundState { Cell = 0, Burned = 100 }, Delta());
        cache.Request(0, 100, 100, 10);
        Check(cache.BeginPart(Part(cache, 3), 100) && cache.ShouldClear(Id, 0, ref tree), "missing host tree not cleared");
        cache.FinishSnapshot();
        Check(cache.TreeCount == 0 && cache.Ground(0) == 0 && cache.HasGround(262143), "complete absence did not clear old state");
        TreeInstance unbound = Tree();
        cache.Request(0, 200, 100, 10);
        Check(cache.BeginPart(Part(cache, 4), 200) && cache.ShouldClear(55, 0, ref unbound), "initial save-only fire not cleared");
    }

    private static void RetryAndThrottle()
    {
        TreeInstance tree = Tree();
        WildfireCache cache = new WildfireCache();
        RequestWildfireStatusCommand request = cache.Request(0, 0, 100, 10, 5);
        Check(cache.BeginPart(Part(cache, 1, 0, 2), 9), "part rejected");
        Check(cache.Request(0, 18, 100, 10, 5) == null, "snapshot retried while making progress");
        RequestWildfireStatusCommand retry = cache.Request(0, 19, 100, 10, 5);
        Check(retry.Token != request.Token, "stalled snapshot reused token");
        Check(cache.BeginPart(Part(cache), 19), "retry baseline rejected");
        cache.AcceptTree(Fire(ref tree), Part(cache), 0, ref tree);
        cache.FinishSnapshot();
        TreeFireState changed = Fire(ref tree);
        changed.Lifetime++;
        Check(!cache.AcceptTree(changed, Delta(), 0, ref tree) && cache.Request(0, 20, 100, 10, 5) == null,
            "new-lifetime requests were not throttled");
        Check(cache.Request(0, 24, 100, 10, 5) != null, "needed baseline stayed throttled");
    }

    private static void RejectMalformedPackets()
    {
        TreeInstance tree = Tree();
        WildfireStatusCommand command = Delta();
        command.Trees = new List<TreeFireState> { Fire(ref tree) };
        Check(command.IsValid(262144), "valid packet rejected");
        command.Trees[0].FireFlags |= (ushort)TreeInstance.Flags.Deleted;
        Check(!command.IsValid(262144), "structural flags accepted");
        command.Trees[0] = Fire(ref tree).WithoutFire();
        command.Trees[0].Damage = 1;
        Check(!command.IsValid(262144), "absent fire with damage accepted");
        command.Trees[0] = Fire(ref tree);
        command.Ground = new List<BurnedGroundState> { new BurnedGroundState { Cell = -1 } };
        Check(!command.IsValid(262144), "negative cell accepted");
        command.Ground[0].Cell = WildfireCache.GroundCellCount;
        Check(!command.IsValid(262144), "out-of-range cell accepted");
        command.Ground = null;
        command.Trees = Enumerable.Repeat(Fire(ref tree), 65).ToList();
        Check(!command.IsValid(262144), "oversized batch accepted");
        command.Trees = new List<TreeFireState> { Fire(ref tree) };
        Check(!command.IsValid(65536), "tree ID truncated to building capacity");
        command.Trees[0].TreeId = 0;
        Check(!command.IsValid(262144), "zero tree ID accepted");
        command.Trees = null;
        command.SnapshotToken = 1;
        command.RequestSession = Host;
        command.PartCount = 2;
        command.PartIndex = 2;
        Check(!command.IsValid(262144), "out-of-range part accepted");
    }

    private static void PatchMetadata()
    {
        Type[] patches = { typeof(HostTreeIgnitionPatch), typeof(HostTreeFireSimulationPatch), typeof(HostTreeExtinguishingPatch),
            typeof(BurnedGroundSimulationPatch), typeof(HostBurnedGroundPatch), typeof(WildfireTreeCreatePatch),
            typeof(WildfireTreeReleasePatch), typeof(WildfireTreeMovePatch) };
        foreach (Type patch in patches)
        {
            HarmonyMethod info = ((HarmonyPatch)patch.GetCustomAttributes(typeof(HarmonyPatch), false).Single()).info;
            MethodInfo target = AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes);
            Check(target != null, patch.Name + " has no original game target");
            foreach (MethodInfo hook in patch.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "Prefix" || method.Name == "Postfix"))
            {
                foreach (ParameterInfo parameter in hook.GetParameters().Where(parameter => !parameter.Name.StartsWith("__")))
                {
                    ParameterInfo original = target.GetParameters().SingleOrDefault(value => value.Name == parameter.Name);
                    Check(original != null, patch.Name + " references a missing named parameter");
                    Type type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                    Type originalType = original.ParameterType.IsByRef ? original.ParameterType.GetElementType() : original.ParameterType;
                    Check(type == originalType, patch.Name + " uses the wrong parameter type");
                }
            }
            if (patch == typeof(HostTreeExtinguishingPatch))
                Check(target.GetParameters()[2].ParameterType == typeof(uint) &&
                    target.GetParameters()[3].ParameterType == typeof(TreeInstance).MakeByRefType(), "building helicopter overload selected");
        }
        Console.WriteLine("Verified 8 wildfire/resource patch targets against original DLL metadata.");
    }

    private static void HostAuthority()
    {
        FieldInfo enabled = typeof(WildfireSync).GetField("_enabled", BindingFlags.Static | BindingFlags.NonPublic);
        bool oldEnabled = (bool)enabled.GetValue(null);
        MultiplayerRole oldRole = Command.CurrentRole;
        try
        {
            enabled.SetValue(null, true);
            bool result = false;
            int resource = 0;
            foreach (MultiplayerRole role in new[] { MultiplayerRole.None, MultiplayerRole.Server })
            {
                Command.CurrentRole = role;
                Check(HostTreeIgnitionPatch.Prefix(ref result) && HostTreeFireSimulationPatch.Prefix(ref result) &&
                    HostBurnedGroundPatch.Prefix(NaturalResourceManager.Resource.Burned, ref resource), "host/single-player behavior blocked");
            }
            Command.CurrentRole = MultiplayerRole.Client;
            Check(!HostTreeIgnitionPatch.Prefix(ref result) && !result, "client can independently ignite trees");
            Check(!HostTreeFireSimulationPatch.Prefix(ref result) && result, "client can advance/remove host fire entries");
            Check(!HostBurnedGroundPatch.Prefix(NaturalResourceManager.Resource.Burned, ref resource) && resource == 0,
                "client can change burned ground independently");
            Check(HostBurnedGroundPatch.Prefix(NaturalResourceManager.Resource.Pollution, ref resource), "unrelated natural resource blocked");
            enabled.SetValue(null, false);
            Check(HostTreeIgnitionPatch.Prefix(ref result), "disconnected client fire behavior changed");
        }
        finally
        {
            enabled.SetValue(null, oldEnabled);
            Command.CurrentRole = oldRole;
        }
    }

    private static void BatchAndProtocol()
    {
        Check(!new WildfireStatusHandler().RelayOnServer && !new RequestWildfireStatusHandler().RelayOnServer,
            "client fire authority can be relayed");
        TreeInstance tree = Tree();
        WildfireStatusCommand packet = Delta(ulong.MaxValue);
        packet.Trees = new List<TreeFireState>();
        for (int i = 0; i < WildfireSync.BatchSize; i++)
        {
            TreeFireState state = Fire(ref tree, 255, 255);
            state.TreeId = (uint)(262143 - i);
            state.Lifetime = ulong.MaxValue;
            state.InfoIndex = ushort.MaxValue;
            state.PositionX = state.PositionZ = short.MinValue;
            state.PositionY = ushort.MaxValue;
            packet.Trees.Add(state);
        }
        using (MemoryStream stream = new MemoryStream())
        {
            Model().Serialize(stream, packet);
            Check(stream.Length < 8192, "full wildfire packet exceeds regression bound");
            Console.WriteLine("Full 64-tree wildfire test packet: " + stream.Length + " bytes.");
        }
        Check(RoundTrip(new ConnectionRequestCommand()).WildfireProtocol == 0 &&
            RoundTrip(new ConnectionResultCommand()).WildfireProtocol == 0, "old building-only build appears wildfire compatible");
        Check(RoundTrip(new ConnectionRequestCommand { WildfireProtocol = WildfireSync.ProtocolVersion }).WildfireProtocol == 1 &&
            RoundTrip(new ConnectionResultCommand { WildfireProtocol = WildfireSync.ProtocolVersion }).WildfireProtocol == 1,
            "wildfire capability lost over wire");
    }

    public static void RunAll(Action<string, Action> run)
    {
        run("wildfire packets preserve uint tree identities, recovery and zero clears", PacketRoundTrip);
        run("tree fire flags preserve placement, growth and grid links", PreserveTreeFields);
        run("extinguished trees retain damage until host recovery finishes", RecoveryAndRemoval);
        run("only requested baselines bind trees; foreign hosts/lifetimes are rejected", BaselineAuthority);
        run("same-position tree ID reuse rejects in-flight baselines", ReusedTreeId);
        run("save reload rejects old snapshots even when tokens restart", ReloadSession);
        run("multipart snapshots complete only once all consistent parts arrive", MultipartSnapshot);
        run("requested baselines preserve newer deferred fire states and clears", DeferredDelta);
        run("initial baselines preserve newer ground updates and reject foreign sessions", DeferredGround);
        run("snapshot absence preserves newer tree and ground deltas", PreserveNewerDeltas);
        run("late snapshot parts cannot resurrect newer fire and ground clears", PreserveNewerClears);
        run("complete baselines clear missing host fires and burned ground", ClearMissingState);
        run("snapshot retries honor progress and baseline requests are throttled", RetryAndThrottle);
        run("malformed wildfire flags, identities, batches and parts are rejected", RejectMalformedPackets);
        run("wildfire Harmony targets match original DLL signatures", PatchMetadata);
        run("clients cannot ignite/progress fires; host and single-player behavior remains", HostAuthority);
        run("wildfire batches remain bounded and protocol negotiation rejects old builds", BatchAndProtocol);
    }
}
