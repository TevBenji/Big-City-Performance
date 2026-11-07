// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;
using BigCityPerformance.Routes;
using BigCityPerformance.Settings;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using HarmonyLib;

namespace BigCityPerformance
{
    public sealed class Mod : IMod
    {
        private const string HarmonyId = "TevBenji.BigCityPerformance";

        internal static readonly ILog Logger = LogManager.GetLogger(nameof(BigCityPerformance)).SetShowsErrorsInUI(false);

        internal static BigCitySettings Settings { get; private set; }

        private Harmony harmony;

        public void OnLoad(UpdateSystem updateSystem)
        {
            Logger.Info("Big City Performance loading");
            Register("settings", () =>
            {
                Settings = new BigCitySettings(this);
                AssetDatabase.global.LoadSettings(nameof(BigCityPerformance), Settings, new BigCitySettings(this));
                Settings.RegisterInOptionsUI();
                GameManager.instance.localizationManager.AddSource("en-US", Settings.Labels());
            });
            Register("route deadlines", () =>
            {
                updateSystem.UpdateAt<RouteDeadlineSystem>(SystemUpdatePhase.UIUpdate);
                harmony = new Harmony(HarmonyId);
                harmony.CreateClassProcessor(typeof(GetQueuePatch)).Patch();
                SearchThrottle.Patch(harmony);
                RouteCache.Patch(harmony);
                harmony.CreateClassProcessor(typeof(RouteGuide)).Patch();
            });
#if BCP_DEV
            Register("dev harness", () => Dev.DevHarness.StartIfAsked(updateSystem));
#endif
        }

        public void OnDispose()
        {
            Register("removing the patches", () => harmony?.UnpatchAll(HarmonyId));
            Register("removing the settings", () => Settings?.UnregisterInOptionsUI());
            Settings = null;
        }

        // Each area registers inside its own try/catch, so a broken one is logged and never takes the game down.
        private static void Register(string area, Action action)
        {
            try { action(); }
            catch (Exception exception) { Logger.Error(exception, "Big City Performance: " + area + " failed"); }
        }
    }
}
