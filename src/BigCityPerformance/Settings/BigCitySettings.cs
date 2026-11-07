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

        [SettingsUISlider(min = 10, max = 100, step = 10, unit = Game.UI.Unit.kPercentage)]
        [SettingsUISection(Section, Group)]
        public int SimulatedPercent { get; set; }

        public override void SetDefaults()
        {
            KeepClockRunning = true;
            SimulatedPercent = 100;
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
                [GetOptionLabelLocaleID(nameof(SimulatedPercent))] = "People simulated in detail",
                [GetOptionDescLocaleID(nameof(SimulatedPercent))] =
                    "The share of households the game simulates one by one. The others stay at home: they keep their homes and jobs, count in the population and pay their taxes, but make no trips. Lower runs big cities much faster. 100% is the game as it is.",
            });
        }
    }
}
