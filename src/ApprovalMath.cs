using System;

namespace PopulationPlanner
{
    // The approval level a city's jobs are held at, up to the player's minimum, when the mod weighs whether it pays.
    internal sealed class LevelChoice
    {
        // Off, Content, Happy or Jubilant.
        public string Level;
        // The highest level above it that was weighed and found not worth it; null when none was.
        public string Skipped;
        // For Skipped: what its job moves would change per turn (null when they can't get there), and what its bonus gives.
        public float[] MovesDelta;
        public float[] LevelGain;
    }

    // Whether an approval level is worth its job moves. Content always is: below it a city falls into crisis and in the
    // end rebels. Happy and Jubilant only add a percentage to the city's Food and Industry gain, so each is chased only
    // when that bonus is worth more than the job moves it takes to get there, both weighed by the city's job strategy.
    // No game types in here, so it can be tested without the game.
    internal static class ApprovalMath
    {
        private static readonly string[] AboveContent = { "Jubilant", "Happy" };

        public static LevelChoice Choose(GameState state, CityState city, string wanted, float buffer, float approval, float food, float minGain, Func<ulong, bool> isFrozen)
        {
            wanted = ApprovalRule.Normalize(wanted);
            var choice = new LevelChoice { Level = wanted };
            int wantedRank = Array.IndexOf(ApprovalRule.Levels, wanted);
            float[] current = Bonus(state, LevelAt(state, approval));
            if (wantedRank <= Array.IndexOf(ApprovalRule.Levels, "Content") || current == null)
            {
                // Off or Content: nothing to weigh. Unreadable game data: as configured.
                return choice;
            }
            foreach (string level in AboveContent)
            {
                if (Array.IndexOf(ApprovalRule.Levels, level) > wantedRank)
                {
                    continue;
                }
                float minimum = ApprovalRule.Minimum(state, level);
                float target = minimum + Math.Max(0f, buffer);
                float[] bonus = Bonus(state, ApprovalRule.Definition(level));
                // A city already at the level but within the buffer stands to lose it, i.e. to fall to the level below:
                // that is what the moves to the target are worth, not nothing.
                float[] from = approval >= minimum ? Bonus(state, LevelAt(state, minimum - 0.01f)) : current;
                if (approval >= target || bonus == null || from == null)
                {
                    // Already there (the floor keeps it), or nothing to weigh with.
                    choice.Level = level;
                    return choice;
                }
                float[] gain = Gain(city, current, from, bonus);
                ApprovalMoves moves = JobOptimizer.PlanApprovalMoves(state, city, approval, food, target, minGain, isFrozen);
                if (moves.Reached && Weighted(city, gain) > -Weighted(city, moves.Delta))
                {
                    choice.Level = level;
                    return choice;
                }
                if (choice.Skipped == null)
                {
                    choice.Skipped = level;
                    choice.MovesDelta = moves.Reached ? moves.Delta : null;
                    choice.LevelGain = gain;
                }
            }
            choice.Level = "Content";
            return choice;
        }

        // "Happy isn't worth it here now: ..." for the window and the log; null when every level up to the minimum is.
        public static string Explain(LevelChoice choice)
        {
            if (choice?.Skipped == null)
            {
                return null;
            }
            string holding = $"job moves only keep {choice.Level} (growth still favors approval)";
            if (choice.MovesDelta == null)
            {
                return $"{choice.Skipped} is out of reach with job moves now (no more populations that can move for approval), so {holding}";
            }
            return $"{choice.Skipped} isn't worth it here now: its job moves would change {JobOptimizer.Describe(choice.MovesDelta)} a turn, "
                + $"for {JobOptimizer.Describe(choice.LevelGain)} from {choice.Skipped}, so {holding}";
        }

        // The game's level definition for an approval value.
        public static string LevelAt(GameState state, float approval)
        {
            string level = "SettlementApproval_Unhappy";
            foreach (string name in new[] { "Content", "Happy", "Jubilant" })
            {
                if (approval >= ApprovalRule.Minimum(state, name))
                {
                    level = ApprovalRule.Definition(name);
                }
            }
            return level;
        }

        // [food, industry] fractions a level adds; null when the game data didn't say.
        private static float[] Bonus(GameState state, string definition)
        {
            return definition != null && state.ApprovalBonuses.TryGetValue(definition, out float[] bonus) ? bonus : null;
        }

        // What a level's bonus adds per turn over another's: percentages apply to the Food and Industry gain before any of
        // them (the one applied now is taken back out; other percentages are left in).
        private static float[] Gain(CityState city, float[] applied, float[] from, float[] bonus)
        {
            var gain = new float[Yield.Count];
            gain[Yield.Food] = Added(city.FoodGain, applied[0], from[0], bonus[0]);
            gain[Yield.Industry] = Added(city.IndustryGain, applied[1], from[1], bonus[1]);
            return gain;
        }

        private static float Added(float total, float appliedPercent, float fromPercent, float newPercent)
        {
            float divisor = 1f + appliedPercent;
            float basis = divisor > 0.01f ? total / divisor : 0f;
            return basis > 0f ? (newPercent - fromPercent) * basis : 0f;
        }

        // Weighed by the city's job strategy, approval left out (it is what the moves buy).
        private static float Weighted(CityState city, float[] yields)
        {
            float[] weights = city.Jobs?.Weights;
            float total = 0f;
            for (int f = 0; f < Yield.Count; f++)
            {
                if (f != Yield.Approval)
                {
                    total += yields[f] * (weights != null && f < weights.Length ? weights[f] : 1f);
                }
            }
            return total;
        }
    }
}
