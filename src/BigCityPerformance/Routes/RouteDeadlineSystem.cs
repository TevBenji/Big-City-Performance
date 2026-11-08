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

        // A status line in the log every five minutes of play, so a session can be looked back on.
        private const float StatusSeconds = 300f;
        private float statusStart = -1f;
        private uint statusFrame;
        private int statusFrames;

        private void Status()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            statusFrames++;
            if (statusStart < 0f || simulation.frameIndex < statusFrame) { statusStart = now; statusFrame = simulation.frameIndex; statusFrames = 0; return; }
            float seconds = now - statusStart;
            if (seconds < StatusSeconds) return;
            var dormancy = People.DormancySystem.Instance;
            Mod.Logger.Info($"Status: game speed {(simulation.frameIndex - statusFrame) / 60f / seconds:F2}x of normal (speed button {simulation.selectedSpeed}), {statusFrames / seconds:F0} fps; "
                + $"route requests waiting {results.pendingRequestCount + setup.pendingRequestCount}, deadline stretch {Stretch}, search throttle {Throttle.Level}; "
                + (dormancy == null ? "" : $"asleep {dormancy.SleepingHouseholds} households ({dormancy.SleepingCitizens} people), {dormancy.SleepingAtStops} at stops; ")
                + $"sleepers' shopping last game hour {People.ShopTwin.LastSales} sales ({People.ShopTwin.LastSpent} money).");
            statusStart = now;
            statusFrame = simulation.frameIndex;
            statusFrames = 0;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            FrameIndex = simulation.frameIndex;
            Status();
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
            statusStart = -1f;
        }
    }

    /// <summary>
    /// Gives each new queue of route requests <see cref="RouteDeadlineSystem.Stretch"/> times the frames the game asked for. One
    /// patch point for every system that asks for routes (all of them go through GetQueue).
    /// </summary>
    [HarmonyPatch(typeof(PathfindSetupSystem), nameof(PathfindSetupSystem.GetQueue))]
    internal static class GetQueuePatch
    {
        /// <summary>People's own trips, which come in waves (commutes at each hour); everyone else keeps a shorter limit.</summary>
        private static readonly System.Collections.Generic.HashSet<System.Type> PeopleTrips = new System.Collections.Generic.HashSet<System.Type>
        {
            typeof(TripNeededSystem), typeof(ResidentAISystem), typeof(PersonalCarAISystem), typeof(LeisureSystem), typeof(TouristFindTargetSystem),
            typeof(FindJobSystem), typeof(FindSchoolSystem), typeof(HouseholdFindPropertySystem), typeof(ResourceBuyerSystem),
        };

        private static void Prefix(object system, ref int maxDelayFrames)
        {
            int stretch = RouteDeadlineSystem.Stretch;
            if (stretch > DeadlinePolicy.OthersMost && (system == null || !PeopleTrips.Contains(system.GetType()))) stretch = DeadlinePolicy.OthersMost;
            if (stretch > 1) maxDelayFrames *= stretch;
        }
    }

    /// <summary>
    /// Lets the systems that start optional searches run only the updates the throttle allows:
    /// - CitizenFindJobSystem turns citizens into job seekers, each a wide route search (two thirds of the route-finder's work
    ///   in a million-person city);
    /// - TaxiDispatchSystem sends empty taxis looking for passengers across the map (about a fifth once job hunting is throttled).
    /// Single updates run, spread out (<see cref="ThrottlePolicy.Stride"/>), each serving one slice of citizens, and every slice
    /// gets its turn: nobody is skipped more than anyone else, everyone just looks less often while the route-finder is behind.
    /// </summary>
    internal static class SearchThrottle
    {
        internal static readonly System.Type[] Systems = { typeof(CitizenFindJobSystem), typeof(TaxiDispatchSystem) };

        private static readonly System.Collections.Generic.Dictionary<System.Type, uint> Intervals = new System.Collections.Generic.Dictionary<System.Type, uint>();

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
            if (!Intervals.TryGetValue(type, out uint frames))
                Intervals[type] = frames = (uint)System.Math.Max(1, __instance.GetUpdateInterval(SystemUpdatePhase.GameSimulation));
            return throttle.Runs(RouteDeadlineSystem.FrameIndex / frames);
        }
    }
}
