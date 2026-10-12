using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M39 - wiring for MapPositions. See it for why; this file is only the hooks. The message
    /// itself is registered with the mod's other channels (NetworkChannelPatches.RegisterChannels)
    /// and the per-frame work runs from its Tick.
    ///
    ///   * ZNet.SendPlayerList (postfix, host): the list just went to everyone, so where it put each
    ///     player is what everyone now has.
    ///   * ZNet.SendServerSyncPlayerData (postfix, client): the position the host was just told,
    ///     whether by vanilla's 2-second report or by an early one.
    /// </summary>
    [HarmonyPatch]
    internal static class MapPositionsPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo list = AccessTools.Method(typeof(ZNet), "SendPlayerList", new System.Type[0]);
            MethodInfo report = AccessTools.Method(typeof(ZNet), "SendServerSyncPlayerData", new[] { typeof(ZNetPeer) });
            FieldInfo players = AccessTools.Field(typeof(ZNet), "m_players");
            FieldInfo character = AccessTools.Field(typeof(ZNet), "m_characterID");
            FieldInfo publicRefPos = AccessTools.Field(typeof(ZNetPeer), "m_publicRefPos");

            if (list == null || report == null || players == null || character == null || publicRefPos == null) {
                PatchGuard.Disable(Mechanism.MapPositions,
                    "ZNet.SendPlayerList / SendServerSyncPlayerData, ZNet's m_players / m_characterID or ZNetPeer.m_publicRefPos do not have the " +
                    "expected shape. Either the game updated or another mod rewrote them. Other players' map positions arrive with the game's " +
                    "player list every 2 seconds, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZNet), "SendPlayerList")]
        [HarmonyPostfix]
        private static void ListSent(ZNet __instance) {
            if (!NpsEnv.IsHost()) { return; }
            MapPositions.NoteListSent(__instance);
        }

        [HarmonyPatch(typeof(ZNet), "SendServerSyncPlayerData")]
        [HarmonyPostfix]
        private static void ReportSent(ZNet __instance) {
            if (NpsEnv.IsHost()) { return; }
            MapPositions.NoteReportSent(__instance.GetReferencePosition());
        }
    }
}
