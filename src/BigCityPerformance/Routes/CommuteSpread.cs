// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using BigCityPerformance.Core;
using Game;
using Game.Citizens;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// Spreads each hour's wave of commuters. The game sends a worker off at their start time, always a whole hour, minus the
    /// commute they had last time (WorkerSystem). Everyone starting at the same hour with a similar commute asks for a route at
    /// the same moment. In a million-person city each hour mark brought 2,000 to 3,000 route requests at once and the clock
    /// stalled (test runs, 7 November 2026). Each
    /// worker's remembered commute gets a fixed extra of up to <see cref="SpreadMinutes"/> game minutes, different for each
    /// worker, so they set off across that window instead: a little earlier, never later. The game rewrites the commute after
    /// every trip to work, so new values are spread again at the next check. A commute the game hasn't measured yet stays as it
    /// is: other systems read "unknown" specially (choosing a home, for one).
    /// </summary>
    public sealed partial class CommuteSpread : GameSystemBase
    {
        internal const float SpreadMinutes = 40f;

        /// <summary>Rendered frames between checks.</summary>
        private const int CheckFrames = 240;

        // Worker.m_LastCommuteTime counts units of 60 simulation frames; a game day is 262,144 frames.
        private const float UnitsPerMinute = 262144f / 1440f / 60f;

        private EntityQuery workers;
        private EndFrameBarrier endFrame;
        private int frames;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            endFrame = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            workers = GetEntityQuery(ComponentType.ReadOnly<Worker>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (Mod.Settings == null || !Mod.Settings.KeepClockRunning || ++frames < CheckFrames) return;
            frames = 0;
            EntityCommandBuffer commands = default;
            bool started = false;
            using (var entities = workers.ToEntityArray(Allocator.Temp))
            using (var data = workers.ToComponentDataArray<Worker>(Allocator.Temp))
                for (int index = 0; index < entities.Length; index++)
                {
                    var worker = data[index];
                    // Under one unit the game treats the commute as unknown.
                    if (worker.m_LastCommuteTime < 1f || CommuteMark.IsMarked(worker.m_LastCommuteTime)) continue;
                    worker.m_LastCommuteTime = CommuteMark.Mark(worker.m_LastCommuteTime + Extra(entities[index]));
                    if (!started) { commands = endFrame.CreateCommandBuffer(); started = true; }
                    commands.SetComponent(entities[index], worker);
                }
        }

        /// <summary>The same extra for a worker every time: a hash of the entity, spread evenly over the window.</summary>
        private static float Extra(Entity entity) => (math.hash(new int2(entity.Index, entity.Version)) % 1000u) / 1000f * SpreadMinutes * UnitsPerMinute;
    }
}
