using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M38 - other players' map icons keep up with them.
    ///
    /// The map draws another player where ZNet's player list says they are, and vanilla moves the
    /// icon toward each new spot at 200 m/s (Minimap.UpdatePlayerPins). That is smooth for a walk,
    /// but a portal trip of a few kilometres becomes half a minute or more of sliding, and a trip
    /// across the world well over a minute - long enough for a group to lose each other. The slide
    /// is in 3D, too, so stepping into a dungeon (5000m up) slows the icon's way across the map
    /// as well. Two changes, both on the viewer's side, so they work against any server:
    ///
    ///   * A gap nobody covers between two lists - a portal, a respawn, a dungeon door - is closed
    ///     at once. Anything shorter still slides, as vanilla.
    ///   * Where this game has a better position than the list's, the icon uses it. A player loaded
    ///     around us is drawn where their character is in our world, and on the host every player
    ///     is drawn where the host has them - M23's live position, or their own report. Only
    ///     players already in the list are touched, so somebody who hides their position from the
    ///     map stays hidden.
    ///
    /// A loaded character is never a stale one: when a player leaves our area the host tells us
    /// their sector is no longer ours (ZDOMan's invalid-sector list) and the character is removed,
    /// so the icon falls back to the list from the next frame.
    /// </summary>
    internal static class MapPins {

        /// <summary>A gap the icon closes at once rather than sliding. Nothing in the game moves a
        /// player 64m between two 2-second lists except a teleport - the same reasoning as
        /// LiveRefPos.DivergenceLimitMeters - so anything further is a jump, and sliding there only
        /// draws a path nobody took.</summary>
        internal const float SnapMeters = 64f;

        // Since start. Main thread, like the minimap.
        internal static long Snapped;          // icons that jumped to a far spot instead of sliding there

        // The last frame's icons, for nps_stats.
        internal static int LastFromLoaded;    // drawn where the player's character is in this world
        internal static int LastFromHost;      // drawn where the host has the player
        internal static int LastFromList;      // drawn where the player list says

        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.MapPins)
            && ValConfig.MapPinsFollowPlayers != null
            && ValConfig.MapPinsFollowPlayers.Value;

        // -- the two calls in Minimap.UpdatePlayerPins -------------------------------------

        /// <summary>
        /// Stands in for UpdatePlayerPins' `ZNet.instance.GetOtherPublicPlayers(m_tempPlayerInfo)`.
        /// The game's own list first, then a better position for each player we have one for. The
        /// list is the minimap's own copy, so ZNet's player list is left as the host sent it.
        /// </summary>
        internal static void GetOtherPublicPlayers(ZNet net, List<ZNet.PlayerInfo> playerList) {
            net.GetOtherPublicPlayers(playerList);
            if (!Active || playerList == null || playerList.Count == 0) { return; }

            try {
                CollectLoaded(Loaded);
                List<ZNetPeer> peers = net.IsServer() ? net.GetPeers() : null;
                Refine(playerList, Loaded, peers, out LastFromLoaded, out LastFromHost, out LastFromList);
            } catch (Exception e) {
                PatchGuard.Disable(Mechanism.MapPins,
                    $"{e.GetType().Name} while placing other players' map icons: {e.Message}. They are placed where the player list says, as vanilla.");
            }
        }

        /// <summary>
        /// Stands in for UpdatePlayerPins' `Vector3.MoveTowards(pin, target, 200f * dt)`, which
        /// only runs for an icon that already shows this player. A gap of more than SnapMeters is
        /// closed at once; anything else moves exactly as vanilla.
        /// </summary>
        internal static Vector3 Step(Vector3 current, Vector3 target, float maxDistanceDelta) {
            if (Active && Jumps(current, target)) {
                Snapped++;
                return target;
            }
            return Vector3.MoveTowards(current, target, maxDistanceDelta);
        }

        // -- pure --------------------------------------------------------------------------

        /// <summary>Whether an icon at `from` should jump straight to `to`. In 3D, like the slide it
        /// replaces: a dungeon door moves a player 5000m up, which on the map is no move at all but
        /// to vanilla's slide is a long one.</summary>
        internal static bool Jumps(Vector3 from, Vector3 to) {
            return (to - from).sqrMagnitude > SnapMeters * SnapMeters;
        }

        /// <summary>
        /// Gives each player in the list the best position there is for them: their character
        /// loaded in this world, else (handed peers, on the host) where the host has them, else the
        /// list's own. Split from the game lookups so it runs without a live ZNet.
        /// </summary>
        internal static void Refine(List<ZNet.PlayerInfo> playerList, List<KeyValuePair<ZDOID, Vector3>> loaded, List<ZNetPeer> peers,
                                    out int fromLoaded, out int fromHost, out int fromList) {
            fromLoaded = 0;
            fromHost = 0;
            fromList = 0;
            for (int i = 0; i < playerList.Count; i++) {
                ZNet.PlayerInfo info = playerList[i];
                Vector3 position;
                if (TryFindLoaded(loaded, info.m_characterID, out position)) {
                    fromLoaded++;
                } else if (peers != null && TryFindPeer(peers, info.m_characterID, out position)) {
                    fromHost++;
                } else {
                    fromList++;
                    continue;
                }
                info.m_position = position;
                playerList[i] = info;
            }
        }

        private static bool TryFindLoaded(List<KeyValuePair<ZDOID, Vector3>> loaded, ZDOID character, out Vector3 position) {
            position = Vector3.zero;
            if (loaded == null || character.IsNone()) { return false; }
            for (int i = 0; i < loaded.Count; i++) {
                if (loaded[i].Key != character) { continue; }
                position = loaded[i].Value;
                return true;
            }
            return false;
        }

        /// <summary>The host's position for the player playing this character - while they still
        /// show it on the map. Someone who has just hidden theirs keeps the list's last position
        /// until the next list drops them, as vanilla.</summary>
        private static bool TryFindPeer(List<ZNetPeer> peers, ZDOID character, out Vector3 position) {
            position = Vector3.zero;
            if (character.IsNone()) { return false; }
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady() || peer.m_characterID != character) { continue; }
                if (!peer.m_publicRefPos || !LiveRefPos.IsPlausibleBody(peer.m_refPos)) { return false; }
                position = peer.m_refPos;
                return true;
            }
            return false;
        }

        // -- live --------------------------------------------------------------------------

        private static readonly List<KeyValuePair<ZDOID, Vector3>> Loaded = new List<KeyValuePair<ZDOID, Vector3>>();

        /// <summary>Every player character in this world with its position. Ours is in there too
        /// and simply never matches: the list leaves out our own character.</summary>
        private static void CollectLoaded(List<KeyValuePair<ZDOID, Vector3>> into) {
            into.Clear();
            List<Player> players = Player.GetAllPlayers();
            for (int i = 0; i < players.Count; i++) {
                Player player = players[i];
                if (player == null) { continue; }
                ZDOID id = player.GetZDOID();
                if (id.IsNone()) { continue; }
                into.Add(new KeyValuePair<ZDOID, Vector3>(id, player.transform.position));
            }
        }

        // -- lifecycle ---------------------------------------------------------------------

        internal static void Reset() {
            Loaded.Clear();
            LastFromLoaded = 0;
            LastFromHost = 0;
            LastFromList = 0;
        }
    }
}
