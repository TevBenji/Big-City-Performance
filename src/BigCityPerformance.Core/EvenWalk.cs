// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
namespace BigCityPerformance.Core
{
    /// <summary>
    /// A walk over a list that is spread evenly and still reaches every item: from any start, stepping by
    /// <see cref="Step"/> and wrapping round visits each index once before any comes up again. So a look at only part of the
    /// list falls evenly over all of it, and the next look can carry on from where the last one stopped instead of looking
    /// at the same items again.
    /// </summary>
    public static class EvenWalk
    {
        /// <summary>
        /// The step for a list of <paramref name="length"/> items when about <paramref name="wanted"/> of them are wanted:
        /// near length / wanted, and sharing no factor with the length (that is what makes the walk reach every index).
        /// </summary>
        public static int Step(int length, int wanted)
        {
            if (length <= 1) return 1;
            int step = length / System.Math.Max(1, wanted) % length;
            if (step < 1) step = 1;
            while (Gcd(step, length) != 1) step++;
            // length - 1 never shares a factor with length, so the search above stops there at the latest.
            return step;
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0) { int rest = a % b; a = b; b = rest; }
            return a;
        }
    }
}
