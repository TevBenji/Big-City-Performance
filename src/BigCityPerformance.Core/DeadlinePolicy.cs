// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
namespace BigCityPerformance.Core
{
    /// <summary>
    /// How much longer than the game asks for route requests may take, so the simulation clock doesn't stop for the
    /// route-finder. The game slows its clock within 48 frames of the earliest open route deadline and stops at it. While that
    /// keeps happening, deadlines double (at most once a second, up to <see cref="Most"/>); after a calm spell they halve back
    /// towards the game's own. Citizens then set off a little later, instead of the whole city waiting.
    /// </summary>
    public sealed class DeadlinePolicy
    {
        /// <summary>
        /// For people's own trips. At 16 the clock still stalled at each hour's wave of commuters (test runs, 7 November 2026);
        /// at 64 the wave is spread over a few game minutes instead.
        /// </summary>
        public const int Most = 64;

        /// <summary>For everything else (emergencies, transit, service vehicles): never more than this.</summary>
        public const int OthersMost = 16;

        /// <summary>The game slows its clock this many frames ahead of a route deadline (SimulationSystem).</summary>
        public const int ThrottleFrames = 48;

        /// <summary>
        /// Rendered frames between doublings: requests already queued keep their old deadlines, so a hold-up lasts a while
        /// after the stretch goes up.
        /// </summary>
        public const int RaiseFrames = 60;

        /// <summary>Rendered frames without a hold-up before the stretch halves (about 10 seconds at 60 fps).</summary>
        public const int CalmFrames = 600;

        private int sinceRaise = RaiseFrames, sinceHold;

        public int Stretch { get; private set; } = 1;

        /// <summary>
        /// Called once per rendered frame while the simulation runs. <paramref name="margin"/> is how many simulation frames
        /// remain before the earliest open route deadline, or null when no route request is waiting.
        /// </summary>
        public void Update(long? margin)
        {
            if (sinceRaise < RaiseFrames) sinceRaise++;
            if (margin.HasValue && margin.Value < ThrottleFrames)
            {
                sinceHold = 0;
                if (sinceRaise >= RaiseFrames && Stretch < Most)
                {
                    Stretch *= 2;
                    sinceRaise = 0;
                }
                return;
            }
            if (++sinceHold < CalmFrames) return;
            sinceHold = 0;
            if (Stretch > 1) Stretch /= 2;
        }

        /// <summary>Back to the game's own deadlines (the option turned off, or a new city).</summary>
        public void Reset()
        {
            Stretch = 1;
            sinceRaise = RaiseFrames;
            sinceHold = 0;
        }
    }
}
