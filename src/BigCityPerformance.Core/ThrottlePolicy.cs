// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
namespace BigCityPerformance.Core
{
    /// <summary>
    /// How often optional searches (job hunting, taxis looking for passengers) may start while the route-finder holds up the
    /// simulation clock.
    /// Level n lets one round in n + 1 run: a round is a system's full pass over all its citizens, so everyone is slowed alike.
    /// While the clock keeps waiting on route deadlines the level rises (at most once a second, up to <see cref="Most"/>); after
    /// a calm spell it falls back. In a million-person city job searches were two thirds of all route requests, and with none
    /// the clock ran 5.4 times faster (test runs, 7 November 2026).
    /// </summary>
    public sealed class ThrottlePolicy
    {
        public const int Most = 15;

        /// <summary>Rendered frames per check: about a second at 60 fps, a few at the frame rates of a city this size.</summary>
        public const int CheckFrames = 60;

        /// <summary>Above this share of held frames in a check the level rises; below <see cref="CalmShare"/> it falls.</summary>
        public const float BusyShare = 0.2f;

        public const float CalmShare = 0.02f;

        /// <summary>Calm checks in a row before the level falls, so it doesn't see-saw.</summary>
        public const int CalmChecks = 5;

        private int frames, held, calm;

        public int Level { get; private set; }

        /// <summary>
        /// Called once per rendered frame while the simulation runs. <paramref name="margin"/> is how many simulation frames
        /// remain before the earliest open route deadline, or null when no route request is waiting.
        /// </summary>
        public void Update(long? margin)
        {
            frames++;
            if (margin.HasValue && margin.Value < DeadlinePolicy.ThrottleFrames) held++;
            if (frames < CheckFrames) return;
            float share = held / (float)frames;
            frames = held = 0;
            if (share > BusyShare)
            {
                calm = 0;
                if (Level < Most) Level++;
                return;
            }
            if (share >= CalmShare) { calm = 0; return; }
            if (++calm < CalmChecks) return;
            calm = 0;
            if (Level > 0) Level--;
        }

        /// <summary>Whether the round numbered <paramref name="round"/> may run at the current level.</summary>
        public bool Runs(long round) => Level == 0 || round % (Level + 1) == 0;

        public void Reset()
        {
            Level = 0;
            frames = held = calm = 0;
        }
    }
}
