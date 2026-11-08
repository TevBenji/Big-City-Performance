// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BigCityPerformance.People;
using BigCityPerformance.Settings;
using Colossal.UI.Binding;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;
using UnityEngine.Scripting;

namespace BigCityPerformance.UI
{
    /// <summary>
    /// Feeds the count in the population box: one JSON string, refreshed about once a second, every word already in the
    /// player's language, so the panel only lays it out. Shown only while people are actually asleep (the city is bigger than
    /// the limit) and the player hasn't turned it off. A click opens the mod's page in Options.
    /// </summary>
    public sealed partial class IndicatorUISystem : UISystemBase
    {
        private const string Group = "BigCityPerformance";
        private ValueBinding<string> indicator;
        private float next;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(indicator = new ValueBinding<string>(Group, "indicator", "{}"));
            AddBinding(new TriggerBinding(Group, "openOptions", OpenOptions));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < next) return;
            next = now + 1f;
            indicator.Update(Json());
        }

        private static string Json()
        {
            var settings = Mod.Settings;
            var dormancy = DormancySystem.Instance;
            if (settings == null || !settings.ShowOnScreen || !settings.LimitPeople || dormancy == null || !dormancy.Ready || dormancy.SleepingHouseholds == 0)
                return "{\"show\":false}";
            var words = Translations.For(GameManager.instance?.localizationManager?.activeLocaleId ?? "en-US");
            int limit = settings.AutoLimit ? ClockWatch.Auto.Limit : settings.PeopleInDetail * 1000;
            var rows = new List<string[]>
            {
                new[] { words["StatusPeople"], LiveStatus.People },
                new[] { words["StatusSpeed"], LiveStatus.Speed },
                new[] { words["Indicator.Limit"], (settings.AutoLimit ? words["Indicator.Automatic"] : words["Indicator.Fixed"]) + ": " + limit.ToString("N0", CultureInfo.InvariantCulture) },
                new[] { words["StatusStops"], LiveStatus.Stops },
            };
            int atHome = System.Math.Max(0, LiveStatus.Population - LiveStatus.InDetail);
            var json = new StringBuilder("{\"show\":true");
            json.Append(",\"count\":").Append(Quote(Compact(LiveStatus.InDetail)));
            json.Append(",\"auto\":").Append(settings.AutoLimit ? "true" : "false");
            json.Append(",\"title\":").Append(Quote("Big City Performance"));
            json.Append(",\"rows\":[");
            for (int index = 0; index < rows.Count; index++)
            {
                if (index > 0) json.Append(',');
                json.Append('[').Append(Quote(rows[index][0])).Append(',').Append(Quote(rows[index][1])).Append(']');
            }
            json.Append("],\"footer\":").Append(Quote(string.Format(words["Indicator.AtHome"], atHome.ToString("N0", CultureInfo.InvariantCulture))));
            json.Append(",\"hint\":").Append(Quote(words["Indicator.Click"]));
            return json.Append('}').ToString();
        }

        /// <summary>48k, 1.2M: short enough for the box.</summary>
        private static string Compact(int people)
        {
            if (people < 1000) return people.ToString(CultureInfo.InvariantCulture);
            if (people < 1000000) return (people / 1000).ToString(CultureInfo.InvariantCulture) + "k";
            return (people / 1000000f).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        private static string Quote(string text)
        {
            var quoted = new StringBuilder("\"");
            foreach (char character in text ?? "")
            {
                switch (character)
                {
                    case '"': quoted.Append("\\\""); break;
                    case '\\': quoted.Append("\\\\"); break;
                    case '\n': quoted.Append("\\n"); break;
                    case '\r': break;
                    case '\t': quoted.Append("\\t"); break;
                    default:
                        if (character < ' ') quoted.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else quoted.Append(character);
                        break;
                }
            }
            return quoted.Append('"').ToString();
        }

        private void OpenOptions()
        {
            try
            {
                if (Mod.Settings != null) World.GetOrCreateSystemManaged<OptionsUISystem>().OpenPage(Mod.Settings.id, null, false);
            }
            catch (System.Exception error) { Mod.Logger.Warn($"Could not open the settings: {error.Message}"); }
        }
    }
}
