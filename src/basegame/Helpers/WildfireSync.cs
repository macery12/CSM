using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CSM.API.Commands;
using CSM.BaseGame.Commands.Data.Trees;
using ICities;
using UnityEngine;

namespace CSM.BaseGame.Helpers
{
    public static class WildfireSync
    {
        public const int ProtocolVersion = 1;
        public const int BatchSize = 64;
        private const int PacketsPerFlush = 8;
        private static readonly long FlushTicks = Math.Max(1, Stopwatch.Frequency / 10);
        private static readonly long SnapshotTicks = Stopwatch.Frequency * 10;
        private static readonly long RetryTicks = Stopwatch.Frequency * 5;
        private static readonly WildfireCache Cache = new WildfireCache();
        private static readonly Dictionary<uint, ulong> Lifetimes = new Dictionary<uint, ulong>();
        private static readonly Dictionary<uint, TreeManager.BurningTree> HostTrees = new Dictionary<uint, TreeManager.BurningTree>();
        private static readonly Dictionary<uint, TreeFireState> SentTrees = new Dictionary<uint, TreeFireState>();
        private static readonly Dictionary<uint, int> ClientPositions = new Dictionary<uint, int>();
        private static readonly HashSet<uint> DirtyTrees = new HashSet<uint>();
        private static readonly HashSet<int> DirtyGround = new HashSet<int>();
        private static readonly Dictionary<int, Queue<WildfireStatusCommand>> Snapshots = new Dictionary<int, Queue<WildfireStatusCommand>>();
        private static byte[] _observedGround = new byte[WildfireCache.GroundCellCount];
        private static string _hostSession = Guid.NewGuid().ToString("N");
        private static ulong _lifetime;
        private static ulong _revision;
        private static long _lastFlush;
        private static int _snapshotCursor;
        private static int _sentStates;
        private static int _receivedStates;
        private static int _resetRequested;
        private static int _resetApplied;
        private static MultiplayerRole _role;
        private static bool _connected;
        private static bool _enabled;

        private static bool ResetPending
        {
            get { return Interlocked.CompareExchange(ref _resetRequested, 0, 0) != _resetApplied; }
        }

        public static bool IsClient
        {
            get { return _enabled && !ResetPending && Command.CurrentRole == MultiplayerRole.Client; }
        }

        private static bool IsHost
        {
            get { return _enabled && !ResetPending && Command.CurrentRole == MultiplayerRole.Server; }
        }

        public static string Diagnostics()
        {
            if (!_enabled || ResetPending)
                return "Wildfire sync: inactive (requires a connected session and Natural Disasters).";
            return IsClient ? "Wildfire sync: protocol " + ProtocolVersion + ", " + Cache.TreeCount +
                " burning/recovering trees, " + _receivedStates + " states received, baseline " +
                (!Cache.GroundReady || Cache.PendingToken != 0 ? "pending." : "complete.") :
                "Wildfire sync: protocol " + ProtocolVersion + ", host authority active, " + _sentStates + " states sent.";
        }

        public static void Reset()
        {
            Interlocked.Increment(ref _resetRequested);
        }

        private static void Clear()
        {
            Cache.Reset();
            Lifetimes.Clear();
            HostTrees.Clear();
            SentTrees.Clear();
            ClientPositions.Clear();
            DirtyTrees.Clear();
            DirtyGround.Clear();
            Snapshots.Clear();
            _observedGround = new byte[WildfireCache.GroundCellCount];
            _hostSession = Guid.NewGuid().ToString("N");
            _lifetime = _revision = 0;
            _lastFlush = 0;
            _snapshotCursor = _sentStates = _receivedStates = 0;
            _connected = _enabled = false;
            _role = MultiplayerRole.None;
        }

        public static void UpdateConnection(MultiplayerRole role, bool connected)
        {
            int reset = Interlocked.CompareExchange(ref _resetRequested, 0, 0);
            if (reset != _resetApplied || role != _role || (_connected && !connected))
            {
                Clear();
                _resetApplied = reset;
            }
            _role = role;
            _connected = connected;
            _enabled = connected && LoadingManager.instance.SupportsExpansion(Expansion.NaturalDisasters);
        }

        private static ulong Lifetime(uint id)
        {
            ulong lifetime;
            return Lifetimes.TryGetValue(id, out lifetime) ? lifetime : 0;
        }

        public static void Invalidate(uint id)
        {
            if (!_enabled || ResetPending || id == 0)
                return;
            Lifetimes[id] = ++_lifetime;
            if (IsClient)
                Cache.Invalidate(id);
            else if (IsHost)
            {
                SentTrees.Remove(id);
                DirtyTrees.Add(id);
            }
        }

        private static void ReadHostTrees()
        {
            HostTrees.Clear();
            TreeManager manager = TreeManager.instance;
            for (int i = 0; i < manager.m_burningTrees.m_size; i++)
            {
                TreeManager.BurningTree fire = manager.m_burningTrees.m_buffer[i];
                if (fire.m_treeIndex != 0 && fire.m_treeIndex < manager.m_trees.m_buffer.Length)
                    HostTrees[fire.m_treeIndex] = fire;
            }
        }

        private static TreeFireState Capture(uint id)
        {
            TreeManager manager = TreeManager.instance;
            if (id == 0 || id >= manager.m_trees.m_buffer.Length ||
                (manager.m_trees.m_buffer[id].m_flags & 3) != 1)
                return null;
            TreeManager.BurningTree fire;
            bool present = HostTrees.TryGetValue(id, out fire);
            return TreeFireState.Capture(id, Lifetime(id), ref manager.m_trees.m_buffer[id], present, fire);
        }

        private static WildfireStatusCommand Packet(ulong revision)
        {
            return new WildfireStatusCommand
            {
                HostSession = _hostSession,
                Revision = revision,
                Trees = new List<TreeFireState>(),
                Ground = new List<BurnedGroundState>()
            };
        }

        public static void Answer(RequestWildfireStatusCommand command)
        {
            if (!IsHost || Command.SendToClient == null || command.Token == 0 ||
                string.IsNullOrEmpty(command.Session) || command.Session.Length != 32)
                return;
            ReadHostTrees();
            ulong revision = ++_revision;
            List<WildfireStatusCommand> parts = new List<WildfireStatusCommand>();
            WildfireStatusCommand part = Packet(revision);
            foreach (uint id in HostTrees.Keys)
            {
                TreeFireState state = Capture(id);
                if (state == null)
                    continue;
                part.Trees.Add(state);
                if (part.Trees.Count == BatchSize)
                {
                    parts.Add(part);
                    part = Packet(revision);
                }
            }
            NaturalResourceManager.ResourceCell[] ground = NaturalResourceManager.instance.m_naturalResources;
            for (int cell = 0; cell < ground.Length && cell < WildfireCache.GroundCellCount; cell++)
            {
                ObserveGround(cell);
                if (ground[cell].m_burned == 0)
                    continue;
                part.Ground.Add(new BurnedGroundState { Cell = cell, Burned = ground[cell].m_burned });
                if (part.Trees.Count + part.Ground.Count == BatchSize)
                {
                    parts.Add(part);
                    part = Packet(revision);
                }
            }
            if (part.Trees.Count + part.Ground.Count != 0 || parts.Count == 0)
                parts.Add(part);
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].RequestSession = command.Session;
                parts[i].SnapshotToken = command.Token;
                parts[i].PartIndex = i;
                parts[i].PartCount = parts.Count;
            }
            Snapshots[command.SenderId] = new Queue<WildfireStatusCommand>(parts);
        }

        public static void SimulationStep()
        {
            if (!_enabled || ResetPending)
                return;
            long now = Stopwatch.GetTimestamp();
            if (_lastFlush != 0 && now - _lastFlush < FlushTicks)
                return;
            _lastFlush = now;
            if (IsClient)
            {
                RequestWildfireStatusCommand request = Cache.Request(_lifetime, now, SnapshotTicks, RetryTicks, Stopwatch.Frequency);
                if (request != null)
                    Command.SendToServer(request);
                return;
            }
            if (!IsHost)
                return;
            ReadHostTrees();
            foreach (uint id in HostTrees.Keys)
            {
                TreeFireState sent;
                if (!SentTrees.TryGetValue(id, out sent) || !sent.SameFire(Lifetime(id),
                    ref TreeManager.instance.m_trees.m_buffer[id], HostTrees[id]))
                    DirtyTrees.Add(id);
            }
            foreach (uint id in SentTrees.Keys)
                if (!HostTrees.ContainsKey(id))
                    DirtyTrees.Add(id);
            Publish();
            SendSnapshots();
        }

        private static void Publish()
        {
            if (DirtyTrees.Count + DirtyGround.Count == 0)
                return;
            ulong revision = ++_revision;
            WildfireStatusCommand packet = Packet(revision);
            List<uint> processedTrees = new List<uint>();
            List<int> processedGround = new List<int>();
            int packets = 0;
            foreach (uint id in DirtyTrees)
            {
                TreeFireState state = Capture(id);
                processedTrees.Add(id);
                if (state == null)
                {
                    SentTrees.Remove(id);
                    continue;
                }
                packet.Trees.Add(state);
                if (state.Present)
                    SentTrees[id] = state;
                else
                    SentTrees.Remove(id);
                if (packet.Trees.Count == BatchSize)
                {
                    Broadcast(packet);
                    packet = Packet(revision);
                    if (++packets == PacketsPerFlush)
                        break;
                }
            }
            foreach (uint id in processedTrees)
                DirtyTrees.Remove(id);
            if (packets < PacketsPerFlush)
            {
                foreach (int cell in DirtyGround)
                {
                    packet.Ground.Add(new BurnedGroundState
                    {
                        Cell = cell,
                        Burned = NaturalResourceManager.instance.m_naturalResources[cell].m_burned
                    });
                    processedGround.Add(cell);
                    if (packet.Trees.Count + packet.Ground.Count == BatchSize)
                    {
                        Broadcast(packet);
                        packet = Packet(revision);
                        if (++packets == PacketsPerFlush)
                            break;
                    }
                }
            }
            foreach (int cell in processedGround)
                DirtyGround.Remove(cell);
            if (packet.Trees.Count + packet.Ground.Count != 0)
                Broadcast(packet);
        }

        private static void Broadcast(WildfireStatusCommand packet)
        {
            Command.SendToClients(packet);
            _sentStates += packet.Trees.Count + packet.Ground.Count;
        }

        private static void SendSnapshots()
        {
            List<int> peers = new List<int>(Snapshots.Keys);
            for (int i = 0; i < PacketsPerFlush && peers.Count != 0; i++)
            {
                _snapshotCursor %= peers.Count;
                int id = peers[_snapshotCursor];
                Queue<WildfireStatusCommand> queue = Snapshots[id];
                WildfireStatusCommand packet = queue.Dequeue();
                Command.SendToClient(id, packet);
                _sentStates += packet.Trees.Count + packet.Ground.Count;
                if (queue.Count == 0)
                {
                    Snapshots.Remove(id);
                    peers.RemoveAt(_snapshotCursor);
                }
                else
                    _snapshotCursor++;
            }
        }

        private struct GroundBounds
        {
            private bool _changed;
            private int _minX;
            private int _minZ;
            private int _maxX;
            private int _maxZ;

            public void Include(int cell)
            {
                int x = cell & 511;
                int z = cell >> 9;
                if (!_changed)
                {
                    _minX = _maxX = x;
                    _minZ = _maxZ = z;
                    _changed = true;
                }
                else
                {
                    _minX = Math.Min(_minX, x);
                    _minZ = Math.Min(_minZ, z);
                    _maxX = Math.Max(_maxX, x);
                    _maxZ = Math.Max(_maxZ, z);
                }
            }

            public void Refresh()
            {
                if (_changed)
                    NaturalResourceManager.instance.AreaModifiedB(_minX, _minZ, _maxX, _maxZ);
            }
        }

        public static void Receive(WildfireStatusCommand command)
        {
            if (!IsClient || command.SenderId != -1 || !command.IsValid(TreeManager.instance.m_trees.m_buffer.Length))
                return;
            bool snapshot = command.SnapshotToken != 0;
            bool firstSnapshot = snapshot && Cache.HostSession == null;
            if (snapshot && !Cache.BeginPart(command, Stopwatch.GetTimestamp()))
                return;
            if (firstSnapshot)
                ApplyGround(0, WildfireCache.GroundCellCount);
            RebuildClientPositions();
            TreeManager manager = TreeManager.instance;
            if (command.Trees != null)
            {
                foreach (TreeFireState state in command.Trees)
                {
                    if (Cache.AcceptTree(state, command, Lifetime(state.TreeId), ref manager.m_trees.m_buffer[state.TreeId]))
                    {
                        TreeFireState accepted = Cache.Get(state.TreeId, Lifetime(state.TreeId), ref manager.m_trees.m_buffer[state.TreeId]);
                        ApplyTree(accepted);
                        _receivedStates++;
                    }
                }
            }
            if (command.Ground != null)
            {
                GroundBounds bounds = default(GroundBounds);
                foreach (BurnedGroundState state in command.Ground)
                    if (Cache.AcceptGround(state, command))
                    {
                        ApplyGroundCell(state.Cell, ref bounds);
                        _receivedStates++;
                    }
                bounds.Refresh();
            }
            if (snapshot && Cache.SnapshotComplete)
            {
                List<uint> clear = new List<uint>();
                foreach (uint id in ClientPositions.Keys)
                    if (id < manager.m_trees.m_buffer.Length &&
                        Cache.ShouldClear(id, Lifetime(id), ref manager.m_trees.m_buffer[id]))
                        clear.Add(id);
                foreach (uint id in clear)
                    ApplyTree(TreeFireState.Capture(id, Lifetime(id), ref manager.m_trees.m_buffer[id],
                        false, default(TreeManager.BurningTree)));
                Cache.FinishSnapshot();
                ApplyGround(0, WildfireCache.GroundCellCount);
            }
        }

        private static void RebuildClientPositions()
        {
            ClientPositions.Clear();
            TreeManager manager = TreeManager.instance;
            for (int i = 0; i < manager.m_burningTrees.m_size; i++)
                ClientPositions[manager.m_burningTrees.m_buffer[i].m_treeIndex] = i;
        }

        private static void ApplyTree(TreeFireState state)
        {
            TreeManager manager = TreeManager.instance;
            int index;
            bool found = ClientPositions.TryGetValue(state.TreeId, out index);
            if (state.Present)
            {
                TreeManager.BurningTree fire = new TreeManager.BurningTree
                {
                    m_treeIndex = state.TreeId,
                    m_fireIntensity = state.Intensity,
                    m_fireDamage = state.Damage
                };
                if (found)
                    manager.m_burningTrees.m_buffer[index] = fire;
                else
                {
                    ClientPositions[state.TreeId] = manager.m_burningTrees.m_size;
                    manager.m_burningTrees.Add(fire);
                }
            }
            else if (found)
            {
                int last = manager.m_burningTrees.m_size - 1;
                TreeManager.BurningTree moved = manager.m_burningTrees.m_buffer[last];
                manager.m_burningTrees.m_buffer[index] = moved;
                manager.m_burningTrees.m_buffer[last] = default(TreeManager.BurningTree);
                manager.m_burningTrees.m_size = last;
                ClientPositions.Remove(state.TreeId);
                if (index != last)
                    ClientPositions[moved.m_treeIndex] = index;
            }
            ushort oldFlags = manager.m_trees.m_buffer[state.TreeId].m_flags;
            state.ApplyFlags(ref manager.m_trees.m_buffer[state.TreeId]);
            if (state.Intensity == 0)
            {
                InstanceID instance = new InstanceID { Tree = state.TreeId };
                InstanceManager.instance.SetGroup(instance, null);
            }
            if (oldFlags != manager.m_trees.m_buffer[state.TreeId].m_flags)
                manager.UpdateTreeRenderer(state.TreeId, true);
        }

        private static void ObserveGround(int cell)
        {
            byte burned = NaturalResourceManager.instance.m_naturalResources[cell].m_burned;
            if (_observedGround[cell] == burned)
                return;
            _observedGround[cell] = burned;
            DirtyGround.Add(cell);
        }

        public static void ResourceStep(uint simulationFrame)
        {
            int start = (int)(simulationFrame & 1023) * 256;
            if (IsClient)
                ApplyGround(start, 256);
            else if (IsHost)
                for (int cell = start; cell < start + 256; cell++)
                    ObserveGround(cell);
        }

        public static void ObserveGroundArea(Vector3 position, float radius)
        {
            if (!IsHost)
                return;
            radius += 33.75f * 0.51f;
            int minX = Math.Max((int)((position.x - radius) / 33.75f + 256f), 0);
            int minZ = Math.Max((int)((position.z - radius) / 33.75f + 256f), 0);
            int maxX = Math.Min((int)((position.x + radius) / 33.75f + 256f), 511);
            int maxZ = Math.Min((int)((position.z + radius) / 33.75f + 256f), 511);
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                    ObserveGround(z * 512 + x);
        }

        private static void ApplyGroundCell(int cell, ref GroundBounds bounds)
        {
            NaturalResourceManager manager = NaturalResourceManager.instance;
            if (!Cache.HasGround(cell) || manager.m_naturalResources[cell].m_burned == Cache.Ground(cell))
                return;
            manager.m_naturalResources[cell].m_burned = Cache.Ground(cell);
            bounds.Include(cell);
        }

        private static void ApplyGround(int start, int count)
        {
            GroundBounds bounds = default(GroundBounds);
            int limit = Math.Min(start + count, NaturalResourceManager.instance.m_naturalResources.Length);
            for (int cell = start; cell < limit; cell++)
                ApplyGroundCell(cell, ref bounds);
            bounds.Refresh();
        }
    }
}
