// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;

namespace BigCityPerformance.Core
{
    /// <summary>
    /// Picks how many people to simulate in detail from how the clock is keeping up, so nobody has to guess a number for
    /// their computer. Every <see cref="CheckSeconds"/> of play it looks at the window just gone:
    /// <list type="bullet">
    /// <item>The clock behind (under 90 % of the speed asked for, at most 2x) and fewer people would help (the clock waited on
    /// routes, or the processor, not the graphics card, set the pace): a fifth fewer.</item>
    /// <item>The clock on time with room to spare (no waiting on routes, the graphics card or spare time setting the pace):
    /// a tenth more, but not within <see cref="QuietChecks"/> checks of going down, so it doesn't see-saw.</item>
    /// </list>
    /// Between <see cref="Floor"/> and <see cref="Ceiling"/>, in whole thousands.
    /// </summary>
    public sealed class AutoLimitPolicy
    {
        public const int Floor = 20000;
        public const int Ceiling = 500000;
        public const int Start = 50000;
        public const float CheckSeconds = 20f;

        /// <summary>Checks after a cut before the limit may rise again.</summary>
        public const int QuietChecks = 3;

        /// <summary>The fastest speed it aims for: the third speed button is "as fast as it goes", not a promise of 4x.</summary>
        public const float MostSpeed = 2f;

        private int sinceCut = QuietChecks;

        public int Limit { get; private set; } = Start;

        /// <param name="achieved">Game speed reached in the window (1 = normal).</param>
        /// <param name="selected">The speed the player picked.</param>
        /// <param name="stalledShare">Share of frames the clock waited on route deadlines.</param>
        /// <param name="cpuBound">The processor set the frame's pace (or it couldn't be told).</param>
        /// <returns>True when the limit changed.</returns>
        public bool Update(float achieved, float selected, float stalledShare, bool cpuBound)
        {
            if (selected <= 0f) return false;
            float ratio = achieved / Math.Min(selected, MostSpeed);
            if (sinceCut < QuietChecks) sinceCut++;
            if (ratio < 0.9f && (stalledShare > 0.1f || cpuBound))
            {
                sinceCut = 0;
                return Set(Limit * 4 / 5);
            }
            if (ratio >= 0.97f && stalledShare < 0.02f && sinceCut >= QuietChecks) return Set(Limit * 11 / 10);
            return false;
        }

        public void Reset()
        {
            Limit = Start;
            sinceCut = QuietChecks;
        }

        private bool Set(int limit)
        {
            limit = Math.Max(Floor, Math.Min(Ceiling, (limit + 500) / 1000 * 1000));
            if (limit == Limit) return false;
            Limit = limit;
            return true;
        }
    }
}
