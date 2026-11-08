// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game.Creatures;
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

        private readonly Dictionary<Entity, Queue<Entity>> asleep = new Dictionary<Entity, Queue<Entity>>();
        private readonly Dictionary<Entity, int> awake = new Dictionary<Entity, int>();

        internal int Asleep { get; private set; }

        /// <param name="waiting">People out of vehicles, with the lane they stand on.</param>
        internal void Update(EntityManager entities, EntityQuery waiting, EntityCommandBuffer commands)
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
                    if (count <= Cap) continue;
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
}
