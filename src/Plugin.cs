using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PopulationPlanner
{
    public enum ManualPickMode
    {
        // The mod leaves a city you set by hand alone until that city grows.
        UntilCityGrows,
        // ...until you click Resume in the window.
        UntilResumed,
        // The mod overrides picks made by hand.
        Ignore,
    }

    [BepInPlugin(Guid, DisplayName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "javierortegap.el2.populationplanner";
        public const string DisplayName = "Population Planner";
        public const string Version = BuildInfo.Version;

        // The id of pre-release builds: their options file is taken over once.
        private const string OldGuid = "el2.populationplanner";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Automation;
        internal static ConfigEntry<bool> SpreadFirst;
        internal static ConfigEntry<bool> ContinueAfterList;
        internal static ConfigEntry<ManualPickMode> ManualPicks;
        internal static ConfigEntry<int> FailedGrowthCooldown;
        internal static ConfigEntry<bool> OptimizeJobs;
        internal static ConfigEntry<float> JobMinGain;
        internal static ConfigEntry<string> MinimumApproval;
        internal static ConfigEntry<int> ApprovalBuffer;
        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<float> UiScale;
        internal static ConfigEntry<float> Opacity;
        internal static ConfigEntry<bool> LogDecisions;

        private Harmony harmony;
        private bool patched;
        private Controller controller;
        private PlannerWindow window;

        private void Awake()
        {
            Log = Logger;
            TakeOverOldOptions();
            Automation = Config.Bind("General", "Automation", true,
                "Pick every city's next population automatically. Also switchable in the window.");
            SpreadFirst = Config.Bind("General", "SpreadUnlockedBonusesFirst", true,
                "Once a population's last bonus is unlocked, grow one of it in every city that has none before anything else: that bonus only applies in cities with at least one.");
            ContinueAfterList = Config.Bind("General", "ContinueAfterOrder", true,
                "When every population in your order is done or can't be grown, keep going with the others, closest to their last bonus first.");
            ManualPicks = Config.Bind("General", "ManualPicks", ManualPickMode.UntilCityGrows,
                "When you pick a city's next population yourself in the city screen: UntilCityGrows = the mod leaves that city alone until it grows; UntilResumed = until you click Resume in the window; Ignore = the mod overrides it.");
            FailedGrowthCooldown = Config.Bind("General", "FailedGrowthCooldownTurns", 5,
                new ConfigDescription("If the game fails to add a population to a city, don't pick that population there again for this many turns.", new AcceptableValueRange<int>(1, 100)));
            OptimizeJobs = Config.Bind("Jobs", "OptimizeJobs", true,
                "Swap populations between jobs so their job effects apply (e.g. Daughter of Bor as artisans, Xavius next to other population types, Sollusk kept together). How many work each job stays as the city's job strategy set it.");
            JobMinGain = Config.Bind("Jobs", "MinimumGain", 0.5f,
                new ConfigDescription("A swap must gain at least this much (yields weighted by the city's job strategy) to be done.", new AcceptableValueRange<float>(0.1f, 20f)));
            MinimumApproval = Config.Bind("Approval", "MinimumLevel", ApprovalRule.Off,
                new ConfigDescription("Keep every city at least at this approval level (Off, Content, Happy or Jubilant; cities can override it in the window). "
                    + "Below it, the city grows the population adding the most approval there and its jobs put approval first.",
                    new AcceptableValueList<string>(ApprovalRule.Levels)));
            ApprovalBuffer = Config.Bind("Approval", "Buffer", 3,
                new ConfigDescription("Start putting approval first this many points above the level, so the next population (about -3 approval as Citizen or Artisan) doesn't drop the city below it.",
                    new AcceptableValueRange<int>(0, 30)));
            ToggleKey = Config.Bind("Window", "ToggleKey", "F7",
                "Key that opens and closes the window (a Unity Input System key name: F7, F8, Backquote, Insert...). F7 is free in the game's default bindings.");
            UiScale = Config.Bind("Window", "Size", 0f,
                new ConfigDescription("Size of the window and its text. 0 = automatic (follows the screen resolution: 1x at 1080p, 2x at 4K); otherwise a multiplier.", new AcceptableValueRange<float>(0f, 4f)));
            Opacity = Config.Bind("Window", "Opacity", 1f,
                new ConfigDescription("Opacity of the window background (1 = solid).", new AcceptableValueRange<float>(0.3f, 1f)));
            LogDecisions = Config.Bind("Debug", "LogDecisions", true,
                "Write every pick the mod makes to BepInEx/LogOutput.log.");

            harmony = new Harmony(Guid);
            controller = new Controller();
            window = new PlannerWindow(controller);
            // Options change what the plan and the job swaps should be: plan again.
            Config.SettingChanged += (sender, args) => controller.ConfigChanged();
            Log.LogInfo($"{DisplayName} {Version} waiting for the game data");
        }

        private void Update()
        {
            if (!patched)
            {
                if (!GameDataReady())
                {
                    return;
                }
                PatchAll();
            }
            Guard.UnpatchFailed(harmony);
            try
            {
                window.Update();
                controller.Update();
            }
            catch (Exception e)
            {
                // Keep the game running; the next frame tries again.
                Log.LogError($"Update failed: {e}");
            }
        }

        private void OnGUI()
        {
            if (!patched)
            {
                return;
            }
            try
            {
                window.OnGUI();
            }
            catch (Exception e)
            {
                Log.LogError($"Drawing the window failed: {e}");
            }
        }

        private void OnApplicationQuit() => controller?.SaveNow();

        private void OnDestroy()
        {
            controller?.SaveNow();
            harmony?.UnpatchSelf();
        }

        private void TakeOverOldOptions()
        {
            try
            {
                string old = System.IO.Path.Combine(Paths.ConfigPath, OldGuid + ".cfg");
                if (System.IO.File.Exists(old) && !System.IO.File.Exists(Config.ConfigFilePath))
                {
                    System.IO.File.Move(old, Config.ConfigFilePath);
                    Config.Reload();
                    Log.LogInfo($"Options taken over from {OldGuid}.cfg");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not take over the old options file: {e.Message}");
            }
        }

        // Patching runs a class's static constructor; wait until the game data those may read is loaded.
        private static bool GameDataReady()
        {
            try
            {
                return Amplitude.Mercury.Utils.DataUtils != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // One patch class at a time, so a game update that breaks one only disables that part, and the log names it.
        private void PatchAll()
        {
            patched = true;
            int failed = 0;
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0)
                {
                    continue;
                }
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failed++;
                    Guard.MarkFailed(type);
                    Log.LogError($"Could not patch {type.Name}, that part of the mod is off (game update?): {e.GetBaseException().Message}");
                }
            }
            Log.LogInfo($"{DisplayName} {Version} active (game {Application.version}, {failed} patches failed). Press {ToggleKey.Value} in game.");
        }
    }
}
