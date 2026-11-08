// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;

namespace BigCityPerformance.Core
{
    /// <summary>
    /// Tells a commute the mod has already spread from one the game measured, without remembering anything per worker. A spread
    /// value sits halfway between eighths (n/8 + 1/16); the game's own commutes are whole simulation frames over 60 (k/60), and
    /// k/60 = n/8 + 1/16 would need 4k = 30n + 15, an even number equal to an odd one, so they never land there.
    /// </summary>
    public static class CommuteMark
    {
        public static float Mark(float units) => (float)Math.Floor(units * 8f) / 8f + 1f / 16f;

        public static bool IsMarked(float units) => units * 8f - (float)Math.Floor(units * 8f) == 0.5f;
    }
}
