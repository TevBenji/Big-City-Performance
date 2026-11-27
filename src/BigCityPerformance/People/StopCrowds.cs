// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game.Creatures;
using Game.Pathfind;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;

namespace BigCityPerformance.People
{
    /// <summary>
    /// Crowds at overloaded stops. In a million-person city 17,000 people stood waiting at 3,200 places, the biggest crowds 1,000
    /// to 1,900 strong. Each of them steps around the others, and each gives up after a while and asks for a new route that the
    /// next vehicle can't serve either. Only the first <see cref="Cap"/> at each place stay awake. The rest sleep where they stand
    /// (their timers stop, so they don't give up) and wake in turn as the ones in front board. With every waiter asleep, the clock
    /// ran 1.4 times faster and stopped stalling (test run, 7 November 2026).
    /// </summary>
    internal sealed class StopCrowds
    {
        internal static int Cap = 100;

        /// <summary>Dev only: people in a group sleep at stops too, as before 0.3.18 (for comparing).</summary>
        internal static bool DevGroupsSleep = false;

        private readonly Dictionary<Entity, Queue<Entity>> asleep = new Dictionary<Entity, Queue<Entity>>();
        private readonly Dictionary<Entity, int> awake = new Dictionary<Entity, int>();

        internal int Asleep { get; private set; }

        /// <param name="waiting">People out of vehicles, with the lane they stand on.</param>
        /// <param name="sleepNew">False while the limit is off and households wake a batch at a time: the queues still move as
        /// the crowds board, but nobody new sleeps.</param>
        internal void Update(EntityManager entities, EntityQuery waiting, EntityCommandBuffer commands, bool sleepNew = true)
        {
            awake.Clear();
            var sleep = new List<Entity>();
            using (var humans = waiting.ToEntityArray(Allocator.Temp))
            using (var residents = waiting.ToComponentDataArray<Resident>(Allocator.Temp))
            using (var bodies = waiting.ToComponentDataArray<Human>(Allocator.Temp))
            using (var lanes = waiting.ToComponentDataArray<HumanCurrentLane>(Allocator.Temp))
                for (int index = 0; index < humans.Length; index++)
                {
                    if ((residents[index].m_Flags & ResidentFlags.InVehicle) != 0) continue;
                    if ((residents[index].m_Flags & ResidentFlags.WaitingTransport) == 0 && (bodies[index].m_Flags & HumanFlags.Waiting) == 0) continue;
                    var place = lanes[index].m_Lane;
                    awake.TryGetValue(place, out int count);
                    awake[place] = ++count;
                    if (count <= Cap || !sleepNew) continue;
                    // Nobody in a group sleeps: a family out together, or someone with their pet. The others follow the group's
                    // leader, and a leader asleep here that the game then removed (its citizen died or left the city) was never
                    // cleaned up: the game's clean-up skips sleepers. A save then kept the others, still following a leader it
                    // left out, and loading it crashed the game (a player's crash log, 21 November 2026; his save, with 51 people
                    // following a leader it didn't have, crashed the same way in a test run, 27 November 2026).
                    if (!DevGroupsSleep && InGroup(entities, humans[index])) continue;
                    if (!asleep.TryGetValue(place, out var queue)) asleep[place] = queue = new Queue<Entity>();
                    queue.Enqueue(humans[index]);
                    sleep.Add(humans[index]);
                    Asleep++;
                }
            var wake = new List<Entity>();
            var emptied = new List<Entity>();
            foreach (var pair in asleep)
            {
                awake.TryGetValue(pair.Key, out int count);
                // Places that just put people to sleep are full; the others let sleepers in as their crowd shrinks.
                for (int room = Cap - count; room > 0 && pair.Value.Count > 0;)
                {
                    var human = pair.Value.Dequeue();
                    Asleep--;
                    if (!entities.Exists(human)) continue;
                    wake.Add(human);
                    room--;
                }
                if (pair.Value.Count == 0) emptied.Add(pair.Key);
            }
            foreach (var place in emptied) asleep.Remove(place);
            if (sleep.Count > 0)
                using (var array = new NativeArray<Entity>(sleep.ToArray(), Allocator.Temp)) commands.AddComponent<Disabled>(array);
            if (wake.Count > 0)
                using (var array = new NativeArray<Entity>(wake.ToArray(), Allocator.Temp)) commands.RemoveComponent<Disabled>(array);
        }

        private static bool InGroup(EntityManager entities, Entity human) =>
            entities.HasComponent<GroupMember>(human) || (entities.HasBuffer<GroupCreature>(human) && entities.GetBuffer<GroupCreature>(human, true).Length > 0);

        /// <summary>Everyone asleep at a stop, handed over to be woken; the queues are emptied.</summary>
        internal List<Entity> TakeAll()
        {
            var all = new List<Entity>(Asleep);
            foreach (var queue in asleep.Values) all.AddRange(queue);
            asleep.Clear();
            Asleep = 0;
            return all;
        }
    }

    /// <summary>
    /// Saves already broken that way load again. As a save loads, the game rebuilds every group from its members, adding each
    /// to its leader's list without checking the leader is there: a member whose leader the save left out made it write where
    /// no list was, and the game crashed (a player's crash log, 21 November 2026). Just before, each such member leaves its
    /// group the way the game's own regrouping takes someone out of one, and goes on alone. His save loads with it: 51 of them
    /// (test run, 27 November 2026).
    /// </summary>
    [HarmonyPatch(typeof(Game.Serialization.GroupCreatureSystem), "OnUpdate")]
    internal static class GroupLoadRepair
    {
        /// <summary>Dev only: saves load without the repair, as before 0.3.18 (for comparing); the members it would repair are counted.</summary>
        internal static bool DevOff = false;

        private static void Prefix(Game.Serialization.GroupCreatureSystem __instance)
        {
            // Never in the way of loading: a failure is logged, and the load goes on as the game's own.
            try
            {
                var entities = __instance.EntityManager;
                var query = __instance.m_Query;
                query.CompleteDependency();
                var strays = new List<Entity>();
                using (var members = query.ToEntityArray(Allocator.Temp))
                using (var groups = query.ToComponentDataArray<GroupMember>(Allocator.Temp))
                    for (int index = 0; index < members.Length; index++)
                    {
                        var leader = groups[index].m_Leader;
                        if (!entities.Exists(leader) || !entities.HasBuffer<GroupCreature>(leader)) strays.Add(members[index]);
                    }
                if (strays.Count == 0) return;
                if (DevOff)
                {
                    Mod.Logger.Info($"Dev: the repair is off, and {strays.Count} people and animals follow a group leader who isn't in the save.");
                    return;
                }
                foreach (var member in strays)
                {
                    // Its route was the leader's: it finds its own.
                    if (!entities.HasComponent<PathOwner>(member)) continue;
                    var path = entities.GetComponentData<PathOwner>(member);
                    path.m_ElementIndex = 0;
                    path.m_State |= PathFlags.Obsolete;
                    entities.SetComponentData(member, path);
                    if (entities.HasBuffer<PathElement>(member)) entities.GetBuffer<PathElement>(member).Clear();
                }
                using (var array = new NativeArray<Entity>(strays.ToArray(), Allocator.Temp)) entities.RemoveComponent<GroupMember>(array);
                Mod.Logger.Info($"Repaired the save: {strays.Count} people and animals followed a group leader who wasn't in it, and now go on alone.");
            }
            catch (System.Exception exception) { Mod.Logger.Warn("Couldn't check the save's groups: " + exception.Message); }
        }
    }
}
