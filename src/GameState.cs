using System;
using System.Collections.Generic;

namespace PopulationPlanner
{
    // An immutable copy of what the planner needs, taken on the sandbox thread (StateCapture) and read on the main
    // thread. No game types in here, so the planner can be tested without the game.
    internal sealed class GameState
    {
        public string GameId = string.Empty;
        public int Turn;
        public int Version;
        public bool IsHuman;
        // The local empire may give orders now: its turn is running and it has not ended it.
        public bool CanAct;
        // Factions that never grow population with food (the mod has nothing to do for them).
        public bool CannotGrowWithFood;
        public readonly List<CityState> Cities = new List<CityState>();
        public readonly Dictionary<string, PopType> Types = new Dictionary<string, PopType>(StringComparer.Ordinal);
        // Yield effects of each population type that depend on its job, read from the game data (JobEffects).
        public readonly Dictionary<string, List<JobEffect>> JobEffects = new Dictionary<string, List<JobEffect>>(StringComparer.Ordinal);
        // Descriptor names each population type carries, for effects on co-workers of a given type.
        public readonly Dictionary<string, HashSet<string>> TypeDescriptors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Populations whose job changes the job's slot count (e.g. Severed Claws): never moved, never swapped out.
        public readonly HashSet<string> FixedJobTypes = new HashSet<string>(StringComparer.Ordinal);
        // City approval needed for each level (game definition name -> minimum), e.g. SettlementApproval_Neutral -> 25.
        public readonly Dictionary<string, float> ApprovalLevels = new Dictionary<string, float>(StringComparer.Ordinal);
        // What each level adds to a city's Food and Industry gain, as fractions (definition name -> [food, industry]):
        // +0.15 for Happy, +0.3 for Jubilant, -0.2 below Content at release. Missing when the game data can't be read.
        public readonly Dictionary<string, float[]> ApprovalBonuses = new Dictionary<string, float[]>(StringComparer.Ordinal);
        // Every population of the game with what its bonuses give, including ones the empire has none of.
        public IReadOnlyList<PopInfo> Catalog = new PopInfo[0];

        public CityState FindCity(ulong guid)
        {
            for (int i = 0; i < Cities.Count; i++)
            {
                if (Cities[i].Guid == guid)
                {
                    return Cities[i];
                }
            }
            return null;
        }
    }

    internal sealed class CityState
    {
        public ulong Guid;
        // The game's name info (boxed EntityNameInfo); turned into text on the main thread, as it localizes.
        public object NameSource;
        public string Name = string.Empty;
        // The population this city grows next (the game's "Next Population").
        public string Growing = string.Empty;
        public int Population;
        // Total populations this city ever grew; tells a growth apart from a population moving in.
        public int TotalGained;
        public float FoodNet;
        // Turns until the next population with the current pick; +infinity when the city is not growing.
        public float TurnsToGrowth = float.PositiveInfinity;
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
        // What the game lets this city grow right now, with the turns it would take.
        public readonly List<GrowOption> Options = new List<GrowOption>();
        // Who works which job; null when not captured.
        public JobState Jobs;
        // City approval: the level comes from the capped value (0-100); the mod's actions change the net value.
        public float Approval = 100f;
        public float ApprovalNet = 100f;
        public string ApprovalLevel = string.Empty;
        // The city's Food and Industry gain per turn (before what it consumes), which approval levels raise by a percentage.
        public float FoodGain;
        public float IndustryGain;

        public bool IsGrowing => FoodNet > 0f && !float.IsInfinity(TurnsToGrowth);

        public int Count(string pop) => Counts.TryGetValue(pop, out int n) ? n : 0;

        public bool CanGrow(string pop)
        {
            for (int i = 0; i < Options.Count; i++)
            {
                if (Options[i].Pop == pop)
                {
                    return true;
                }
            }
            return false;
        }

        public float TurnsFor(string pop)
        {
            for (int i = 0; i < Options.Count; i++)
            {
                if (Options[i].Pop == pop)
                {
                    return Options[i].Turns;
                }
            }
            return float.PositiveInfinity;
        }
    }

    internal readonly struct GrowOption
    {
        public readonly string Pop;
        public readonly float Turns;

        public GrowOption(string pop, float turns)
        {
            Pop = pop;
            Turns = turns;
        }
    }

    internal sealed class PopType
    {
        public string Name = string.Empty;
        // Populations of this type in the empire: what the game compares to the collection thresholds.
        public int Count;
        // Collection bonuses unlocked so far. The game never takes one back, even if Count drops later.
        public int Level;
        // Population needed for each bonus (index 0 = first bonus), after faction reductions.
        public int[] Thresholds = new int[0];
        // The last bonus only applies in cities that have at least one population of this type.
        public bool PresenceMatters;
        // Created by game actions only (e.g. Called, Divined): never grown with food.
        public bool ActionOnly;
        // One of the local empire's own faction populations.
        public bool FactionPop;
        // In the empire's list of populations it can grow.
        public bool AvailableToEmpire;
        public int CitiesWith;

        public int MaxLevel => Thresholds.Length;

        public int MaxThreshold => Thresholds.Length == 0 ? 0 : Thresholds[Thresholds.Length - 1];

        public bool Done => MaxLevel > 0 && Level >= MaxLevel;

        // Populations still to grow before the last bonus unlocks. The game checks one level per population added,
        // so a type that is already over the threshold still needs one growth per missing level.
        public int Need => Done ? 0 : Math.Max(MaxThreshold - Count, MaxLevel - Level);
    }

    // A population as the game describes it: the window's reference, whether the empire has any or not.
    internal sealed class PopInfo
    {
        public string Name = string.Empty;
        public bool ActionOnly;
        public bool PresenceMatters;
        // Population needed for each bonus, for this empire (faction reductions included).
        public int[] Thresholds = new int[0];
        // What each bonus gives, in the game's words (its markup still in).
        public string[] TierTexts = new string[0];
    }

    // The yields the game's job scoring adds up (its FimsInfo), in this order everywhere.
    internal static class Yield
    {
        public const int Food = 0;
        public const int Industry = 1;
        public const int Money = 2;
        public const int Science = 3;
        public const int Influence = 4;
        public const int Approval = 5;
        public const int Count = 6;

        public static readonly string[] Labels = { "Food", "Industry", "Dust", "Science", "Influence", "Approval" };

        // The game's property names for what a population produces.
        public static int FromProperty(string property)
        {
            switch (property)
            {
                case "FoodProduced": return Food;
                case "IndustryProduced": return Industry;
                case "MoneyProduced": return Money;
                case "ScienceProduced": return Science;
                case "InfluenceProduced": return Influence;
                case "ApprovalProduced": return Approval;
                default: return -1;
            }
        }
    }

    // A city's jobs (the game's population categories, homeless excluded).
    internal sealed class JobState
    {
        // False while the city is under subjugation: the game refuses job changes then.
        public bool CanReassign = true;
        // The city's job strategy (e.g. PopulationStrategy_Food) and its weights per yield. With FillLowestFirst
        // (Balanced) the game puts a new population in one of the least filled jobs.
        public string Strategy = string.Empty;
        public float[] Weights = { 1f, 1f, 1f, 1f, 1f, 1f };
        public bool FillLowestFirst;
        public readonly List<JobCategory> Categories = new List<JobCategory>();

        // A copy to try job moves on (populations and slots copied; the rest shared, as it doesn't change).
        public JobState Clone()
        {
            var copy = new JobState { CanReassign = CanReassign, Strategy = Strategy, Weights = Weights, FillLowestFirst = FillLowestFirst };
            foreach (JobCategory category in Categories)
            {
                var job = new JobCategory { Guid = category.Guid, Name = category.Name, Slots = category.Slots, Base = category.Base };
                job.Tags.UnionWith(category.Tags);
                job.Pops.AddRange(category.Pops);
                copy.Categories.Add(job);
            }
            return copy;
        }

        public JobCategory Find(ulong guid)
        {
            foreach (JobCategory category in Categories)
            {
                if (category.Guid == guid)
                {
                    return category;
                }
            }
            return null;
        }

        public JobCategory CategoryOf(ulong pop)
        {
            foreach (JobCategory category in Categories)
            {
                foreach (JobPop worker in category.Pops)
                {
                    if (worker.Guid == pop)
                    {
                        return category;
                    }
                }
            }
            return null;
        }
    }

    internal sealed class JobCategory
    {
        public ulong Guid;
        // The game's definition name, e.g. PopulationCategory_02.
        public string Name = string.Empty;
        public int Slots;
        // What every population working here yields, before its own job effects.
        public float[] Base = new float[Yield.Count];
        // Descriptors on the job that population effects check (e.g. Tag_PopulationCategory_02).
        public readonly HashSet<string> Tags = new HashSet<string>(StringComparer.Ordinal);
        // In the game's order: when a full job takes a population, the game sends out its lowest-scoring one, the
        // first of them in this order.
        public readonly List<JobPop> Pops = new List<JobPop>();

        public bool IsFull => Pops.Count >= Slots;
    }

    internal readonly struct JobPop
    {
        public readonly ulong Guid;
        public readonly string Type;
        // The game's own score of this population (what it produces); the lowest is sent out of a full job.
        public readonly float Score;

        public JobPop(ulong guid, string type, float score)
        {
            Guid = guid;
            Type = type;
            Score = score;
        }
    }

    // One yield a population type adds or removes depending on its job, decoded from the game's descriptors:
    // e.g. Daughter of Bor +1 Industry in job 02, or Xavius +4 Approval when its job has another population type.
    internal sealed class JobEffect
    {
        private static readonly string[] None = new string[0];

        public int Field;
        public float Sign = 1f;
        // The job must carry all of these descriptors, and none of the forbidden ones.
        public string[] RequiredTags = None;
        public string[] ForbiddenTags = None;
        // When set: the value counts once per co-worker carrying this descriptor (e.g. Primordial Last Lord).
        public string PerCoworkerDescriptor;
        // The game's formula (reverse Polish), with TypeOfPopulationCount = population types in the job.
        public int[] Ops = new int[0];
        public float[] Constants = new float[0];
        public string[] Properties = None;

        public bool AppliesIn(JobCategory category)
        {
            foreach (string tag in RequiredTags)
            {
                if (!category.Tags.Contains(tag))
                {
                    return false;
                }
            }
            foreach (string tag in ForbiddenTags)
            {
                if (category.Tags.Contains(tag))
                {
                    return false;
                }
            }
            return true;
        }

        // NaN when the formula uses something the mod can't evaluate.
        public float Evaluate(int typesInJob)
        {
            if (Ops.Length == 0)
            {
                return Constants.Length > 0 ? Constants[0] : 0f;
            }
            var stack = new Stack<float>();
            int constant = 0;
            int property = 0;
            foreach (int op in Ops)
            {
                switch (op)
                {
                    case 10:
                        if (constant >= Constants.Length)
                        {
                            return float.NaN;
                        }
                        stack.Push(Constants[constant++]);
                        break;
                    case 8:
                    case 9:
                        if (property >= Properties.Length || Properties[property++] != "TypeOfPopulationCount")
                        {
                            return float.NaN;
                        }
                        stack.Push(typesInJob);
                        break;
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                    case 5:
                    case 6:
                    case 7:
                        if (stack.Count < 2)
                        {
                            return float.NaN;
                        }
                        float b = stack.Pop();
                        float a = stack.Pop();
                        stack.Push(Apply(op, a, b));
                        break;
                    default:
                        return float.NaN;
                }
            }
            return stack.Count == 1 ? stack.Pop() : float.NaN;
        }

        private static float Apply(int op, float a, float b)
        {
            switch (op)
            {
                case 0: return a + b;
                case 1: return a - b;
                case 2: return a * b;
                case 3: return b == 0f ? 0f : a / b;
                case 5: return (float)Math.Pow(a, b);
                case 6: return Math.Max(a, b);
                default: return Math.Min(a, b);
            }
        }
    }
}
