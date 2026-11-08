// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
namespace BigCityPerformance.Core
{
    /// <summary>Hands out a whole number of items in proportion to weights.</summary>
    public static class ShareOut
    {
        /// <summary>
        /// How many of <paramref name="total"/> items each weight gets: evenly spaced picks along the weights laid end to end,
        /// so every count is within one of its exact share and the counts add up to <paramref name="total"/>.
        /// <paramref name="offset"/> (0 to 1) is where in its slot each pick lands: moving it between calls lets items fewer than
        /// the weights reach every weight in turn, instead of the same ones every time.
        /// </summary>
        public static int[] Counts(long[] weights, int total, double offset = 0.5)
        {
            var counts = new int[weights.Length];
            long sum = 0;
            foreach (long weight in weights) sum += weight;
            if (sum <= 0 || total <= 0) return counts;
            int index = 0;
            long reached = weights.Length > 0 ? weights[0] : 0;
            for (int pick = 0; pick < total; pick++)
            {
                long at = (long)((pick + offset) * sum / total);
                while (at >= reached && index < weights.Length - 1) reached += weights[++index];
                counts[index]++;
            }
            return counts;
        }
    }
}
