// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using System.Linq;
using Game.Pathfind;
using Game.Simulation;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// Remembers routes the route-finder just found (and requests it just failed) and answers the same request again from
    /// memory, so the route-finder never runs it. In a million-person city, 20 to 37% of the routes found repeated one found in
    /// the last game hour (test runs, 7 November 2026).
    ///
    /// How a remembered answer goes back: the game hands a waiting request to the route-finder's workers in queue order, once
    /// its setup jobs are done, marking it Scheduled; the route-finder sets it Completed; and the result system delivers every
    /// item that is Scheduled and Completed, whoever did the work. So an answered request gets its path and result filled in,
    /// is marked Completed and Scheduled, and moves to the front of the queue past the scheduler's position: the scheduler
    /// never sees it, and the result system delivers it like any other.
    ///
    /// A request is the same when its start and end targets and its travel settings are byte for byte the same, apart from the
    /// citizen's own cost weights and search limit (a near-identical citizen takes the same way). Everything is forgotten when
    /// roads change, routes after a game hour and failures after a quarter of that.
    /// </summary>
    internal static class RouteCache
    {
        internal enum Mode { Off, Count, On }

        internal static Mode Current = Mode.Off;

        // 262,144 simulation frames a day.
        private const uint RouteFrames = 262144u / 24u, FailureFrames = 262144u / 96u;
        private const int MostEntries = 60000, MostTargets = 16, MostChecksPerFrame = 4000;

        private struct Entry
        {
            public uint Frame;
            public bool Failed;
            public PathfindPath[] Path;
            public PathfindResult[] Result;
        }

        private static readonly Dictionary<ulong, Entry> Entries = new Dictionary<ulong, Entry>();
        internal static long Checked, Hits, FailureHits, Answered, Recorded, Cleared, Creates, Updates, Deletes;

        internal static void Patch(Harmony harmony)
        {
            // PathfindQueueSystem.OnUpdate calls CompleteSetup (every waiting request's targets filled in) and then hands them all to
            // the route-finder's workers: right after CompleteSetup is the one moment to answer them.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PathfindSetupSystem), nameof(PathfindSetupSystem.CompleteSetup)), postfix: new HarmonyMethod(typeof(RouteCache), nameof(SetupPostfix)));
            var process = typeof(PathfindResultSystem).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .First(method => method.Name == "ProcessResults" && method.GetParameters().Length > 0
                    && method.GetParameters()[0].ParameterType == typeof(PathfindQueueSystem.ActionList<PathfindAction>));
            harmony.Patch(process, prefix: new HarmonyMethod(typeof(RouteCache), nameof(ResultsPrefix)));
        }

        internal static void ResetCounts() => Checked = Hits = FailureHits = Answered = Recorded = Cleared = Creates = Updates = Deletes = 0;

        internal static void Clear() => Entries.Clear();

        private static PathfindQueueSystem queueSystem;

#if BCP_DEV
        /// <summary>The test runner asks for a sample of waiting requests; the next CompleteSetup writes them here.</summary>
        internal static int DumpWanted;
        internal static readonly List<string> DumpLines = new List<string>();
        internal static System.Func<PathfindQueueSystem.ActionListItem<PathfindAction>, string> Describe;
#endif

        /// <summary>After the setup, before the scheduler: answer waiting requests that repeat a remembered one.</summary>
        private static void SetupPostfix(PathfindSetupSystem __instance)
        {
            if (queueSystem == null) queueSystem = __instance.World.GetExistingSystemManaged<PathfindQueueSystem>();
            if (queueSystem == null) return;
#if BCP_DEV
            if (DumpWanted > 0 && Describe != null)
            {
                var waiting = queueSystem.m_PathfindActions;
                for (int index = waiting.m_NextIndex; index < waiting.m_Items.Count && DumpWanted > 0; index++, DumpWanted--) DumpLines.Add(Describe(waiting.m_Items[index]));
            }
#endif
#if BCP_DEV
            CountRerouteReasons(queueSystem);
#endif
            QueuePrefix(queueSystem);
        }

#if BCP_DEV
        /// <summary>Why travellers ask for a new route: their route state (PathOwner flags) when the request is made (test runner only).</summary>
        internal static readonly Dictionary<string, long> RerouteReasons = new Dictionary<string, long>();

        private static void CountRerouteReasons(PathfindQueueSystem queue)
        {
            if (!entities.HasValue) entities = Unity.Entities.World.DefaultGameObjectInjectionWorld.EntityManager;
            var manager = entities.Value;
            var waiting = queue.m_PathfindActions;
            for (int index = waiting.m_NextIndex; index < waiting.m_Items.Count; index++)
            {
                var item = waiting.m_Items[index];
                string system = item.m_System?.GetType().Name;
                if (system != "ResidentAISystem" && system != "PersonalCarAISystem") continue;
                var owner = item.m_Owner;
                string reason = manager.Exists(owner) && manager.HasComponent<PathOwner>(owner) ? manager.GetComponentData<PathOwner>(owner).m_State.ToString() : "no route state";
                string key = system + ": " + reason;
                RerouteReasons.TryGetValue(key, out long count);
                RerouteReasons[key] = count + 1;
            }
        }
#endif

        private static void QueuePrefix(PathfindQueueSystem __instance)
        {
            if (Current == Mode.Off) return;
            // The route network changes in small ways almost every frame (parking spots, spawn points, buildings): pieces come
            // and go many times a second (test runs). A remembered route over a piece that's gone is what the game already
            // copes with when a player bulldozes a road under traffic: the traveller asks again. So nothing is cleared for those;
            // remembered answers simply expire (routes after a game hour, failures after a quarter of that).
            if (__instance.m_DeleteActions.m_Items.Count > 0) Deletes++;
            if (__instance.m_CreateActions.m_Items.Count > 0) Creates++;
            if (__instance.m_UpdateActions.m_Items.Count > 0) Updates++;
            var list = __instance.m_PathfindActions;
            // Urgent requests wait at the front; leave the order alone while there are any.
            if (list.m_PriorityCount > 0 || Entries.Count == 0) return;
            uint frame = RouteDeadlineSystem.FrameIndex;
            var items = list.m_Items;
            int answered = 0;
            int end = System.Math.Min(items.Count, list.m_NextIndex + MostChecksPerFrame);
            for (int index = list.m_NextIndex; index < end; index++)
            {
                var item = items[index];
                if ((item.m_Flags & (PathFlags.Scheduled | PathFlags.Debug)) != 0 || !item.m_Dependencies.IsCompleted) continue;
                ref var data = ref item.m_Action.data;
                if (data.m_State != PathfindActionState.Pending || !Cacheable(ref data)) continue;
                Checked++;
                ulong key = Key(ref data);
                if (!Entries.TryGetValue(key, out var entry)) continue;
                if (frame - entry.Frame >= (entry.Failed ? FailureFrames : RouteFrames)) { Entries.Remove(key); continue; }
                Hits++;
                if (entry.Failed) FailureHits++;
                if (Current != Mode.On) continue;

                item.m_Dependencies.Complete();
                data.m_Path.Clear();
                foreach (var step in entry.Path) data.m_Path.Add(step);
                data.m_Result.Clear();
                foreach (var result in entry.Result) data.m_Result.Add(result);
                data.m_State = PathfindActionState.Completed;
                item.m_Flags = (item.m_Flags & ~PathFlags.Pending) | PathFlags.Scheduled;
                // To the front of the waiting part, then past the scheduler's position.
                int front = list.m_NextIndex;
                items[index] = items[front];
                items[front] = item;
                list.m_NextIndex = front + 1;
                answered++;
                Answered++;
            }
            if (answered > 0) DropTickets(__instance, answered);
        }

        /// <summary>
        /// The queue keeps one ticket per waiting request (m_ActionTypes, in arrival order); the scheduler takes a ticket, then the
        /// next request of that type. A request answered here has left the line, so one route-request ticket goes too, or the
        /// scheduler would reach past the end of the list (it did, in the first test). They're all alike, so the newest go,
        /// leaving the front of the line as it was.
        /// </summary>
        private static void DropTickets(PathfindQueueSystem queue, int count)
        {
            var tickets = queue.m_ActionTypes.ToArray();
            var drop = new bool[tickets.Length];
            for (int index = tickets.Length - 1; index >= 0 && count > 0; index--)
                if (tickets[index] == PathfindQueueSystem.ActionType.Pathfind) { drop[index] = true; count--; }
            queue.m_ActionTypes.Clear();
            for (int index = 0; index < tickets.Length; index++) if (!drop[index]) queue.m_ActionTypes.Enqueue(tickets[index]);
            if (count > 0) Mod.Logger.Warn($"Big City Performance route cache: {count} answered requests had no ticket in the queue");
        }

#if BCP_DEV
        /// <summary>Per system: requests finished, how many failed, and the search effort spent on each kind (test runner only).</summary>
        internal static readonly Dictionary<string, (long Count, long Failed, double SuccessWork, double FailedWork)> Effort = new Dictionary<string, (long, long, double, double)>();

        // Would-be failure prediction by area: (start square, end square, travel modes, requester system) -> outcomes so far.
        internal static readonly Dictionary<ulong, (int Successes, int Failures)> AreaOutcomes = new Dictionary<ulong, (int, int)>();
        internal static long Predicted, PredictedRight, PredictedWrong, PredictableFailures, PredictedWork;
        private static EntityManager? entities;

        private static Unity.Mathematics.float3? Where(Entity entity)
        {
            var manager = entities.Value;
            if (entity == Entity.Null || !manager.Exists(entity)) return null;
            if (manager.HasComponent<Game.Net.Curve>(entity)) return manager.GetComponentData<Game.Net.Curve>(entity).m_Bezier.a;
            if (manager.HasComponent<Game.Objects.Transform>(entity)) return manager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            return null;
        }

        private static void Predict(PathfindQueueSystem.ActionListItem<PathfindAction> item, ref PathfindActionData data, bool failed, double work)
        {
            if (data.m_StartTargets.Length == 0 || data.m_EndTargets.Length == 0) return;
            var from = Where(data.m_StartTargets[0].m_Entity);
            var to = Where(data.m_EndTargets[0].m_Entity);
            if (!from.HasValue || !to.HasValue) return;
            const float Square = 250f;
            ulong key = 14695981039346656037UL;
            void Mix(ulong value) { key ^= value; key *= 1099511628211UL; }
            Mix((ulong)(long)Unity.Mathematics.math.floor(from.Value.x / Square)); Mix((ulong)(long)Unity.Mathematics.math.floor(from.Value.z / Square));
            Mix((ulong)(long)Unity.Mathematics.math.floor(to.Value.x / Square)); Mix((ulong)(long)Unity.Mathematics.math.floor(to.Value.z / Square));
            Mix((ulong)data.m_Parameters.m_Methods);
            Mix((ulong)(item.m_System?.GetType().GetHashCode() ?? 0));
            AreaOutcomes.TryGetValue(key, out var seen);
            // The rule under test: at least 3 tries between these areas, and none got through.
            if (seen.Failures >= 3 && seen.Successes == 0)
            {
                Predicted++;
                if (failed) { PredictedRight++; PredictedWork += (long)work; } else PredictedWrong++;
            }
            if (failed) PredictableFailures++;
            AreaOutcomes[key] = (seen.Successes + (failed ? 0 : 1), seen.Failures + (failed ? 1 : 0));
        }

        private static void CountEffort(PathfindQueueSystem.ActionList<PathfindAction> list)
        {
            if (!entities.HasValue) entities = Unity.Entities.World.DefaultGameObjectInjectionWorld.EntityManager;
            var items = list.m_Items;
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if ((item.m_Flags & PathFlags.Scheduled) == 0) continue;
                ref var data = ref item.m_Action.data;
                if (data.m_State != PathfindActionState.Completed || data.m_Result.Length == 0) continue;
                bool failed = data.m_Path.Length == 0 || data.m_Result[0].m_ErrorCode != 0;
                double work = data.m_Result[data.m_Result.Length - 1].m_GraphTraversal;
                string name = (item.m_System?.GetType().Name ?? "?") + " " + data.m_OriginType + "->" + data.m_DestinationType;
                // Only real routes (trips and re-routes) can fail this way; searches for a job, school or home return a place, not a path.
                if (data.m_OriginType == SetupTargetType.CurrentLocation && data.m_DestinationType == SetupTargetType.CurrentLocation) Predict(item, ref data, failed, work);
                Effort.TryGetValue(name, out var row);
                Effort[name] = (row.Count + 1, row.Failed + (failed ? 1 : 0), row.SuccessWork + (failed ? 0 : work), row.FailedWork + (failed ? work : 0));
            }
        }
#endif

        /// <summary>Before the game delivers finished routes: remember each one (the route-finder's, not ours).</summary>
        private static void ResultsPrefix(PathfindQueueSystem.ActionList<PathfindAction> list)
        {
#if BCP_DEV
            CountEffort(list);
#endif
            if (Current == Mode.Off) return;
            uint frame = RouteDeadlineSystem.FrameIndex;
            var items = list.m_Items;
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if ((item.m_Flags & PathFlags.Scheduled) == 0) continue;
                ref var data = ref item.m_Action.data;
                // The game reads the first result of every answer it delivers: never remember one without.
                if (data.m_State != PathfindActionState.Completed || !Cacheable(ref data) || data.m_Result.Length == 0) continue;
                var path = new PathfindPath[data.m_Path.Length];
                for (int step = 0; step < path.Length; step++) path[step] = data.m_Path[step];
                var results = new PathfindResult[data.m_Result.Length];
                for (int at = 0; at < results.Length; at++)
                {
                    results[at] = data.m_Result[at];
                    // An answer from memory cost the route-finder nothing: keep the game's own statistics honest.
                    results[at].m_GraphTraversal = 0;
                }
                bool failed = path.Length == 0 || results.Length == 0 || results[0].m_ErrorCode != 0;
                Entries[Key(ref data)] = new Entry { Frame = frame, Failed = failed, Path = path, Result = results };
                Recorded++;
            }
            if (Entries.Count > MostEntries) Prune(frame);
        }

        private static bool Cacheable(ref PathfindActionData data) =>
            data.m_StartTargets.Length > 0 && data.m_EndTargets.Length > 0 && data.m_StartTargets.Length + data.m_EndTargets.Length <= MostTargets;

        /// <summary>
        /// The request's identity, loosely: the same places at both ends (which building exits, which destination spots) by the
        /// same travel modes, with the same car if any. Not who's asking (the game puts the traveller in the start target) nor
        /// each traveller's own small costs to reach the exits (every request differs in those, so exact copies never repeat:
        /// 0 in 30,000, test run 7 November 2026).
        /// </summary>
        private static ulong Key(ref PathfindActionData data)
        {
            ulong hash = 14695981039346656037UL;
            void Mix(ulong value) { hash ^= value; hash *= 1099511628211UL; }
            void Targets(Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> targets)
            {
                Mix((ulong)targets.Length);
                for (int index = 0; index < targets.Length; index++)
                {
                    var target = targets[index];
                    Mix(((ulong)(uint)target.m_Entity.Index << 32) | (uint)target.m_Entity.Version);
                    Mix(Unity.Mathematics.math.asuint(target.m_Delta));
                    Mix((ulong)target.m_Flags);
                }
            }
            Targets(data.m_StartTargets);
            Targets(data.m_EndTargets);
            var parameters = data.m_Parameters;
            Mix((ulong)parameters.m_Methods);
            Mix((ulong)parameters.m_PathfindFlags);
            Mix((ulong)parameters.m_IgnoredRules);
            Mix((ulong)parameters.m_TaxiIgnoredRules);
            Mix(((ulong)(uint)parameters.m_ParkingTarget.Index << 32) | (uint)parameters.m_ParkingTarget.Version);
            Mix(Unity.Mathematics.math.asuint(parameters.m_ParkingDelta));
            Mix((ulong)parameters.m_MaxResultCount);
            Mix((ulong)data.m_OriginType);
            Mix((ulong)data.m_DestinationType);
            return hash;
        }

        private static void Prune(uint frame)
        {
            foreach (var key in Entries.Where(pair => frame - pair.Value.Frame >= (pair.Value.Failed ? FailureFrames : RouteFrames)).Select(pair => pair.Key).ToList())
                Entries.Remove(key);
            // Still full: forget the oldest half.
            if (Entries.Count > MostEntries)
                foreach (var key in Entries.OrderBy(pair => pair.Value.Frame).Take(Entries.Count / 2).Select(pair => pair.Key).ToList())
                    Entries.Remove(key);
        }

        internal static string Summary() =>
            $"{Current}: checked {Checked}, repeats found {Hits} ({(Checked > 0 ? Hits / (double)Checked : 0):P0}; {FailureHits} of them failures), answered from memory {Answered}, "
            + $"recorded {Recorded}, frames with roads added {Creates} / changed {Updates} / removed {Deletes}, cleared {Cleared}, remembered now {Entries.Count}";
    }
}
