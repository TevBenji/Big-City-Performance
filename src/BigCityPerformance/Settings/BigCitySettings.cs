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
    [SettingsUIShowGroupName(HelpGroup)]
    public sealed class BigCitySettings : ModSetting
    {
        private const string Section = "Main";
        private const string Group = "Simulation";
        private const string StatusGroup = "Status";
        private const string HelpGroup = "Help";
        private const string BugReportAddress = "https://github.com/TevBenji/Big-City-Performance/issues/new";
        // The Discord button stays hidden while this is empty.
        private const string DiscordAddress = "https://discord.com/channels/263634513861541888/1568997116595150939";

        public BigCitySettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(Section, Group)]
        public bool KeepClockRunning { get; set; }

        /// <summary>Up to twice the game's simulation steps per frame below 30 fps (<see cref="Routes.StepBudget"/>).</summary>
        [SettingsUISection(Section, Group)]
        public bool ExtraSteps { get; set; }

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

        /// <summary>The game's pollution systems switched off and its pollution cleared (<see cref="Services.PollutionSwitch"/>).</summary>
        [SettingsUISection(Section, Group)]
        public bool TurnOffPollution { get; set; }

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

        [Exclude]
        [SettingsUIButton]
        [SettingsUISection(Section, HelpGroup)]
        public bool WriteReport { set => Diagnostics.PerformanceReport.Requested = true; }

        [Exclude]
        [SettingsUIButton]
        [SettingsUISection(Section, HelpGroup)]
        public bool ReportBug { set => UnityEngine.Application.OpenURL(BugReportAddress); }

        [Exclude]
        [SettingsUIButton]
        [SettingsUISection(Section, HelpGroup)]
        [SettingsUIHideByCondition(typeof(BigCitySettings), nameof(NoDiscordAddress))]
        public bool Discord { set => UnityEngine.Application.OpenURL(DiscordAddress); }

        public bool PeopleUnlimited() => !LimitPeople;

        public bool LimitChosenForYou() => !LimitPeople || AutoLimit;

        public int StatusVersion() => LiveStatus.Version;

        public bool NoDiscordAddress() => DiscordAddress.Length == 0;

        public override void SetDefaults()
        {
            KeepClockRunning = true;
            // Off: on a computer where the processor sets the pace, the extra steps cost far more frames than they gain
            // (a player's report, 11 November 2026: 30 fps down to 9-12 for a quarter more simulation).
            ExtraSteps = false;
            LimitPeople = true;
            AutoLimit = true;
            PeopleInDetail = 50;
            ShowOnScreen = true;
            TurnOffPollution = false;
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
                [GetOptionGroupLocaleID(HelpGroup)] = words[nameof(HelpGroup)],
            };
            foreach (string name in new[] { nameof(KeepClockRunning), nameof(ExtraSteps), nameof(LimitPeople), nameof(AutoLimit), nameof(PeopleInDetail), nameof(TurnOffPollution), nameof(ShowOnScreen), nameof(StatusPeople), nameof(StatusSpeed), nameof(StatusStops), nameof(WriteReport), nameof(ReportBug), nameof(Discord) })
            {
                labels[GetOptionLabelLocaleID(name)] = words[name];
                labels[GetOptionDescLocaleID(name)] = words[name + ".Desc"];
            }
            return new MemorySource(labels);
        }
    }
}
