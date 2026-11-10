// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using HarmonyLib;
using Unity.Entities;
using UnityEngine.Scripting;

namespace BigCityPerformance.People
{
    /// <summary>
    /// The city-wide counts each walk the whole city every 16 frames: every household and citizen (about 47 ms of work on a
    /// million people; test run, 7 November 2026), every company, every home (160 ms and 58 ms of work in a three-million
    /// city; test run, 9 November 2026). They feed demand and the statistics, which don't need a fresh count four times a
    /// second. While anyone sleeps each runs one time in <see cref="Every"/>: every 64 frames, still several times a game hour.
    /// </summary>
    internal static class CityPace
    {
        /// <summary>1 = the game's own pace.</summary>
        internal static int Every = 4;

        private static readonly System.Type[] Systems = { typeof(CountHouseholdDataSystem), typeof(CountCompanyDataSystem), typeof(CountResidentialPropertySystem) };

        private static readonly Dictionary<System.Type, int> Calls = new Dictionary<System.Type, int>();

        internal static void Patch(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(CityPace), nameof(Prefix));
            foreach (var type in Systems) harmony.Patch(AccessTools.DeclaredMethod(type, "OnUpdate"), prefix: prefix);
        }

        private static bool Prefix(GameSystemBase __instance)
        {
            if (Every <= 1 || (DormancySystem.Instance?.SleepingHouseholds ?? 0) == 0) return true;
            var type = __instance.GetType();
            Calls.TryGetValue(type, out int calls);
            Calls[type] = ++calls;
            return calls % Every == 0;
        }
    }

    /// <summary>
    /// Water and electricity are each worked out over the whole network in a cycle of 128 frames, about two seconds of play,
    /// whether or not a pipe or a cable changed. Each frame of a cycle is one more job in a chain that the end of the cycle
    /// waits for. In a three-million city on a laptop short of memory the water chain was most of every simulation step (162
    /// of 231 ms), and with one cycle in four running, the time lost to frames longer than a quarter of a second fell from
    /// 109 to 29 seconds in three minutes (test runs, 10 November 2026). While anyone
    /// sleeps, only one cycle in <see cref="Every"/> starts; after a pipe, a cable or a connected building changes
    /// (<see cref="FlowWatch"/>), one in <see cref="ChangedEvery"/> does until the change has been worked in. In between,
    /// every building keeps the supply the last cycle gave it. A cycle is skipped whole, at the frame it would have started,
    /// so the ones that run are the game's own from start to end.
    /// </summary>
    internal static class FlowPace
    {
        /// <summary>1 = the game's own pace.</summary>
        internal static int Every = 8, ChangedEvery = 2;

        private const uint CycleFrames = 128, WaterStart = 65, ElectricityStart = 1;

        // A cycle is owed since a change: each network works it in on its own.
        private static bool waterOwed = true, electricityOwed = true;

        /// <summary>Cycles started and skipped, for the test runner.</summary>
        internal static int Started, Skipped;

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(WaterPipeFlowSystem), "OnUpdate"), prefix: new HarmonyMethod(typeof(FlowPace), nameof(Water)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ElectricityFlowSystem), "OnUpdate"), prefix: new HarmonyMethod(typeof(FlowPace), nameof(Electricity)));
        }

        /// <summary>A pipe, a cable or a connected building was built, changed or removed, or a city loaded.</summary>
        internal static void Changed() => waterOwed = electricityOwed = true;

        // The first phase (0) waits for its start frame; a cycle under way is never touched.
        private static bool Water(WaterPipeFlowSystem __instance) =>
            (int)__instance.m_NextPhase != 0 || Starts(__instance.m_SimulationSystem.frameIndex, WaterStart, ref waterOwed);

        private static bool Electricity(ElectricityFlowSystem __instance) =>
            (int)__instance.m_NextPhase != 0 || Starts(__instance.m_SimulationSystem.frameIndex, ElectricityStart, ref electricityOwed);

        private static bool Starts(uint frame, uint start, ref bool owed)
        {
            if (frame % CycleFrames != start) return true;
            if (Every <= 1 || (DormancySystem.Instance?.SleepingHouseholds ?? 0) == 0) return true;
            uint cycle = frame / CycleFrames;
            bool runs = cycle % (uint)(owed ? System.Math.Max(1, ChangedEvery) : Every) == 0;
            if (runs) { owed = false; Started++; } else Skipped++;
            return runs;
        }
    }

    /// <summary>
    /// Tells <see cref="FlowPace"/> when the water or electricity network changed: the game marks a new, changed or removed
    /// piece of either for one frame, and this runs in the frames that have any, paused or not.
    /// </summary>
    public sealed partial class FlowWatch : GameSystemBase
    {
        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            var marks = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() };
            var none = new[] { ComponentType.ReadOnly<Temp>() };
            EntityQuery Marked<T>() => GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<T>() }, Any = marks, None = none });
            RequireAnyForUpdate(Marked<WaterPipeEdge>(), Marked<WaterPipeNode>(), Marked<ElectricityFlowEdge>(), Marked<ElectricityFlowNode>());
        }

        [Preserve]
        protected override void OnUpdate() => FlowPace.Changed();

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            FlowPace.Changed();
        }
    }
}
