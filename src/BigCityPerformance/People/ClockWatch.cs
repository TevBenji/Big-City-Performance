// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using BigCityPerformance.Core;
using BigCityPerformance.Settings;
using Game;
using Game.Pathfind;
using Game.Simulation;
using UnityEngine;
using UnityEngine.Scripting;

namespace BigCityPerformance.People
{
    /// <summary>
    /// Watches how the clock keeps up. About once a second it refreshes the Options page's "Right now" rows
    /// (<see cref="LiveStatus"/>); every <see cref="AutoLimitPolicy.CheckSeconds"/> of play it hands the automatic limit what it
    /// needs: the game speed reached, how often the clock waited on route deadlines, and whether the processor or the graphics
    /// card set the frame's pace.
    /// </summary>
    public sealed partial class ClockWatch : GameSystemBase
    {
        internal static readonly AutoLimitPolicy Auto = new AutoLimitPolicy();

        // The game slows its clock this many frames before a route deadline.
        private const long HoldFrames = DeadlinePolicy.ThrottleFrames;

        private readonly FrameTiming[] timing = new FrameTiming[1];
        private SimulationSystem simulation;
        private PathfindResultSystem results;
        private PathfindSetupSystem setup;

        private float statusStart = -1f, autoStart = -1f;
        private uint statusFrame, autoFrame;
        private int autoFrames, autoStalled, autoTimed;

        // Checks left before the automatic limit may change, after a city loads.
        private const int SettlingChecks = 3;
        private int settling = SettlingChecks;
        private double autoMain, autoGpu;

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
            var dormancy = DormancySystem.Instance;
            if (dormancy == null || !dormancy.Ready)
            {
                LiveStatus.Clear();
                return;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;
            uint frame = simulation.frameIndex;
            bool running = simulation.selectedSpeed > 0f;
            // A jump in the clock (a city loaded) or a pause starts both windows over: paused time isn't slow time.
            if (statusStart < 0f || frame < statusFrame) { statusStart = now; statusFrame = frame; }
            if (!running || autoStart < 0f || frame < autoFrame) StartAutoWindow(now, frame);

            if (now - statusStart >= 1f)
            {
                float speed = (frame - statusFrame) / 60f / (now - statusStart);
                int population = dormancy.Population();
                LiveStatus.Set(dormancy.PeopleInDetail(population), population, speed, dormancy.SleepingAtStops);
                statusStart = now;
                statusFrame = frame;
            }
            if (!running) return;

            autoFrames++;
            if (WaitingOnRoutes(frame)) autoStalled++;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].gpuFrameTime > 0)
            {
                autoMain += timing[0].cpuMainThreadFrameTime;
                autoGpu += timing[0].gpuFrameTime;
                autoTimed++;
            }
            if (now - autoStart < AutoLimitPolicy.CheckSeconds) return;
            // The first minute after loading is the load's own rush (everyone loads awake, and their routes come at once), not
            // a sign of too many people: it cut the limit twice before the city had settled (test run, 8 November 2026).
            if (settling > 0) settling--;
            else if (Mod.Settings != null && Mod.Settings.LimitPeople && Mod.Settings.AutoLimit && dormancy.Population() > AutoLimitPolicy.Floor)
            {
                float achieved = (frame - autoFrame) / 60f / (now - autoStart);
                float stalled = autoStalled / (float)System.Math.Max(1, autoFrames);
                // Without timings (some graphics drivers give none) it can't tell, so it assumes fewer people would help.
                bool cpuBound = autoTimed == 0 || autoMain >= autoGpu * 0.9;
                int before = Auto.Limit;
                if (Auto.Update(achieved, simulation.selectedSpeed, stalled, cpuBound))
                    Mod.Logger.Info($"Automatic limit {before:N0} -> {Auto.Limit:N0}: game speed {achieved:F2}x on speed {simulation.selectedSpeed}, "
                        + $"waiting on routes {stalled:P0} of frames, {(cpuBound ? "processor" : "graphics card")} setting the pace.");
            }
            StartAutoWindow(now, frame);
        }

        private bool WaitingOnRoutes(uint frame)
        {
            if (results.pendingRequestCount == 0 && setup.pendingRequestCount == 0) return false;
            uint deadline = results.pendingSimulationFrame < setup.pendingSimulationFrame ? results.pendingSimulationFrame : setup.pendingSimulationFrame;
            return (long)deadline - frame < HoldFrames;
        }

        private void StartAutoWindow(float now, uint frame)
        {
            autoStart = now;
            autoFrame = frame;
            autoFrames = autoStalled = autoTimed = 0;
            autoMain = autoGpu = 0;
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // Each city starts from the same middle and finds its own level.
            Auto.Reset();
            settling = SettlingChecks;
            statusStart = autoStart = -1f;
        }
    }
}
