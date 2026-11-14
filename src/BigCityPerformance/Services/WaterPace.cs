// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using Game.Simulation;
using HarmonyLib;

namespace BigCityPerformance.Services
{
    /// <summary>
    /// Water pipes without the wait (experimental). The game works out the flow through every pipe in a cycle of 128
    /// simulation steps: a chain of about 124 jobs on one worker thread, one link per step, the last at step 61, and at step
    /// 62 it applies the result to the pipes. At the apply the main thread waits for whatever of the chain is left. On a busy
    /// processor the chain falls behind and that wait is a hitch (a player's report, 13 November 2026: 7.9 ms of every step on
    /// average, 12.3 with extra steps).
    /// Here a chain that was still behind at its last link is applied at the next cycle's step 62 instead, by when it has
    /// finished: the main thread never waits, and the next cycle starts after it, so a busy processor also does less water work.
    /// Two of the game's water systems then run only in a cycle whose result was applied. The dispatch (step 62) gives each
    /// building its share of the flow by comparing what it wants with what reached it; the consumption update (step 64) sets
    /// what each building wants, which the next cycle's flow starts from. Held together, every building is always measured
    /// against the flow worked out for what it wanted, and in between it keeps the supply it had. 0.3.0 skipped cycles but not
    /// these two, and each building's wanted water moves a little every cycle (it is rounded at random), so buildings were
    /// measured against out-of-date flows and showed false no-water and sewage warnings.
    /// </summary>
    [HarmonyPatch]
    internal static class WaterPace
    {
        private const uint CycleSteps = 128, LastLinkStep = 61, ApplyStep = 62;

        private static WaterPipeFlowSystem flow;

        // The chain hadn't finished the links before its last one when the last one was added.
        private static bool behind;

        /// <summary>Cycles applied on time and put off, for the test runner.</summary>
        internal static int OnTime, PutOff;

        /// <summary>Test runner only: every apply but one in N is put off, as if the chain were always behind.</summary>
        internal static int DevPutOffEvery = 1;

        private static int devApplies;

        [HarmonyPatch(typeof(WaterPipeFlowSystem), "OnUpdate")]
        [HarmonyPrefix]
        private static bool Flow(WaterPipeFlowSystem __instance)
        {
            flow = __instance;
            if (!Mod.Settings.WaterNoWait) return true;
            uint step = __instance.m_SimulationSystem.frameIndex % CycleSteps;
            // Judged before the last link is added: the last link finishes whatever is left, so even a chain that kept up is
            // often still on it at step 62, when both steps fall in the same frame.
            if (__instance.m_NextPhase == WaterPipeFlowSystem.Phase.Flow && step == LastLinkStep) behind = !__instance.m_DataDependency.IsCompleted;
            if (__instance.m_NextPhase != WaterPipeFlowSystem.Phase.Apply || step != ApplyStep) return true;
            if ((behind && !__instance.m_DataDependency.IsCompleted) || (DevPutOffEvery > 1 && ++devApplies % DevPutOffEvery != 0))
            {
                PutOff++;
                return false;
            }
            OnTime++;
            return true;
        }

        // A put-off result is still waiting for its step 62. Never true at the game's own pace, where the result is applied at
        // step 62 before the dispatch runs, so the holds also cover a result put off just before the option was turned off.
        private static bool Pending() => flow != null && flow.m_NextPhase == WaterPipeFlowSystem.Phase.Apply;

        [HarmonyPatch(typeof(DispatchWaterSystem), "OnUpdate")]
        [HarmonyPrefix]
        private static bool Dispatch() => !Pending();

        [HarmonyPatch(typeof(AdjustWaterConsumptionSystem), "OnUpdate")]
        [HarmonyPrefix]
        private static bool Consumption() => !Pending();
    }
}
