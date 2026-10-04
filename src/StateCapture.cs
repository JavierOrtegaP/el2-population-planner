using System;
using System.Collections.Generic;
using System.Threading;
using Amplitude;
using Amplitude.Collections;
using Amplitude.Framework;
using Amplitude.Framework.Simulation;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Sandbox;
using Amplitude.Mercury.Simulation;
using HarmonyLib;
using DataEffect = Amplitude.Mercury.Data.Simulation.SimulationEventEffect;
using Descriptor = Amplitude.Framework.Simulation.Description.Descriptor;

namespace PopulationPlanner
{
    // Reads the simulation on the sandbox thread, right after the game copies it for its own UI (Snapshots.Synchronize
    // runs there every 100 ms, while the simulation is idle), and hands the main thread an immutable GameState.
    [HarmonyPatch(typeof(Snapshots), nameof(Snapshots.Synchronize))]
    internal static class StateCapture
    {
        private const long IntervalMs = 250;

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        // Whether a population's last bonus only applies where it is present: static game data, so cached by name.
        private static readonly Dictionary<string, bool> PresenceCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ApprovalLevelCache = new Dictionary<string, float>(StringComparer.Ordinal);
        private static long nextCaptureMs;
        private static long lastSignature;
        private static int version;
        private static GameState latest;
        private static volatile bool refreshRequested;

        internal static GameState Latest => Volatile.Read(ref latest);

        // Capture on the next sync instead of waiting for the interval (after posting orders, a failed growth...).
        internal static void RequestRefresh() => refreshRequested = true;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (Guard.HasFailed(typeof(StateCapture)))
            {
                return;
            }
            try
            {
                Capture();
            }
            catch (Exception e)
            {
                Guard.Fail(typeof(StateCapture), e);
            }
        }

        private static void Capture()
        {
            long now = Clock.ElapsedMilliseconds;
            if (!refreshRequested && now < nextCaptureMs)
            {
                return;
            }
            refreshRequested = false;
            nextCaptureMs = now + IntervalMs;

            Sandbox sandbox = SandboxManager.Sandbox;
            if (sandbox == null || !sandbox.IsInitialized)
            {
                return;
            }
            int empireIndex = sandbox.LocalEmpireIndex;
            MajorEmpire[] empires = Sandbox.MajorEmpires;
            if (empires == null || empireIndex < 0 || empireIndex >= empires.Length || ReferenceEquals(empires[empireIndex], null))
            {
                return;
            }
            MajorEmpire empire = empires[empireIndex];

            var state = new GameState
            {
                GameId = SandboxManager.GameID ?? string.Empty,
                Turn = sandbox.Turn,
                IsHuman = empire.IsControlledByHuman,
                CanAct = sandbox.LocalPlayerJoined && empire.IsAlive && !empire.IsReady && sandbox.IsCurrentStateOfType<SandboxState_TurnMain>(),
                CannotGrowWithFood = (empire.ExoticAbilityFlags & EmpireExoticAbilityFlags.CannotGrowPopulationWithFood) != 0,
            };

            var definitions = new Dictionary<string, PopulationDefinition>(StringComparer.Ordinal);
            var settlements = new List<Settlement>();
            for (int i = 0; i < empire.Settlements.Count; i++)
            {
                Settlement settlement = empire.Settlements[i];
                if (ReferenceEquals(settlement, null) || settlement.SettlementStatus != SettlementStatuses.City)
                {
                    continue;
                }
                var city = new CityState
                {
                    Guid = settlement.GUID,
                    NameSource = settlement.EntityName,
                    Growing = settlement.GrowingPopulationName.ToString(),
                    Population = (int)settlement.Population.Value,
                    TotalGained = (int)settlement.TotalGainedPopulation.Value,
                    FoodNet = (float)settlement.FoodNet.Value,
                    Approval = (float)settlement.ApprovalCapped.Value,
                    ApprovalNet = (float)settlement.ApprovalNet.Value,
                    ApprovalLevel = ReferenceEquals(settlement.SettlementApprovalDefinition, null) ? string.Empty : settlement.SettlementApprovalDefinition.Name.ToString(),
                };
                AddCounts(settlement.AssignedPopulations, city.Counts, definitions);
                AddCounts(settlement.OverPopulations, city.Counts, definitions);
                ListOfStruct<AvailablePopulation> available = settlement.AvailablePopulations;
                for (int j = 0; j < available.Length; j++)
                {
                    PopulationDefinition definition = available.Data[j].PopulationDefinition;
                    if (ReferenceEquals(definition, null))
                    {
                        continue;
                    }
                    string name = definition.Name.ToString();
                    definitions[name] = definition;
                    float turns = ToTurns(available.Data[j].TurnBeforeGrowth);
                    city.Options.Add(new GrowOption(name, turns));
                    if (name == city.Growing)
                    {
                        city.TurnsToGrowth = turns;
                    }
                }
                state.Cities.Add(city);
                settlements.Add(settlement);
            }

            var availableToEmpire = new HashSet<string>(StringComparer.Ordinal);
            List<StaticString> availableNames = empire.AvailablePopulationNames;
            for (int i = 0; availableNames != null && i < availableNames.Count; i++)
            {
                string name = availableNames[i].ToString();
                availableToEmpire.Add(name);
                if (!definitions.ContainsKey(name) && TryGetDefinition(availableNames[i], out PopulationDefinition definition))
                {
                    definitions[name] = definition;
                }
            }

            var levels = new Dictionary<string, int>(StringComparer.Ordinal);
            ListOfStruct<PopulationCollection> collections = empire.PopulationCollections;
            for (int i = 0; collections != null && i < collections.Length; i++)
            {
                PopulationDefinition definition = collections.Data[i].PopulationDefinition;
                if (ReferenceEquals(definition, null))
                {
                    continue;
                }
                string name = definition.Name.ToString();
                levels[name] = collections.Data[i].CollectionLevel;
                definitions[name] = definition;
            }

            var factionPops = new HashSet<string>(StringComparer.Ordinal);
            Amplitude.Framework.DatatableElementReference[] factionPopulations = empire.FactionDefinition?.Population;
            for (int i = 0; factionPopulations != null && i < factionPopulations.Length; i++)
            {
                factionPops.Add(factionPopulations[i].ElementName.ToString());
            }

            DepartmentOfTheInterior interior = empire.DepartmentOfTheInterior;
            foreach (KeyValuePair<string, PopulationDefinition> pair in definitions)
            {
                PopulationDefinition definition = pair.Value;
                int tierCount = definition.PopulationCollection?.Length ?? 0;
                var thresholds = new int[tierCount];
                for (int tier = 0; tier < tierCount; tier++)
                {
                    thresholds[tier] = (int)interior.GetPopulationCollectionThreshold(definition, tier + 1);
                }
                var type = new PopType
                {
                    Name = pair.Key,
                    Count = empire.CountPopulationOfType(definition.Name),
                    Level = levels.TryGetValue(pair.Key, out int level) ? level : 0,
                    Thresholds = thresholds,
                    PresenceMatters = PresenceMatters(definition),
                    ActionOnly = definition.IsCreatedByActionOnly,
                    FactionPop = factionPops.Contains(pair.Key),
                    AvailableToEmpire = availableToEmpire.Contains(pair.Key),
                };
                foreach (CityState city in state.Cities)
                {
                    if (city.Count(pair.Key) > 0)
                    {
                        type.CitiesWith++;
                    }
                }
                state.Types[pair.Key] = type;
            }

            ReadApprovalLevels(state);
            HashSet<string> jobTags = JobCapture.ReadTypes(state, definitions.Values);
            for (int i = 0; i < settlements.Count; i++)
            {
                state.Cities[i].Jobs = JobCapture.ReadJobs(settlements[i], jobTags);
            }

            long signature = Signature(state);
            if (signature == lastSignature && Latest != null && Latest.GameId == state.GameId)
            {
                return;
            }
            lastSignature = signature;
            state.Version = ++version;
            Volatile.Write(ref latest, state);
        }

        private static void AddCounts(ReferenceCollection<Population> populations, Dictionary<string, int> counts, Dictionary<string, PopulationDefinition> definitions)
        {
            for (int i = 0; populations != null && i < populations.Count; i++)
            {
                Population population = populations[i];
                PopulationDefinition definition = ReferenceEquals(population, null) ? null : population.PopulationDefinition;
                if (ReferenceEquals(definition, null))
                {
                    continue;
                }
                string name = definition.Name.ToString();
                counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
                definitions[name] = definition;
            }
        }

        // The approval each city level starts at (game data: 25 Content, 60 Happy, 85 Jubilant at release).
        private static void ReadApprovalLevels(GameState state)
        {
            if (ApprovalLevelCache.Count == 0)
            {
                IDatabase<SettlementApprovalDefinition> database = Databases.GetDatabase<SettlementApprovalDefinition>();
                SettlementApprovalDefinition[] definitions = database?.GetValues();
                for (int i = 0; definitions != null && i < definitions.Length; i++)
                {
                    if (!ReferenceEquals(definitions[i], null))
                    {
                        ApprovalLevelCache[definitions[i].Name.ToString()] = definitions[i].ApprovalRangeMin;
                    }
                }
            }
            foreach (KeyValuePair<string, float> level in ApprovalLevelCache)
            {
                state.ApprovalLevels[level.Key] = level.Value;
            }
        }

        private static bool TryGetDefinition(StaticString name, out PopulationDefinition definition)
        {
            definition = null;
            IDatabase<PopulationDefinition> database = Databases.GetDatabase<PopulationDefinition>();
            return database != null && database.TryGetValue(name, out definition) && !ReferenceEquals(definition, null);
        }

        // The game's TurnBeforeGrowth: MaxValue when the city gains no food, negative when it starves.
        private static float ToTurns(FixedPoint turns)
        {
            float value = (float)turns;
            return value <= 0f || value > 100000f ? float.PositiveInfinity : value;
        }

        // True when the last bonus's effects only reach settlements carrying the population's presence tag, i.e. cities
        // with at least one population of this type. Read from the game data, so it follows balance changes.
        private static bool PresenceMatters(PopulationDefinition definition)
        {
            string name = definition.Name.ToString();
            if (PresenceCache.TryGetValue(name, out bool cached))
            {
                return cached;
            }
            bool result = false;
            PopulationDefinition.PopulationCollectionDefinition[] tiers = definition.PopulationCollection;
            StaticString tag = definition.SettlementPresenceDescriptor.ElementName;
            if (tiers != null && tiers.Length > 0 && !StaticString.IsNullOrEmpty(tag))
            {
                DataEffect[] effects = tiers[tiers.Length - 1].SimulationEventEffects;
                for (int i = 0; !result && effects != null && i < effects.Length; i++)
                {
                    if (effects[i] is Amplitude.Mercury.Data.Simulation.SimulationEventEffect_ApplyDescriptor apply)
                    {
                        Descriptor descriptor = apply.Descriptor.GetDatatableElement<Descriptor>();
                        result = ValidatesOn(descriptor, tag);
                    }
                }
            }
            PresenceCache[name] = result;
            return result;
        }

        private static bool ValidatesOn(Descriptor descriptor, StaticString tag)
        {
            if (ReferenceEquals(descriptor, null) || descriptor.Effects == null)
            {
                return false;
            }
            foreach (var effect in descriptor.Effects)
            {
                var validations = effect.Path.Validations;
                for (int i = 0; validations != null && i < validations.Length; i++)
                {
                    // A copy: OnEnable only fills its name from the serialized one, should the game not have yet.
                    var validation = validations[i];
                    if (StaticString.IsNullOrEmpty(validation.ElementName))
                    {
                        validation.OnEnable();
                    }
                    if (validation.ElementName == tag)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static long Signature(GameState state)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                void Mix(long value) => hash = (hash ^ value) * 1099511628211L;
                Mix(state.GameId.GetHashCode());
                Mix(state.Turn);
                Mix(state.CanAct ? 1 : 0);
                Mix(state.IsHuman ? 1 : 0);
                foreach (CityState city in state.Cities)
                {
                    Mix((long)city.Guid);
                    Mix(city.Growing.GetHashCode());
                    Mix(city.Population);
                    Mix(city.TotalGained);
                    Mix(city.FoodNet > 0f ? 1 : 0);
                    Mix((long)System.Math.Floor(city.ApprovalNet));
                    foreach (KeyValuePair<string, int> count in city.Counts)
                    {
                        Mix(count.Key.GetHashCode());
                        Mix(count.Value);
                    }
                    foreach (GrowOption option in city.Options)
                    {
                        Mix(option.Pop.GetHashCode());
                        Mix(float.IsInfinity(option.Turns) ? -1 : (long)System.Math.Ceiling(option.Turns));
                    }
                    if (city.Jobs != null)
                    {
                        Mix(city.Jobs.CanReassign ? 1 : 0);
                        foreach (float weight in city.Jobs.Weights)
                        {
                            Mix((long)(weight * 1000f));
                        }
                        foreach (JobCategory job in city.Jobs.Categories)
                        {
                            Mix((long)job.Guid);
                            Mix(job.Slots);
                            foreach (JobPop pop in job.Pops)
                            {
                                Mix((long)pop.Guid);
                            }
                        }
                    }
                }
                foreach (PopType type in state.Types.Values)
                {
                    Mix(type.Name.GetHashCode());
                    Mix(type.Count);
                    Mix(type.Level);
                    Mix(type.AvailableToEmpire ? 1 : 0);
                }
                return hash;
            }
        }
    }
}
