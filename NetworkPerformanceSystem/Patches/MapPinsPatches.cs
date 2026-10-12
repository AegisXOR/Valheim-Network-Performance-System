using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M38 - wiring for MapPins. See it for why; this file is only the hook.
    ///
    ///   * Minimap.UpdatePlayerPins, transpiled: its one `ZNet.GetOtherPublicPlayers(list)` becomes
    ///     MapPins.GetOtherPublicPlayers(ZNet, list), and its one `Vector3.MoveTowards(pin, target,
    ///     200f * dt)` becomes MapPins.Step with the same three arguments. A call swapped for a call
    ///     each, the stack the same on both sides, so it composes with anything else patching the
    ///     method; vanilla's check right after the move, which flags the pins for a redraw, still
    ///     sees the new position.
    ///
    /// Not applied on a dedicated server, which draws no map.
    /// </summary>
    [HarmonyPatch]
    internal static class MapPinsPatches {

        private const string PinsMethod = "UpdatePlayerPins";

        private static readonly MethodInfo GetOtherPublicPlayers =
            AccessTools.Method(typeof(ZNet), nameof(ZNet.GetOtherPublicPlayers), new[] { typeof(List<ZNet.PlayerInfo>) });

        private static readonly MethodInfo MoveTowards =
            AccessTools.Method(typeof(Vector3), nameof(Vector3.MoveTowards), new[] { typeof(Vector3), typeof(Vector3), typeof(float) });

        [HarmonyPrepare]
        private static bool Prepare() {
            if (NpsEnv.IsDedicated()) { return false; }

            MethodInfo pins = AccessTools.Method(typeof(Minimap), PinsMethod, new[] { typeof(float) });
            if (pins == null || GetOtherPublicPlayers == null || MoveTowards == null) {
                PatchGuard.Disable(Mechanism.MapPins,
                    $"Minimap.{PinsMethod}, ZNet.GetOtherPublicPlayers or Vector3.MoveTowards do not have the expected shape. Either the game " +
                    "updated or another mod rewrote them. Other players' map icons slide to each new position at 200 m/s, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Minimap), PinsMethod)]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> FollowPlayers(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            int lists = 0;
            int moves = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (Calls(codes[i], GetOtherPublicPlayers)) { lists++; }
                if (Calls(codes[i], MoveTowards)) { moves++; }
            }
            if (lists != 1 || moves != 1) {
                PatchGuard.Disable(Mechanism.MapPins,
                    $"Minimap.{PinsMethod} calls ZNet.GetOtherPublicPlayers {lists} time(s) and Vector3.MoveTowards {moves} time(s), expected one " +
                    "each. Either the game updated or another mod rewrote this method first. Other players' map icons slide to each new position " +
                    $"at 200 m/s, as vanilla. Found: {IlMatch.DescribeNeighbours(codes, nameof(ZNet.GetOtherPublicPlayers), nameof(Vector3.MoveTowards))}");
                return codes;
            }

            // (ZNet, List) for (ZNet, List), and (Vector3, Vector3, float) for the same three.
            // ReplaceInPlace carries the labels over: the move sits inside the loop over the pins.
            MethodInfo list = AccessTools.Method(typeof(MapPins), nameof(MapPins.GetOtherPublicPlayers));
            MethodInfo step = AccessTools.Method(typeof(MapPins), nameof(MapPins.Step));
            for (int i = 0; i < codes.Count; i++) {
                if (Calls(codes[i], GetOtherPublicPlayers)) {
                    IlMatch.ReplaceInPlace(codes, i, new CodeInstruction(OpCodes.Call, list));
                } else if (Calls(codes[i], MoveTowards)) {
                    IlMatch.ReplaceInPlace(codes, i, new CodeInstruction(OpCodes.Call, step));
                }
            }

            IlMatch.LogOnce($"Map icons follow players (Minimap.{PinsMethod}).");
            return codes;
        }

        private static bool Calls(CodeInstruction code, MethodInfo target) {
            return (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call)
                   && code.operand is MethodInfo method
                   && method == target;
        }
    }
}
