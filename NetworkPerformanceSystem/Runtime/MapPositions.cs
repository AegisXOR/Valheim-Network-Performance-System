using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M39 - where players are reaches everyone's map as soon as it matters, not on the next
    /// 2-second tick.
    ///
    /// Other players' map icons come from the player list the host sends every 2 seconds
    /// (ZNet.SendPeriodicData -> SendPlayerList), built from each peer's m_refPos - which M23
    /// already keeps live on an NPS host. So the host knows within a frame that somebody has gone
    /// through a portal; everyone else hears it up to 2 seconds later. Resending the whole list
    /// sooner would cost about 100 bytes for every player online, to every player, and re-run the
    /// player history update M32 watches. Instead:
    ///
    ///   * Host: once a frame, every player whose spot in the last list is now more than the
    ///     accuracy setting away (flat - the map has no height, and a dungeon door is no move on
    ///     it) goes out in a small "Nps.MapPositions" message: their character and position, 24
    ///     bytes each. A portal trip always qualifies. The list itself is untouched and resets
    ///     what everyone is known to have each time it goes out.
    ///   * Client: such a message updates the matching entries in ZNet's player list. It travels
    ///     on the same ordered connection as the list, so a newer list is never overwritten by an
    ///     older message. Players without the mod drop the unknown method and keep vanilla's list.
    ///   * Client: when this player's own reference position jumps (a portal, a respawn), their
    ///     report goes to the host at once instead of within 2 seconds - vanilla's own message
    ///     (ZNet.SendServerSyncPlayerData), sent early. The host then also starts sending the new
    ///     area's objects sooner. On an NPS host M23 has the character anyway; this is for the
    ///     moments a player has no character, and for servers without the mod.
    /// </summary>
    internal static class MapPositions {

        /// <summary>The host's update to everyone's player list. A version byte first, so a later
        /// version can change the layout and an older client simply ignores it.</summary>
        internal const string RpcMapPositions = "Nps.MapPositions";

        internal const byte Version = 1;

        /// <summary>A move this long is a jump - a portal, a respawn. Nothing else in the game
        /// covers it between two 2-second reports (LiveRefPos.DivergenceLimitMeters).</summary>
        internal const float JumpMeters = 64f;

        /// <summary>A player's own early reports are at least this far apart, however their
        /// reference position bounces (a portal that turns out blocked sends them straight back).</summary>
        internal const float EarlyReportSpacingSeconds = 0.5f;

        /// <summary>More entries than this is not a message we sent. Bounds the read.</summary>
        internal const int MaxEntries = 1024;

        internal const int MinAccuracyMeters = 5;
        internal const int MaxAccuracyMeters = 64;

        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.MapPositions)
            && ValConfig.SendMapPositionsRightAway != null
            && ValConfig.SendMapPositionsRightAway.Value;

        internal static float AccuracyMeters =>
            ValConfig.MapPositionAccuracyMeters != null
                ? Mathf.Clamp(ValConfig.MapPositionAccuracyMeters.Value, MinAccuracyMeters, MaxAccuracyMeters)
                : 20f;

        // Since start. Main thread.
        internal static long Updates;           // host: messages sent, one per player told
        internal static long PlayersMoved;      // host: players whose position went out
        internal static long Jumped;            // host: of those, ones that had moved more than JumpMeters
        internal static long BytesSent;         // host
        internal static long UpdatesApplied;    // client: messages read
        internal static long PositionsApplied;  // client: player list entries they moved
        internal static long EarlyReports;      // client: own reports sent ahead of the 2-second one

        internal struct Mover {
            internal long Key;          // peer uid, or the listen host's own session id
            internal ZDOID Character;
            internal Vector3 Position;
            internal bool Jumped;
        }

        // -- host --------------------------------------------------------------------------

        /// <summary>Where everyone's player list has each player: the last list, or a message
        /// since. Keyed like Mover.Key.</summary>
        private static readonly Dictionary<long, Vector3> Announced = new Dictionary<long, Vector3>();
        private static readonly List<Mover> Movers = new List<Mover>();

        /// <summary>Once per frame on the host, from NetworkChannelPatches.Tick: after
        /// SendPeriodicData and after M23's refresh in ZDOMan.Update, so m_refPos is this frame's.</summary>
        internal static void TickHost() {
            if (!Active) { return; }
            ZNet net = ZNet.instance;
            if (net == null) { return; }

            List<ZNetPeer> peers = net.GetPeers();
            bool hasHost = TryGetHostEntry(net, out Mover host);
            Movers.Clear();
            CollectMovers(peers, hasHost, host, AccuracyMeters, Announced, Movers);
            if (Movers.Count == 0) { return; }
            Send(peers, Movers);
        }

        /// <summary>From the postfix on ZNet.SendPlayerList: everyone now has every player where
        /// m_refPos says, so that is the new baseline. Runs whatever the setting says, so that
        /// switching it on mid-session starts from what players really have.</summary>
        internal static void NoteListSent(ZNet net) {
            if (net == null) { return; }
            bool hasHost = TryGetHostEntry(net, out Mover host);
            NoteListSent(net.GetPeers(), hasHost, host, Announced);
        }

        /// <summary>The listen host's own entry in the list, as UpdatePlayerList builds it. None on
        /// a dedicated server, which is not a player.</summary>
        private static bool TryGetHostEntry(ZNet net, out Mover host) {
            host = default(Mover);
            if (NpsEnv.IsDedicated() || !net.IsReferencePositionPublic() || net.m_characterID.IsNone()) { return false; }
            host = new Mover { Key = NpsEnv.LocalSessionId(), Character = net.m_characterID, Position = net.GetReferencePosition() };
            return host.Key != 0L;
        }

        // -- host, pure --------------------------------------------------------------------

        /// <summary>
        /// Every player whose position in everyone's list is now more than `accuracy` away, flat.
        /// A player seen for the first time is only recorded: the last list had them wherever
        /// they were, and the next one corrects anything this misses. A player with no position
        /// on the map (hidden, no character yet, dead) is forgotten, so their first position after
        /// coming back is a fresh baseline rather than a jump.
        /// </summary>
        internal static void CollectMovers(List<ZNetPeer> peers, bool hasHost, Mover host, float accuracy,
                                           Dictionary<long, Vector3> announced, List<Mover> into) {
            float limitSq = accuracy * accuracy;
            if (peers != null) {
                for (int i = 0; i < peers.Count; i++) {
                    ZNetPeer peer = peers[i];
                    if (peer == null || !peer.IsReady()) { continue; }
                    if (!OnTheMap(peer)) {
                        announced.Remove(peer.m_uid);
                        continue;
                    }
                    Consider(peer.m_uid, peer.m_characterID, peer.m_refPos, limitSq, announced, into);
                }
            }
            if (hasHost) {
                if (LiveRefPos.IsPlausibleBody(host.Position)) {
                    Consider(host.Key, host.Character, host.Position, limitSq, announced, into);
                } else {
                    announced.Remove(host.Key);
                }
            }
        }

        private static void Consider(long key, ZDOID character, Vector3 position, float limitSq,
                                     Dictionary<long, Vector3> announced, List<Mover> into) {
            if (!announced.TryGetValue(key, out Vector3 had)) {
                announced[key] = position;
                return;
            }
            float movedSq = FlatDistanceSq(had, position);
            if (movedSq <= limitSq) { return; }
            into.Add(new Mover { Key = key, Character = character, Position = position, Jumped = movedSq > JumpMeters * JumpMeters });
        }

        /// <summary>The baseline after a list went out: what UpdatePlayerList just read.</summary>
        internal static void NoteListSent(List<ZNetPeer> peers, bool hasHost, Mover host, Dictionary<long, Vector3> announced) {
            if (peers != null) {
                for (int i = 0; i < peers.Count; i++) {
                    ZNetPeer peer = peers[i];
                    if (peer == null || !peer.IsReady()) { continue; }
                    if (OnTheMap(peer)) {
                        announced[peer.m_uid] = peer.m_refPos;
                    } else {
                        announced.Remove(peer.m_uid);
                    }
                }
            }
            if (hasHost && LiveRefPos.IsPlausibleBody(host.Position)) {
                announced[host.Key] = host.Position;
            }
        }

        /// <summary>Shown on other players' maps: public, with a character, somewhere real.</summary>
        private static bool OnTheMap(ZNetPeer peer) {
            return peer.m_publicRefPos && !peer.m_characterID.IsNone() && LiveRefPos.IsPlausibleBody(peer.m_refPos);
        }

        internal static float FlatDistanceSq(Vector3 a, Vector3 b) {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// The message for one player: every mover except themselves (their own game leaves its
        /// own character off the map anyway). Null when that leaves nothing. A new package per
        /// player - a PlayFab connection appends to the package it is handed.
        /// </summary>
        internal static ZPackage BuildPackage(List<Mover> movers, long recipientKey) {
            int count = 0;
            for (int i = 0; i < movers.Count; i++) {
                if (movers[i].Key != recipientKey) { count++; }
            }
            if (count == 0) { return null; }

            ZPackage pkg = new ZPackage();
            pkg.Write(Version);
            pkg.Write(count);
            for (int i = 0; i < movers.Count; i++) {
                if (movers[i].Key == recipientKey) { continue; }
                pkg.Write(movers[i].Character);
                pkg.Write(movers[i].Position);
            }
            return pkg;
        }

        private static void Send(List<ZNetPeer> peers, List<Mover> movers) {
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady() || peer.m_rpc == null) { continue; }
                ZPackage pkg = BuildPackage(movers, peer.m_uid);
                if (pkg == null) { continue; }
                int size = pkg.Size();
                peer.m_rpc.Invoke(RpcMapPositions, pkg);
                Updates++;
                BytesSent += size;
            }
            for (int i = 0; i < movers.Count; i++) {
                Announced[movers[i].Key] = movers[i].Position;
                PlayersMoved++;
                if (movers[i].Jumped) { Jumped++; }
            }
        }

        // -- client ------------------------------------------------------------------------

        /// <summary>Client: the host's update. A host ignores it - only a host sends one.</summary>
        internal static void RPC_MapPositions(ZRpc rpc, ZPackage pkg) {
            if (NpsEnv.IsHost() || !PatchGuard.IsActive(Mechanism.MapPositions)) { return; }
            ZNet net = ZNet.instance;
            if (net == null) { return; }

            int applied = Apply(net.m_players, pkg);
            if (applied < 0) { return; }
            UpdatesApplied++;
            PositionsApplied += applied;
        }

        /// <summary>
        /// Moves the matching entries of the player list. Only the position: whether a player is
        /// on the map at all is the list's to say, and the next one does. Returns how many entries
        /// moved, or -1 for a message that cannot be read, which changes nothing it has not
        /// already changed.
        /// </summary>
        internal static int Apply(List<ZNet.PlayerInfo> players, ZPackage pkg) {
            if (players == null || pkg == null) { return -1; }
            try {
                byte version = pkg.ReadByte();
                if (version != Version) { return -1; }
                int count = pkg.ReadInt();
                if (count < 0 || count > MaxEntries) { return -1; }

                int applied = 0;
                for (int i = 0; i < count; i++) {
                    ZDOID character = pkg.ReadZDOID();
                    Vector3 position = pkg.ReadVector3();
                    if (character.IsNone() || !LiveRefPos.IsPlausible(position)) { continue; }
                    for (int j = 0; j < players.Count; j++) {
                        ZNet.PlayerInfo info = players[j];
                        if (info.m_characterID != character) { continue; }
                        info.m_position = position;
                        players[j] = info;
                        applied++;
                        break;
                    }
                }
                return applied;
            } catch (Exception) {
                return -1;
            }
        }

        private static Vector3 _lastReported;
        private static bool _haveReported;
        private static float _sinceEarly = EarlyReportSpacingSeconds;

        /// <summary>From the postfix on ZNet.SendServerSyncPlayerData: the position the host was
        /// just told, by vanilla's 2-second report or by ours.</summary>
        internal static void NoteReportSent(Vector3 position) {
            _lastReported = position;
            _haveReported = true;
        }

        /// <summary>Once per frame on a client, from NetworkChannelPatches.Tick - after
        /// ZDOMan.Update, so a character that jumped has normally gone out ahead of the report.</summary>
        internal static void TickClient(float dt) {
            _sinceEarly += dt;
            if (!Active) { return; }
            ZNet net = ZNet.instance;
            if (net == null) { return; }
            if (!ShouldReportEarly(_haveReported, _lastReported, net.GetReferencePosition(), _sinceEarly)) { return; }

            bool sent = false;
            List<ZNetPeer> peers = net.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                if (peers[i] == null || !peers[i].IsReady()) { continue; }
                net.SendServerSyncPlayerData(peers[i]);    // vanilla's own report; its postfix moves the baseline
                sent = true;
            }
            if (!sent) { return; }
            _sinceEarly = 0f;
            EarlyReports++;
        }

        /// <summary>Whether this player's position has jumped since the host last heard it. Never
        /// before the first report: the game sends that one itself once the connection is up.</summary>
        internal static bool ShouldReportEarly(bool haveReported, Vector3 lastReported, Vector3 now, float sinceEarly) {
            if (!haveReported || sinceEarly < EarlyReportSpacingSeconds) { return false; }
            if (!LiveRefPos.IsPlausible(now)) { return false; }
            return FlatDistanceSq(lastReported, now) > JumpMeters * JumpMeters;
        }

        // -- lifecycle ---------------------------------------------------------------------

        internal static void ForgetPeer(long peerUid) {
            Announced.Remove(peerUid);
        }

        internal static void Reset() {
            Announced.Clear();
            Movers.Clear();
            _haveReported = false;
            _lastReported = Vector3.zero;
            _sinceEarly = EarlyReportSpacingSeconds;
        }
    }
}
