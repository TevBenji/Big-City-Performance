// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BigCityPerformance.Core;
using BigCityPerformance.People;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Net;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Scripting;

namespace BigCityPerformance.Diagnostics
{
    /// <summary>
    /// Times every system's update with a prefix and postfix on SystemBase.Update, patched only while it measures. With each
    /// system's jobs finished inside its update it shows each system's whole work; without, only the main thread's own time
    /// (with any wait for jobs a system forces there), which is what bounds a computer with many cores.
    /// </summary>
    internal static class SystemTimes
    {
        private const string HarmonyId = "TevBenji.BigCityPerformance.SystemTimes";

        private struct Open { public Type System; public long Start, Children; }

        private static readonly Stack<Open> Running = new Stack<Open>();
        private static readonly Dictionary<Type, (long Ticks, int Calls)> Totals = new Dictionary<Type, (long, int)>();
        private static bool enabled, completeJobs, patched;

        internal static void Start(bool finishJobs)
        {
            if (!patched)
            {
                var update = AccessTools.Method(typeof(SystemBase), nameof(SystemBase.Update));
                new Harmony(HarmonyId).Patch(update, prefix: new HarmonyMethod(typeof(SystemTimes), nameof(Prefix)), postfix: new HarmonyMethod(typeof(SystemTimes), nameof(Postfix)));
                patched = true;
            }
            Totals.Clear();
            Running.Clear();
            completeJobs = finishJobs;
            enabled = true;
        }

        /// <summary>Stops timing and takes the patch off, so nothing is left on every system's update.</summary>
        internal static void Stop()
        {
            enabled = false;
            if (!patched) return;
            new Harmony(HarmonyId).UnpatchAll(HarmonyId);
            patched = false;
        }

        private static void Prefix(SystemBase __instance, ref bool __state)
        {
            __state = enabled;
            if (!__state) return;
            // Jobs left running by earlier systems are finished first, so they aren't charged to this one.
            long before = Stopwatch.GetTimestamp();
            if (completeJobs) __instance.EntityManager.CompleteAllTrackedJobs();
            long start = Stopwatch.GetTimestamp();
            if (Running.Count > 0) { var parent = Running.Pop(); parent.Children += start - before; Running.Push(parent); }
            Running.Push(new Open { System = __instance.GetType(), Start = start });
        }

        private static void Postfix(SystemBase __instance, bool __state)
        {
            if (!__state || Running.Count == 0) return;
            if (completeJobs) __instance.EntityManager.CompleteAllTrackedJobs();
            long end = Stopwatch.GetTimestamp();
            var open = Running.Pop();
            long elapsed = end - open.Start;
            Totals.TryGetValue(open.System, out var total);
            Totals[open.System] = (total.Ticks + elapsed - open.Children, total.Calls + 1);
            if (Running.Count > 0) { var parent = Running.Pop(); parent.Children += elapsed; Running.Push(parent); }
        }

        /// <summary>
        /// Milliseconds per simulation step in all systems, then the <paramref name="top"/> costliest. Another mod's system is
        /// named with its assembly, so a report shows which mod it comes from.
        /// </summary>
        internal static string Summary(long steps, int top)
        {
            double perStep = 1000.0 / Stopwatch.Frequency / Math.Max(1, steps);
            string Name(Type type)
            {
                string assembly = type.Assembly.GetName().Name;
                bool game = assembly == "Game" || assembly.StartsWith("Unity", StringComparison.Ordinal) || assembly.StartsWith("Colossal", StringComparison.Ordinal);
                return game ? type.Name : $"{type.Name} [{assembly}]";
            }
            return $"{Totals.Values.Sum(row => row.Ticks) * perStep:F1} ms in all; "
                + string.Join(", ", Totals.OrderByDescending(pair => pair.Value.Ticks).Take(top).Select(pair => $"{Name(pair.Key)} {pair.Value.Ticks * perStep:F2}"));
        }
    }

    /// <summary>
    /// Options, Write a performance report: 90 seconds of main-thread times, then 90 seconds of whole-work times (finishing
    /// each system's jobs inside it slows the game, so speed and frame rate come from the first half), then a few lines in
    /// BigCityPerformance.log a player can paste: the city, its speed, whether the processor or the graphics card sets the
    /// pace, and where the time goes. For finding what bounds a player's city on their own computer, with their own mods.
    /// </summary>
    public sealed partial class PerformanceReport : GameSystemBase
    {
        private const float HalfSeconds = 90f;
        private const int Top = 10;

        /// <summary>Set by the Options button; picked up on the next frame.</summary>
        internal static bool Requested;

        /// <summary>The last report written, for the test runner.</summary>
        internal static string Last = "";

        /// <summary>
        /// True while a report measures. The automatic limit takes no decisions then: the second half slows the game on
        /// purpose, and a cut made for that would change the work being measured and outlast the report (code review, 12
        /// November 2026).
        /// </summary>
        internal static bool Measuring => phase != Phase.Idle;

        private enum Phase { Idle, MainThread, AllWork }

        private static Phase phase;
        private float phaseStart;
        private uint stepStart;
        private int frameStart;
        private string mainThread = "";

        // The first half's speed, frames and timings.
        private float speed, fps, stepsPerFrame;
        private int frames, waitingFrames, timed;
        private double mainMs, gpuMs;
        private readonly FrameTiming[] timing = new FrameTiming[1];

        private SimulationSystem simulation;
        private PathfindResultSystem results;
        private PathfindSetupSystem setup;
        private EntityQuery buildings, companies, roads;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            results = World.GetOrCreateSystemManaged<PathfindResultSystem>();
            setup = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            buildings = GetEntityQuery(ComponentType.ReadOnly<Building>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            companies = GetEntityQuery(ComponentType.ReadOnly<CompanyData>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            roads = GetEntityQuery(ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (phase == Phase.Idle)
            {
                if (!Requested) return;
                Requested = false;
                if (DormancySystem.Instance == null || !DormancySystem.Instance.Ready)
                {
                    Mod.Logger.Info("Performance report: open a city first, then press the button again.");
                    return;
                }
                frames = waitingFrames = timed = 0;
                mainMs = gpuMs = 0;
                Begin(Phase.MainThread);
                Mod.Logger.Info($"Performance report: measuring for {2 * HalfSeconds / 60f:F0} minutes; the last {HalfSeconds:F0} seconds run slower, while it times all of each system's work. Keep the city running; the report follows here.");
                return;
            }
            if (phase == Phase.MainThread) Sample();
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - phaseStart < HalfSeconds) return;
            long steps = simulation.frameIndex - stepStart;
            int framesHere = UnityEngine.Time.frameCount - frameStart;
            if (phase == Phase.MainThread)
            {
                mainThread = SystemTimes.Summary(steps, Top);
                speed = steps / 60f / (now - phaseStart);
                fps = framesHere / (now - phaseStart);
                stepsPerFrame = steps / (float)Math.Max(1, framesHere);
                Begin(Phase.AllWork);
                return;
            }
            string allWork = SystemTimes.Summary(steps, Top);
            SystemTimes.Stop();
            phase = Phase.Idle;
            Write(allWork);
        }

        /// <summary>
        /// A report under way when the city closes is dropped, and the timing patch comes off: finishing every system's jobs
        /// inside it would slow the next city's loading (code review, 12 November 2026).
        /// </summary>
        private void Cancel()
        {
            SystemTimes.Stop();
            if (phase == Phase.Idle) return;
            phase = Phase.Idle;
            Mod.Logger.Info("Performance report: stopped, the city closed before it finished.");
        }

        [Preserve]
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            Cancel();
        }

        [Preserve]
        protected override void OnDestroy()
        {
            Cancel();
            base.OnDestroy();
        }

        private void Begin(Phase next)
        {
            phase = next;
            phaseStart = UnityEngine.Time.realtimeSinceStartup;
            stepStart = simulation.frameIndex;
            frameStart = UnityEngine.Time.frameCount;
            SystemTimes.Start(finishJobs: next == Phase.AllWork);
        }

        private void Sample()
        {
            frames++;
            if (results.pendingRequestCount > 0 || setup.pendingRequestCount > 0)
            {
                uint deadline = results.pendingSimulationFrame < setup.pendingSimulationFrame ? results.pendingSimulationFrame : setup.pendingSimulationFrame;
                if ((long)deadline - simulation.frameIndex < DeadlinePolicy.ThrottleFrames) waitingFrames++;
            }
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].gpuFrameTime > 0)
            {
                mainMs += timing[0].cpuMainThreadFrameTime;
                gpuMs += timing[0].gpuFrameTime;
                timed++;
            }
        }

        private void Write(string allWork)
        {
            var dormancy = DormancySystem.Instance;
            var settings = Mod.Settings;
            double kilometres = 0;
            using (var curves = roads.ToComponentDataArray<Curve>(Allocator.Temp))
                foreach (var curve in curves) kilometres += curve.m_Length / 1000.0;
            string pace = timed == 0 ? "no frame timings from the graphics driver"
                : $"main thread {mainMs / timed:F0} ms, graphics card {gpuMs / timed:F0} ms per frame: the {(mainMs >= gpuMs * 0.9 ? "processor" : "graphics card")} sets the pace";
            string limit = settings == null || !settings.LimitPeople ? "off" : settings.AutoLimit ? $"automatic, now {ClockWatch.Auto.Limit:N0}" : $"{settings.PeopleInDetail * 1000:N0}";
            var lines = new[]
            {
                $"Performance report (game {Game.Version.current.shortVersion}):",
                $"City: {dormancy?.Population() ?? 0:N0} people, {dormancy?.Households ?? 0:N0} households ({dormancy?.SleepingHouseholds ?? 0:N0} asleep), "
                    + $"{buildings.CalculateEntityCount():N0} buildings, {companies.CalculateEntityCount():N0} companies, {kilometres:N0} km of road.",
                speed <= 0f ? "Speed: the game was paused while it measured; the times below are for drawing only."
                    : $"Speed (first {HalfSeconds:F0} seconds, before the slower half): {speed:F2}x of normal on speed {simulation.selectedSpeed}, {fps:F0} fps, {stepsPerFrame:F1} simulation steps per frame, "
                        + $"waiting on routes {waitingFrames / (float)Math.Max(1, frames):P0} of frames, {results.pendingRequestCount + setup.pendingRequestCount} route requests waiting.",
                $"Pace: {pace}; {Environment.ProcessorCount} logical processors, {JobsUtility.JobWorkerCount} job workers.",
                $"Settings: keep the clock running {(settings?.KeepClockRunning == true ? "on" : "off")}, extra simulation steps {(settings?.ExtraSteps == true ? "on" : "off")}, limit {limit}.",
                $"Main thread per simulation step: {mainThread}",
                $"All work per simulation step: {allWork}",
            };
            Last = string.Join(Environment.NewLine, lines);
            Mod.Logger.Info(Last);
        }
    }
}
