// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Json;
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
        private const string StatusGroup = "Status";

        public BigCitySettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(Section, Group)]
        public bool KeepClockRunning { get; set; }

        [SettingsUISection(Section, Group)]
        public bool LimitPeople { get; set; }

        [SettingsUISection(Section, Group)]
        [SettingsUIDisableByCondition(typeof(BigCitySettings), nameof(PeopleUnlimited))]
        public bool AutoLimit { get; set; }

        /// <summary>In thousands; used when <see cref="AutoLimit"/> is off.</summary>
        [SettingsUISlider(min = 10, max = 500, step = 10, unit = Game.UI.Unit.kInteger)]
        [SettingsUISection(Section, Group)]
        [SettingsUIDisableByCondition(typeof(BigCitySettings), nameof(LimitChosenForYou))]
        public int PeopleInDetail { get; set; }

        /// <summary>The count beside the population at the bottom of the screen.</summary>
        [SettingsUISection(Section, StatusGroup)]
        public bool ShowOnScreen { get; set; }

        [Exclude]
        [SettingsUISection(Section, StatusGroup)]
        [SettingsUIValueVersion(typeof(BigCitySettings), nameof(StatusVersion))]
        public string StatusPeople => LiveStatus.People;

        [Exclude]
        [SettingsUISection(Section, StatusGroup)]
        [SettingsUIValueVersion(typeof(BigCitySettings), nameof(StatusVersion))]
        public string StatusSpeed => LiveStatus.Speed;

        [Exclude]
        [SettingsUISection(Section, StatusGroup)]
        [SettingsUIValueVersion(typeof(BigCitySettings), nameof(StatusVersion))]
        public string StatusStops => LiveStatus.Stops;

        public bool PeopleUnlimited() => !LimitPeople;

        public bool LimitChosenForYou() => !LimitPeople || AutoLimit;

        public int StatusVersion() => LiveStatus.Version;

        public override void SetDefaults()
        {
            KeepClockRunning = true;
            LimitPeople = true;
            AutoLimit = true;
            PeopleInDetail = 50;
            ShowOnScreen = true;
        }

        /// <summary>The Options page's words in one language (English for any it doesn't have): without them it shows raw ids.</summary>
        public MemorySource Labels(string locale)
        {
            var words = Translations.For(locale);
            var labels = new Dictionary<string, string>
            {
                [GetSettingsLocaleID()] = "Big City Performance",
                [GetOptionTabLocaleID(Section)] = "Big City Performance",
                [GetOptionGroupLocaleID(Group)] = words[nameof(Group)],
                [GetOptionGroupLocaleID(StatusGroup)] = words[nameof(StatusGroup)],
            };
            foreach (string name in new[] { nameof(KeepClockRunning), nameof(LimitPeople), nameof(AutoLimit), nameof(PeopleInDetail), nameof(ShowOnScreen), nameof(StatusPeople), nameof(StatusSpeed), nameof(StatusStops) })
            {
                labels[GetOptionLabelLocaleID(name)] = words[name];
                labels[GetOptionDescLocaleID(name)] = words[name + ".Desc"];
            }
            return new MemorySource(labels);
        }
    }
}
