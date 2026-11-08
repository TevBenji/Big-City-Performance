// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Globalization;

namespace BigCityPerformance.Settings
{
    /// <summary>
    /// What the Options page shows under "Right now", in numbers only (the labels carry the words, in every language).
    /// RouteDeadlineSystem refreshes it about once a second; <see cref="Version"/> tells the page a value changed.
    /// </summary>
    internal static class LiveStatus
    {
        private const string None = "-";

        internal static string People { get; private set; } = None;
        internal static string Speed { get; private set; } = None;
        internal static string Stops { get; private set; } = None;
        internal static int Version { get; private set; }

        internal static void Set(int inDetail, int population, float speed, int atStops)
        {
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
            if (People == None && Speed == None && Stops == None) return;
            People = Speed = Stops = None;
            Version++;
        }
    }
}
