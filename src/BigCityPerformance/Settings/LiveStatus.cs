// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Globalization;

namespace BigCityPerformance.Settings
{
    /// <summary>
    /// What the mod is doing right now, for the Options page's "Right now" rows (numbers only: the labels carry the words, in
    /// every language) and the indicator in the population box. ClockWatch refreshes it about once a second;
    /// <see cref="Version"/> tells the Options page a value changed.
    /// </summary>
    internal static class LiveStatus
    {
        private const string None = "-";

        internal static string People { get; private set; } = None;
        internal static string Speed { get; private set; } = None;
        internal static string Stops { get; private set; } = None;
        internal static int Version { get; private set; }

        internal static int InDetail { get; private set; }
        internal static int Population { get; private set; }
        internal static float GameSpeed { get; private set; }
        internal static int AtStops { get; private set; }

        internal static void Set(int inDetail, int population, float speed, int atStops)
        {
            InDetail = inDetail;
            Population = population;
            GameSpeed = speed;
            AtStops = atStops;
            string people = population > 0
                ? inDetail.ToString("N0", CultureInfo.InvariantCulture) + " / " + population.ToString("N0", CultureInfo.InvariantCulture)
                : None;
            string pace = speed.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            string stops = atStops.ToString("N0", CultureInfo.InvariantCulture);
            if (people == People && pace == Speed && stops == Stops) return;
            People = people;
            Speed = pace;
            Stops = stops;
            Version++;
        }

        /// <summary>No city loaded.</summary>
        internal static void Clear()
        {
            InDetail = Population = AtStops = 0;
            GameSpeed = 0f;
            if (People == None && Speed == None && Stops == None) return;
            People = Speed = Stops = None;
            Version++;
        }
    }
}
