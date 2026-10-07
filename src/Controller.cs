using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Sandbox;
using Amplitude.Mercury.Simulation;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace PopulationPlanner
{
    // Main thread: turns the latest GameState into a plan and the plan into the game's own "select growing
    // population" orders, and keeps the player's choices for the current game.
    internal sealed class Controller
    {
        internal static Controller Instance;

        // How long to wait for the game to apply an order before sending it again, and how many times.
        private const float OrderRetrySeconds = 3f;
        private const int MaxOrderAttempts = 3;

        private sealed class PendingOrder
        {
            public string Pop;
            public float Time;
            public int Attempts;
        }


        private sealed class JobPlan
        {
            public JobSwap Swap;
            public int Step = 1;
            public float Time;
            public int Attempts;
        }

        private readonly Dictionary<ulong, PendingOrder> pending = new Dictionary<ulong, PendingOrder>();
        // Job swap in progress per city, populations moved this turn, and cities the player rearranged by hand.
        private readonly Dictionary<ulong, JobPlan> jobPlans = new Dictionary<ulong, JobPlan>();
        private readonly HashSet<ulong> frozenPops = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> swapsThisTurn = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> jobsHeld = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> lastJobChange = new Dictionary<ulong, string>();
        // Why populations still miss a job bonus of their own when no swap was found (shown in the Cities tab).
        private readonly Dictionary<ulong, string> jobNotes = new Dictionary<ulong, string>();
        // Each city's note (with who works where) and level choice last logged: logged again only when they change, not
        // every turn.
        private readonly Dictionary<ulong, string> loggedJobNotes = new Dictionary<ulong, string>();
        // Each city's approval level choice and what it was worked out from.
        private readonly Dictionary<ulong, ((int, int, long, int, string) Key, LevelChoice Choice)> levelChoices = new Dictionary<ulong, ((int, int, long, int, string) Key, LevelChoice Choice)>();
        private readonly Dictionary<ulong, string> loggedLevelNotes = new Dictionary<ulong, string>();
        private readonly HashSet<string> loggedFailures = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, long> jobCheckedAt = new Dictionary<ulong, long>();
        // Approval and food when the mod first changed a city's jobs this turn, and what its changes added since: the
        // game may only update those values at the end of the turn, and the mod must not count the same gain twice.
        private readonly Dictionary<ulong, float[]> jobTurnBase = new Dictionary<ulong, float[]>();
        private readonly Dictionary<ulong, float[]> jobTurnDelta = new Dictionary<ulong, float[]>();
        private int jobTurn = -1;
        // Each city's job strategy weights when last seen: a change means the game has just placed its populations again,
        // unless the game reset the strategy on its own (city -> turn), which places nobody; and the note shown for it.
        private readonly Dictionary<ulong, float[]> strategyWeights = new Dictionary<ulong, float[]>();
        private int strategyCheckedVersion = -1;
        private readonly Dictionary<ulong, int> strategyResets = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, string> strategyNotes = new Dictionary<ulong, string>();
        // Strategies to set back after the game reset them, and the order sent for each (Pop = the strategy).
        private readonly Dictionary<ulong, PendingOrder> strategyRestores = new Dictionary<ulong, PendingOrder>();
        // Each city's jobs when last seen this turn, and the slot and yield layouts already seen this turn: when a city's
        // slots or yields per population change during the turn (a construction finished, e.g. bought out), the mod
        // sorts out its jobs again. A layout seen before this turn doesn't count twice, so this always settles.
        private readonly Dictionary<ulong, (JobState Jobs, string Layout)> layouts = new Dictionary<ulong, (JobState Jobs, string Layout)>();
        private readonly HashSet<string> layoutsSeen = new HashSet<string>(StringComparer.Ordinal);
        private int layoutTurn = -1;
        private int layoutCheckedVersion = -1;
        // "city|pop" -> first turn that population may be picked again in that city.
        private readonly Dictionary<string, int> blockedUntil = new Dictionary<string, int>(StringComparer.Ordinal);
        private string gameId;
        private object sandbox;
        private int plannedVersion = -1;
        private int plannedRevision = -1;
        private float saveAt = -1f;
        private bool summaryLogged;

        public Controller()
        {
            Instance = this;
        }

        public GameState State { get; private set; }

        public GameSettings Settings { get; private set; }

        public PlanResult Plan { get; private set; }

        // First time the mod sees this game: the window shows a hint.
        public bool IsNewGame { get; private set; }

        // Bumped on every change of the player's choices or options; the mod plans again when it or the state changes.
        public int Revision { get; private set; }

        public bool InGame => State != null && Settings != null && Plan != null;

        public void Update()
        {
            GameState state = StateCapture.Latest;
            Sandbox current = SandboxManager.Sandbox;
            if (current == null)
            {
                if (gameId != null)
                {
                    CloseGame();
                }
                return;
            }
            if (state == null || state.GameId != SandboxManager.GameID)
            {
                return;
            }
            if (state.GameId != gameId || !ReferenceEquals(current, sandbox))
            {
                OpenGame(state.GameId, current);
            }
            if (!ReferenceEquals(state, State))
            {
                State = state;
                foreach (CityState city in state.Cities)
                {
                    Names.City(city);
                }
                if (!summaryLogged)
                {
                    summaryLogged = true;
                    LogSummary();
                }
            }
            DrainFailures();
            DrainStrategyResets();
            ReleaseHolds();
            if (State.Turn != jobTurn)
            {
                StartJobTurn();
            }
            NoticeStrategyChanges();
            NoticeLayoutChanges();
            if (State.Version != plannedVersion || Revision != plannedRevision)
            {
                Replan();
            }
            if (Plugin.Automation.Value && State.CanAct && State.IsHuman)
            {
                RestoreStrategies();
                ApplyOrders();
                if (Plugin.OptimizeJobs.Value)
                {
                    OptimizeJobs();
                }
            }
            if (saveAt >= 0f && Time.unscaledTime >= saveAt)
            {
                SaveNow();
            }
        }

        public bool IsBlocked(ulong city, string pop)
        {
            return State != null && blockedUntil.TryGetValue(Key(city, pop), out int until) && State.Turn < until;
        }

        public int BlockedUntil(ulong city, string pop) => blockedUntil.TryGetValue(Key(city, pop), out int until) ? until : 0;

        // Populations currently blocked in a city, for the window.
        public IEnumerable<string> BlockedIn(ulong city)
        {
            string prefix = city.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|";
            foreach (KeyValuePair<string, int> pair in blockedUntil)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && State != null && State.Turn < pair.Value)
                {
                    yield return pair.Key.Substring(prefix.Length);
                }
            }
        }

        // ---- choices made in the window ----

        public void AimNext(string pop) => Reorder(pop, int.MinValue);

        public void MoveUp(string pop) => Reorder(pop, -1);

        public void MoveDown(string pop) => Reorder(pop, +1);

        public void ResetOrder()
        {
            Settings.Priority.Clear();
            Touch();
        }

        public void ToggleSkip(string pop)
        {
            if (!Settings.Skipped.Remove(pop))
            {
                Settings.Skipped.Add(pop);
            }
            Touch();
        }

        public void ToggleSpread(string pop)
        {
            if (!Settings.NoSpread.Remove(pop))
            {
                Settings.NoSpread.Add(pop);
            }
            Touch();
        }

        public void SetCityOff(ulong city, bool off)
        {
            Settings.GetOrAddCity(city).Off = off;
            pending.Remove(city);
            Touch();
        }

        public void SetCityTarget(ulong city, string pop)
        {
            Settings.GetOrAddCity(city).Target = string.IsNullOrEmpty(pop) ? null : pop;
            Touch();
        }

        public void Resume(ulong city)
        {
            CitySettings settings = Settings.GetCity(city);
            if (settings != null)
            {
                Release(settings);
                Touch();
            }
        }

        public void Unblock(ulong city)
        {
            string prefix = city.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|";
            foreach (string key in blockedUntil.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                blockedUntil.Remove(key);
            }
            Touch();
        }

        // Called from the city screen patch when the player picks a city's next population by hand.
        public void OnManualPick(ulong city, string pop)
        {
            if (Settings == null || Plugin.ManualPicks.Value == ManualPickMode.Ignore)
            {
                return;
            }
            CitySettings settings = Settings.GetOrAddCity(city);
            if (settings.Off)
            {
                return;
            }
            CityState state = State?.FindCity(city);
            settings.HeldPop = pop;
            settings.HeldAtGained = state?.TotalGained ?? -1;
            settings.HeldUntilResumed = Plugin.ManualPicks.Value == ManualPickMode.UntilResumed;
            pending.Remove(city);
            Touch();
            Plugin.Log.LogInfo($"{CityName(city)}: you picked {Names.Pop(pop)}; leaving this city alone "
                + (settings.HeldUntilResumed ? "until you resume it in the window." : "until it grows."));
        }

        public void ConfigChanged() => Touch(save: false);

        // Approval below which the city puts approval first (its level's minimum + buffer); NaN = no minimum. The level is
        // the city's minimum, or with "only when it pays" the highest level up to it that is worth its job moves.
        public float ApprovalTarget(CityState city)
        {
            if (Settings == null || State == null)
            {
                return float.NaN;
            }
            float minimum = ApprovalRule.Minimum(State, LevelChoiceOf(city).Level);
            return float.IsNaN(minimum) ? float.NaN : minimum + Math.Max(0f, Plugin.ApprovalBuffer.Value);
        }

        // Growth puts approval first below the city's minimum as configured: growing a population that adds approval costs
        // no yields, so "only when it pays" (which weighs job moves) leaves it alone.
        public float GrowthApprovalTarget(CityState city)
        {
            return Settings == null || State == null
                ? float.NaN
                : ApprovalRule.Target(State, Settings, city, Plugin.MinimumApproval.Value, Plugin.ApprovalBuffer.Value);
        }

        // The level the city's jobs are held at, worked out again whenever the state, the options or its approval change.
        public LevelChoice LevelChoiceOf(CityState city)
        {
            string wanted = Settings?.GetCity(city.Guid)?.MinApproval ?? Plugin.MinimumApproval.Value;
            if (!Plugin.ApprovalOnlyWhenItPays.Value || State == null)
            {
                return new LevelChoice { Level = ApprovalRule.Normalize(wanted) };
            }
            float approval = ApprovalOf(city);
            var key = (State.Version, Revision, (long)Math.Round(approval * 10f), frozenPops.Count, wanted);
            if (levelChoices.TryGetValue(city.Guid, out var cached) && cached.Key.Equals(key))
            {
                return cached.Choice;
            }
            LevelChoice choice = ApprovalMath.Choose(State, city, wanted, Plugin.ApprovalBuffer.Value, approval, FoodOf(city),
                Mathf.Max(0.1f, Plugin.JobMinGain.Value), frozenPops.Contains);
            levelChoices[city.Guid] = (key, choice);
            // Logged when the decision changes (the level held and why), not again every turn.
            string explained = ApprovalMath.Explain(choice);
            string decision = explained == null ? null : $"{choice.Skipped}:{choice.Level}:{choice.MovesDelta == null}";
            loggedLevelNotes.TryGetValue(city.Guid, out string logged);
            if (decision != logged)
            {
                if (decision == null)
                {
                    loggedLevelNotes.Remove(city.Guid);
                }
                else
                {
                    loggedLevelNotes[city.Guid] = decision;
                }
                if (Plugin.LogDecisions.Value)
                {
                    Plugin.Log.LogInfo($"Turn {State.Turn}: {Names.City(city)}: "
                        + (explained ?? $"job moves keep {ApprovalRule.Normalize(wanted)} again") + ".");
                }
            }
            return choice;
        }

        public bool ApprovalFirst(CityState city)
        {
            float target = ApprovalTarget(city);
            return !float.IsNaN(target) && ApprovalOf(city) < target;
        }

        // The city's approval counting this turn's job changes, whether or not the game has applied them yet.
        public float ApprovalOf(CityState city)
        {
            return jobTurnBase.TryGetValue(city.Guid, out float[] start)
                ? Math.Max(city.ApprovalNet, start[Yield.Approval] + jobTurnDelta[city.Guid][Yield.Approval])
                : city.ApprovalNet;
        }

        // The city's food surplus the same way; the lower of the two, as losses must never be missed.
        public float FoodOf(CityState city)
        {
            return jobTurnBase.TryGetValue(city.Guid, out float[] start)
                ? Math.Min(city.FoodNet, start[Yield.Food] + jobTurnDelta[city.Guid][Yield.Food])
                : city.FoodNet;
        }

        // Puts the city's populations back where its job strategy wants them (the game's own optimization, as when the
        // strategy is changed), and leaves its jobs alone for the rest of the turn.
        public void ResetJobs(ulong city)
        {
            SandboxManager.PostOrder(new OrderOptimizePopulationAssignement { SettlementGUID = city });
            StateCapture.RequestRefresh();
            jobsHeld.Add(city);
            jobPlans.Remove(city);
            CityState state = State?.FindCity(city);
            Plugin.Log.LogInfo($"{CityName(city)}: jobs reset to the city's job strategy; leaving them until next turn.");
            if (state != null)
            {
                lastJobChange[city] = "reset to the city's job strategy";
            }
        }

        // null = follow the global setting.
        public void SetCityMinApproval(ulong city, string level)
        {
            Settings.GetOrAddCity(city).MinApproval = level == null ? null : ApprovalRule.Normalize(level);
            Touch();
        }

        public void SetCityJobsOff(ulong city, bool off)
        {
            Settings.GetOrAddCity(city).JobsOff = off;
            jobPlans.Remove(city);
            Touch();
        }

        // Called from the population window patch when the player drags populations to another job.
        public void OnManualJobMove(ulong targetJob)
        {
            CityState city = State?.Cities.FirstOrDefault(c => c.Jobs?.Find(targetJob) != null);
            if (city == null || !jobsHeld.Add(city.Guid))
            {
                return;
            }
            jobPlans.Remove(city.Guid);
            Plugin.Log.LogInfo($"{Names.City(city)}: you moved populations between jobs; leaving its jobs alone until next turn.");
        }

        public bool IsJobsHeld(ulong city) => jobsHeld.Contains(city);

        public bool IsSwapping(ulong city) => jobPlans.ContainsKey(city);

        public int SwapsThisTurn(ulong city) => swapsThisTurn.TryGetValue(city, out int n) ? n : 0;

        // The player's optional cap on job changes per city per turn (0 = none) is used up. Without it, a city still
        // settles: each population moves at most once a turn.
        public bool JobLimitReached(ulong city)
        {
            int max = Plugin.MaxJobChanges.Value;
            return max > 0 && SwapsThisTurn(city) >= max;
        }

        public string LastJobChange(ulong city) => lastJobChange.TryGetValue(city, out string text) ? text : null;

        public string JobNote(ulong city) => jobNotes.TryGetValue(city, out string text) ? text : null;

        public string StrategyNote(ulong city) => strategyNotes.TryGetValue(city, out string text) ? text : null;

        public string JobName(CityState city, ulong job)
        {
            JobCategory category = city.Jobs?.Find(job);
            return category == null ? "?" : Names.Job(category.Name);
        }

        public void SaveNow()
        {
            saveAt = -1f;
            if (Settings == null || string.IsNullOrEmpty(gameId))
            {
                return;
            }
            try
            {
                foreach (string key in Settings.Cities.Where(pair => pair.Value == null || pair.Value.IsEmpty()).Select(pair => pair.Key).ToList())
                {
                    Settings.Cities.Remove(key);
                }
                string path = PathFor(gameId);
                if (Settings.Priority.Count == 0 && Settings.Skipped.Count == 0 && Settings.NoSpread.Count == 0 && Settings.Cities.Count == 0 && !File.Exists(path))
                {
                    // Nothing chosen (e.g. the main menu's background game): no file to leave behind.
                    return;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(Settings, Formatting.Indented));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not save the settings of game {gameId}: {e.Message}");
            }
        }

        // ---- internals ----

        private void OpenGame(string id, Sandbox current)
        {
            if (id != gameId)
            {
                SaveNow();
                Settings = Load(id, out bool isNew);
                IsNewGame = isNew;
                gameId = id;
                Plugin.Log.LogInfo($"Game {id}: " + (isNew ? "no saved choices yet, using the automatic order." : "choices loaded."));
            }
            sandbox = current;
            pending.Clear();
            blockedUntil.Clear();
            jobTurn = -1;
            strategyWeights.Clear();
            strategyCheckedVersion = -1;
            strategyResets.Clear();
            strategyNotes.Clear();
            strategyRestores.Clear();
            while (StrategyResetPatch.Resets.TryDequeue(out _))
            {
            }
            layouts.Clear();
            layoutsSeen.Clear();
            layoutTurn = -1;
            layoutCheckedVersion = -1;
            loggedJobNotes.Clear();
            loggedLevelNotes.Clear();
            levelChoices.Clear();
            plannedVersion = -1;
            summaryLogged = false;
            Names.ClearCache();
            Touch(save: false);
        }

        // What the mod sees when a game starts, for the log.
        private void LogSummary()
        {
            var lines = new List<string>();
            foreach (PopType type in State.Types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                string flags = (type.ActionOnly ? " action-only" : string.Empty)
                    + (type.PresenceMatters ? " last-bonus-needs-presence" : string.Empty)
                    + (type.FactionPop ? " faction" : string.Empty)
                    + (type.AvailableToEmpire ? string.Empty : " not-available");
                lines.Add($"  {Names.Pop(type.Name)} ({type.Name}): {type.Count} pops, bonuses {type.Level}/{type.MaxLevel} at [{string.Join(", ", type.Thresholds.Select(t => t.ToString()).ToArray())}], in {type.CitiesWith} cities{flags}");
            }
            Plugin.Log.LogInfo($"Turn {State.Turn}: {State.Cities.Count} cities, can act: {State.CanAct}, human: {State.IsHuman}, grows with food: {!State.CannotGrowWithFood}."
                + Environment.NewLine + string.Join(Environment.NewLine, lines.ToArray()));
        }

        private void CloseGame()
        {
            SaveNow();
            gameId = null;
            sandbox = null;
            State = null;
            Settings = null;
            Plan = null;
            pending.Clear();
            blockedUntil.Clear();
        }

        private void Replan()
        {
            var options = new PlanOptions
            {
                SpreadFirst = Plugin.SpreadFirst.Value,
                ContinueAfterList = Plugin.ContinueAfterList.Value,
                IsBlocked = IsBlocked,
                ApprovalTarget = GrowthApprovalTarget,
                ApprovalOf = ApprovalOf,
            };
            Plan = Planner.Plan(State, Settings, options);
            plannedVersion = State.Version;
            plannedRevision = Revision;
        }

        private void ApplyOrders()
        {
            float now = Time.unscaledTime;
            foreach (CityPlan plan in Plan.Cities.Values)
            {
                if (plan.Pop == null)
                {
                    continue;
                }
                CityState city = State.FindCity(plan.Guid);
                if (city == null)
                {
                    continue;
                }
                if (city.Growing == plan.Pop)
                {
                    pending.Remove(plan.Guid);
                    continue;
                }
                if (pending.TryGetValue(plan.Guid, out PendingOrder order) && order.Pop == plan.Pop)
                {
                    if (now - order.Time < OrderRetrySeconds)
                    {
                        continue;
                    }
                    if (order.Attempts >= MaxOrderAttempts)
                    {
                        // The game keeps refusing this pick: leave this population out in this city for the turn.
                        pending.Remove(plan.Guid);
                        Block(plan.Guid, plan.Pop, State.Turn + 1);
                        Plugin.Log.LogWarning($"Turn {State.Turn}: the game refused {Names.Pop(plan.Pop)} for {Names.City(city)} {order.Attempts} times; trying something else there this turn.");
                        continue;
                    }
                }
                else
                {
                    order = new PendingOrder { Pop = plan.Pop };
                }
                order.Time = now;
                order.Attempts++;
                pending[plan.Guid] = order;
                SandboxManager.PostOrder(new OrderSelectGrowingPopulation(plan.Guid, new StaticString(plan.Pop)));
                StateCapture.RequestRefresh();
                if (Plugin.LogDecisions.Value)
                {
                    string why = Describe(plan.Kind) + (plan.Kind == PlanKind.Approval
                        ? ", about " + plan.ApprovalGain.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture) + " approval"
                        : string.Empty);
                    Plugin.Log.LogInfo($"Turn {State.Turn}: {Names.City(city)} now grows {Names.Pop(plan.Pop)} (was {Names.Pop(city.Growing)}; {why}).");
                }
            }
        }

        // ---- jobs ----

        private void StartJobTurn()
        {
            jobTurn = State.Turn;
            frozenPops.Clear();
            swapsThisTurn.Clear();
            jobsHeld.Clear();
            jobPlans.Clear();
            lastJobChange.Clear();
            jobNotes.Clear();
            loggedFailures.Clear();
            jobCheckedAt.Clear();
            jobTurnBase.Clear();
            jobTurnDelta.Clear();
        }

        // The player changed a city's job strategy: the game has just placed all its populations again by its own rules, so
        // this turn's job moves there no longer stand. The mod starts over in that city with the new strategy's weights,
        // instead of leaving the populations it already moved this turn wherever the game put them until next turn.
        // When the game reset the strategy on its own, nobody was placed again: only the weights the mod uses changed.
        private void NoticeStrategyChanges()
        {
            if (strategyCheckedVersion == State.Version)
            {
                return;
            }
            strategyCheckedVersion = State.Version;
            foreach (CityState city in State.Cities)
            {
                float[] weights = city.Jobs?.Weights;
                if (weights == null)
                {
                    continue;
                }
                bool seen = strategyWeights.TryGetValue(city.Guid, out float[] before);
                strategyWeights[city.Guid] = (float[])weights.Clone();
                if (!seen || SameWeights(before, weights))
                {
                    continue;
                }
                if (strategyResets.TryGetValue(city.Guid, out int resetTurn) && resetTurn >= State.Turn - 1)
                {
                    // Only the weights changed: the mod's moves stand, but its populations may be weighed again.
                    strategyResets.Remove(city.Guid);
                    foreach (JobCategory job in city.Jobs.Categories)
                    {
                        foreach (JobPop pop in job.Pops)
                        {
                            frozenPops.Remove(pop.Guid);
                        }
                    }
                    jobCheckedAt.Remove(city.Guid);
                    jobNotes.Remove(city.Guid);
                    continue;
                }
                StartCityOver(city);
                strategyNotes.Remove(city.Guid);
                bool restored = strategyRestores.TryGetValue(city.Guid, out PendingOrder restore) && restore.Pop == city.Jobs.Strategy;
                // Set back by the mod, or picked by the player (whose choice then wins over a pending restore).
                strategyRestores.Remove(city.Guid);
                if (restored)
                {
                    lastJobChange[city.Guid] = $"job strategy set back to {Names.Strategy(city.Jobs.Strategy)} after the game reset it";
                }
                if (Plugin.LogDecisions.Value)
                {
                    Plugin.Log.LogInfo($"Turn {State.Turn}: {Names.City(city)}: "
                        + (restored ? $"job strategy set back to {Names.Strategy(city.Jobs.Strategy)}" : "job strategy changed")
                        + " and the game placed its populations again; optimizing them for it.");
                }
            }
        }

        // The game placed all of a city's populations again (a strategy picked, or set back): this turn's job moves there
        // no longer stand, and the mod starts over in that city.
        private void StartCityOver(CityState city)
        {
            foreach (JobCategory job in city.Jobs.Categories)
            {
                foreach (JobPop pop in job.Pops)
                {
                    frozenPops.Remove(pop.Guid);
                }
            }
            swapsThisTurn.Remove(city.Guid);
            jobPlans.Remove(city.Guid);
            jobCheckedAt.Remove(city.Guid);
            jobTurnBase.Remove(city.Guid);
            jobTurnDelta.Remove(city.Guid);
            lastJobChange.Remove(city.Guid);
            jobNotes.Remove(city.Guid);
        }

        // Sets back the strategies the game reset, with the game's own order (the one the city screen sends), so the game
        // places the city's populations again as when the player picks it. Not in a city the player turned off (or its
        // jobs), and not while the player's own job moves there hold (next turn then). The game refuses the order if
        // the strategy is no longer allowed for the empire.
        private void RestoreStrategies()
        {
            if (strategyRestores.Count == 0)
            {
                return;
            }
            float now = Time.unscaledTime;
            foreach (KeyValuePair<ulong, PendingOrder> pair in strategyRestores.ToList())
            {
                CityState city = State.FindCity(pair.Key);
                CitySettings settings = Settings.GetCity(pair.Key);
                if (city?.Jobs == null || (settings != null && (settings.Off || settings.JobsOff)) || !Plugin.RestoreStrategies.Value)
                {
                    // Switched off since: the player picks it again.
                    strategyRestores.Remove(pair.Key);
                    if (city?.Jobs != null && city.Jobs.Strategy != pair.Value.Pop)
                    {
                        strategyNotes[pair.Key] = $"The game reset this city's job strategy from {Names.Strategy(pair.Value.Pop)} to {Names.Strategy(city.Jobs.Strategy)}: "
                            + "pick it again in the city screen if you want it back.";
                    }
                    continue;
                }
                PendingOrder order = pair.Value;
                if (city.Jobs.Strategy == order.Pop)
                {
                    // Not reset yet in this state (taken before the reset), or back already: NoticeStrategyChanges
                    // usually sees it come back; this only ends what it can't see (a change that came and went between two
                    // states), once a state taken since then would have shown it.
                    if (now - order.Time >= (order.Attempts == 0 ? 10f : OrderRetrySeconds))
                    {
                        strategyRestores.Remove(pair.Key);
                        strategyNotes.Remove(pair.Key);
                        strategyResets.Remove(pair.Key);
                    }
                    continue;
                }
                if (jobsHeld.Contains(pair.Key) || (order.Attempts > 0 && now - order.Time < OrderRetrySeconds))
                {
                    continue;
                }
                if (order.Attempts >= MaxOrderAttempts)
                {
                    strategyRestores.Remove(pair.Key);
                    strategyNotes[pair.Key] = $"The game reset this city's job strategy to {Names.Strategy(city.Jobs.Strategy)} and refused to take {Names.Strategy(order.Pop)} back: pick a strategy in the city screen.";
                    Plugin.Log.LogWarning($"Turn {State.Turn}: {Names.City(city)}: the game did not take back {Names.Strategy(order.Pop)} as its job strategy; pick it again in the city screen.");
                    continue;
                }
                order.Attempts++;
                order.Time = now;
                SandboxManager.PostOrder(new OrderChangePopulationAssignementStrategy(pair.Key, new StaticString(order.Pop)));
                StateCapture.RequestRefresh();
                // The game places the city's populations again right away, whether or not the next state shows the reset.
                StartCityOver(city);
            }
        }

        // A city's job slots or yields per population changed during the turn: a construction finished (e.g. bought out;
        // Communal Habitations adds a slot to each job), an improvement, a governor... Who should work where may be
        // different now, so the mod sorts out that city's jobs again in the same turn: populations it already moved may
        // move again. Its moves this turn still stand, so their approval and food stay counted.
        private void NoticeLayoutChanges()
        {
            if (layoutCheckedVersion == State.Version)
            {
                return;
            }
            layoutCheckedVersion = State.Version;
            if (layoutTurn != State.Turn)
            {
                // A new turn starts over anyway.
                layoutTurn = State.Turn;
                layouts.Clear();
                layoutsSeen.Clear();
            }
            foreach (CityState city in State.Cities)
            {
                if (city.Jobs == null)
                {
                    continue;
                }
                string layout = JobOptimizer.Layout(city.Jobs);
                bool seen = layouts.TryGetValue(city.Guid, out var before);
                layouts[city.Guid] = (city.Jobs, layout);
                if (!layoutsSeen.Add(city.Guid + "|" + layout) || !seen || before.Layout == layout)
                {
                    continue;
                }
                foreach (JobCategory job in city.Jobs.Categories)
                {
                    foreach (JobPop pop in job.Pops)
                    {
                        frozenPops.Remove(pop.Guid);
                    }
                }
                jobCheckedAt.Remove(city.Guid);
                jobNotes.Remove(city.Guid);
                CitySettings settings = Settings.GetCity(city.Guid);
                bool managed = Plugin.Automation.Value && Plugin.OptimizeJobs.Value && State.CanAct && !jobsHeld.Contains(city.Guid)
                    && (settings == null || (!settings.Off && !settings.JobsOff));
                if (managed && Plugin.LogDecisions.Value)
                {
                    Plugin.Log.LogInfo($"Turn {State.Turn}: {Names.City(city)}: its jobs changed ({JobOptimizer.LayoutChanges(before.Jobs, city.Jobs, Names.Job)}); sorting them out again.");
                }
            }
        }

        // No swap found: say which populations still miss a job bonus of their own, and why (Cities tab; the log when the
        // text or who works where changes, so a placement can be checked from the log alone).
        private void NoteMissedBonuses(CityState city)
        {
            List<MissedBonus> missed = JobOptimizer.MissedBonuses(State, city, frozenPops.Contains);
            if (missed.Count == 0)
            {
                jobNotes.Remove(city.Guid);
                loggedJobNotes.Remove(city.Guid);
                return;
            }
            string note = string.Join("; ", missed.Select(MissedText).ToArray());
            jobNotes[city.Guid] = note;
            string line = $"{note}. Jobs: {JobOptimizer.Composition(city.Jobs, Names.Pop, Names.Job)}";
            if (loggedJobNotes.TryGetValue(city.Guid, out string logged) && logged == line)
            {
                return;
            }
            loggedJobNotes[city.Guid] = line;
            if (Plugin.LogDecisions.Value)
            {
                Plugin.Log.LogInfo($"Turn {State.Turn}: {Names.City(city)}: no job swap found; {line}.");
            }
        }

        private static string MissedText(MissedBonus missed)
        {
            string type = Names.Pop(missed.Type);
            string job = Names.Job(missed.Job.Name);
            string who = missed.Count == 1 ? $"1 {type} works outside {job}, where it gets a bonus" : $"{missed.Count} {type} work outside {job}, where they get a bonus";
            switch (missed.Reason)
            {
                case MissedReason.MovedThisTurn:
                    return $"{who} (already moved this turn: each population moves at most once a turn)";
                case MissedReason.FullOfSameType:
                    return $"{who} ({job} is full, and the worker the game would send out to make room is also a {type})";
                default:
                    return $"{who} (no swap for them, nor a move into a free slot, gains at least the minimum with the city's job strategy, within the approval and food limits)";
            }
        }

        private static bool SameWeights(float[] a, float[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i] - b[i]) > 1e-4f)
                {
                    return false;
                }
            }
            return true;
        }

        // One swap at a time per city: each is planned on the latest state, so the game's own reactions (including the
        // population a full job sends out) are seen before the next one.
        private void OptimizeJobs()
        {
            float now = Time.unscaledTime;
            float minGain = Mathf.Max(0.1f, Plugin.JobMinGain.Value);
            foreach (CityState city in State.Cities)
            {
                if (city.Jobs == null || jobsHeld.Contains(city.Guid))
                {
                    continue;
                }
                CitySettings settings = Settings.GetCity(city.Guid);
                if (settings != null && (settings.Off || settings.JobsOff))
                {
                    jobPlans.Remove(city.Guid);
                    continue;
                }
                if (jobPlans.TryGetValue(city.Guid, out JobPlan plan))
                {
                    AdvanceJobPlan(city, plan, now);
                    continue;
                }
                if (JobLimitReached(city.Guid))
                {
                    continue;
                }
                // Search again only when something changed since the last search found nothing.
                long stamp = State.Version * 1000003L + Revision * 1009L + (long)(minGain * 100f);
                if (jobCheckedAt.TryGetValue(city.Guid, out long checkedAt) && checkedAt == stamp)
                {
                    continue;
                }
                float target = ApprovalTarget(city);
                float approval = ApprovalOf(city);
                var rules = new JobRules
                {
                    MinGain = minGain,
                    IsFrozen = frozenPops.Contains,
                    ApprovalFirst = !float.IsNaN(target) && approval < target,
                    ApprovalFloor = target,
                    CurrentApproval = approval,
                    CurrentFood = FoodOf(city),
                };
                JobSwap swap = JobOptimizer.BestSwap(State, city, rules);
                if (swap == null)
                {
                    jobCheckedAt[city.Guid] = stamp;
                    NoteMissedBonuses(city);
                    continue;
                }
                if (!jobTurnBase.ContainsKey(city.Guid))
                {
                    var start = new float[Yield.Count];
                    start[Yield.Approval] = city.ApprovalNet;
                    start[Yield.Food] = city.FoodNet;
                    jobTurnBase[city.Guid] = start;
                    jobTurnDelta[city.Guid] = new float[Yield.Count];
                }
                // Both stay where they land for the rest of the turn, so later swaps can't undo this one.
                frozenPops.Add(swap.Pop);
                frozenPops.Add(swap.Partner);
                swapsThisTurn[city.Guid] = SwapsThisTurn(city.Guid) + 1;
                jobPlans[city.Guid] = new JobPlan { Swap = swap, Time = now, Attempts = 1 };
                PostJobMove(swap.Pop, swap.To);
            }
        }

        private void AdvanceJobPlan(CityState city, JobPlan plan, float now)
        {
            JobSwap swap = plan.Swap;
            ulong popJob = city.Jobs.CategoryOf(swap.Pop)?.Guid ?? 0UL;
            ulong partnerJob = city.Jobs.CategoryOf(swap.Partner)?.Guid ?? 0UL;
            if (plan.Step == 1 && popJob == swap.To)
            {
                if (!swap.TwoOrders || partnerJob == swap.From)
                {
                    FinishJobPlan(city, plan, swap.IsMove || partnerJob == swap.From);
                    return;
                }
                plan.Step = 2;
                plan.Time = now;
                plan.Attempts = 1;
                PostJobMove(swap.Partner, swap.From);
                return;
            }
            if (plan.Step == 2 && partnerJob == swap.From)
            {
                FinishJobPlan(city, plan, asPlanned: true);
                return;
            }
            if (now - plan.Time < OrderRetrySeconds)
            {
                return;
            }
            if (plan.Attempts >= MaxOrderAttempts)
            {
                jobPlans.Remove(city.Guid);
                Plugin.Log.LogWarning($"Turn {State.Turn}: {Names.City(city)}: the game did not carry out the job swap of {Names.Pop(swap.PopType)} and {Names.Pop(swap.PartnerType)}; leaving them.");
                return;
            }
            plan.Attempts++;
            plan.Time = now;
            if (plan.Step == 1)
            {
                PostJobMove(swap.Pop, swap.To);
            }
            else
            {
                PostJobMove(swap.Partner, swap.From);
            }
        }

        private void FinishJobPlan(CityState city, JobPlan plan, bool asPlanned)
        {
            jobPlans.Remove(city.Guid);
            JobSwap swap = plan.Swap;
            if (jobTurnDelta.TryGetValue(city.Guid, out float[] total))
            {
                for (int f = 0; f < Yield.Count; f++)
                {
                    total[f] += swap.Delta[f];
                }
            }
            string text;
            if (swap.IsMove)
            {
                text = $"{Names.Pop(swap.PopType)} moved to {JobName(city, swap.To)} "
                    + (swap.ForApproval ? "for approval" : "into a free slot") + $" ({JobOptimizer.Describe(swap.Delta)})";
            }
            else if (asPlanned)
            {
                text = $"{Names.Pop(swap.PopType)} to {JobName(city, swap.To)}, {Names.Pop(swap.PartnerType)} to {JobName(city, swap.From)} ({JobOptimizer.Describe(swap.Delta)})";
            }
            else
            {
                text = $"{Names.Pop(swap.PopType)} to {JobName(city, swap.To)} (the game moved a different population out than expected)";
            }
            lastJobChange[city.Guid] = text;
            if (Plugin.LogDecisions.Value)
            {
                Plugin.Log.LogInfo($"Turn {State.Turn}: {Names.City(city)}: jobs: {text}.");
            }
        }

        private static void PostJobMove(ulong pop, ulong job)
        {
            SandboxManager.PostOrder(new OrderSwitchPopulationBetweenCategories(new SimulationEntityGUID[] { pop }, 1, job));
            StateCapture.RequestRefresh();
        }

        private void DrainFailures()
        {
            while (GrowthFailurePatch.Failures.TryDequeue(out GrowthFailure failure))
            {
                int until = failure.Turn + Math.Max(1, Plugin.FailedGrowthCooldown.Value);
                Block(failure.City, failure.Pop, until);
                // The game may try the same population several times in a turn: one line is enough.
                if (loggedFailures.Add($"{failure.Turn}:{failure.City}:{failure.Pop}"))
                {
                    Plugin.Log.LogWarning($"Turn {failure.Turn}: the game could not add {Names.Pop(failure.Pop)} to {CityName(failure.City)}; not picking it there before turn {until}.");
                }
            }
        }

        // The game reset a city's job strategy to Balanced on its own (see StrategyResetPatch): the mod sets it back (or,
        // if it may not, says so in the Cities tab until the player picks a strategy again), and logs it. Cities the mod
        // hasn't seen yet just joined the empire, and a new city always starts at Balanced: nothing to say.
        private void DrainStrategyResets()
        {
            while (StrategyResetPatch.Resets.TryDequeue(out StrategyReset reset))
            {
                CityState city = State.FindCity(reset.City);
                if (city == null)
                {
                    continue;
                }
                strategyResets[reset.City] = State.Turn;
                string from = Names.Strategy(reset.From);
                string to = Names.Strategy(reset.To);
                CitySettings settings = Settings.GetCity(reset.City);
                bool restore = Plugin.RestoreStrategies.Value && Plugin.Automation.Value && (settings == null || (!settings.Off && !settings.JobsOff));
                if (restore)
                {
                    strategyRestores[reset.City] = new PendingOrder { Pop = reset.From, Time = Time.unscaledTime };
                }
                strategyNotes[reset.City] = $"The game reset this city's job strategy from {from} to {to} on turn {State.Turn} (it does that to every city when your "
                    + "empire gains or loses a special ability): " + (restore ? $"setting it back to {from}." : "pick it again in the city screen if you want it back.");
                Plugin.Log.LogWarning($"Turn {State.Turn}: {Names.City(city)}: the game reset its job strategy from {from} to {to} without placing its populations again "
                    + "(a game bug: it does that to every city when your empire gains or loses a special ability); "
                    + (restore ? $"setting it back to {from}." : "pick it again in the city screen if you want it back."));
            }
        }

        private void ReleaseHolds()
        {
            if (Settings == null)
            {
                return;
            }
            bool ignore = Plugin.ManualPicks.Value == ManualPickMode.Ignore;
            foreach (CityState city in State.Cities)
            {
                CitySettings settings = Settings.GetCity(city.Guid);
                if (settings == null || string.IsNullOrEmpty(settings.HeldPop))
                {
                    continue;
                }
                bool grew = !settings.HeldUntilResumed && settings.HeldAtGained >= 0 && city.TotalGained > settings.HeldAtGained;
                // The game dropped the pick, e.g. that population can no longer be grown.
                bool dropped = city.Growing != settings.HeldPop && !city.CanGrow(settings.HeldPop);
                if (ignore || grew || dropped)
                {
                    Release(settings);
                    Touch();
                    if (!ignore)
                    {
                        Plugin.Log.LogInfo($"{Names.City(city)}: " + (grew ? "grew, back to automatic." : $"{Names.Pop(city.Growing)} replaced your pick, back to automatic."));
                    }
                }
            }
        }

        private static void Release(CitySettings settings)
        {
            settings.HeldPop = null;
            settings.HeldAtGained = -1;
            settings.HeldUntilResumed = false;
        }

        private void Block(ulong city, string pop, int untilTurn)
        {
            string key = Key(city, pop);
            blockedUntil[key] = blockedUntil.TryGetValue(key, out int existing) ? Math.Max(existing, untilTurn) : untilTurn;
            Touch(save: false);
        }

        // Rewrites the player's order from what the window shows (the current aim order), then moves one population.
        private void Reorder(string pop, int delta)
        {
            List<string> order = Plan != null ? new List<string>(Plan.Order) : new List<string>(Settings.Priority);
            int index = order.IndexOf(pop);
            if (index < 0)
            {
                order.Add(pop);
                index = order.Count - 1;
            }
            int target = delta == int.MinValue ? 0 : Mathf.Clamp(index + delta, 0, order.Count - 1);
            order.RemoveAt(index);
            order.Insert(target, pop);
            // Keep earlier entries that are not shown (unlocked or skipped), so they still order the spreading.
            foreach (string previous in Settings.Priority)
            {
                if (!order.Contains(previous))
                {
                    order.Add(previous);
                }
            }
            Settings.Priority = order;
            Touch();
        }

        private void Touch(bool save = true)
        {
            Revision++;
            if (save)
            {
                saveAt = Time.unscaledTime + 1.5f;
            }
        }

        private string CityName(ulong guid)
        {
            CityState city = State?.FindCity(guid);
            return city != null ? Names.City(city) : "city " + guid;
        }

        internal static string Describe(PlanKind kind)
        {
            switch (kind)
            {
                case PlanKind.Approval: return "approval first";
                case PlanKind.Spread: return "one per city for an unlocked bonus";
                case PlanKind.CityTarget: return "city target";
                case PlanKind.Aim: return "aiming for its bonus";
                case PlanKind.Ready: return "ready for when the city grows";
                case PlanKind.Fallback: return "previous pick not useful";
                default: return kind.ToString();
            }
        }

        private static string Key(ulong city, string pop) => city.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + pop;

        private static string PathFor(string id)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                id = id.Replace(c, '_');
            }
            return System.IO.Path.Combine(System.IO.Path.Combine(Paths.ConfigPath, "PopulationPlanner"), id + ".json");
        }

        private static GameSettings Load(string id, out bool isNew)
        {
            string path = PathFor(id);
            isNew = !File.Exists(path);
            if (isNew)
            {
                return new GameSettings();
            }
            try
            {
                GameSettings settings = JsonConvert.DeserializeObject<GameSettings>(File.ReadAllText(path)) ?? new GameSettings();
                settings.Normalize();
                return settings;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not read {path} ({e.Message}); starting with fresh choices for this game.");
                return new GameSettings();
            }
        }
    }
}
