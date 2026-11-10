// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;

namespace BigCityPerformance.Core
{
    /// <summary>
    /// Holds the frame rate by drawing less detail while the graphics card can't keep up. It works on the game's own level of
    /// detail (Options, Graphics): <see cref="Factor"/> is the share of the player's setting to draw with. Every window:
    /// <list type="bullet">
    /// <item>under <see cref="Target"/> frames a second with the graphics card setting the pace: a step less detail, down to
    /// <see cref="Floor"/>. A slow processor isn't helped by drawing less, so nothing changes then.</item>
    /// <item>over <see cref="Room"/> frames a second: a smaller step back, up to the player's own setting.</item>
    /// </list>
    /// Between the two nothing moves, so it settles instead of see-sawing.
    /// </summary>
    public sealed class DetailPolicy
    {
        public const float Target = 30f;
        public const float Room = 40f;

        /// <summary>The least it draws: half the player's own level of detail.</summary>
        public const float Floor = 0.5f;
        public const float StepDown = 0.1f, StepBack = 0.05f;

        public float Factor { get; private set; } = 1f;

        /// <param name="framesPerSecond">Frames a second over the window just gone.</param>
        /// <param name="graphicsBound">The graphics card, not the processor, set the frame's pace.</param>
        /// <returns>True when the factor changed.</returns>
        public bool Update(float framesPerSecond, bool graphicsBound)
        {
            if (framesPerSecond < Target * 0.95f) return graphicsBound && Set(Factor - StepDown);
            return framesPerSecond > Room && Set(Factor + StepBack);
        }

        public void Reset() => Factor = 1f;

        private bool Set(float factor)
        {
            // Whole hundredths, so steps down and back land on the same values.
            factor = (float)Math.Round(Math.Max(Floor, Math.Min(1f, factor)), 2);
            if (factor == Factor) return false;
            Factor = factor;
            return true;
        }
    }
}
