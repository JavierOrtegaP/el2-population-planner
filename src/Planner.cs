using System;
using System.Collections.Generic;
using System.Linq;

namespace PopulationPlanner
{
    internal enum PlanKind
    {
        // The city is below its minimum approval: the population adding the most approval here.
        Approval,
        // Add one of a population whose last bonus is unlocked, so the bonus also applies in this city.
        Spread,
        // The city's own target.
        CityTarget,
        // The global order.
        Aim,
        // A city that is not growing: put it on the first population it can aim for, so it is ready when it grows.
        Ready,
        // Nothing left to aim for here: keep the city's current pick.
        Leave,
        // The current pick can't be grown or would be wasted; something else is picked.
        Fallback,
        // Picked by hand in the city screen.
        Held,
        // The player turned the mod off for this city.
        Off,
        // The game offers nothing the mod may pick here.
        NothingGrowable,
        // The faction does not grow population with food.
        NoFood,
    }

    internal enum PlanNote
    {
        None,
        // The city's target already has its last bonus.
        TargetDone,
        // The game won't let this city grow its target right now.
        TargetNotGrowable,
        // Enough of the city's target is already on the way in cities that grow sooner.
        TargetEnough,
    }

    internal sealed class CityPlan
    {
        public ulong Guid;
        public PlanKind Kind;
        // What the city should grow next; null = leave its pick alone.
        public string Pop;
        public PlanNote Note;
        public string NotePop;
        // For Approval: what the pick adds to the city's approval (estimate).
        public float ApprovalGain;
    }

    internal sealed class PlanOptions
    {
        public bool SpreadFirst = true;
        public bool ContinueAfterList = true;
        public Func<ulong, string, bool> IsBlocked = (guid, pop) => false;
        // Approval the city must keep (minimum + buffer); NaN or null = no minimum.
        public Func<CityState, float> ApprovalTarget;
        // The city's approval as the mod counts it (null = the game's net value).
        public Func<CityState, float> ApprovalOf;
        // Whether the mod optimizes the city's jobs (so it may move a new population to its bonus job), and the gain a
        // move needs.
        public Func<CityState, bool> JobsOptimized = city => true;
        public float JobMinGain = 0.5f;
    }

    internal sealed class PlanResult
    {
        public readonly Dictionary<ulong, CityPlan> Cities = new Dictionary<ulong, CityPlan>();
        // Populations being aimed for (last bonus not unlocked yet), in order.
        public readonly List<string> Order = new List<string>();
        // Cities that will grow each population next under this plan.
        public readonly Dictionary<string, int> Assigned = new Dictionary<string, int>(StringComparer.Ordinal);
        // The first population of Order that some city grows: the bonus being worked on.
        public string Current;

        public int AssignedTo(string pop) => Assigned.TryGetValue(pop, out int n) ? n : 0;
    }

    internal static class Planner
    {
        private sealed class Candidate
        {
            public CityState City;
            public CityPlan Plan;
            public CitySettings Settings;
            public List<string> Growable;
            public bool Assigned;
        }

        public static PlanResult Plan(GameState state, GameSettings settings, PlanOptions options)
        {
            var result = new PlanResult();
            result.Order.AddRange(AimOrder(state, settings, options.ContinueAfterList));
            var reserved = new Dictionary<string, int>(StringComparer.Ordinal);
            var open = new List<Candidate>();

            foreach (CityState city in state.Cities)
            {
                var plan = new CityPlan { Guid = city.Guid };
                result.Cities[city.Guid] = plan;
                CitySettings citySettings = settings.GetCity(city.Guid);
                if (state.CannotGrowWithFood)
                {
                    plan.Kind = PlanKind.NoFood;
                    continue;
                }
                if (citySettings != null && citySettings.Off)
                {
                    plan.Kind = PlanKind.Off;
                    continue;
                }
                if (citySettings != null && !string.IsNullOrEmpty(citySettings.HeldPop))
                {
                    plan.Kind = PlanKind.Held;
                    continue;
                }
                List<string> growable = Growable(state, settings, options, city);
                if (growable.Count == 0)
                {
                    plan.Kind = PlanKind.NothingGrowable;
                    continue;
                }
                open.Add(new Candidate { City = city, Plan = plan, Settings = citySettings, Growable = growable });
            }

            if (options.ApprovalTarget != null)
            {
                ApprovalFirst(state, open, reserved, result, options);
            }
            if (options.SpreadFirst)
            {
                Spread(state, settings, open, result);
            }
            CityTargets(state, open, reserved, result);
            Aim(state, open, reserved, result);
            if (!options.SpreadFirst)
            {
                Spread(state, settings, open, result);
            }
            Leftovers(state, open, result);

            result.Current = result.Order.FirstOrDefault(pop => result.AssignedTo(pop) > 0);
            return result;
        }

        // Populations still to aim for: the player's order, then (if allowed, or if the player set none) the others,
        // closest to their last bonus first.
        public static List<string> AimOrder(GameState state, GameSettings settings, bool continueAfterList)
        {
            var order = new List<string>();
            foreach (string pop in settings.Priority)
            {
                if (state.Types.TryGetValue(pop, out PopType type) && IsAimable(type, settings) && !order.Contains(pop))
                {
                    order.Add(pop);
                }
            }
            if (continueAfterList || settings.Priority.Count == 0)
            {
                IEnumerable<PopType> rest = state.Types.Values
                    .Where(t => IsAimable(t, settings) && !order.Contains(t.Name))
                    .OrderBy(t => t.Need)
                    .ThenByDescending(t => t.Count)
                    .ThenBy(t => t.Name, StringComparer.Ordinal);
                order.AddRange(rest.Select(t => t.Name));
            }
            return order;
        }

        public static bool IsAimable(PopType type, GameSettings settings)
        {
            return !type.ActionOnly && type.MaxLevel > 0 && !type.Done && !settings.IsSkipped(type.Name);
        }

        public static bool IsSpreadable(PopType type, GameSettings settings)
        {
            return type.Done && type.PresenceMatters && !type.ActionOnly && !settings.IsSkipped(type.Name) && !settings.IsNoSpread(type.Name);
        }

        private static List<string> Growable(GameState state, GameSettings settings, PlanOptions options, CityState city)
        {
            var list = new List<string>();
            foreach (GrowOption option in city.Options)
            {
                if (!state.Types.TryGetValue(option.Pop, out PopType type) || type.ActionOnly || settings.IsSkipped(option.Pop)
                    || options.IsBlocked(city.Guid, option.Pop) || list.Contains(option.Pop))
                {
                    continue;
                }
                list.Add(option.Pop);
            }
            return list;
        }

        // Cities below their minimum approval grow the population adding the most approval there, in the job it will work,
        // before anything else. When every choice adds the same (e.g. no job has room: any new population is Destitute),
        // approval can't decide and the normal plan goes on.
        private static void ApprovalFirst(GameState state, List<Candidate> open, Dictionary<string, int> reserved, PlanResult result, PlanOptions options)
        {
            Func<CityState, float> approvalOf = options.ApprovalOf ?? (city => city.ApprovalNet);
            foreach (Candidate candidate in open)
            {
                float needed = options.ApprovalTarget(candidate.City);
                if (float.IsNaN(needed) || approvalOf(candidate.City) >= needed)
                {
                    continue;
                }
                bool moved = options.JobsOptimized == null || options.JobsOptimized(candidate.City);
                var gains = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (string pop in candidate.Growable)
                {
                    gains[pop] = JobOptimizer.ApprovalOfNewPop(state, candidate.City, pop, moved, options.JobMinGain);
                }
                float best = gains.Values.Max();
                if (best - gains.Values.Min() < 0.5f)
                {
                    continue;
                }
                // Among the best: the current pick, then the order's.
                string pick = candidate.Growable
                    .Where(pop => gains[pop] >= best - 1e-3f)
                    .OrderBy(pop => pop == candidate.City.Growing ? 0 : 1)
                    .ThenBy(pop => result.Order.IndexOf(pop) < 0 ? int.MaxValue : result.Order.IndexOf(pop))
                    .ThenBy(pop => pop, StringComparer.Ordinal)
                    .First();
                Assign(candidate, pick, PlanKind.Approval, result);
                candidate.Plan.ApprovalGain = gains[pick];
                if (candidate.City.IsGrowing && state.Types.TryGetValue(pick, out PopType type) && !type.Done)
                {
                    // It still counts towards that population's bonus.
                    reserved[pick] = Reserved(reserved, pick) + 1;
                }
            }
        }

        // One population of every unlocked presence bonus in every city that lacks it.
        private static void Spread(GameState state, GameSettings settings, List<Candidate> open, PlanResult result)
        {
            List<string> spreadable = state.Types.Values
                .Where(t => IsSpreadable(t, settings))
                .OrderBy(t => PriorityIndex(settings, t.Name))
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => t.Name)
                .ToList();
            if (spreadable.Count == 0)
            {
                return;
            }
            foreach (Candidate candidate in open)
            {
                if (candidate.Assigned)
                {
                    continue;
                }
                List<string> missing = spreadable.Where(pop => candidate.City.Count(pop) == 0 && candidate.Growable.Contains(pop)).ToList();
                if (missing.Count == 0)
                {
                    continue;
                }
                // Keep the current pick when it is one of them.
                string pop = missing.Contains(candidate.City.Growing) ? candidate.City.Growing : missing[0];
                Assign(candidate, pop, PlanKind.Spread, result);
            }
        }

        private static void CityTargets(GameState state, List<Candidate> open, Dictionary<string, int> reserved, PlanResult result)
        {
            IEnumerable<IGrouping<string, Candidate>> byTarget = open
                .Where(c => !c.Assigned && !string.IsNullOrEmpty(c.Settings?.Target))
                .GroupBy(c => c.Settings.Target, StringComparer.Ordinal)
                .ToList();
            foreach (IGrouping<string, Candidate> group in byTarget)
            {
                string pop = group.Key;
                bool known = state.Types.TryGetValue(pop, out PopType type);
                int remaining = known ? type.Need - Reserved(reserved, pop) : 0;
                foreach (Candidate candidate in SoonestFirst(group, pop))
                {
                    if (!known || type.Done)
                    {
                        Note(candidate, PlanNote.TargetDone, pop);
                        continue;
                    }
                    if (!candidate.Growable.Contains(pop))
                    {
                        Note(candidate, PlanNote.TargetNotGrowable, pop);
                        continue;
                    }
                    if (candidate.City.IsGrowing)
                    {
                        if (remaining <= 0)
                        {
                            Note(candidate, PlanNote.TargetEnough, pop);
                            continue;
                        }
                        remaining--;
                        reserved[pop] = Reserved(reserved, pop) + 1;
                    }
                    Assign(candidate, pop, PlanKind.CityTarget, result);
                }
            }
        }

        // Fill each population of the order up to its last bonus, soonest-growing cities first.
        private static void Aim(GameState state, List<Candidate> open, Dictionary<string, int> reserved, PlanResult result)
        {
            foreach (string pop in result.Order)
            {
                PopType type = state.Types[pop];
                int remaining = type.Need - Reserved(reserved, pop);
                if (remaining <= 0)
                {
                    continue;
                }
                IEnumerable<Candidate> candidates = SoonestFirst(open.Where(c => !c.Assigned && c.City.IsGrowing && c.Growable.Contains(pop)), pop);
                foreach (Candidate candidate in candidates.ToList())
                {
                    if (remaining <= 0)
                    {
                        break;
                    }
                    remaining--;
                    reserved[pop] = Reserved(reserved, pop) + 1;
                    Assign(candidate, pop, PlanKind.Aim, result);
                }
            }
        }

        private static void Leftovers(GameState state, List<Candidate> open, PlanResult result)
        {
            foreach (Candidate candidate in open)
            {
                if (candidate.Assigned)
                {
                    continue;
                }
                CityState city = candidate.City;
                string first = result.Order.FirstOrDefault(candidate.Growable.Contains);
                if (!city.IsGrowing && first != null)
                {
                    Assign(candidate, first, PlanKind.Ready, result);
                    continue;
                }
                if (candidate.Growable.Contains(city.Growing) && !IsWasted(state, city, city.Growing))
                {
                    candidate.Plan.Kind = PlanKind.Leave;
                    candidate.Assigned = true;
                    continue;
                }
                // The current pick can't be grown, or adds to a bonus that is unlocked and already applies here.
                string fallback = first
                    ?? candidate.Growable.FirstOrDefault(pop => !IsWasted(state, city, pop))
                    ?? (candidate.Growable.Contains(city.Growing) ? null : candidate.Growable[0]);
                if (fallback == null)
                {
                    candidate.Plan.Kind = PlanKind.Leave;
                    candidate.Assigned = true;
                    continue;
                }
                Assign(candidate, fallback, PlanKind.Fallback, result);
            }
        }

        // One more of this population in this city adds nothing towards a bonus.
        private static bool IsWasted(GameState state, CityState city, string pop)
        {
            return state.Types.TryGetValue(pop, out PopType type) && type.Done && (!type.PresenceMatters || city.Count(pop) > 0);
        }

        private static IEnumerable<Candidate> SoonestFirst(IEnumerable<Candidate> candidates, string pop)
        {
            return candidates
                .OrderBy(c => c.City.IsGrowing ? 0 : 1)
                .ThenBy(c => Turns(c.City.TurnsFor(pop)))
                .ThenBy(c => c.City.Growing == pop ? 0 : 1)
                .ThenBy(c => c.City.Guid);
        }

        private static double Turns(float turns) => float.IsInfinity(turns) || turns < 0f ? double.MaxValue : Math.Ceiling(turns);

        private static void Assign(Candidate candidate, string pop, PlanKind kind, PlanResult result)
        {
            candidate.Assigned = true;
            candidate.Plan.Kind = kind;
            candidate.Plan.Pop = pop;
            result.Assigned[pop] = result.AssignedTo(pop) + 1;
        }

        private static void Note(Candidate candidate, PlanNote note, string pop)
        {
            candidate.Plan.Note = note;
            candidate.Plan.NotePop = pop;
        }

        private static int Reserved(Dictionary<string, int> reserved, string pop) => reserved.TryGetValue(pop, out int n) ? n : 0;

        private static int PriorityIndex(GameSettings settings, string pop)
        {
            int index = settings.Priority.IndexOf(pop);
            return index < 0 ? int.MaxValue : index;
        }
    }
}
