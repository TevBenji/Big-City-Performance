// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using BigCityPerformance.Core;
using Game;
using Game.Pathfind;
using Game.Simulation;
using HarmonyLib;
using UnityEngine.Scripting;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// Once per rendered frame: watches how close the simulation clock is to the earliest open route deadline, and from that
    /// sets how much longer new route requests may take (<see cref="DeadlinePolicy"/>) and how often job hunting may start
    /// (<see cref="ThrottlePolicy"/>). The patches below read both.
    /// </summary>
    public sealed partial class RouteDeadlineSystem : GameSystemBase
    {
        /// <summary>Read by the GetQueue patch on the main thread; 1 means the game's own deadlines.</summary>
        internal static int Stretch = 1;

        internal static readonly ThrottlePolicy Throttle = new ThrottlePolicy();
        internal static uint FrameIndex;

#if BCP_DEV
        // The test runner turns each part off on its own, to measure what each one does.
        internal static bool DevNoStretch, DevNoThrottle;
#endif

        private readonly DeadlinePolicy policy = new DeadlinePolicy();
        private SimulationSystem simulation;
        private PathfindResultSystem results;
        private PathfindSetupSystem setup;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            results = World.GetOrCreateSystemManaged<PathfindResultSystem>();
            setup = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            FrameIndex = simulation.frameIndex;
            if (Mod.Settings == null || !Mod.Settings.KeepClockRunning)
            {
                Reset();
                return;
            }
            // Paused: nothing is waiting on the clock, so leave both as they are.
            if (simulation.selectedSpeed <= 0f) return;
            long? margin = null;
            if (results.pendingRequestCount > 0 || setup.pendingRequestCount > 0)
            {
                uint deadline = results.pendingSimulationFrame < setup.pendingSimulationFrame ? results.pendingSimulationFrame : setup.pendingSimulationFrame;
                margin = (long)deadline - simulation.frameIndex;
            }
            policy.Update(margin);
            Throttle.Update(margin);
            Stretch = policy.Stretch;
#if BCP_DEV
            if (DevNoStretch) Stretch = 1;
            if (DevNoThrottle) Throttle.Reset();
#endif
        }

        private void Reset()
        {
            policy.Reset();
            Throttle.Reset();
            Stretch = 1;
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            Reset();
        }
    }

    /// <summary>
    /// Gives each new queue of route requests <see cref="RouteDeadlineSystem.Stretch"/> times the frames the game asked for. One
    /// patch point for every system that asks for routes (all of them go through GetQueue).
    /// </summary>
    [HarmonyPatch(typeof(PathfindSetupSystem), nameof(PathfindSetupSystem.GetQueue))]
    internal static class GetQueuePatch
    {
        private static void Prefix(ref int maxDelayFrames)
        {
            int stretch = RouteDeadlineSystem.Stretch;
            if (stretch > 1) maxDelayFrames *= stretch;
        }
    }

    /// <summary>
    /// Lets the systems that start optional searches run only the rounds the throttle allows:
    /// - CitizenFindJobSystem turns citizens into job seekers, each a wide route search (two thirds of the route-finder's work
    ///   in a million-person city);
    /// - TaxiDispatchSystem sends empty taxis looking for passengers across the map (about a fifth once job hunting is throttled).
    /// A round is a system's full pass (16 updates, one slice each), so nobody is skipped more than anyone else: everyone just
    /// looks less often while the route-finder is behind.
    /// </summary>
    internal static class SearchThrottle
    {
        internal static readonly System.Type[] Systems = { typeof(CitizenFindJobSystem), typeof(TaxiDispatchSystem) };

        private static readonly System.Collections.Generic.Dictionary<System.Type, uint> RoundFrames = new System.Collections.Generic.Dictionary<System.Type, uint>();

        internal static void Patch(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(SearchThrottle), nameof(Prefix));
            foreach (var type in Systems) harmony.Patch(AccessTools.DeclaredMethod(type, "OnUpdate"), prefix: prefix);
        }

        private static bool Prefix(GameSystemBase __instance)
        {
            var throttle = RouteDeadlineSystem.Throttle;
            if (throttle.Level == 0) return true;
            var type = __instance.GetType();
            if (!RoundFrames.TryGetValue(type, out uint frames))
                RoundFrames[type] = frames = (uint)System.Math.Max(1, __instance.GetUpdateInterval(SystemUpdatePhase.GameSimulation)) * 16u;
            return throttle.Runs(RouteDeadlineSystem.FrameIndex / frames);
        }
    }
}
