using System;
using System.Collections.Generic;
using System.Linq;

namespace PopulationPlanner
{
    // Two populations of different types trading jobs, done with the game's "move to job" order. Head-counts per job
    // stay as they were (the city's job strategy set them); only who works where changes. Or one population moving
    // into a free slot: for approval, or into the job where its own bonus applies (out of one where its own malus does).
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
        // No partner: a plain move into a free slot.
        public bool IsMove => Partner == 0UL;
        // A plain move made because approval comes first (otherwise it is for the population's own job effects).
        public bool ForApproval;
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
        // Only changes that raise approval (to work out what reaching an approval level would cost).
        public bool ApprovalMovesOnly;
    }

    // What the approval moves needed to reach a level would change in a city, all together.
    internal sealed class ApprovalMoves
    {
        public int Count;
        public bool Reached;
        // Unweighted, per turn; approval included.
        public float[] Delta = new float[Yield.Count];
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
        // No swap for them, nor a move into a free slot, gains at least the minimum within the approval and food limits.
        NoGain,
    }

    internal static class JobOptimizer
    {
        private const float FoodMargin = 1.25f;
        // A population not grown yet, tried in a copy of the jobs (0 means "none" to Without).
        private const ulong Newcomer = ulong.MaxValue;

        public static JobSwap BestSwap(GameState state, CityState city, float minGain, Func<ulong, bool> isFrozen)
        {
            return BestSwap(state, city, new JobRules { MinGain = minGain, IsFrozen = isFrozen });
        }

        // The swap (or move into a free slot) with the largest gain above the minimum, or null.
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
                        // A plain move into the free slot, changing the head-counts: approval first, or for the
                        // population's own job effects (e.g. a Green Scion into a free Citizens slot).
                        bool ownMove = OwnValue(state, pop.Type, to, weights) - OwnValue(state, pop.Type, from, weights) > 1e-4f;
                        if (rules.ApprovalFirst || ownMove)
                        {
                            Offer(from, pop, to, null, twoOrders: false, ownMove);
                        }
                    }
                }
            }
            return best;

            void Offer(JobCategory from, JobPop pop, JobCategory to, JobPop? partner, bool twoOrders, bool ownMove = false)
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
                if (rules.ApprovalMovesOnly && delta[Yield.Approval] <= 1e-3f)
                {
                    return;
                }
                bool forApproval = !partner.HasValue && rules.ApprovalFirst && delta[Yield.Approval] > 1e-3f;
                if (!partner.HasValue && !forApproval && !ownMove)
                {
                    // Plain moves change the head-counts the city's strategy chose: only for approval, or for the
                    // population's own job effects.
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
                int rank = partner.HasValue ? (twoOrders ? 1 : 0) : 2;
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
                        ForApproval = forApproval,
                        Delta = delta,
                    };
                }
            }
        }

        private static int Rank(JobSwap swap) => swap.IsMove ? 2 : swap.TwoOrders ? 1 : 0;

        // What a population's own job effects are worth in a job, weighted: those that don't depend on who works next
        // to it (e.g. Green Scion +4 Food as a Citizen, Last Lord -3 Approval as a Citizen).
        private static float OwnValue(GameState state, string type, JobCategory job, float[] weights)
        {
            if (!state.JobEffects.TryGetValue(type, out List<JobEffect> effects))
            {
                return 0f;
            }
            float value = 0f;
            foreach (JobEffect effect in effects)
            {
                if (effect.PerCoworkerDescriptor != null || !effect.AppliesIn(job))
                {
                    continue;
                }
                float alone = effect.Evaluate(1);
                if (float.IsNaN(alone) || Math.Abs(alone - effect.Evaluate(2)) > 1e-4f)
                {
                    continue;
                }
                value += weights[effect.Field] * effect.Sign * alone;
            }
            return value;
        }

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

        // The approval moves the city would make to reach the target, best first as approval-first makes them, tried on a
        // copy of its jobs: what they change together, and whether they get there.
        public static ApprovalMoves PlanApprovalMoves(GameState state, CityState city, float approval, float food, float target, float minGain, Func<ulong, bool> isFrozen)
        {
            var result = new ApprovalMoves();
            if (city.Jobs == null)
            {
                return result;
            }
            var copy = new CityState { Guid = city.Guid, Jobs = city.Jobs.Clone() };
            var moved = new HashSet<ulong>();
            while (approval < target && result.Count < 100)
            {
                var rules = new JobRules
                {
                    MinGain = minGain,
                    IsFrozen = guid => moved.Contains(guid) || (isFrozen != null && isFrozen(guid)),
                    ApprovalFirst = true,
                    ApprovalMovesOnly = true,
                    CurrentApproval = approval,
                    CurrentFood = food,
                };
                JobSwap swap = BestSwap(state, copy, rules);
                if (swap == null)
                {
                    break;
                }
                Apply(copy.Jobs, swap);
                moved.Add(swap.Pop);
                moved.Add(swap.Partner);
                for (int f = 0; f < Yield.Count; f++)
                {
                    result.Delta[f] += swap.Delta[f];
                }
                approval += swap.Delta[Yield.Approval];
                food += swap.Delta[Yield.Food];
                result.Count++;
            }
            result.Reached = approval >= target;
            return result;
        }

        // Makes a swap in a copy of the jobs, as the game would: the population into its new job, and the partner (the
        // one a full job sends out, or the second order's) into the job left.
        public static void Apply(JobState jobs, JobSwap swap)
        {
            JobCategory from = jobs.Find(swap.From);
            JobCategory to = jobs.Find(swap.To);
            int index = from.Pops.FindIndex(p => p.Guid == swap.Pop);
            JobPop pop = from.Pops[index];
            from.Pops.RemoveAt(index);
            if (!swap.IsMove)
            {
                int partnerIndex = to.Pops.FindIndex(p => p.Guid == swap.Partner);
                from.Pops.Add(to.Pops[partnerIndex]);
                to.Pops.RemoveAt(partnerIndex);
            }
            to.Pops.Add(pop);
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

        // Each job's slots and yields per population: what a construction (e.g. Communal Habitations), an improvement or
        // a governor changes, and who works where doesn't.
        public static string Layout(JobState jobs)
        {
            var text = new System.Text.StringBuilder();
            foreach (JobCategory job in jobs.Categories)
            {
                text.Append(job.Guid).Append(':').Append(job.Slots);
                foreach (float value in job.Base)
                {
                    text.Append(',').Append(Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                text.Append(';');
            }
            return text.ToString();
        }

        // "slots Citizens 3 -> 4, Scribes 3 -> 4; yields per population in Artisans": how a city's jobs changed.
        public static string LayoutChanges(JobState before, JobState after, Func<string, string> jobName)
        {
            var slots = new List<string>();
            var yields = new List<string>();
            foreach (JobCategory job in after.Categories)
            {
                JobCategory old = before.Find(job.Guid);
                int oldSlots = old?.Slots ?? 0;
                if (oldSlots != job.Slots)
                {
                    slots.Add($"{jobName(job.Name)} {oldSlots} -> {job.Slots}");
                }
                if (old != null && Enumerable.Range(0, Yield.Count).Any(f => Math.Abs(old.Base[f] - job.Base[f]) > 0.005f))
                {
                    yields.Add(jobName(job.Name));
                }
            }
            foreach (JobCategory old in before.Categories)
            {
                if (after.Find(old.Guid) == null)
                {
                    slots.Add($"{jobName(old.Name)} {old.Slots} -> 0");
                }
            }
            var parts = new List<string>();
            if (slots.Count > 0)
            {
                parts.Add("slots " + string.Join(", ", slots.ToArray()));
            }
            if (yields.Count > 0)
            {
                parts.Add("yields per population in " + string.Join(", ", yields.ToArray()));
            }
            return string.Join("; ", parts.ToArray());
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

        // Approval one more population of this type adds in the city, in the job it will work: the one the game places it
        // in, or the one the mod then moves it to for its own job effects (when it optimizes the city's jobs). Its own
        // effects, its job's approval per population and its effect on co-workers count (e.g. a new type next to Xavius).
        // 0 when no job has room: it would be Destitute whatever its type.
        public static float ApprovalOfNewPop(GameState state, CityState city, string type, bool movedByMod = true, float minGain = 0.5f)
        {
            JobState jobs = city.Jobs;
            JobCategory placed = jobs == null ? null : PlacementJob(state, jobs, type);
            if (placed == null)
            {
                return 0f;
            }
            JobState after = jobs.Clone();
            after.Find(placed.Guid).Pops.Add(new JobPop(Newcomer, type, 0f));
            if (movedByMod)
            {
                // Only the newcomer may move: everyone else stays where they are.
                JobSwap move = BestSwap(state, new CityState { Guid = city.Guid, Jobs = after }, new JobRules { MinGain = minGain, IsFrozen = guid => guid != Newcomer });
                if (move != null && move.IsMove)
                {
                    Apply(after, move);
                }
            }
            return TotalApproval(state, after) - TotalApproval(state, jobs);
        }

        // The job the game gives a new population of this type: among the jobs with room (with Balanced, only the least
        // filled ones), the one where the job's yields per population plus the population's own effects there are worth
        // the most by the city's job strategy (the game leaves out effects that depend on co-workers); the later one on a
        // tie, as the game does. Null when no job has room.
        public static JobCategory PlacementJob(GameState state, JobState jobs, string type)
        {
            float lowest = float.MaxValue;
            foreach (JobCategory job in jobs.Categories)
            {
                if (job.Slots > 0 && !job.IsFull)
                {
                    lowest = Math.Min(lowest, (float)job.Pops.Count / job.Slots);
                }
            }
            JobCategory best = null;
            float bestScore = float.NegativeInfinity;
            foreach (JobCategory job in jobs.Categories)
            {
                if (job.Slots <= 0 || job.IsFull || (jobs.FillLowestFirst && (float)job.Pops.Count / job.Slots > lowest + 1e-6f))
                {
                    continue;
                }
                float score = OwnValue(state, type, job, jobs.Weights);
                for (int f = 0; f < Yield.Count; f++)
                {
                    score += jobs.Weights[f] * job.Base[f];
                }
                if (score >= bestScore - 1e-4f)
                {
                    best = job;
                    bestScore = Math.Max(bestScore, score);
                }
            }
            return best;
        }

        private static float TotalApproval(GameState state, JobState jobs)
        {
            float total = 0f;
            foreach (JobCategory job in jobs.Categories)
            {
                total += Yields(state, job, job.Pops)[Yield.Approval];
            }
            return total;
        }

        private static bool HasDescriptor(GameState state, string type, string descriptor)
        {
            return state.TypeDescriptors.TryGetValue(type, out HashSet<string> descriptors) && descriptors.Contains(descriptor);
        }
    }
}
