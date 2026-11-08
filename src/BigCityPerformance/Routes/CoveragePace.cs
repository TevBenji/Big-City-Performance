// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using Game.Simulation;
using HarmonyLib;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// Service coverage (how far each school, clinic, police or fire station reaches) is recomputed with a route search from
    /// every service building, one service after another, all eight services every 256 frames: about four game seconds. In a
    /// million-person city that was a quarter to a third of all the route-finder's work (test runs, 7 November 2026), for maps
    /// that only change when roads or service buildings do. With the clock option on, each service starts one pass in
    /// <see cref="Every"/>: every buildings' coverage still refreshes about once a minute of play at normal speed. Each service
    /// keeps its own count, so none is skipped more than another.
    /// </summary>
    [HarmonyPatch(typeof(ServiceCoverageSystem), "OnUpdate")]
    internal static class CoveragePace
    {
        internal const int Every = 16;

        private static readonly int[] Passes = new int[256];

        private static bool Prefix(ServiceCoverageSystem __instance)
        {
            if (Mod.Settings == null || !Mod.Settings.KeepClockRunning) return true;
            uint frame = __instance.m_SimulationSystem.frameIndex;
            var next = ServiceCoverageSystem.GetFrameService(frame + 1);
            // Only the frame that starts the next service's pass is ever skipped; every other update runs as the game's own.
            if (ServiceCoverageSystem.GetFrameService(frame) == next) return true;
            return Passes[(int)next]++ % Every == 0;
        }
    }
}
