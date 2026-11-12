// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Game.Simulation;
using HarmonyLib;
using UnityEngine;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// The game runs at most two simulation steps per frame for each step of speed (two at 1x, four at 2x), so below 30 frames a
    /// second the clock can't keep up however little each step costs: at 22 fps a city ran at 0.64x on normal speed (test run,
    /// 7 November 2026). This doubles the limit. The game only runs the steps real time asks for (one per 60th of a second at
    /// 1x), so above 30 fps nothing changes; below it the clock keeps time down to 15 fps. Where the graphics card sets the
    /// pace, the extra steps mostly use time the processor would have spent idle. Where the processor does, every step makes
    /// the frame longer: a player's city went from 30 fps to 9-12 for a quarter more simulation (11 November 2026). So it's
    /// its own option, off by default.
    /// </summary>
    [HarmonyPatch(typeof(SimulationSystem), "OnUpdate")]
    internal static class StepBudget
    {
        internal static bool Applied;

        /// <summary>The last multiplier handed to the game, for the test runner.</summary>
        internal static float Last = 2f;
        internal static bool DevOff = false;

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

        // ponytail: a fixed double. A limit worked out from the frame rate read Time.unscaledDeltaTime, which didn't match the
        // frames the game ran, and stayed at 2 (test run, 7 November 2026).
        private static float StepsPerSpeed()
        {
            Last = DevOff || Mod.Settings == null || !Mod.Settings.ExtraSteps ? 2f : 4f;
            return Last;
        }
    }
}
