using System;
using System.Collections.Generic;
using CSM.BaseGame.Commands.Data.Trees;

namespace CSM.BaseGame.Helpers
{
    public class WildfireCache
    {
        public const int GroundCellCount = 512 * 512;
        private const int MaximumParts = (262144 + GroundCellCount) / 64;

        private class Entry
        {
            public TreeFireState State;
            public ulong LocalLifetime;
            public ulong Revision;
        }

        private class Deferred
        {
            public TreeFireState State;
            public ulong LocalLifetime;
            public ulong Revision;
            public string HostSession;
        }

        private class DeferredGround
        {
            public BurnedGroundState State;
            public ulong Revision;
            public string HostSession;
        }

        private class Snapshot
        {
            public ulong Revision;
            public bool[] Parts;
            public int Received;
            public readonly HashSet<uint> Trees = new HashSet<uint>();
            public readonly HashSet<int> Ground = new HashSet<int>();
        }

        private readonly Dictionary<uint, Entry> _trees = new Dictionary<uint, Entry>();
        private readonly Dictionary<uint, Deferred> _deferred = new Dictionary<uint, Deferred>();
        private readonly Dictionary<int, DeferredGround> _deferredGround = new Dictionary<int, DeferredGround>();
        private byte[] _ground = new byte[GroundCellCount];
        private ulong[] _groundRevisions = new ulong[GroundCellCount];
        private Snapshot _snapshot;
        private ulong _nextToken;
        private long _lastRequest;
        private long _lastProgress;
        private bool _needsSnapshot = true;

        public string Session { get; private set; }
        public string HostSession { get; private set; }
        public ulong PendingToken { get; private set; }
        public ulong PendingLocalLifetime { get; private set; }
        public bool GroundReady { get; private set; }
        public int TreeCount { get; private set; }

        public WildfireCache()
        {
            Reset();
        }

        public void Reset()
        {
            _trees.Clear();
            _deferred.Clear();
            _deferredGround.Clear();
            _ground = new byte[GroundCellCount];
            _groundRevisions = new ulong[GroundCellCount];
            _snapshot = null;
            _nextToken = 0;
            _lastRequest = _lastProgress = 0;
            _needsSnapshot = true;
            Session = Guid.NewGuid().ToString("N");
            HostSession = null;
            PendingToken = PendingLocalLifetime = 0;
            GroundReady = false;
            TreeCount = 0;
        }

        public void Invalidate(uint id)
        {
            _deferred.Remove(id);
            Entry entry;
            if (_trees.TryGetValue(id, out entry))
            {
                if (entry.State.Present)
                    TreeCount--;
                _trees.Remove(id);
            }
            if (entry != null)
                _needsSnapshot = true;
        }

        public RequestWildfireStatusCommand Request(ulong localLifetime, long now, long interval, long retry, long minimumInterval = 0)
        {
            if (PendingToken != 0 ? now - _lastProgress < retry :
                (_nextToken != 0 && now - _lastRequest < minimumInterval) ||
                (!_needsSnapshot && now - _lastRequest < interval))
                return null;

            PendingToken = ++_nextToken;
            PendingLocalLifetime = localLifetime;
            _snapshot = null;
            _needsSnapshot = false;
            _lastRequest = _lastProgress = now;
            return new RequestWildfireStatusCommand { Session = Session, Token = PendingToken };
        }

        public bool BeginPart(WildfireStatusCommand command, long now)
        {
            if (command.SnapshotToken == 0 || command.SnapshotToken != PendingToken ||
                command.RequestSession != Session || string.IsNullOrEmpty(command.HostSession) ||
                command.Revision == 0 || command.PartCount < 1 || command.PartCount > MaximumParts ||
                command.PartIndex < 0 || command.PartIndex >= command.PartCount ||
                (HostSession != null && HostSession != command.HostSession))
                return false;

            if (_snapshot == null)
                _snapshot = new Snapshot { Revision = command.Revision, Parts = new bool[command.PartCount] };
            if (_snapshot.Revision != command.Revision || _snapshot.Parts.Length != command.PartCount ||
                _snapshot.Parts[command.PartIndex])
                return false;

            HostSession = command.HostSession;
            foreach (DeferredGround pending in _deferredGround.Values)
            {
                int cell = pending.State.Cell;
                if (pending.HostSession == HostSession && pending.Revision > _groundRevisions[cell])
                {
                    _ground[cell] = pending.State.Burned;
                    _groundRevisions[cell] = pending.Revision;
                }
            }
            _deferredGround.Clear();
            _snapshot.Parts[command.PartIndex] = true;
            _snapshot.Received++;
            _lastProgress = now;
            return true;
        }

        public bool AcceptTree(TreeFireState state, WildfireStatusCommand command,
            ulong localLifetime, ref TreeInstance tree)
        {
            if (state == null || !state.IsValid() || command.Revision == 0 ||
                !state.Matches(ref tree))
                return false;

            if (command.SnapshotToken == 0 && HostSession == null)
            {
                Defer(state, command, localLifetime);
                return false;
            }
            if (HostSession != command.HostSession)
                return false;

            Entry entry;
            _trees.TryGetValue(state.TreeId, out entry);
            if (command.SnapshotToken != 0)
            {
                if (_snapshot == null || command.SnapshotToken != PendingToken ||
                    command.RequestSession != Session || _snapshot.Revision != command.Revision)
                    return false;
                if (localLifetime > PendingLocalLifetime)
                {
                    _needsSnapshot = true;
                    return false;
                }
                if (state.Present)
                    _snapshot.Trees.Add(state.TreeId);
            }
            else if (entry == null || entry.LocalLifetime != localLifetime || entry.State.Lifetime != state.Lifetime)
            {
                Defer(state, command, localLifetime);
                return false;
            }

            if (entry != null && entry.Revision > command.Revision)
                return false;
            if (entry != null && entry.State.Present)
                TreeCount--;
            ulong revision = command.Revision;
            Deferred deferred;
            if (_deferred.TryGetValue(state.TreeId, out deferred) && deferred.HostSession == HostSession &&
                deferred.LocalLifetime == localLifetime && deferred.State.Lifetime == state.Lifetime &&
                deferred.State.Matches(ref tree) && deferred.Revision > revision)
            {
                state = deferred.State;
                revision = deferred.Revision;
            }
            _deferred.Remove(state.TreeId);
            _trees[state.TreeId] = new Entry { State = state, LocalLifetime = localLifetime, Revision = revision };
            if (state.Present)
                TreeCount++;
            return true;
        }

        private void Defer(TreeFireState state, WildfireStatusCommand command, ulong localLifetime)
        {
            _needsSnapshot = true;
            Deferred previous;
            if (!_deferred.TryGetValue(state.TreeId, out previous) || previous.Revision <= command.Revision)
                _deferred[state.TreeId] = new Deferred
                {
                    State = state,
                    LocalLifetime = localLifetime,
                    Revision = command.Revision,
                    HostSession = command.HostSession
                };
        }

        public bool AcceptGround(BurnedGroundState state, WildfireStatusCommand command)
        {
            if (state == null || state.Cell < 0 || state.Cell >= GroundCellCount ||
                command.Revision == 0)
                return false;
            if (HostSession == null && command.SnapshotToken == 0)
            {
                DeferredGround previous;
                if (!_deferredGround.TryGetValue(state.Cell, out previous) || previous.Revision <= command.Revision)
                    _deferredGround[state.Cell] = new DeferredGround { State = state, Revision = command.Revision, HostSession = command.HostSession };
                return false;
            }
            if (HostSession == null || HostSession != command.HostSession)
                return false;
            if (command.SnapshotToken != 0)
            {
                if (_snapshot == null || command.SnapshotToken != PendingToken ||
                    command.RequestSession != Session || _snapshot.Revision != command.Revision)
                    return false;
                _snapshot.Ground.Add(state.Cell);
            }
            if (_groundRevisions[state.Cell] > command.Revision)
                return false;
            _ground[state.Cell] = state.Burned;
            _groundRevisions[state.Cell] = command.Revision;
            return true;
        }

        public TreeFireState Get(uint id, ulong localLifetime, ref TreeInstance tree)
        {
            Entry entry;
            return _trees.TryGetValue(id, out entry) && entry.LocalLifetime == localLifetime && entry.State.Matches(ref tree)
                ? entry.State : null;
        }

        public bool HasGround(int cell)
        {
            return GroundReady || _groundRevisions[cell] != 0;
        }

        public byte Ground(int cell)
        {
            return _ground[cell];
        }

        public bool SnapshotComplete
        {
            get { return _snapshot != null && _snapshot.Received == _snapshot.Parts.Length; }
        }

        public bool ShouldClear(uint id, ulong localLifetime, ref TreeInstance tree)
        {
            if (!SnapshotComplete || localLifetime > PendingLocalLifetime)
                return false;
            Entry entry;
            if (_trees.TryGetValue(id, out entry) && entry.Revision > _snapshot.Revision)
                return false;
            TreeFireState state = Get(id, localLifetime, ref tree);
            return state == null || !state.Present || !_snapshot.Trees.Contains(id);
        }

        public void FinishSnapshot()
        {
            if (!SnapshotComplete)
                return;
            List<uint> ids = new List<uint>(_trees.Keys);
            foreach (uint id in ids)
            {
                Entry entry = _trees[id];
                if (entry.LocalLifetime <= PendingLocalLifetime && entry.Revision <= _snapshot.Revision &&
                    !_snapshot.Trees.Contains(id))
                {
                    if (entry.State.Present)
                        TreeCount--;
                    _trees[id] = new Entry
                    {
                        State = entry.State.WithoutFire(),
                        LocalLifetime = entry.LocalLifetime,
                        Revision = _snapshot.Revision
                    };
                }
            }
            for (int cell = 0; cell < GroundCellCount; cell++)
            {
                if (_groundRevisions[cell] <= _snapshot.Revision && !_snapshot.Ground.Contains(cell))
                {
                    _ground[cell] = 0;
                    _groundRevisions[cell] = _snapshot.Revision;
                }
            }
            foreach (uint id in new List<uint>(_deferred.Keys))
                if (_deferred[id].Revision <= _snapshot.Revision)
                    _deferred.Remove(id);
            GroundReady = true;
            PendingToken = 0;
            _snapshot = null;
        }
    }
}
