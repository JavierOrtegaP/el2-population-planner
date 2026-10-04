using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PopulationPlanner
{
    // The in-game window (Unity IMGUI), opened with F7 by default.
    internal sealed class PlannerWindow
    {
        private const int WindowId = 0x50504C31;
        private const float Width = 660f;
        private const float Height = 620f;

        // Read by InputBlockPatch, on the main thread.
        internal static bool MouseOver;

        private static readonly string[] Tabs = { "Bonuses", "Cities", "Populations", "Settings" };

        // Mod Menu, a separate optional mod: when installed it takes the key and shows this window's content as one of
        // its pages (Plugin.ModMenuDraw). Without it, nothing changes.
        private const string MenuGuid = "el2.modmenu";

        private readonly Controller controller;
        // Clicks only queue their effect; it runs in the next Update. Changing what the window draws in the middle of
        // an IMGUI pass makes the layout pass and the event pass disagree, which Unity reports as errors.
        private readonly List<Action> deferred = new List<Action>();
        private Rect rect = new Rect(0f, 0f, Width, Height);
        private bool visible;
        private bool positioned;
        private int tab;
        private Vector2 scroll;
        private ulong pickingTargetFor;
        private string keyName;
        private Key key = Key.F7;
        private float hintUntil = -1f;
        private bool hintShown;
        private bool? menuInstalled;
        // Populations tab: whose bonuses show (all but the clicked ones, or only the clicked ones).
        private readonly HashSet<string> toggledPops = new HashSet<string>(StringComparer.Ordinal);
        private bool allPopsOpen;
        // The game's bonus text made plain, by text: the same text comes back every turn.
        private readonly Dictionary<string, List<string>> bonusLines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private GameState popRowsState;
        private List<PopRow> ownedRows = new List<PopRow>();
        private List<PopRow> otherRows = new List<PopRow>();

        private bool stylesReady;
        // Unity's default window and box backgrounds are see-through; these are solid, at the configured opacity.
        private Texture2D windowBackground;
        private Texture2D rowBackground;
        private float backgroundOpacity = -1f;
        private GUIStyle windowStyle;
        private GUIStyle textStyle;
        private GUIStyle mutedStyle;
        private GUIStyle goodStyle;
        private GUIStyle warnStyle;
        private GUIStyle badStyle;
        private GUIStyle headingStyle;
        private GUIStyle boxStyle;
        private GUIStyle hintStyle;
        private GUIStyle nameButtonStyle;

        public PlannerWindow(Controller controller)
        {
            this.controller = controller;
        }

        private static bool AutoScale => Plugin.UiScale.Value < 0.25f;

        // Laid out for 1080p: automatic size grows with the screen height, in quarter steps.
        private static float Scale => AutoScale ? Mathf.Max(1f, Mathf.Round(Screen.height / 1080f * 4f) / 4f) : Mathf.Clamp(Plugin.UiScale.Value, 0.5f, 4f);

        // Looked up once a game runs: BepInEx may load the menu after this mod.
        private bool MenuInstalled
        {
            get
            {
                if (menuInstalled == null && controller.InGame)
                {
                    menuInstalled = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(MenuGuid);
                    if (menuInstalled == true)
                    {
                        Plugin.Log.LogInfo("Mod Menu found: the planner is its Population page, opened with the menu's key.");
                    }
                }
                return menuInstalled == true;
            }
        }

        public void Update()
        {
            if (deferred.Count > 0)
            {
                Action[] actions = deferred.ToArray();
                deferred.Clear();
                foreach (Action action in actions)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Window action failed: {e}");
                    }
                }
            }
            ReadKey();
            Keyboard keyboard = Keyboard.current;
            bool menu = MenuInstalled;
            if (!menu && keyboard != null && key != Key.None && keyboard[key].wasPressedThisFrame)
            {
                visible = !visible;
            }
            visible = visible && !menu;
            if (controller.InGame && controller.IsNewGame && !hintShown)
            {
                hintShown = true;
                hintUntil = Time.unscaledTime + 15f;
            }
            MouseOver = visible && controller.InGame && IsMouseInside();
        }

        public void OnGUI()
        {
            if (!controller.InGame)
            {
                return;
            }
            EnsureStyles();
            RefreshBackgrounds();
            Matrix4x4 previous = GUI.matrix;
            float scale = Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float screenWidth = Screen.width / scale;
            float screenHeight = Screen.height / scale;
            if (!visible && Time.unscaledTime < hintUntil)
            {
                var hint = new Rect((screenWidth - 520f) / 2f, 70f, 520f, 46f);
                string open = MenuInstalled ? "Open Population in Mod Menu" : $"Press {key}";
                GUI.Box(hint, $"Population Planner is choosing your cities' next population.\n{open} to pick which bonus to aim for.", hintStyle);
            }
            if (visible)
            {
                if (!positioned)
                {
                    rect.x = Mathf.Max(0f, screenWidth - Width - 24f);
                    rect.y = 90f;
                    positioned = true;
                }
                rect = GUI.Window(WindowId, rect, DrawWindow, $"Population Planner  ({key} to close)", windowStyle);
                rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, screenWidth - rect.width));
                rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, screenHeight - 40f));
            }
            GUI.matrix = previous;
        }

        private void DrawWindow(int id)
        {
            GameState state = controller.State;
            PlanResult plan = controller.Plan;
            GUILayout.Space(2f);
            DrawTopBar(state, plan);
            DrawTabBar();
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            DrawTab(state, plan);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        // The same content as Mod Menu's Population page, drawn inside the menu's window and scroll view.
        public void DrawEmbedded()
        {
            if (!controller.InGame)
            {
                GUILayout.Label("Load a game to see the plan.");
                return;
            }
            EnsureStyles();
            RefreshBackgrounds();
            GameState state = controller.State;
            PlanResult plan = controller.Plan;
            DrawTopBar(state, plan);
            DrawTabBar();
            DrawTab(state, plan);
        }

        // One line for Mod Menu's All mods list: what the window's top bar says.
        public string StatusLine()
        {
            if (Guard.AnyFailed)
            {
                return "Partly off after an error (see BepInEx/LogOutput.log)";
            }
            return controller.InGame ? Status(controller.State, controller.Plan) : (Plugin.Automation.Value ? "On, waiting for a game" : "Off");
        }

        private void DrawTabBar()
        {
            int selectedTab = GUILayout.Toolbar(tab, Tabs);
            if (selectedTab != tab)
            {
                Defer(() => tab = selectedTab);
            }
            GUILayout.Space(4f);
        }

        private void DrawTab(GameState state, PlanResult plan)
        {
            switch (tab)
            {
                case 0:
                    DrawBonuses(state, plan);
                    break;
                case 1:
                    DrawCities(state, plan);
                    break;
                case 2:
                    DrawPopulations(state);
                    break;
                default:
                    DrawSettings();
                    break;
            }
        }

        private void DrawTopBar(GameState state, PlanResult plan)
        {
            GUILayout.BeginHorizontal();
            bool automation = Plugin.Automation.Value;
            if (GUILayout.Button(automation ? "Automation: ON" : "Automation: OFF", GUILayout.Width(140f)))
            {
                Defer(() => Plugin.Automation.Value = !automation);
            }
            GUILayout.Label($"Turn {state.Turn} · {state.Cities.Count} cities", mutedStyle, GUILayout.Width(150f));
            GUILayout.Label(Status(state, plan), StatusStyle(state), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (Guard.AnyFailed)
            {
                GUILayout.Label("Part of the mod switched itself off after an error (game update?). See BepInEx/LogOutput.log.", badStyle);
            }
        }

        private string Status(GameState state, PlanResult plan)
        {
            if (state.CannotGrowWithFood)
            {
                return "Your faction doesn't grow population with food: nothing to do.";
            }
            if (!state.IsHuman)
            {
                return "Your empire is not controlled by you right now.";
            }
            if (!Plugin.Automation.Value)
            {
                return "Off: showing the plan only.";
            }
            if (!state.CanAct)
            {
                return "Waiting for your turn.";
            }
            if (plan.Current != null && state.Types.TryGetValue(plan.Current, out PopType type))
            {
                return $"Aiming for {Names.Pop(plan.Current)} ({type.Count}/{type.MaxThreshold})";
            }
            return plan.Order.Count == 0 ? "Every bonus within reach is unlocked." : "Nothing can be grown towards a bonus right now.";
        }

        private GUIStyle StatusStyle(GameState state)
        {
            if (state.CannotGrowWithFood || !state.IsHuman)
            {
                return warnStyle;
            }
            return Plugin.Automation.Value ? goodStyle : warnStyle;
        }

        // ---- Bonuses ----

        private void DrawBonuses(GameState state, PlanResult plan)
        {
            GameSettings settings = controller.Settings;
            GUILayout.Label("Cities grow the first population below until its last bonus unlocks (usually at 30), then the next one. "
                + "★ = aim for it next. Once a bonus is unlocked, every city gets one of that population so the bonus applies there too. "
                + "The Populations tab shows what each bonus gives.", textStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Aiming for, in order", headingStyle);
            GUILayout.FlexibleSpace();
            if (settings.Priority.Count > 0 && GUILayout.Button("Back to automatic order", GUILayout.Width(190f)))
            {
                Defer(controller.ResetOrder);
            }
            GUILayout.EndHorizontal();
            if (plan.Order.Count == 0)
            {
                GUILayout.Label("Nothing left to aim for.", mutedStyle);
            }
            for (int i = 0; i < plan.Order.Count; i++)
            {
                DrawAimRow(state, plan, settings, plan.Order[i], i);
            }

            List<PopType> unlocked = state.Types.Values.Where(t => t.Done && !t.ActionOnly).OrderBy(t => Names.Pop(t.Name)).ToList();
            if (unlocked.Count > 0)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Last bonus unlocked", headingStyle);
                foreach (PopType type in unlocked)
                {
                    DrawUnlockedRow(state, settings, type);
                }
            }

            List<PopType> skipped = state.Types.Values.Where(t => settings.IsSkipped(t.Name) && !t.ActionOnly && !t.Done).OrderBy(t => Names.Pop(t.Name)).ToList();
            if (skipped.Count > 0)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Skipped (never picked)", headingStyle);
                foreach (PopType type in skipped)
                {
                    GUILayout.BeginHorizontal(boxStyle);
                    GUILayout.Label(Names.Pop(type.Name), textStyle, GUILayout.Width(220f));
                    GUILayout.Label(Progress(type), mutedStyle, GUILayout.Width(150f));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Unskip", GUILayout.Width(80f)))
                    {
                        Defer(() => controller.ToggleSkip(type.Name));
                    }
                    GUILayout.EndHorizontal();
                }
            }

            List<PopType> actionOnly = state.Types.Values.Where(t => t.ActionOnly).OrderBy(t => Names.Pop(t.Name)).ToList();
            if (actionOnly.Count > 0)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Can't be grown with food (created by actions)", headingStyle);
                GUILayout.Label(string.Join(", ", actionOnly.Select(t => $"{Names.Pop(t.Name)} {Progress(t)}").ToArray()), mutedStyle);
            }
        }

        private void DrawAimRow(GameState state, PlanResult plan, GameSettings settings, string pop, int index)
        {
            PopType type = state.Types[pop];
            GUILayout.BeginHorizontal(boxStyle);
            GUI.enabled = index > 0;
            if (GUILayout.Button(new GUIContent("★", "Aim for this one next"), GUILayout.Width(28f)))
            {
                Defer(() => controller.AimNext(pop));
            }
            if (GUILayout.Button(new GUIContent("▲", "Earlier"), GUILayout.Width(26f)))
            {
                Defer(() => controller.MoveUp(pop));
            }
            GUI.enabled = index < plan.Order.Count - 1;
            if (GUILayout.Button(new GUIContent("▼", "Later"), GUILayout.Width(26f)))
            {
                Defer(() => controller.MoveDown(pop));
            }
            GUI.enabled = true;
            GUILayout.Label($"{index + 1}.", mutedStyle, GUILayout.Width(26f));
            string name = Names.Pop(pop) + (settings.Priority.Contains(pop) ? string.Empty : "  (auto)");
            GUILayout.Label(name, pop == plan.Current ? goodStyle : textStyle, GUILayout.Width(200f));
            GUILayout.Label(Progress(type), textStyle, GUILayout.Width(110f));
            int assigned = plan.AssignedTo(pop);
            if (assigned > 0)
            {
                GUILayout.Label(assigned == 1 ? "1 city grows it next" : $"{assigned} cities grow it next", goodStyle);
            }
            else if (!GrowableAnywhere(state, pop))
            {
                GUILayout.Label("can't be grown right now", warnStyle);
            }
            else
            {
                GUILayout.Label("queued", mutedStyle);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Skip", "Never grow this population automatically"), GUILayout.Width(56f)))
            {
                Defer(() => controller.ToggleSkip(pop));
            }
            GUILayout.EndHorizontal();
        }

        private void DrawUnlockedRow(GameState state, GameSettings settings, PopType type)
        {
            GUILayout.BeginHorizontal(boxStyle);
            GUILayout.Label(Names.Pop(type.Name), goodStyle, GUILayout.Width(220f));
            GUILayout.Label(Progress(type), textStyle, GUILayout.Width(110f));
            if (type.PresenceMatters)
            {
                int cities = state.Cities.Count;
                GUILayout.Label($"applies in {type.CitiesWith}/{cities} cities", type.CitiesWith >= cities ? goodStyle : warnStyle, GUILayout.Width(160f));
                GUILayout.FlexibleSpace();
                bool spread = !settings.IsNoSpread(type.Name) && !settings.IsSkipped(type.Name);
                if (GUILayout.Button(new GUIContent(spread ? "1 per city: on" : "1 per city: off", "Grow one in every city that has none, so the bonus applies there"), GUILayout.Width(120f)))
                {
                    if (settings.IsSkipped(type.Name))
                    {
                        Defer(() => controller.ToggleSkip(type.Name));
                    }
                    else
                    {
                        Defer(() => controller.ToggleSpread(type.Name));
                    }
                }
            }
            else
            {
                GUILayout.Label("applies empire-wide", mutedStyle);
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
        }

        // ---- Cities ----

        private void DrawCities(GameState state, PlanResult plan)
        {
            GUILayout.Label("Target = what this city aims for before following the global order. Off = the mod never touches the city. "
                + "If you pick a city's next population yourself in the city screen, the mod leaves it alone until it grows.", textStyle);
            GUILayout.Space(6f);
            foreach (CityState city in state.Cities.OrderBy(c => Names.City(c), StringComparer.CurrentCultureIgnoreCase))
            {
                plan.Cities.TryGetValue(city.Guid, out CityPlan cityPlan);
                DrawCity(state, city, cityPlan);
            }
        }

        private void DrawCity(GameState state, CityState city, CityPlan plan)
        {
            CitySettings settings = controller.Settings.GetCity(city.Guid);
            bool off = settings != null && settings.Off;
            GUILayout.BeginVertical(boxStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Names.City(city)}", headingStyle, GUILayout.Width(200f));
            GUILayout.Label($"{city.Population} pop", mutedStyle, GUILayout.Width(55f));
            bool approvalFirst = controller.ApprovalFirst(city);
            GUILayout.Label($"approval {Mathf.FloorToInt(city.Approval)} {LevelName(city.ApprovalLevel)}", approvalFirst ? warnStyle : mutedStyle, GUILayout.Width(150f));
            string cityLevel = settings?.MinApproval;
            if (GUILayout.Button(cityLevel == null ? $"min: {Plugin.MinimumApproval.Value} (all)" : $"min: {cityLevel}", GUILayout.Width(130f)))
            {
                string next = NextLevel(cityLevel);
                Defer(() => controller.SetCityMinApproval(city.Guid, next));
            }
            GUILayout.FlexibleSpace();
            string targetLabel = string.IsNullOrEmpty(settings?.Target) ? "Target: global order" : "Target: " + Names.Pop(settings.Target);
            GUI.enabled = !off;
            if (GUILayout.Button(targetLabel + "  ▾", GUILayout.Width(230f)))
            {
                Defer(() => pickingTargetFor = pickingTargetFor == city.Guid ? 0UL : city.Guid);
            }
            GUI.enabled = true;
            if (GUILayout.Button(off ? "Off" : "Auto", GUILayout.Width(52f)))
            {
                Defer(() => controller.SetCityOff(city.Guid, !off));
            }
            GUILayout.EndHorizontal();

            string now = city.Growing.Length == 0 ? "nothing" : Names.Pop(city.Growing);
            string turns = city.IsGrowing ? $" ({Mathf.CeilToInt(city.TurnsToGrowth)} turns)" : " (not growing)";
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Now: {now}{turns}", textStyle, GUILayout.Width(250f));
            GUILayout.Label(PlanText(city, plan), PlanStyle(plan), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            if (plan != null && plan.Note != PlanNote.None)
            {
                GUILayout.Label(NoteText(plan), warnStyle);
            }
            if (!off && Plugin.OptimizeJobs.Value)
            {
                bool jobsOff = settings != null && settings.JobsOff;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(jobsOff ? "Jobs: off" : "Jobs: auto", GUILayout.Width(90f)))
                {
                    Defer(() => controller.SetCityJobsOff(city.Guid, !jobsOff));
                }
                GUILayout.Label(JobText(city, jobsOff), jobsOff ? mutedStyle : textStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("Reset jobs", "Put this city's populations back where its job strategy wants them (as when you change the strategy), and leave them until next turn"), GUILayout.Width(90f)))
                {
                    Defer(() => controller.ResetJobs(city.Guid));
                }
                GUILayout.EndHorizontal();
            }
            if (settings != null && !string.IsNullOrEmpty(settings.HeldPop))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"You picked {Names.Pop(settings.HeldPop)} here" + (settings.HeldUntilResumed ? "." : "; automatic again once the city grows."), warnStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Resume", GUILayout.Width(80f)))
                {
                    Defer(() => controller.Resume(city.Guid));
                }
                GUILayout.EndHorizontal();
            }
            List<string> blocked = controller.BlockedIn(city.Guid).ToList();
            if (blocked.Count > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Couldn't grow here lately: " + string.Join(", ", blocked.Select(p => $"{Names.Pop(p)} (until turn {controller.BlockedUntil(city.Guid, p)})").ToArray()), warnStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Retry", GUILayout.Width(70f)))
                {
                    Defer(() => controller.Unblock(city.Guid));
                }
                GUILayout.EndHorizontal();
            }
            if (pickingTargetFor == city.Guid && !off)
            {
                DrawTargetPicker(state, city, settings);
            }
            GUILayout.EndVertical();
        }

        private void DrawTargetPicker(GameState state, CityState city, CitySettings settings)
        {
            GUILayout.Label("Aim this city for:", mutedStyle);
            if (GUILayout.Button("The global order"))
            {
                Defer(() => controller.SetCityTarget(city.Guid, null));
                Defer(() => pickingTargetFor = 0UL);
            }
            foreach (GrowOption option in city.Options.OrderBy(o => Names.Pop(o.Pop)))
            {
                if (!state.Types.TryGetValue(option.Pop, out PopType type) || type.ActionOnly)
                {
                    continue;
                }
                string label = $"{Names.Pop(option.Pop)}   {Progress(type)}" + (type.Done ? "  (unlocked)" : string.Empty);
                bool current = settings != null && settings.Target == option.Pop;
                if (GUILayout.Button(current ? "✓ " + label : label))
                {
                    string chosen = option.Pop;
                    Defer(() => controller.SetCityTarget(city.Guid, chosen));
                    Defer(() => pickingTargetFor = 0UL);
                }
            }
        }

        private static string PlanText(CityState city, CityPlan plan)
        {
            if (plan == null)
            {
                return string.Empty;
            }
            string next = plan.Pop ?? city.Growing;
            string arrow = plan.Pop != null && plan.Pop != city.Growing ? "→ " : "= ";
            switch (plan.Kind)
            {
                case PlanKind.Approval: return $"{arrow}{Names.Pop(next)}: approval first ({plan.ApprovalGain:+0;-0;0} approval)";
                case PlanKind.Spread: return $"{arrow}{Names.Pop(next)}: one here so its bonus applies";
                case PlanKind.CityTarget: return $"{arrow}{Names.Pop(next)}: city target";
                case PlanKind.Aim: return $"{arrow}{Names.Pop(next)}: aiming for its bonus";
                case PlanKind.Ready: return $"{arrow}{Names.Pop(next)}: ready for when the city grows";
                case PlanKind.Fallback: return $"{arrow}{Names.Pop(next)}: previous pick not useful";
                case PlanKind.Leave: return "= keeping the current pick (nothing left to aim for)";
                case PlanKind.Held: return "= your pick";
                case PlanKind.Off: return "mod off for this city";
                case PlanKind.NothingGrowable: return "nothing the mod may pick here right now";
                case PlanKind.NoFood: return "this faction doesn't grow with food";
                default: return plan.Kind.ToString();
            }
        }

        private GUIStyle PlanStyle(CityPlan plan)
        {
            if (plan == null)
            {
                return mutedStyle;
            }
            switch (plan.Kind)
            {
                case PlanKind.Approval:
                    return warnStyle;
                case PlanKind.Spread:
                case PlanKind.CityTarget:
                case PlanKind.Aim:
                case PlanKind.Ready:
                    return goodStyle;
                case PlanKind.NothingGrowable:
                case PlanKind.Fallback:
                case PlanKind.Held:
                    return warnStyle;
                default:
                    return mutedStyle;
            }
        }

        private string JobText(CityState city, bool jobsOff)
        {
            if (jobsOff)
            {
                return "the mod leaves this city's jobs alone";
            }
            if (city.Jobs == null)
            {
                return string.Empty;
            }
            if (!city.Jobs.CanReassign)
            {
                return "jobs can't be changed while the city is under subjugation";
            }
            if (controller.IsJobsHeld(city.Guid))
            {
                return "you moved populations here this turn: jobs left as you set them until next turn";
            }
            if (controller.IsSwapping(city.Guid))
            {
                return "swapping...";
            }
            string last = controller.LastJobChange(city.Guid);
            int swaps = controller.SwapsThisTurn(city.Guid);
            string prefix = controller.ApprovalFirst(city) ? "approval first: " : string.Empty;
            if (last != null)
            {
                return prefix + (swaps > 1 ? $"{swaps} changes this turn, last: " : "changed: ") + last;
            }
            return prefix + "no better job swap found";
        }

        private static string NoteText(CityPlan plan)
        {
            string pop = Names.Pop(plan.NotePop);
            switch (plan.Note)
            {
                case PlanNote.TargetDone: return $"Target {pop}: last bonus already unlocked, following the global order.";
                case PlanNote.TargetNotGrowable: return $"Target {pop} can't be grown here right now, following the global order.";
                case PlanNote.TargetEnough: return $"Target {pop}: enough already on the way in cities that grow sooner.";
                default: return string.Empty;
            }
        }

        // ---- Populations ----

        private sealed class PopRow
        {
            public PopInfo Info;
            public string Name;
            public int Count;
            public int Level;
            public int[] Thresholds;
            public bool CanGrow;
        }

        private void DrawPopulations(GameState state)
        {
            GUILayout.Label("Every population in the game and what each of its bonuses gives, in the game's own words, "
                + "including populations you have none of. Click a name to show or hide its bonuses.", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Show all", GUILayout.Width(100f)))
            {
                Defer(() =>
                {
                    allPopsOpen = true;
                    toggledPops.Clear();
                });
            }
            if (GUILayout.Button("Hide all", GUILayout.Width(100f)))
            {
                Defer(() =>
                {
                    allPopsOpen = false;
                    toggledPops.Clear();
                });
            }
            GUILayout.EndHorizontal();
            if (state.Catalog.Count == 0)
            {
                GUILayout.Label("The game's list of populations isn't available (see BepInEx/LogOutput.log).", mutedStyle);
                return;
            }
            // Built once per captured state, which only changes in Update: every IMGUI pass of a frame sees the same rows.
            if (!ReferenceEquals(state, popRowsState))
            {
                List<PopRow> rows = PopRows(state);
                ownedRows = rows.Where(r => r.Count > 0 || r.Level > 0).ToList();
                otherRows = rows.Where(r => r.Count == 0 && r.Level == 0).ToList();
                popRowsState = state;
            }
            DrawPopSection("In your empire", ownedRows);
            DrawPopSection("Not in your empire", otherRows);
        }

        private static List<PopRow> PopRows(GameState state)
        {
            var rows = new List<PopRow>(state.Catalog.Count);
            foreach (PopInfo info in state.Catalog)
            {
                state.Types.TryGetValue(info.Name, out PopType type);
                rows.Add(new PopRow
                {
                    Info = info,
                    Name = Names.Pop(info.Name),
                    Count = type?.Count ?? 0,
                    Level = type?.Level ?? 0,
                    // The empire's own thresholds are read every capture; the list's once per turn.
                    Thresholds = type != null && type.Thresholds.Length == info.Thresholds.Length ? type.Thresholds : info.Thresholds,
                    CanGrow = type != null && type.AvailableToEmpire,
                });
            }
            // Populations the game gives the same name (variants of a faction's own) get their data name added.
            foreach (IGrouping<string, PopRow> same in rows.GroupBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1))
            {
                foreach (PopRow row in same)
                {
                    row.Name += $" ({Names.Internal(row.Info.Name)})";
                }
            }
            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return rows;
        }

        private void DrawPopSection(string title, List<PopRow> rows)
        {
            if (rows.Count == 0)
            {
                return;
            }
            GUILayout.Space(8f);
            GUILayout.Label(title, headingStyle);
            foreach (PopRow row in rows)
            {
                DrawPopRow(row);
            }
        }

        private void DrawPopRow(PopRow row)
        {
            string pop = row.Info.Name;
            bool open = allPopsOpen != toggledPops.Contains(pop);
            GUILayout.BeginVertical(boxStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button((open ? "▼ " : "► ") + row.Name, nameButtonStyle, GUILayout.Width(250f)))
            {
                Defer(() =>
                {
                    if (!toggledPops.Remove(pop))
                    {
                        toggledPops.Add(pop);
                    }
                });
            }
            bool done = row.Thresholds.Length > 0 && row.Level >= row.Thresholds.Length;
            GUILayout.Label(Progress(row.Count, row.Level, row.Thresholds), done ? goodStyle : textStyle, GUILayout.Width(110f));
            string note = row.Info.ActionOnly ? "created by actions, never grown with food" : row.CanGrow ? "you can grow it" : string.Empty;
            GUILayout.Label(note, mutedStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (open)
            {
                for (int tier = 0; tier < row.Thresholds.Length; tier++)
                {
                    DrawTier(row, tier);
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawTier(PopRow row, int tier)
        {
            bool unlocked = tier < row.Level;
            GUILayout.BeginHorizontal();
            GUILayout.Space(20f);
            GUILayout.Label($"{(unlocked ? '●' : '○')} {row.Thresholds[tier]}", unlocked ? goodStyle : mutedStyle, GUILayout.Width(52f));
            GUILayout.BeginVertical();
            List<string> lines = BonusLines(tier < row.Info.TierTexts.Length ? row.Info.TierTexts[tier] : string.Empty);
            if (lines.Count == 0)
            {
                GUILayout.Label("(the game gives no description for this bonus)", mutedStyle);
            }
            foreach (string line in lines)
            {
                GUILayout.Label(line, textStyle);
            }
            if (tier == row.Thresholds.Length - 1 && row.Info.PresenceMatters)
            {
                GUILayout.Label($"Only in cities with at least one {Names.Pop(row.Info.Name)}.", mutedStyle);
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private List<string> BonusLines(string text)
        {
            if (!bonusLines.TryGetValue(text ?? string.Empty, out List<string> lines))
            {
                lines = BonusText.Lines(text, Names.IconWord);
                bonusLines[text ?? string.Empty] = lines;
            }
            return lines;
        }

        // ---- Settings ----

        private void DrawSettings()
        {
            Toggle(Plugin.SpreadFirst, "Spread unlocked bonuses first",
                "One population of every unlocked bonus in each city before anything else (the bonus only applies in cities that have one).");
            Toggle(Plugin.ContinueAfterList, "Keep going after my order",
                "When everything in your order is done or can't be grown, continue with the other populations, closest to their bonus first.");

            GUILayout.Space(8f);
            GUILayout.Label("Keep every city at least", headingStyle);
            GUILayout.BeginHorizontal();
            foreach (string level in ApprovalRule.Levels)
            {
                bool selected = Plugin.MinimumApproval.Value == level;
                string label = level == ApprovalRule.Off ? "Off" : $"{level} ({ApprovalRule.Minimum(controller.State, level):0}+)";
                if (GUILayout.Button(selected ? "● " + label : label) && !selected)
                {
                    string chosen = level;
                    Defer(() => Plugin.MinimumApproval.Value = chosen);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Below it (plus the buffer), a city grows the population adding the most approval there (e.g. Xavius next to other types, "
                + "Noquensii as Scribes), its jobs put approval first (e.g. Last Lords and Hydracorns out of Citizens, populations into free Scribe slots, "
                + "which cost no approval), and no job swap may take it back under. Cities can override the level in the Cities tab.", mutedStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Buffer: start this many points above the level (a new Citizen or Artisan costs about 3)", textStyle, GUILayout.Width(420f));
            if (GUILayout.Button("-", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.ApprovalBuffer.Value = Math.Max(0, Plugin.ApprovalBuffer.Value - 1));
            }
            GUILayout.Label($"{Plugin.ApprovalBuffer.Value}", textStyle, GUILayout.Width(70f));
            if (GUILayout.Button("+", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.ApprovalBuffer.Value = Math.Min(30, Plugin.ApprovalBuffer.Value + 1));
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            Toggle(Plugin.OptimizeJobs, "Optimize jobs",
                "Swaps populations between jobs so their job effects apply: Daughter of Bor as artisans, Xavius next to other population types, "
                + "Sollusk kept together, and so on (read from the game data). How many work each job stays as your city's job strategy set it. "
                + "A city where you drag populations between jobs yourself is left alone until next turn.");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Smallest gain worth a swap (yields weighted by the city's job strategy)", textStyle, GUILayout.Width(420f));
            if (GUILayout.Button("-", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.JobMinGain.Value = Mathf.Max(0.5f, Plugin.JobMinGain.Value - 0.5f));
            }
            GUILayout.Label($"{Plugin.JobMinGain.Value:0.#}", textStyle, GUILayout.Width(70f));
            if (GUILayout.Button("+", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.JobMinGain.Value = Mathf.Min(20f, Plugin.JobMinGain.Value + 0.5f));
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("When I pick a city's next population myself", headingStyle);
            GUILayout.BeginHorizontal();
            ManualPickButton(ManualPickMode.UntilCityGrows, "Leave it until it grows");
            ManualPickButton(ManualPickMode.UntilResumed, "Leave it until I resume");
            ManualPickButton(ManualPickMode.Ignore, "Override it");
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("If the game can't add a population to a city, avoid it there for", textStyle, GUILayout.Width(420f));
            if (GUILayout.Button("-", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.FailedGrowthCooldown.Value = Math.Max(1, Plugin.FailedGrowthCooldown.Value - 1));
            }
            GUILayout.Label($"{Plugin.FailedGrowthCooldown.Value} turns", textStyle, GUILayout.Width(70f));
            if (GUILayout.Button("+", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.FailedGrowthCooldown.Value = Math.Min(100, Plugin.FailedGrowthCooldown.Value + 1));
            }
            GUILayout.EndHorizontal();

            if (!MenuInstalled)
            {
                DrawWindowOptions();
            }

            Toggle(Plugin.LogDecisions, "Log every change to BepInEx/LogOutput.log", null);

            GUILayout.Space(10f);
            GUILayout.Label(MenuInstalled
                ? "Shown as the Population page of Mod Menu, whose key, size and opacity apply."
                : $"Window key: {key} (change ToggleKey in BepInEx/config/{Plugin.Guid}.cfg).", mutedStyle);
            GUILayout.Label("Your order and city choices are saved per game in BepInEx/config/PopulationPlanner/.", mutedStyle);
            GUILayout.Label($"Population Planner {Plugin.Version}", mutedStyle);
        }

        // Only for this mod's own window: inside Mod Menu, the menu's size and opacity apply.
        private void DrawWindowOptions()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Window size", textStyle, GUILayout.Width(340f));
            float size = Scale;
            if (GUILayout.Button("-", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.UiScale.Value = Mathf.Max(0.5f, size - 0.25f));
            }
            GUILayout.Label(AutoScale ? $"auto ({size:0.##}x)" : $"{size:0.##}x", textStyle, GUILayout.Width(100f));
            if (GUILayout.Button("+", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.UiScale.Value = Mathf.Min(4f, size + 0.25f));
            }
            GUI.enabled = !AutoScale;
            if (GUILayout.Button("Auto", GUILayout.Width(52f)))
            {
                Defer(() => Plugin.UiScale.Value = 0f);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Window opacity", textStyle, GUILayout.Width(420f));
            if (GUILayout.Button("-", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.Opacity.Value = Mathf.Max(0.3f, Mathf.Round((Plugin.Opacity.Value - 0.05f) * 20f) / 20f));
            }
            GUILayout.Label($"{Plugin.Opacity.Value * 100f:0}%", textStyle, GUILayout.Width(70f));
            if (GUILayout.Button("+", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.Opacity.Value = Mathf.Min(1f, Mathf.Round((Plugin.Opacity.Value + 0.05f) * 20f) / 20f));
            }
            GUILayout.EndHorizontal();
        }

        private void Toggle(BepInEx.Configuration.ConfigEntry<bool> entry, string label, string help)
        {
            bool value = GUILayout.Toggle(entry.Value, " " + label);
            if (value != entry.Value)
            {
                Defer(() => entry.Value = value);
            }
            if (!string.IsNullOrEmpty(help))
            {
                GUILayout.Label(help, mutedStyle);
            }
        }

        private void ManualPickButton(ManualPickMode mode, string label)
        {
            bool selected = Plugin.ManualPicks.Value == mode;
            if (GUILayout.Button(selected ? "● " + label : label) && !selected)
            {
                Defer(() => Plugin.ManualPicks.Value = mode);
            }
        }

        // ---- helpers ----

        private void Defer(Action action) => deferred.Add(action);

        // The game's level definition -> the name the game shows.
        private static string LevelName(string definition)
        {
            switch (definition)
            {
                case "SettlementApproval_Unhappy": return "(Mutinous)";
                case "SettlementApproval_Neutral": return "(Content)";
                case "SettlementApproval_Happy": return "(Happy)";
                case "SettlementApproval_VeryHappy": return "(Jubilant)";
                default: return string.Empty;
            }
        }

        // Per-city minimum: all (global) -> Off -> Content -> Happy -> Jubilant -> all.
        private static string NextLevel(string current)
        {
            if (current == null)
            {
                return ApprovalRule.Off;
            }
            int index = Array.IndexOf(ApprovalRule.Levels, ApprovalRule.Normalize(current));
            return index + 1 < ApprovalRule.Levels.Length ? ApprovalRule.Levels[index + 1] : null;
        }

        private bool GrowableAnywhere(GameState state, string pop)
        {
            foreach (CityState city in state.Cities)
            {
                if (city.CanGrow(pop) && !controller.IsBlocked(city.Guid, pop))
                {
                    return true;
                }
            }
            return false;
        }

        // "18/30  ●●○"
        private static string Progress(PopType type) => Progress(type.Count, type.Level, type.Thresholds);

        private static string Progress(int count, int level, int[] thresholds)
        {
            if (thresholds.Length == 0)
            {
                return count.ToString();
            }
            var dots = new char[thresholds.Length];
            for (int i = 0; i < thresholds.Length; i++)
            {
                dots[i] = i < level ? '●' : '○';
            }
            return $"{count}/{thresholds[thresholds.Length - 1]}  {new string(dots)}";
        }

        private bool IsMouseInside()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return false;
            }
            Vector2 position = mouse.position.ReadValue();
            float scale = Scale;
            var gui = new Vector2(position.x / scale, (Screen.height - position.y) / scale);
            return rect.Contains(gui);
        }

        private void ReadKey()
        {
            string name = Plugin.ToggleKey.Value;
            if (name == keyName)
            {
                return;
            }
            keyName = name;
            if (!Enum.TryParse(name?.Trim(), true, out Key parsed) || parsed == Key.None)
            {
                Plugin.Log.LogWarning($"Unknown ToggleKey '{name}', using F7.");
                parsed = Key.F7;
            }
            key = parsed;
        }

        private void EnsureStyles()
        {
            if (stylesReady)
            {
                return;
            }
            stylesReady = true;
            textStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            mutedStyle = new GUIStyle(textStyle);
            mutedStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            goodStyle = new GUIStyle(textStyle);
            goodStyle.normal.textColor = new Color(0.55f, 0.9f, 0.55f);
            warnStyle = new GUIStyle(textStyle);
            warnStyle.normal.textColor = new Color(1f, 0.8f, 0.35f);
            badStyle = new GUIStyle(textStyle);
            badStyle.normal.textColor = new Color(1f, 0.45f, 0.4f);
            headingStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(6, 6, 4, 4), margin = new RectOffset(0, 0, 2, 2), border = new RectOffset(0, 0, 0, 0) };
            hintStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 14, wordWrap = true, border = new RectOffset(0, 0, 0, 0) };
            // Every state: a style copied from the skin keeps its dark text for hover etc., which would turn it black.
            SetTextColor(hintStyle, Color.white);
            nameButtonStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            nameButtonStyle.hover.textColor = nameButtonStyle.active.textColor = new Color(0.95f, 0.85f, 0.6f);
            windowStyle = new GUIStyle(GUI.skin.window) { border = new RectOffset(0, 0, 0, 0) };
            SetTextColor(windowStyle, new Color(0.95f, 0.85f, 0.6f));
        }

        private void RefreshBackgrounds()
        {
            float opacity = Mathf.Clamp(Plugin.Opacity.Value, 0.3f, 1f);
            if (Mathf.Abs(opacity - backgroundOpacity) < 0.001f && windowBackground != null)
            {
                return;
            }
            backgroundOpacity = opacity;
            Replace(ref windowBackground, new Color(0.08f, 0.09f, 0.11f, opacity));
            Replace(ref rowBackground, new Color(0.15f, 0.16f, 0.19f, opacity));
            SetBackground(windowStyle, windowBackground);
            SetBackground(boxStyle, rowBackground);
            SetBackground(hintStyle, windowBackground);
        }

        private static void Replace(ref Texture2D texture, Color color)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }
            texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
        }

        private static void SetTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.onNormal.textColor = color;
            style.hover.textColor = color;
            style.onHover.textColor = color;
            style.active.textColor = color;
            style.onActive.textColor = color;
            style.focused.textColor = color;
            style.onFocused.textColor = color;
        }

        private static void SetBackground(GUIStyle style, Texture2D texture)
        {
            style.normal.background = texture;
            style.onNormal.background = texture;
            style.focused.background = texture;
            style.onFocused.background = texture;
            style.hover.background = texture;
            style.onHover.background = texture;
            style.active.background = texture;
            style.onActive.background = texture;
        }
    }
}
