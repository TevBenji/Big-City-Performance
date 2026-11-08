// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Game.Simulation;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// The game runs at most two simulation steps per frame for each step of speed (two at 1x, four at 2x), so below 30 frames a
    /// second the clock can't keep up however little each step costs: at 22 fps a city ran at 0.64x on normal speed (test run,
    /// 7 November 2026). Below 30 fps this lets it run as many as keep the clock on time, up to twice the game's own number (so
    /// 1x holds down to 15 fps). Big cities spend most of a frame waiting for the graphics card, so the extra steps mostly use
    /// time the processor would have spent idle.
    /// </summary>
    [HarmonyPatch(typeof(SimulationSystem), "OnUpdate")]
    internal static class StepBudget
    {
        internal static bool Applied;
        internal static bool DevOff = false;
        private static float smoothedFps = 60f;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var getSpeed = AccessTools.PropertyGetter(typeof(SimulationSystem), nameof(SimulationSystem.selectedSpeed));
            var round = AccessTools.Method(typeof(Mathf), nameof(Mathf.RoundToInt));
            // The cap is RoundToInt(selectedSpeed * slowdown * 2): the 2 becomes StepsPerSpeed(). If a game update changes this
            // code, nothing is changed.
            for (int at = 0; at + 5 < list.Count; at++)
            {
                if (!list[at].Calls(getSpeed) || list[at + 2].opcode != OpCodes.Mul || list[at + 4].opcode != OpCodes.Mul || !list[at + 5].Calls(round)) continue;
                if (list[at + 3].opcode != OpCodes.Ldc_R4 || !(list[at + 3].operand is float two) || two != 2f) continue;
                list[at + 3] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StepBudget), nameof(StepsPerSpeed))).MoveLabelsFrom(list[at + 3]);
                Applied = true;
                break;
            }
            if (!Applied) Mod.Logger.Warn("The game's step limit has changed; the clock keeps the game's own limit.");
            return list;
        }

        private static float StepsPerSpeed()
        {
            float seconds = Time.unscaledDeltaTime;
            if (seconds > 0f) smoothedFps = math.lerp(smoothedFps, 1f / seconds, 0.1f);
            if (DevOff || Mod.Settings == null || !Mod.Settings.KeepClockRunning) return 2f;
            return 2f * math.clamp(30f / smoothedFps, 1f, 2f);
        }
    }
}
