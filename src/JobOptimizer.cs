using System;
using System.Collections.Generic;
using System.Linq;

namespace PopulationPlanner
{
    // Two populations of different types trading jobs, done with the game's "move to job" order. Head-counts per job
    // stay as they were (the city's job strategy set them); only who works where changes.
    internal sealed class JobSwap
    {
        public ulong City;
        // Moves into To.
        public ulong Pop;
        public string PopType;
        public ulong From;
        public ulong To;
        // Moves into From: sent out by the game itself when To is full, else by a second order.
        public ulong Partner;
        public string PartnerType;
        public bool TwoOrders;
        // Weighted by the city's job strategy.
        public float Gain;
        // No partner: a plain move into a free slot (only when approval comes first).
        public bool IsMove => Partner == 0UL;
        // Change per yield, unweighted.
        public float[] Delta = new float[Yield.Count];
    }

    internal sealed class JobRules
    {
        public float MinGain = 0.5f;
        // Populations moved this turn: not touched again, so swaps can't undo each other.
        public Func<ulong, bool> IsFrozen;
        // The city is below its minimum approval: approval weighs ApprovalBoost times more, and populations may also
        // move into free slots (changing how many work each job), e.g. to Scribes, whose job costs no approval.
        public bool ApprovalFirst;
        public float ApprovalBoost = 10f;
        // No swap may take the city's approval below this (NaN = no floor).
        public float ApprovalFloor = float.NaN;
        public float CurrentApproval;
        // The city's food surplus: no change may turn it negative (NaN = not checked).
        public float CurrentFood = float.NaN;
    }

    // Populations working outside the job where a bonus of their own applies (e.g. Daughter of Bor +1 Industry as an
    // Artisan), when no swap moves them: how many of a type, that job, and why. Shown to the player.
    internal sealed class MissedBonus
    {
        public string Type;
        public JobCategory Job;
        public int Count;
        // Of them, moved already this turn.
        public int Frozen;
        public MissedReason Reason;
    }

    internal enum MissedReason
    {
        // Every one of them was already moved this turn: each population moves at most once a turn.
        MovedThisTurn,
        // The job is full, and the worker the game would send out to make room is of the same type.
        FullOfSameType,
        // No swap for them gains at least the minimum within the approval and food limits.
        NoGain,
    }

    internal static class JobOptimizer
    {
        private const float FoodMargin = 1.25f;

        public static JobSwap BestSwap(GameState state, CityState city, float minGain, Func<ulong, bool> isFrozen)
        {
            return BestSwap(state, city, new JobRules { MinGain = minGain, IsFrozen = isFrozen });
        }

        // The swap (or, approval first, move) with the largest gain above the minimum, or null.
        public static JobSwap BestSwap(GameState state, CityState city, JobRules rules)
        {
            JobState jobs = city.Jobs;
            if (jobs == null || !jobs.CanReassign || jobs.Categories.Count < 2)
            {
                return null;
            }
            float minGain = rules.MinGain;
            Func<ulong, bool> isFrozen = rules.IsFrozen;
            var weights = (float[])jobs.Weights.Clone();
            if (rules.ApprovalFirst)
            {
                weights[Yield.Approval] = Math.Max(weights[Yield.Approval], 0.5f) * rules.ApprovalBoost;
            }
            var current = new Dictionary<ulong, float[]>();
            foreach (JobCategory category in jobs.Categories)
            {
                current[category.Guid] = Yields(state, category, category.Pops);
            }
            JobSwap best = null;
            foreach (JobCategory from in jobs.Categories)
            {
                foreach (JobPop pop in from.Pops)
                {
                    if (!IsMovable(state, pop, isFrozen))
                    {
                        continue;
                    }
                    foreach (JobCategory to in jobs.Categories)
                    {
                        if (ReferenceEquals(to, from) || to.Slots <= 0)
                        {
                            continue;
                        }
                        if (to.IsFull)
                        {
                            // One order: a full job takes the population and sends out its lowest-scoring one.
                            Offer(from, pop, to, Worst(to), twoOrders: false);
                            continue;
                        }
                        // Two orders: into the free slot, then one of that job's populations into the slot left.
                        foreach (JobPop partner in OnePerType(to.Pops, state, isFrozen))
                        {
                            Offer(from, pop, to, partner, twoOrders: true);
                        }
                        if (rules.ApprovalFirst)
                        {
                            // Approval first: a plain move into the free slot, changing the head-counts.
                            Offer(from, pop, to, null, twoOrders: false);
                        }
                    }
                }
            }
            return best;

            void Offer(JobCategory from, JobPop pop, JobCategory to, JobPop? partner, bool twoOrders)
            {
                if (partner.HasValue && (partner.Value.Type == pop.Type || !IsMovable(state, partner.Value, isFrozen)))
                {
                    return;
                }
                List<JobPop> fromAfter = partner.HasValue ? Replace(from.Pops, pop.Guid, partner.Value) : Without(from.Pops, pop.Guid);
                List<JobPop> toAfter = partner.HasValue ? Replace(to.Pops, partner.Value.Guid, pop) : Without(to.Pops, 0UL, pop);
                float[] newFrom = Yields(state, from, fromAfter);
                float[] newTo = Yields(state, to, toAfter);
                var delta = new float[Yield.Count];
                float gain = 0f;
                for (int f = 0; f < Yield.Count; f++)
                {
                    delta[f] = newFrom[f] + newTo[f] - current[from.Guid][f] - current[to.Guid][f];
                    gain += weights[f] * delta[f];
                }
                if (!partner.HasValue && delta[Yield.Approval] <= 1e-3f)
                {
                    // Plain moves change the head-counts the city's strategy chose: only ever for approval.
                    return;
                }
                if (!float.IsNaN(rules.ApprovalFloor) && delta[Yield.Approval] < -1e-4f && rules.CurrentApproval + delta[Yield.Approval] < rules.ApprovalFloor)
                {
                    // Would take the city below its minimum approval.
                    return;
                }
                if (!float.IsNaN(rules.CurrentFood) && delta[Yield.Food] < -1e-4f && rules.CurrentFood + delta[Yield.Food] * FoodMargin < 0f)
                {
                    // Would make the city starve (with a margin: food bonuses in percent make the real loss larger).
                    return;
                }
                // Ties go to keeping head-counts, then to fewer orders.
                int rank = partner.HasValue ? (twoOrders ? 2 : 0) : 1;
                bool better = best == null || gain > best.Gain + 1e-4f || (Math.Abs(gain - best.Gain) <= 1e-4f && rank < Rank(best));
                // At least the minimum gain: e.g. a +1 Industry job bonus at a Food focus (Industry x0.5) is worth exactly 0.5.
                if (gain >= minGain - 1e-4f && better)
                {
                    best = new JobSwap
                    {
                        City = city.Guid,
                        Pop = pop.Guid,
                        PopType = pop.Type,
                        From = from.Guid,
                        To = to.Guid,
                        Partner = partner?.Guid ?? 0UL,
                        PartnerType = partner?.Type,
                        TwoOrders = twoOrders,
                        Gain = gain,
                        Delta = delta,
                    };
                }
            }
        }

        private static int Rank(JobSwap swap) => swap.IsMove ? 1 : swap.TwoOrders ? 2 : 0;

        // What the populations of one job yield together, in the terms of the game's own job scoring: the job's yield
        // per population plus each population's job effects.
        public static float[] Yields(GameState state, JobCategory category, IList<JobPop> pops)
        {
            var total = new float[Yield.Count];
            int types = pops.Select(p => p.Type).Distinct(StringComparer.Ordinal).Count();
            foreach (JobPop pop in pops)
            {
                for (int f = 0; f < Yield.Count; f++)
                {
                    total[f] += category.Base[f];
                }
                if (!state.JobEffects.TryGetValue(pop.Type, out List<JobEffect> effects))
                {
                    continue;
                }
                foreach (JobEffect effect in effects)
                {
                    if (!effect.AppliesIn(category))
                    {
                        continue;
                    }
                    float value = effect.Evaluate(types);
                    if (float.IsNaN(value))
                    {
                        continue;
                    }
                    if (effect.PerCoworkerDescriptor != null)
                    {
                        value *= pops.Count(other => other.Guid != pop.Guid && HasDescriptor(state, other.Type, effect.PerCoworkerDescriptor));
                    }
                    total[effect.Field] += effect.Sign * value;
                }
            }
            return total;
        }

        // The population the game sends out of a full job: the first one with the lowest score.
        public static JobPop Worst(JobCategory category)
        {
            JobPop worst = category.Pops[0];
            foreach (JobPop pop in category.Pops)
            {
                if (pop.Score < worst.Score)
                {
                    worst = pop;
                }
            }
            return worst;
        }

        // Populations outside the job where a plain bonus of their own applies, grouped by type and job, with the reason
        // nothing moves them. Plain bonuses only: mixing effects depend on the co-workers, and maluses aren't bonuses.
        public static List<MissedBonus> MissedBonuses(GameState state, CityState city, Func<ulong, bool> isFrozen)
        {
            var result = new List<MissedBonus>();
            JobState jobs = city.Jobs;
            if (jobs == null)
            {
                return result;
            }
            foreach (JobCategory from in jobs.Categories)
            {
                foreach (JobPop pop in from.Pops)
                {
                    if (state.FixedJobTypes.Contains(pop.Type) || !state.JobEffects.TryGetValue(pop.Type, out List<JobEffect> effects))
                    {
                        continue;
                    }
                    JobCategory target = BonusJob(effects, jobs, from);
                    if (target == null)
                    {
                        continue;
                    }
                    MissedBonus entry = result.Find(m => m.Type == pop.Type && ReferenceEquals(m.Job, target));
                    if (entry == null)
                    {
                        entry = new MissedBonus { Type = pop.Type, Job = target };
                        result.Add(entry);
                    }
                    entry.Count++;
                    if (isFrozen != null && isFrozen(pop.Guid))
                    {
                        entry.Frozen++;
                    }
                }
            }
            foreach (MissedBonus entry in result)
            {
                if (entry.Frozen == entry.Count)
                {
                    entry.Reason = MissedReason.MovedThisTurn;
                }
                else if (entry.Job.IsFull && entry.Job.Pops.Count > 0 && Worst(entry.Job).Type == entry.Type)
                {
                    entry.Reason = MissedReason.FullOfSameType;
                }
                else
                {
                    entry.Reason = MissedReason.NoGain;
                }
            }
            return result;
        }

        // "Citizens 6/6 (3 Sandshaper, 2 Last Lord, 1 Gorog), Artisans 4/4 (4 Daughter of Bor)"
        public static string Composition(JobState jobs, Func<string, string> popName, Func<string, string> jobName)
        {
            return string.Join(", ", jobs.Categories.Select(job =>
            {
                string[] counts = job.Pops.GroupBy(p => p.Type, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {popName(g.Key)}")
                    .ToArray();
                return $"{jobName(job.Name)} {job.Pops.Count}/{job.Slots}" + (counts.Length > 0 ? $" ({string.Join(", ", counts)})" : string.Empty);
            }).ToArray());
        }

        // A job of the city, other than the current one, where one of these plain bonuses applies; null when the current
        // job already gets one, or no job of the city would.
        private static JobCategory BonusJob(List<JobEffect> effects, JobState jobs, JobCategory current)
        {
            foreach (JobEffect effect in effects)
            {
                if (IsPlainBonus(effect) && effect.AppliesIn(current))
                {
                    return null;
                }
            }
            foreach (JobEffect effect in effects)
            {
                if (!IsPlainBonus(effect))
                {
                    continue;
                }
                foreach (JobCategory job in jobs.Categories)
                {
                    if (!ReferenceEquals(job, current) && job.Slots > 0 && effect.AppliesIn(job))
                    {
                        return job;
                    }
                }
            }
            return null;
        }

        // A population's own job bonus: positive, tied to a job, and the same whoever works next to it.
        private static bool IsPlainBonus(JobEffect effect)
        {
            if (effect.Sign <= 0f || effect.RequiredTags.Length == 0 || effect.PerCoworkerDescriptor != null)
            {
                return false;
            }
            float alone = effect.Evaluate(1);
            float mixed = effect.Evaluate(2);
            return !float.IsNaN(alone) && alone > 0f && Math.Abs(alone - mixed) < 1e-4f;
        }

        // "+4 Approval, -1 Food"
        public static string Describe(float[] delta)
        {
            var parts = new List<string>();
            for (int f = 0; f < Yield.Count; f++)
            {
                if (Math.Abs(delta[f]) >= 0.05f)
                {
                    parts.Add((delta[f] > 0 ? "+" : string.Empty) + delta[f].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + Yield.Labels[f]);
                }
            }
            return parts.Count == 0 ? "no change" : string.Join(", ", parts.ToArray());
        }

        private static bool IsMovable(GameState state, JobPop pop, Func<ulong, bool> isFrozen)
        {
            return !state.FixedJobTypes.Contains(pop.Type) && (isFrozen == null || !isFrozen(pop.Guid));
        }

        private static IEnumerable<JobPop> OnePerType(List<JobPop> pops, GameState state, Func<ulong, bool> isFrozen)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JobPop pop in pops)
            {
                if (IsMovable(state, pop, isFrozen) && seen.Add(pop.Type))
                {
                    yield return pop;
                }
            }
        }

        private static List<JobPop> Replace(List<JobPop> pops, ulong leaving, JobPop arriving) => Without(pops, leaving, arriving);

        // The populations minus one (guid 0 = none), plus an arriving one if given.
        private static List<JobPop> Without(List<JobPop> pops, ulong leaving, JobPop? arriving = null)
        {
            var result = new List<JobPop>(pops.Count + 1);
            foreach (JobPop pop in pops)
            {
                if (leaving == 0UL || pop.Guid != leaving)
                {
                    result.Add(pop);
                }
            }
            if (arriving.HasValue)
            {
                result.Add(arriving.Value);
            }
            return result;
        }

        // Approval one more population of this type adds in the city, working the job where that is highest among the
        // jobs with a free slot (its own effects, its job's approval per population, and its effect on co-workers, e.g.
        // a new type next to Xavius). 0 when no job has room: it would be Destitute whatever its type.
        public static float ApprovalOfNewPop(GameState state, CityState city, string type)
        {
            if (city.Jobs == null)
            {
                return 0f;
            }
            float best = float.NegativeInfinity;
            foreach (JobCategory job in city.Jobs.Categories)
            {
                if (job.Slots <= 0 || job.IsFull)
                {
                    continue;
                }
                float before = Yields(state, job, job.Pops)[Yield.Approval];
                float after = Yields(state, job, Without(job.Pops, 0UL, new JobPop(0UL, type, 0f)))[Yield.Approval];
                best = Math.Max(best, after - before);
            }
            return float.IsNegativeInfinity(best) ? 0f : best;
        }

        private static bool HasDescriptor(GameState state, string type, string descriptor)
        {
            return state.TypeDescriptors.TryGetValue(type, out HashSet<string> descriptors) && descriptors.Contains(descriptor);
        }
    }
}
