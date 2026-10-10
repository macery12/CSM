using System.Collections.Generic;
using CSM.API.Commands;
using CSM.BaseGame.Helpers;
using ProtoBuf;

namespace CSM.BaseGame.Commands.Data.Trees
{
    [ProtoContract]
    public class WildfireStatusCommand : CommandBase
    {
        [ProtoMember(1)]
        public string HostSession { get; set; }

        [ProtoMember(2)]
        public ulong Revision { get; set; }

        [ProtoMember(3)]
        public List<TreeFireState> Trees { get; set; }

        [ProtoMember(4)]
        public List<BurnedGroundState> Ground { get; set; }

        [ProtoMember(5)]
        public string RequestSession { get; set; }

        [ProtoMember(6)]
        public ulong SnapshotToken { get; set; }

        [ProtoMember(7)]
        public int PartIndex { get; set; }

        [ProtoMember(8)]
        public int PartCount { get; set; }

        public bool IsValid(int treeCapacity)
        {
            if (string.IsNullOrEmpty(HostSession) || HostSession.Length != 32 || Revision == 0 ||
                (Trees == null ? 0 : Trees.Count) + (Ground == null ? 0 : Ground.Count) > WildfireSync.BatchSize)
                return false;
            if (Trees != null)
                foreach (TreeFireState state in Trees)
                    if (state == null || !state.IsValid() || state.TreeId >= treeCapacity)
                        return false;
            if (Ground != null)
                foreach (BurnedGroundState state in Ground)
                    if (state == null || state.Cell < 0 || state.Cell >= WildfireCache.GroundCellCount)
                        return false;
            return SnapshotToken != 0 ? !string.IsNullOrEmpty(RequestSession) && RequestSession.Length == 32 &&
                PartCount >= 1 && PartCount <= 8192 && PartIndex >= 0 && PartIndex < PartCount :
                PartCount == 0 && PartIndex == 0 && string.IsNullOrEmpty(RequestSession);
        }
    }

    [ProtoContract]
    public class RequestWildfireStatusCommand : CommandBase
    {
        [ProtoMember(1)]
        public string Session { get; set; }

        [ProtoMember(2)]
        public ulong Token { get; set; }
    }

    [ProtoContract]
    public class TreeFireState
    {
        public const ushort FireMask = (ushort)(TreeInstance.Flags.Burning | TreeInstance.Flags.FireDamage);

        [ProtoMember(1)]
        public uint TreeId { get; set; }

        [ProtoMember(2)]
        public ulong Lifetime { get; set; }

        [ProtoMember(3)]
        public ushort InfoIndex { get; set; }

        [ProtoMember(4)]
        public short PositionX { get; set; }

        [ProtoMember(5)]
        public ushort PositionY { get; set; }

        [ProtoMember(6)]
        public short PositionZ { get; set; }

        [ProtoMember(7)]
        public bool Present { get; set; }

        [ProtoMember(8)]
        public byte Intensity { get; set; }

        [ProtoMember(9)]
        public byte Damage { get; set; }

        [ProtoMember(10)]
        public ushort FireFlags { get; set; }

        public bool IsValid()
        {
            return TreeId != 0 && (FireFlags & ~FireMask) == 0 &&
                (Present || (Intensity == 0 && Damage == 0 && FireFlags == 0));
        }

        public bool Matches(ref TreeInstance tree)
        {
            return (tree.m_flags & (ushort)(TreeInstance.Flags.Created | TreeInstance.Flags.Deleted)) ==
                (ushort)TreeInstance.Flags.Created && tree.m_infoIndex == InfoIndex &&
                tree.m_posX == PositionX && tree.m_posY == PositionY && tree.m_posZ == PositionZ;
        }

        public static TreeFireState Capture(uint id, ulong lifetime, ref TreeInstance tree,
            bool present, TreeManager.BurningTree fire)
        {
            return new TreeFireState
            {
                TreeId = id,
                Lifetime = lifetime,
                InfoIndex = tree.m_infoIndex,
                PositionX = tree.m_posX,
                PositionY = tree.m_posY,
                PositionZ = tree.m_posZ,
                Present = present,
                Intensity = present ? fire.m_fireIntensity : (byte)0,
                Damage = present ? fire.m_fireDamage : (byte)0,
                FireFlags = present ? (ushort)(tree.m_flags & FireMask) : (ushort)0
            };
        }

        public bool SameAs(TreeFireState other)
        {
            return other != null && TreeId == other.TreeId && Lifetime == other.Lifetime &&
                InfoIndex == other.InfoIndex && PositionX == other.PositionX && PositionY == other.PositionY &&
                PositionZ == other.PositionZ && Present == other.Present && Intensity == other.Intensity &&
                Damage == other.Damage && FireFlags == other.FireFlags;
        }

        public bool SameFire(ulong lifetime, ref TreeInstance tree, TreeManager.BurningTree fire)
        {
            return Present && Lifetime == lifetime && InfoIndex == tree.m_infoIndex &&
                PositionX == tree.m_posX && PositionY == tree.m_posY && PositionZ == tree.m_posZ &&
                Intensity == fire.m_fireIntensity && Damage == fire.m_fireDamage &&
                FireFlags == (tree.m_flags & FireMask);
        }

        public TreeFireState WithoutFire()
        {
            return new TreeFireState
            {
                TreeId = TreeId,
                Lifetime = Lifetime,
                InfoIndex = InfoIndex,
                PositionX = PositionX,
                PositionY = PositionY,
                PositionZ = PositionZ
            };
        }

        public void ApplyFlags(ref TreeInstance tree)
        {
            tree.m_flags = (ushort)((tree.m_flags & ~FireMask) | FireFlags);
        }
    }

    [ProtoContract]
    public class BurnedGroundState
    {
        [ProtoMember(1)]
        public int Cell { get; set; }

        [ProtoMember(2)]
        public byte Burned { get; set; }
    }
}
