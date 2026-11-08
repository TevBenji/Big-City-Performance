// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Game.Modding;
using Game.Settings;

namespace BigCityPerformance.Settings
{
    /// <summary>Saved settings and the Options page. Property names are save format: name them once and keep them.</summary>
    [FileLocation("ModsSettings/BigCityPerformance/BigCityPerformance")]
    public sealed class BigCitySettings : ModSetting
    {
        private const string Section = "Main";
        private const string Group = "Simulation";

        public BigCitySettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(Section, Group)]
        public bool KeepClockRunning { get; set; }

        [SettingsUISection(Section, Group)]
        public bool LimitPeople { get; set; }

        /// <summary>In thousands.</summary>
        [SettingsUISlider(min = 10, max = 500, step = 10, unit = Game.UI.Unit.kInteger)]
        [SettingsUISection(Section, Group)]
        [SettingsUIDisableByCondition(typeof(BigCitySettings), nameof(PeopleUnlimited))]
        public int PeopleInDetail { get; set; }

        public bool PeopleUnlimited() => !LimitPeople;

        public override void SetDefaults()
        {
            KeepClockRunning = true;
            LimitPeople = true;
            PeopleInDetail = 50;
        }

        /// <summary>English labels for the Options page; without them the page shows the raw ids.</summary>
        public MemorySource Labels()
        {
            return new MemorySource(new Dictionary<string, string>
            {
                [GetSettingsLocaleID()] = "Big City Performance",
                [GetOptionTabLocaleID(Section)] = "Big City Performance",
                [GetOptionGroupLocaleID(Group)] = "Simulation",
                [GetOptionLabelLocaleID(nameof(KeepClockRunning))] = "Keep the clock running",
                [GetOptionDescLocaleID(nameof(KeepClockRunning))] =
                    "When the game's route-finder falls behind, route requests get more time instead of the simulation pausing to wait, and citizens look for new jobs (and empty taxis for passengers) less often until it catches up. Citizens set off a moment later.",
                [GetOptionLabelLocaleID(nameof(LimitPeople))] = "Limit the people simulated in detail",
                [GetOptionDescLocaleID(nameof(LimitPeople))] =
                    "In a city bigger than the limit below, the game simulates that many people one by one and the rest stay at home: they keep their homes and jobs, count in the population, earn wages and pay taxes, but make no trips. Big cities run much faster. Smaller cities are not changed.",
                [GetOptionLabelLocaleID(nameof(PeopleInDetail))] = "Most people simulated in detail (thousands)",
                [GetOptionDescLocaleID(nameof(PeopleInDetail))] =
                    "How many people the game simulates one by one in a big city. Lower runs faster.",
            });
        }
    }
}
