// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using Game;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using HarmonyLib;
using Unity.Entities;
using UnityEngine.Scripting;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// Service coverage (how far each school, clinic, police or fire station reaches) is recomputed with a route search from
    /// every service building, one service after another, all eight services every 256 frames: about four game seconds. In a
    /// million-person city that was a quarter to a third of all the route-finder's work (test runs, 7 November 2026), for maps
    /// that only change when roads or service buildings do. With the clock option on, each service starts one pass in
    /// <see cref="Every"/> for a while after a road or a service building changed (<see cref="CoverageWatch"/>): a new
    /// school's coverage shows within about a minute of play at normal speed. With nothing changed it starts one pass in
    /// <see cref="QuietEvery"/>, which still picks up a service's budget or staffing. Each service keeps its own count, so
    /// none is skipped more than another.
    /// </summary>
    [HarmonyPatch(typeof(ServiceCoverageSystem), "OnUpdate")]
    internal static class CoveragePace
    {
        internal static int Every = 16, QuietEvery = 64;

        // Two rounds at the quicker pace after a change: every service's pass is 256 frames apart.
        private const uint ChangedFrames = 2 * 16 * 256;

        private static readonly int[] Passes = new int[256];
        private static uint lastChange;
        private static bool changed;

        /// <summary>Passes started and passes skipped, for the test runner.</summary>
        internal static int Started, Skipped, Changes;

        /// <summary>A road or a service building was built, changed or removed.</summary>
        internal static void Changed()
        {
            changed = true;
            Changes++;
        }

        private static bool Prefix(ServiceCoverageSystem __instance)
        {
            if (Mod.Settings == null || !Mod.Settings.KeepClockRunning) return true;
            uint frame = __instance.m_SimulationSystem.frameIndex;
            // A change seen while paused counts from the frame the clock next moves; a loaded city starts as just changed.
            if (changed || frame < lastChange) { changed = false; lastChange = frame; }
            var next = ServiceCoverageSystem.GetFrameService(frame + 1);
            // Only the frame that starts the next service's pass is ever skipped; every other update runs as the game's own.
            if (ServiceCoverageSystem.GetFrameService(frame) == next) return true;
            int every = frame - lastChange < ChangedFrames ? Every : QuietEvery;
            bool runs = Passes[(int)next]++ % System.Math.Max(1, every) == 0;
            if (runs) Started++; else Skipped++;
            return runs;
        }
    }

    /// <summary>
    /// Tells <see cref="CoveragePace"/> when a road or a service building was built, changed or removed. The game marks those
    /// for one frame; this runs in the frames that have any, paused or not.
    /// </summary>
    public sealed partial class CoverageWatch : GameSystemBase
    {
        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            var marks = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() };
            RequireAnyForUpdate(
                GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.ServiceCoverage>() }, Any = marks, None = new[] { ComponentType.ReadOnly<Temp>() } }),
                GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Net.CoverageServiceType>() }, Any = marks, None = new[] { ComponentType.ReadOnly<Temp>() } }));
        }

        [Preserve]
        protected override void OnUpdate() => CoveragePace.Changed();
    }
}
