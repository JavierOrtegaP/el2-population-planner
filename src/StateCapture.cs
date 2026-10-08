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
    // runs there while the simulation is idle), and hands the main thread an immutable GameState. Only when the
    // simulation moved on since the last read (its frame counter goes up whenever it processed something), or when asked.
    [HarmonyPatch(typeof(Snapshots), nameof(Snapshots.Synchronize))]
    internal static class StateCapture
    {
        private static int lastFrame = -1;
        private static Sandbox lastSandbox;
        // Whether a population's last bonus only applies where it is present: static game data, so cached by name.
        private static readonly Dictionary<string, bool> PresenceCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ApprovalLevelCache = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float[]> ApprovalBonusCache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private static IReadOnlyList<PopInfo> catalog;
        private static string catalogGame;
        private static int catalogTurn;
        private static bool catalogFailed;
        private static long lastSignature;
        private static int version;
        private static GameState latest;
        private static volatile bool refreshRequested;

        internal static GameState Latest => Volatile.Read(ref latest);

        // Capture on the next sync even if the frame counter hasn't moved yet (after posting orders, a failed growth...).
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
            Sandbox sandbox = SandboxManager.Sandbox;
            if (sandbox == null || !sandbox.IsInitialized)
            {
                return;
            }
            int frame = Sandbox.Frame;
            if (!refreshRequested && frame == lastFrame && ReferenceEquals(sandbox, lastSandbox))
            {
                return;
            }
            refreshRequested = false;
            lastFrame = frame;
            lastSandbox = sandbox;
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
                    FoodGain = (float)settlement.FoodGain.Value,
                    IndustryGain = (float)settlement.IndustryGain.Value,
                    FoodMultiplier = ChainMultiplier(settlement, settlement.FoodGain.GlobalPropertyIndex, settlement.FoodNet.GlobalPropertyIndex),
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
            state.Catalog = Catalog(state.GameId, state.Turn, empire);
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

        // How much the game multiplies what is added to a chain of properties, each feeding the next (FoodGain feeds
        // FoodNet), read from the entity's own modifiers on them (one pass: a city has thousands): for each, 1 plus its
        // percent bonuses (the game adds them up, each on the value before the first) times its multipliers. For a
        // city's food: the approval level, collection bonuses, improvements... whatever applies there.
        private static float ChainMultiplier(BaseSimulationEntity entity, params int[] globalPropertyIndexes)
        {
            int count = globalPropertyIndexes.Length;
            var local = new int[count];
            var percent = new float[count];
            var factor = new float[count];
            for (int k = 0; k < count; k++)
            {
                local[k] = -1;
                factor[k] = 1f;
            }
            var properties = entity.Properties;
            for (int i = 0; properties != null && i < properties.Length; i++)
            {
                int k = Array.IndexOf(globalPropertyIndexes, properties[i].GlobalPropertyIndex);
                if (k >= 0)
                {
                    local[k] = i;
                }
            }
            float one = FixedPoint.One.Raw;
            for (int i = 0; entity.Modifiers.Data != null && i < entity.Modifiers.DataCount; i++)
            {
                ref var modifier = ref entity.Modifiers.Data[i];
                int k = modifier.IsValid && modifier.LocalPropertyIndex >= 0 ? Array.IndexOf(local, modifier.LocalPropertyIndex) : -1;
                if (k < 0)
                {
                    continue;
                }
                float value = modifier.LastComputedValue / one;
                switch (modifier.PropertyEffect.ToTargetOperation)
                {
                    case Operation.Percent:
                        percent[k] += value;
                        break;
                    case Operation.Mult:
                        factor[k] *= value;
                        break;
                    case Operation.Div:
                        factor[k] = value != 0f ? factor[k] / value : factor[k];
                        break;
                }
            }
            float result = 1f;
            for (int k = 0; k < count; k++)
            {
                result *= (1f + percent[k]) * factor[k];
            }
            return result;
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

        // The approval each city level starts at (game data: 25 Content, 60 Happy, 85 Jubilant at release), and what it
        // adds to a city's Food and Industry gain (+15% Happy, +30% Jubilant, -20% below Content).
        private static void ReadApprovalLevels(GameState state)
        {
            if (ApprovalLevelCache.Count == 0)
            {
                IDatabase<SettlementApprovalDefinition> database = Databases.GetDatabase<SettlementApprovalDefinition>();
                SettlementApprovalDefinition[] definitions = database?.GetValues();
                for (int i = 0; definitions != null && i < definitions.Length; i++)
                {
                    if (ReferenceEquals(definitions[i], null))
                    {
                        continue;
                    }
                    string name = definitions[i].Name.ToString();
                    ApprovalLevelCache[name] = definitions[i].ApprovalRangeMin;
                    float[] bonus = ReadApprovalBonus(definitions[i]);
                    if (bonus != null)
                    {
                        ApprovalBonusCache[name] = bonus;
                    }
                }
            }
            foreach (KeyValuePair<string, float> level in ApprovalLevelCache)
            {
                state.ApprovalLevels[level.Key] = level.Value;
            }
            foreach (KeyValuePair<string, float[]> bonus in ApprovalBonusCache)
            {
                state.ApprovalBonuses[bonus.Key] = bonus.Value;
            }
        }

        // A level's percentages on the city's Food and Industry gain, as fractions ([food, industry]); null when its
        // descriptors do something to those the mod can't weigh, so the level is then taken as configured.
        private static float[] ReadApprovalBonus(SettlementApprovalDefinition definition)
        {
            var bonus = new float[2];
            Amplitude.Framework.DatatableElementReference[] references = definition.DescriptorReferences;
            for (int i = 0; references != null && i < references.Length; i++)
            {
                Descriptor descriptor = references[i].GetDatatableElement<Descriptor>();
                if (ReferenceEquals(descriptor, null) || descriptor.Effects == null)
                {
                    continue;
                }
                foreach (var effect in descriptor.Effects)
                {
                    if (effect.PropertyEffects == null)
                    {
                        continue;
                    }
                    foreach (var propertyEffect in effect.PropertyEffects)
                    {
                        int field = propertyEffect == null ? -1 : propertyEffect.TargetProperty == "FoodGain" ? 0 : propertyEffect.TargetProperty == "IndustryGain" ? 1 : -1;
                        if (field < 0)
                        {
                            continue;
                        }
                        bool onTheCity = (effect.Path.PropertyToFollow?.Length ?? 0) == 0 && (effect.Path.Validations?.Length ?? 0) == 0;
                        if (!onTheCity || propertyEffect.ToTargetOperation != Amplitude.Framework.Simulation.Operation.Percent
                            || (propertyEffect.RpnOperationStack?.Length ?? 0) > 0 || propertyEffect.ConstantStack == null || propertyEffect.ConstantStack.Length != 1)
                        {
                            Plugin.Log.LogDebug($"Approval level {definition.Name}: an effect on {propertyEffect.TargetProperty} the mod can't weigh.");
                            return null;
                        }
                        bonus[field] += (float)propertyEffect.ConstantStack[0];
                    }
                }
            }
            return bonus;
        }

        // Every population of the game with its bonus thresholds for this empire and what each bonus gives, in the
        // words of the game's population screen: the same call the game makes for it, here for populations the empire
        // has none of too. Once per turn; an error only leaves the list empty for the rest of the game.
        private static IReadOnlyList<PopInfo> Catalog(string gameId, int turn, MajorEmpire empire)
        {
            if (catalog != null && catalogGame == gameId && (catalogTurn == turn || catalogFailed))
            {
                return catalog;
            }
            if (catalogGame != gameId)
            {
                catalogFailed = false;
            }
            catalogGame = gameId;
            catalogTurn = turn;
            var list = new List<PopInfo>();
            var withoutText = new List<string>();
            try
            {
                PopulationDefinition[] all = Databases.GetDatabase<PopulationDefinition>()?.GetValues();
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    PopulationDefinition definition = all[i];
                    int tierCount = ReferenceEquals(definition, null) ? 0 : definition.PopulationCollection?.Length ?? 0;
                    if (tierCount == 0 || definition.Hidden)
                    {
                        continue;
                    }
                    var info = new PopInfo
                    {
                        Name = definition.Name.ToString(),
                        ActionOnly = definition.IsCreatedByActionOnly,
                        PresenceMatters = PresenceMatters(definition),
                        Thresholds = new int[tierCount],
                        TierTexts = new string[tierCount],
                    };
                    int empty = 0;
                    for (int tier = 0; tier < tierCount; tier++)
                    {
                        info.Thresholds[tier] = (int)empire.DepartmentOfTheInterior.GetPopulationCollectionThreshold(definition, tier + 1);
                        info.TierTexts[tier] = Sandbox.SimulationEvaluator.GetSimulationEventEffectTranslation(definition.PopulationCollection[tier].SimulationEventEffects, empire) ?? string.Empty;
                        if (info.TierTexts[tier].Trim().Length == 0)
                        {
                            empty++;
                        }
                    }
                    if (empty == tierCount)
                    {
                        // Nothing to show (e.g. Mangrove of Harmony's Elder variant): left out of the Populations tab.
                        withoutText.Add($"{info.Name} (left out)");
                        continue;
                    }
                    for (int tier = 0; tier < tierCount; tier++)
                    {
                        if (info.TierTexts[tier].Trim().Length == 0)
                        {
                            withoutText.Add($"{info.Name} bonus {tier + 1}");
                        }
                    }
                    list.Add(info);
                }
            }
            catch (Exception e)
            {
                catalogFailed = true;
                catalog = new PopInfo[0];
                Plugin.Log.LogWarning($"Could not read the populations' bonuses; the Populations tab stays empty this game. {e}");
                return catalog;
            }
            if (catalog == null || catalog.Count != list.Count)
            {
                Plugin.Log.LogInfo($"Bonuses read for {list.Count} populations"
                    + (withoutText.Count > 0 ? $"; the game has no text for {string.Join(", ", withoutText.ToArray())}." : "."));
            }
            catalog = list;
            return catalog;
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
                            Mix(weight.GetHashCode());
                        }
                        foreach (JobCategory job in city.Jobs.Categories)
                        {
                            Mix((long)job.Guid);
                            Mix(job.Slots);
                            foreach (float value in job.Base)
                            {
                                Mix(value.GetHashCode());
                            }
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
