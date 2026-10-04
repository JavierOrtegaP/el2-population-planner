using System;
using System.Collections.Generic;
using System.Linq;

namespace PopulationPlanner.Tests
{
    internal static class Program
    {
        private static int failures;
        private static int checks;

        private static int Main()
        {
            Run(nameof(AimsForClosestBonusByDefault), AimsForClosestBonusByDefault);
            Run(nameof(StopsAtThirtyInsteadOfOvershooting), StopsAtThirtyInsteadOfOvershooting);
            Run(nameof(SpreadsOnePerCityOnceUnlocked), SpreadsOnePerCityOnceUnlocked);
            Run(nameof(DoesNotGrowMoreOfAnUnlockedBonusThanOnePerCity), DoesNotGrowMoreOfAnUnlockedBonusThanOnePerCity);
            Run(nameof(PlayerOrderWins), PlayerOrderWins);
            Run(nameof(AimingNextMovesEveryCity), AimingNextMovesEveryCity);
            Run(nameof(CityTargetOverridesGlobal), CityTargetOverridesGlobal);
            Run(nameof(CityTargetDoneFollowsGlobal), CityTargetDoneFollowsGlobal);
            Run(nameof(CityTargetNotGrowableFollowsGlobal), CityTargetNotGrowableFollowsGlobal);
            Run(nameof(UngrowableTypeIsSkippedPerCity), UngrowableTypeIsSkippedPerCity);
            Run(nameof(TypeGrowableNowhereFallsThroughToNext), TypeGrowableNowhereFallsThroughToNext);
            Run(nameof(BlockedTypeIsAvoided), BlockedTypeIsAvoided);
            Run(nameof(HeldAndOffCitiesAreLeftAlone), HeldAndOffCitiesAreLeftAlone);
            Run(nameof(NonGrowingCityIsReadiedWithoutReserving), NonGrowingCityIsReadiedWithoutReserving);
            Run(nameof(LeavesPickWhenNothingLeft), LeavesPickWhenNothingLeft);
            Run(nameof(ReplacesWastedPick), ReplacesWastedPick);
            Run(nameof(NeedCountsMissingLevels), NeedCountsMissingLevels);
            Run(nameof(NoFoodFactionDoesNothing), NoFoodFactionDoesNothing);
            Run(nameof(SkippedTypeIsNeverPicked), SkippedTypeIsNeverPicked);
            Run(nameof(StopsAfterOrderWhenAsked), StopsAfterOrderWhenAsked);
            Run(nameof(KeepsCurrentPickOnTies), KeepsCurrentPickOnTies);
            Run(nameof(ActionOnlyTypesAreNeverPicked), ActionOnlyTypesAreNeverPicked);
            Run(nameof(NothingGrowableIsReported), NothingGrowableIsReported);
            Run(nameof(PresenceIndependentBonusIsNotSpread), PresenceIndependentBonusIsNotSpread);
            Run(nameof(SoonestCitiesGetTheLastFewPops), SoonestCitiesGetTheLastFewPops);
            Run(nameof(SimulatedGameFollowsTheRule), SimulatedGameFollowsTheRule);
            Run(nameof(JobFormulasMatchTheGameData), JobFormulasMatchTheGameData);
            Run(nameof(JobBonusPullsPopIntoItsJob), JobBonusPullsPopIntoItsJob);
            Run(nameof(GainOfExactlyTheMinimumCounts), GainOfExactlyTheMinimumCounts);
            Run(nameof(MissedBonusesSayWhy), MissedBonusesSayWhy);
            Run(nameof(MixingBonusSpreadsXavius), MixingBonusSpreadsXavius);
            Run(nameof(MixingMalusKeepsSolluskTogether), MixingMalusKeepsSolluskTogether);
            Run(nameof(StrategyWeightsDecideTradeOffs), StrategyWeightsDecideTradeOffs);
            Run(nameof(FullJobSwapUsesTheGamesEjectedPop), FullJobSwapUsesTheGamesEjectedPop);
            Run(nameof(FreeSlotSwapNeedsTwoOrders), FreeSlotSwapNeedsTwoOrders);
            Run(nameof(FixedAndFrozenPopsStayPut), FixedAndFrozenPopsStayPut);
            Run(nameof(NoSwapWhenNothingGains), NoSwapWhenNothingGains);
            Run(nameof(RepeatedSwapsConverge), RepeatedSwapsConverge);
            Run(nameof(LastLordLeavesCitizens), LastLordLeavesCitizens);
            Run(nameof(ApprovalFirstFillsFreeScribeSlots), ApprovalFirstFillsFreeScribeSlots);
            Run(nameof(ApprovalFloorBlocksCostlySwaps), ApprovalFloorBlocksCostlySwaps);
            Run(nameof(ApprovalFirstNeverReshufflesOrStarves), ApprovalFirstNeverReshufflesOrStarves);
            Run(nameof(LowApprovalCityGrowsXavius), LowApprovalCityGrowsXavius);
            Run(nameof(LowApprovalCityAvoidsLastLordAsCitizen), LowApprovalCityAvoidsLastLordAsCitizen);
            Run(nameof(ApprovalTargetsFollowSettings), ApprovalTargetsFollowSettings);
            Run(nameof(BonusTextIsPlain), BonusTextIsPlain);
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? $"All {checks} checks passed." : $"{failures} of {checks} checks FAILED.");
            return failures == 0 ? 0 : 1;
        }

        // ---- scenarios ----

        // No order set: aim for the type closest to its last bonus.
        private static void AimsForClosestBonusByDefault()
        {
            var state = NewState(Type("A", 25, 2), Type("B", 3, 0));
            AddCity(state, 1, "B", 2, "A", "B");
            AddCity(state, 2, "B", 3, "A", "B");
            AddCity(state, 3, "B", 4, "A", "B");
            PlanResult plan = Plan(state);
            Expect(plan.Order.SequenceEqual(new[] { "A", "B" }), "order A, B");
            ExpectPick(plan, 1, "A", PlanKind.Aim);
            ExpectPick(plan, 2, "A", PlanKind.Aim);
            ExpectPick(plan, 3, "A", PlanKind.Aim);
            Expect(plan.Current == "A", "current is A");
        }

        // 28 of 30: only two cities (the soonest) grow A; the rest move on to B.
        private static void StopsAtThirtyInsteadOfOvershooting()
        {
            var state = NewState(Type("A", 28, 2), Type("B", 3, 0));
            AddCity(state, 1, "A", 1, "A", "B");
            AddCity(state, 2, "A", 5, "A", "B");
            AddCity(state, 3, "A", 2, "A", "B");
            PlanResult plan = Plan(state);
            ExpectPick(plan, 1, "A", PlanKind.Aim);
            ExpectPick(plan, 3, "A", PlanKind.Aim);
            ExpectPick(plan, 2, "B", PlanKind.Aim);
        }

        // A reached 30: cities without A get one; the city that has A aims for the next bonus.
        private static void SpreadsOnePerCityOnceUnlocked()
        {
            var state = NewState(Type("A", 30, 3), Type("B", 3, 0));
            AddCity(state, 1, "B", 2, "A", "B");
            CityState withA = AddCity(state, 2, "A", 2, "A", "B");
            withA.Counts["A"] = 2;
            PlanResult plan = Plan(state);
            ExpectPick(plan, 1, "A", PlanKind.Spread);
            ExpectPick(plan, 2, "B", PlanKind.Aim);
        }

        private static void DoesNotGrowMoreOfAnUnlockedBonusThanOnePerCity()
        {
            var state = NewState(Type("A", 30, 3), Type("B", 3, 0));
            CityState city = AddCity(state, 1, "A", 2, "A", "B");
            city.Counts["A"] = 1;
            PlanResult plan = Plan(state);
            ExpectPick(plan, 1, "B", PlanKind.Aim);
        }

        private static void PlayerOrderWins()
        {
            var state = NewState(Type("A", 25, 2), Type("B", 3, 0));
            AddCity(state, 1, "A", 2, "A", "B");
            AddCity(state, 2, "A", 3, "A", "B");
            var settings = new GameSettings { Priority = new List<string> { "B" } };
            PlanResult plan = Plan(state, settings);
            Expect(plan.Order.SequenceEqual(new[] { "B", "A" }), "B first, then the rest");
            ExpectPick(plan, 1, "B", PlanKind.Aim);
            ExpectPick(plan, 2, "B", PlanKind.Aim);
        }

        // What the ★ button does: the chosen type moves to the top of the order.
        private static void AimingNextMovesEveryCity()
        {
            var state = NewState(Type("A", 10, 1), Type("B", 3, 0), Type("C", 12, 1));
            AddCity(state, 1, "A", 2, "A", "B", "C");
            AddCity(state, 2, "A", 3, "A", "B", "C");
            var settings = new GameSettings { Priority = new List<string> { "A", "B", "C" } };
            ExpectPick(Plan(state, settings), 1, "A", PlanKind.Aim);
            settings.Priority = new List<string> { "C", "A", "B" };
            PlanResult plan = Plan(state, settings);
            ExpectPick(plan, 1, "C", PlanKind.Aim);
            ExpectPick(plan, 2, "C", PlanKind.Aim);
        }

        private static void CityTargetOverridesGlobal()
        {
            var state = NewState(Type("A", 3, 0), Type("B", 3, 0), Type("C", 3, 0));
            AddCity(state, 1, "A", 2, "A", "B", "C");
            AddCity(state, 2, "A", 2, "A", "B", "C");
            var settings = new GameSettings { Priority = new List<string> { "B" } };
            settings.GetOrAddCity(1).Target = "C";
            PlanResult plan = Plan(state, settings);
            ExpectPick(plan, 1, "C", PlanKind.CityTarget);
            ExpectPick(plan, 2, "B", PlanKind.Aim);
        }

        private static void CityTargetDoneFollowsGlobal()
        {
            var state = NewState(Type("A", 3, 0), Type("C", 30, 3));
            CityState city = AddCity(state, 1, "C", 2, "A", "C");
            city.Counts["C"] = 3;
            var settings = new GameSettings();
            settings.GetOrAddCity(1).Target = "C";
            PlanResult plan = Plan(state, settings);
            ExpectPick(plan, 1, "A", PlanKind.Aim);
            Expect(plan.Cities[1].Note == PlanNote.TargetDone, "note: target done");
        }

        private static void CityTargetNotGrowableFollowsGlobal()
        {
            var state = NewState(Type("A", 3, 0), Type("C", 3, 0));
            AddCity(state, 1, "A", 2, "A");
            var settings = new GameSettings();
            settings.GetOrAddCity(1).Target = "C";
            PlanResult plan = Plan(state, settings);
            ExpectPick(plan, 1, "A", PlanKind.Aim);
            Expect(plan.Cities[1].Note == PlanNote.TargetNotGrowable, "note: target not growable");
        }

        // The game stopped offering B in city 2 (e.g. lost access): that city takes the next type.
        private static void UngrowableTypeIsSkippedPerCity()
        {
            var state = NewState(Type("A", 3, 0), Type("B", 10, 1));
            AddCity(state, 1, "B", 2, "A", "B");
            AddCity(state, 2, "B", 2, "A");
            var settings = new GameSettings { Priority = new List<string> { "B", "A" } };
            PlanResult plan = Plan(state, settings);
            ExpectPick(plan, 1, "B", PlanKind.Aim);
            ExpectPick(plan, 2, "A", PlanKind.Aim);
        }

        private static void TypeGrowableNowhereFallsThroughToNext()
        {
            PopType lost = Type("B", 10, 1);
            lost.AvailableToEmpire = false;
            var state = NewState(Type("A", 3, 0), lost);
            AddCity(state, 1, "A", 2, "A");
            AddCity(state, 2, "A", 2, "A");
            var settings = new GameSettings { Priority = new List<string> { "B", "A" } };
            PlanResult plan = Plan(state, settings);
            Expect(plan.Order.First() == "B", "B stays first in the order, ready for when it is back");
            ExpectPick(plan, 1, "A", PlanKind.Aim);
            ExpectPick(plan, 2, "A", PlanKind.Aim);
            Expect(plan.Current == "A", "current is A while B can't be grown");
        }

        // The game refused to add B to city 1: B is blocked there for a while.
        private static void BlockedTypeIsAvoided()
        {
            var state = NewState(Type("A", 3, 0), Type("B", 10, 1));
            AddCity(state, 1, "B", 2, "A", "B");
            AddCity(state, 2, "B", 2, "A", "B");
            var settings = new GameSettings { Priority = new List<string> { "B", "A" } };
            var options = new PlanOptions { IsBlocked = (guid, pop) => guid == 1 && pop == "B" };
            PlanResult plan = Planner.Plan(state, settings, options);
            ExpectPick(plan, 1, "A", PlanKind.Aim);
            ExpectPick(plan, 2, "B", PlanKind.Aim);
        }

        private static void HeldAndOffCitiesAreLeftAlone()
        {
            var state = NewState(Type("A", 3, 0), Type("B", 3, 0));
            AddCity(state, 1, "B", 2, "A", "B");
            AddCity(state, 2, "B", 2, "A", "B");
            AddCity(state, 3, "B", 2, "A", "B");
            var settings = new GameSettings { Priority = new List<string> { "A" } };
            settings.GetOrAddCity(1).HeldPop = "B";
            settings.GetOrAddCity(2).Off = true;
            PlanResult plan = Plan(state, settings);
            ExpectPick(plan, 1, null, PlanKind.Held);
            ExpectPick(plan, 2, null, PlanKind.Off);
            ExpectPick(plan, 3, "A", PlanKind.Aim);
        }

        // A city without food surplus gets the first aim, but does not take a slot from cities that will grow.
        private static void NonGrowingCityIsReadiedWithoutReserving()
        {
            var state = NewState(Type("A", 29, 2), Type("B", 3, 0));
            AddCity(state, 1, "B", 2, "A", "B");
            CityState starving = AddCity(state, 2, "B", float.PositiveInfinity, "A", "B");
            starving.FoodNet = -2f;
            AddCity(state, 3, "B", 4, "A", "B");
            PlanResult plan = Plan(state);
            ExpectPick(plan, 1, "A", PlanKind.Aim);
            ExpectPick(plan, 3, "B", PlanKind.Aim);
            ExpectPick(plan, 2, "A", PlanKind.Ready);
        }

        private static void LeavesPickWhenNothingLeft()
        {
            var state = NewState(Type("A", 30, 3), Type("B", 30, 3));
            CityState city = AddCity(state, 1, "B", 2, "A", "B");
            city.Counts["A"] = 1;
            city.Counts["B"] = 4;
            PlanResult plan = Plan(state);
            Expect(plan.Order.Count == 0, "nothing to aim for");
            ExpectPick(plan, 1, null, PlanKind.Leave);
        }

        // Everything is unlocked; the city grows an unlocked type it already has, while another unlocked type is
        // missing here (spreading turned off for it): picking the missing one still beats wasting the growth.
        private static void ReplacesWastedPick()
        {
            var state = NewState(Type("A", 30, 3), Type("B", 30, 3));
            CityState city = AddCity(state, 1, "A", 2, "A", "B");
            city.Counts["A"] = 2;
            var settings = new GameSettings { NoSpread = new List<string> { "B" } };
            PlanResult plan = Plan(state, settings);
            ExpectPick(plan, 1, "B", PlanKind.Fallback);
        }

        // The game unlocks one level per population added: over the threshold but a level behind still needs growth.
        private static void NeedCountsMissingLevels()
        {
            Expect(Type("A", 31, 2).Need == 1, "31 pops at level 2 needs 1");
            Expect(Type("A", 12, 0).Need == 18, "12 pops needs 18");
            Expect(Type("A", 30, 3).Need == 0, "unlocked needs 0");
            Expect(Type("A", 4, 0, thresholds: new[] { 3, 6, 9 }).Need == 5, "Divined-like 3/6/9");
        }

        private static void NoFoodFactionDoesNothing()
        {
            var state = NewState(Type("A", 3, 0));
            state.CannotGrowWithFood = true;
            AddCity(state, 1, "A", 2, "A");
            ExpectPick(Plan(state), 1, null, PlanKind.NoFood);
        }

        private static void SkippedTypeIsNeverPicked()
        {
            var state = NewState(Type("A", 25, 2), Type("B", 3, 0));
            AddCity(state, 1, "A", 2, "A", "B");
            var settings = new GameSettings { Skipped = new List<string> { "A" } };
            PlanResult plan = Plan(state, settings);
            Expect(!plan.Order.Contains("A"), "A not in the order");
            ExpectPick(plan, 1, "B", PlanKind.Aim);
        }

        private static void StopsAfterOrderWhenAsked()
        {
            var state = NewState(Type("A", 30, 3), Type("B", 3, 0));
            CityState city = AddCity(state, 1, "B", 2, "A", "B");
            city.Counts["A"] = 1;
            var settings = new GameSettings { Priority = new List<string> { "A" } };
            PlanResult plan = Planner.Plan(state, settings, new PlanOptions { ContinueAfterList = false });
            Expect(plan.Order.Count == 0, "only A was asked for, and it is unlocked");
            ExpectPick(plan, 1, null, PlanKind.Leave);
        }

        // Same turns to grow and only one slot left: the city already growing A keeps it (no flip-flopping).
        private static void KeepsCurrentPickOnTies()
        {
            var state = NewState(Type("A", 29, 2), Type("B", 3, 0));
            AddCity(state, 1, "B", 3, "A", "B");
            AddCity(state, 2, "A", 3, "A", "B");
            PlanResult plan = Plan(state);
            ExpectPick(plan, 2, "A", PlanKind.Aim);
            ExpectPick(plan, 1, "B", PlanKind.Aim);
        }

        private static void ActionOnlyTypesAreNeverPicked()
        {
            PopType called = Type("Called", 0, 0);
            called.ActionOnly = true;
            var state = NewState(called, Type("A", 3, 0));
            AddCity(state, 1, "Called", 2, "Called", "A");
            PlanResult plan = Plan(state);
            Expect(!plan.Order.Contains("Called"), "Called not aimed for");
            ExpectPick(plan, 1, "A", PlanKind.Aim);
        }

        private static void NothingGrowableIsReported()
        {
            var state = NewState(Type("A", 3, 0));
            AddCity(state, 1, "A", 2);
            ExpectPick(Plan(state), 1, null, PlanKind.NothingGrowable);
        }

        // A bonus that applies empire-wide anyway (no presence needed) is not spread.
        private static void PresenceIndependentBonusIsNotSpread()
        {
            var state = NewState(Type("A", 30, 3, presence: false), Type("B", 3, 0));
            AddCity(state, 1, "A", 2, "A", "B");
            ExpectPick(Plan(state), 1, "B", PlanKind.Aim);
        }

        private static void SoonestCitiesGetTheLastFewPops()
        {
            var state = NewState(Type("A", 27, 2), Type("B", 3, 0));
            for (ulong i = 1; i <= 6; i++)
            {
                AddCity(state, i, "B", 7 - i, "A", "B");
            }
            PlanResult plan = Plan(state);
            int onA = plan.Cities.Values.Count(c => c.Pop == "A");
            Expect(onA == 3, $"exactly 3 cities on A (got {onA})");
            ExpectPick(plan, 6, "A", PlanKind.Aim);
            ExpectPick(plan, 5, "A", PlanKind.Aim);
            ExpectPick(plan, 4, "A", PlanKind.Aim);
            ExpectPick(plan, 1, "B", PlanKind.Aim);
        }

        // Turn by turn: five cities growing at different speeds, the planner's pick applied every turn. Checks the whole
        // rule: each type stops at exactly 30 (plus the one-per-city spread), every city gets one of every unlocked type,
        // and the player's order is followed.
        private static void SimulatedGameFollowsTheRule()
        {
            string[] pops = { "A", "B", "C" };
            var types = pops.ToDictionary(p => p, p => Type(p, 0, 0));
            int[] speed = { 1, 2, 2, 3, 4 };
            var cities = new List<CityState>();
            for (int i = 0; i < speed.Length; i++)
            {
                var city = new CityState { Guid = (ulong)(i + 1), Name = "City" + (i + 1), Growing = "A", FoodNet = 3f };
                cities.Add(city);
            }
            var settings = new GameSettings { Priority = new List<string> { "B", "A", "C" } };
            var unlockedAt = new Dictionary<string, int>();
            var countAtUnlock = new Dictionary<string, int>();
            int changes = 0;
            for (int turn = 1; turn <= 200 && types.Values.Any(t => !t.Done || cities.Any(c => c.Count(t.Name) == 0)); turn++)
            {
                var state = NewState(types.Values.ToArray());
                state.Turn = turn;
                foreach (CityState city in cities)
                {
                    int k = speed[(int)city.Guid - 1];
                    city.TurnsToGrowth = k - (turn - 1) % k;
                    city.Options.Clear();
                    foreach (string pop in pops)
                    {
                        city.Options.Add(new GrowOption(pop, city.TurnsToGrowth));
                    }
                    state.Cities.Add(city);
                }
                PlanResult plan = Plan(state, settings);
                foreach (CityState city in cities)
                {
                    string pick = plan.Cities[city.Guid].Pop;
                    if (pick != null && pick != city.Growing)
                    {
                        city.Growing = pick;
                        changes++;
                    }
                }
                // End of turn: cities whose food is full grow their pick; the collection level rises one step at a time.
                foreach (CityState city in cities)
                {
                    int k = speed[(int)city.Guid - 1];
                    if (turn % k != 0)
                    {
                        continue;
                    }
                    PopType type = types[city.Growing];
                    city.Counts[city.Growing] = city.Count(city.Growing) + 1;
                    type.Count++;
                    if (type.Level < type.MaxLevel && type.Count >= type.Thresholds[type.Level])
                    {
                        type.Level++;
                        if (type.Done)
                        {
                            unlockedAt[type.Name] = turn;
                            countAtUnlock[type.Name] = type.Count;
                        }
                    }
                }
            }
            foreach (string pop in pops)
            {
                PopType type = types[pop];
                int citiesWith = cities.Count(c => c.Count(pop) > 0);
                Console.WriteLine($"      {pop}: unlocked turn {(unlockedAt.TryGetValue(pop, out int t) ? t.ToString() : "never")}, "
                    + $"{(countAtUnlock.TryGetValue(pop, out int n) ? n : 0)} at unlock, {type.Count} in the end, in {citiesWith}/{cities.Count} cities");
                Expect(type.Done, $"{pop} unlocked");
                Expect(countAtUnlock.TryGetValue(pop, out int atUnlock) && atUnlock == 30, $"{pop} unlocked at exactly 30 (got {atUnlock})");
                Expect(citiesWith == cities.Count, $"{pop} present in every city");
                Expect(type.Count <= 30 + cities.Count, $"{pop} total {type.Count} <= 30 + one per city");
            }
            Expect(unlockedAt["B"] < unlockedAt["A"] && unlockedAt["A"] < unlockedAt["C"], "unlocked in the player's order B, A, C");
            Console.WriteLine($"      {changes} pick changes in total");
        }

        // ---- jobs ----

        // The formulas decoded from the game data (Effect_Population_Minor_*), evaluated like the game does.
        private static void JobFormulasMatchTheGameData()
        {
            JobEffect xavius = Mixing(Yield.Approval, +1f, 4f);
            Expect(xavius.Evaluate(1) == 0f, "Xavius alone in its job: +0 Approval");
            Expect(xavius.Evaluate(2) == 4f && xavius.Evaluate(5) == 4f, "Xavius with other types: +4 Approval");
            // Aspect: 1 x (T - 1) Influence.
            var aspect = new JobEffect { Field = Yield.Influence, Ops = new[] { 10, 8, 10, 1, 2 }, Constants = new[] { 1f, 1f }, Properties = new[] { "TypeOfPopulationCount" } };
            Expect(aspect.Evaluate(3) == 2f, "Aspect with 3 types: +2 Influence");
            // Horatio: 1 x T Food.
            var horatio = new JobEffect { Field = Yield.Food, Ops = new[] { 10, 8, 2 }, Constants = new[] { 1f }, Properties = new[] { "TypeOfPopulationCount" } };
            Expect(horatio.Evaluate(3) == 3f, "Horatio with 3 types: +3 Food");
            var unknown = new JobEffect { Field = Yield.Food, Ops = new[] { 10, 8, 2 }, Constants = new[] { 1f }, Properties = new[] { "SomethingElse" } };
            Expect(float.IsNaN(unknown.Evaluate(3)), "unsupported property gives NaN (effect ignored)");
        }

        // Daughter of Bor yields +1 Industry in job 02 (artisans): a farmer DoB trades places with an artisan of
        // another type.
        private static void JobBonusPullsPopIntoItsJob()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "DaughterOfBor", 3f), Pop(12, "Plain", 3f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "Plain", 2f)));
            JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(swap != null && swap.Pop == 11 && swap.To == 2 && swap.Partner == 22 && !swap.TwoOrders,
                $"DoB moves to job 02, the game sends out the weakest artisan (got {Show(swap)})");
            Expect(swap != null && Math.Abs(swap.Delta[Yield.Industry] - 1f) < 0.01f, "+1 Industry");
        }

        // Food focus (the game's weights: Food 2, Industry 0.5, Dust 1, the rest 0.5): a Daughter of Bor's +1 Industry
        // as an artisan is worth exactly 0.5, the default minimum gain, which "at least" includes.
        private static void GainOfExactlyTheMinimumCounts()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(3, "Job03", 2, Pop(31, "DaughterOfBor", 3f), Pop(32, "Plain", 3f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "Plain", 2f)));
            city.Jobs.Weights = new[] { 2f, 0.5f, 1f, 0.5f, 0.5f, 0.5f };
            JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(swap != null && swap.Pop == 31 && swap.To == 2 && swap.Partner == 22,
                $"the scribe DoB moves to the artisans at Food focus (got {Show(swap)})");
            Expect(swap != null && Math.Abs(swap.Gain - 0.5f) < 0.01f, $"worth exactly 0.5 (got {Show(swap)})");
            Expect(JobOptimizer.BestSwap(state, city, 0.6f, null) == null, "a higher minimum gain leaves it");
        }

        // When no swap gets a Daughter of Bor into the artisans, the player is told why: the artisans are full and the one
        // the game would send out to make room is a Daughter of Bor too.
        private static void MissedBonusesSayWhy()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(2, "Job02", 2, Pop(21, "DaughterOfBor", 1f), Pop(22, "DaughterOfBor", 2f)),
                Job(3, "Job03", 2, Pop(31, "DaughterOfBor", 5f), Pop(32, "Plain", 1f)));
            Expect(JobOptimizer.BestSwap(state, city, 0.5f, null) == null, "no swap: the full artisans would send out a DoB");
            List<MissedBonus> missed = JobOptimizer.MissedBonuses(state, city, null);
            Expect(missed.Count == 1 && missed[0].Type == "DaughterOfBor" && missed[0].Count == 1 && missed[0].Job.Guid == 2
                && missed[0].Reason == MissedReason.FullOfSameType, $"1 DoB outside job 02, which is full of DoBs (got {missed.Count})");
            missed = JobOptimizer.MissedBonuses(state, city, guid => guid == 31);
            Expect(missed.Count == 1 && missed[0].Reason == MissedReason.MovedThisTurn, "already moved this turn");
            string composition = JobOptimizer.Composition(city.Jobs, t => t, j => j);
            Expect(composition == "Job02 2/2 (2 DaughterOfBor), Job03 2/2 (1 DaughterOfBor, 1 Plain)", $"who works where: {composition}");
            Expect(JobOptimizer.MissedBonuses(state, JobCity(JobWorld(), Job(2, "Job02", 2, Pop(21, "DaughterOfBor", 1f))), null).Count == 0,
                "a DoB already in the artisans misses nothing");
        }

        // Xavius: +4 Approval each when its job has another population type. Three Xavius farmers next to three
        // Sandshaper artisans: mixing them gains Approval.
        private static void MixingBonusSpreadsXavius()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 3, Pop(11, "Xavius", 2f), Pop(12, "Xavius", 2f), Pop(13, "Xavius", 2f)),
                Job(2, "Job02", 3, Pop(21, "Plain", 2f), Pop(22, "Plain", 2f), Pop(23, "Plain", 1f)));
            JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, null);
            // After the swap: job 01 = 2 Xavius + 1 Plain (both Xavius +4), job 02 = 2 Plain + 1 Xavius (+4): +12.
            Expect(swap != null && Math.Abs(swap.Delta[Yield.Approval] - 12f) < 0.01f, $"+12 Approval (got {Show(swap)})");
        }

        // Sollusk: -2 Approval each when mixed. Sollusk mixed with others should be grouped back together.
        private static void MixingMalusKeepsSolluskTogether()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "Sollusk", 3f), Pop(12, "Plain", 3f)),
                Job(2, "Job02", 2, Pop(21, "Sollusk", 3f), Pop(22, "Plain", 3f)));
            JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, null);
            // Grouping: job 01 = 2 Sollusk (alone: no malus), job 02 = 2 Plain: from -4 to 0.
            Expect(swap != null && Math.Abs(swap.Delta[Yield.Approval] - 4f) < 0.01f, $"+4 Approval by grouping Sollusk (got {Show(swap)})");
        }

        // Foundling: +3 Science, -2 Approval when mixed. Worth it with Science weights, not with Approval-heavy ones.
        private static void StrategyWeightsDecideTradeOffs()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "Foundling", 3f), Pop(12, "Foundling", 3f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "Plain", 3f)));
            city.Jobs.Weights = new[] { 0.5f, 0.5f, 0.5f, 2f, 1f, 0.5f };
            JobSwap science = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(science != null && science.Delta[Yield.Science] > 0f, $"Science strategy mixes Foundling (got {Show(science)})");
            city.Jobs.Weights = new[] { 1f, 1f, 1f, 0.2f, 1f, 2f };
            JobSwap approval = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(approval == null, $"Approval-heavy weights keep Foundling apart (got {Show(approval)})");
        }

        // A full job sends out its lowest-scoring population, not necessarily the one we'd pick: the swap evaluated is
        // the one the game will actually do.
        private static void FullJobSwapUsesTheGamesEjectedPop()
        {
            GameState state = JobWorld();
            // Swapping DoB #11 with Plain #21 would gain +1 Industry, but the game can't be told to do it: moving #11
            // into the full artisans sends out their weakest, DoB #22, and moving #21 into the full farmers sends out
            // their weakest, Plain #12. Both are same-type exchanges, so nothing is planned.
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "DaughterOfBor", 3f), Pop(12, "Plain", 1f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "DaughterOfBor", 1f)));
            JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(swap == null, $"no swap the game would carry out (got {Show(swap)})");
        }

        private static void FreeSlotSwapNeedsTwoOrders()
        {
            GameState state = JobWorld();
            // Neither job is full: no population is sent out automatically, so the swap takes two orders.
            CityState city = JobCity(state,
                Job(1, "Job01", 3, Pop(11, "DaughterOfBor", 3f), Pop(12, "Plain", 3f)),
                Job(2, "Job02", 3, Pop(21, "Plain", 3f)));
            JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(swap != null && swap.Pop == 11 && swap.Partner == 21 && swap.TwoOrders, $"DoB in, a Plain out, two orders (got {Show(swap)})");
            // With the farmers full, the same swap is one order (Plain #21 in, the game sends out DoB #11): preferred.
            city.Jobs.Find(1).Slots = 2;
            JobSwap single = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(single != null && single.Pop == 21 && single.Partner == 11 && !single.TwoOrders, $"one-order swap preferred (got {Show(single)})");
        }

        private static void FixedAndFrozenPopsStayPut()
        {
            GameState state = JobWorld();
            state.FixedJobTypes.Add("DaughterOfBor");
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "DaughterOfBor", 3f), Pop(12, "Plain", 1f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "Plain", 2f)));
            Expect(JobOptimizer.BestSwap(state, city, 0.5f, null) == null, "fixed type never moved");
            state.FixedJobTypes.Clear();
            Expect(JobOptimizer.BestSwap(state, city, 0.5f, guid => guid == 11) == null, "frozen pop never moved");
            Expect(JobOptimizer.BestSwap(state, city, 0.5f, guid => guid == 22) == null, "the game would send out a frozen pop: no swap");
        }

        private static void NoSwapWhenNothingGains()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "Plain", 3f), Pop(12, "Plain", 3f)),
                Job(2, "Job02", 2, Pop(21, "DaughterOfBor", 3f), Pop(22, "Plain", 2f)));
            Expect(JobOptimizer.BestSwap(state, city, 0.5f, null) == null, "already optimal: no swap");
        }

        // Applying the best swap again and again (as the controller does, one at a time) ends, and ends better.
        private static void RepeatedSwapsConverge()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 4, Pop(11, "Xavius", 2f), Pop(12, "Xavius", 2f), Pop(13, "DaughterOfBor", 2f), Pop(14, "Sollusk", 2f)),
                Job(2, "Job02", 4, Pop(21, "Xavius", 2f), Pop(22, "Plain", 2f), Pop(23, "Sollusk", 1f), Pop(24, "Plain", 2f)),
                Job(3, "Job03", 3, Pop(31, "DaughterOfBor", 2f), Pop(32, "Sollusk", 2f), Pop(33, "Plain", 1f)));
            float before = Total(state, city);
            var frozen = new HashSet<ulong>();
            int swaps = 0;
            for (; swaps < 20; swaps++)
            {
                JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, frozen.Contains);
                if (swap == null)
                {
                    break;
                }
                Apply(city, swap);
                frozen.Add(swap.Pop);
                frozen.Add(swap.Partner);
            }
            float after = Total(state, city);
            Console.WriteLine($"      {swaps} swaps, score {before:0.#} -> {after:0.#}");
            Expect(swaps < 20, "stops");
            Expect(after > before, "better than before");
            int artisansDoB = city.Jobs.Find(2).Pops.Count(p => p.Type == "DaughterOfBor");
            Expect(artisansDoB == 2, $"both DoB end up as artisans (got {artisansDoB})");
        }

        // Last Lords: -3 Approval as Citizens (job 01). Swapped out of it even without an approval minimum.
        private static void LastLordLeavesCitizens()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "LastLord", 3f), Pop(12, "Plain", 3f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "Plain", 2f)));
            JobSwap swap = JobOptimizer.BestSwap(state, city, 0.5f, null);
            Expect(swap != null && swap.Pop == 11 && swap.To == 2 && Math.Abs(swap.Delta[Yield.Approval] - 3f) < 0.01f,
                $"Last Lord out of Citizens for +3 Approval (got {Show(swap)})");
        }

        // Below the minimum, populations may also move into free slots: to Scribes (no approval cost) from Citizens.
        private static void ApprovalFirstFillsFreeScribeSlots()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 3, Pop(11, "Plain", 3f), Pop(12, "Plain", 3f), Pop(13, "Plain", 3f)),
                Job(3, "Job03", 3, Pop(31, "Plain", 3f)));
            Expect(JobOptimizer.BestSwap(state, city, 0.5f, null) == null, "normally: head-counts are the strategy's, no move");
            var rules = new JobRules { MinGain = 0.5f, ApprovalFirst = true, CurrentApproval = 20f, ApprovalFloor = 28f };
            JobSwap move = JobOptimizer.BestSwap(state, city, rules);
            Expect(move != null && move.IsMove && move.To == 3 && Math.Abs(move.Delta[Yield.Approval] - 3f) < 0.01f,
                $"approval first: a Citizen moves to Scribes for +3 Approval (got {Show(move)})");
        }

        // With a floor, a swap that gains Science but loses Approval may not cross it.
        private static void ApprovalFloorBlocksCostlySwaps()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 2, Pop(11, "Foundling", 3f), Pop(12, "Foundling", 3f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "Plain", 3f)));
            city.Jobs.Weights = new[] { 0.5f, 0.5f, 0.5f, 2f, 1f, 0.5f };
            Expect(JobOptimizer.BestSwap(state, city, new JobRules { MinGain = 0.5f, ApprovalFloor = 28f, CurrentApproval = 40f }) != null,
                "well above the floor: Foundling mixed for Science");
            Expect(JobOptimizer.BestSwap(state, city, new JobRules { MinGain = 0.5f, ApprovalFloor = 28f, CurrentApproval = 30f }) == null,
                "close to the floor: the -4 Approval swap is refused");
        }

        // Found in a real game: approval first moved Citizens to Artisans (more Industry, same approval) and emptied the
        // Citizens' food. Plain moves must gain approval, and nothing may make the city starve.
        private static void ApprovalFirstNeverReshufflesOrStarves()
        {
            GameState state = JobWorld();
            CityState city = JobCity(state,
                Job(1, "Job01", 3, Pop(11, "Plain", 3f), Pop(12, "Plain", 3f), Pop(13, "Plain", 3f)),
                Job(2, "Job02", 3, Pop(21, "Plain", 3f)),
                Job(3, "Job03", 3, Pop(31, "Plain", 3f)));
            city.Jobs.Weights = new[] { 0.5f, 2f, 1f, 0.5f, 0.5f, 0.5f };
            var hungry = new JobRules { MinGain = 0.5f, ApprovalFirst = true, CurrentApproval = 10f, ApprovalFloor = 28f, CurrentFood = 2f };
            JobSwap safe = JobOptimizer.BestSwap(state, city, hungry);
            // Citizens to Artisans gains no approval, Citizens to Scribes would starve: the Artisan goes to Scribes.
            Expect(safe != null && safe.IsMove && safe.From == 2 && safe.To == 3 && safe.Delta[Yield.Food] >= 0f,
                $"short of food: the Artisan, not a Citizen, moves to Scribes (got {Show(safe)})");
            city.Jobs.Find(2).Pops.Clear();
            JobSwap none = JobOptimizer.BestSwap(state, city, hungry);
            Expect(none == null, $"only Citizens left to move and too little food: nothing (got {Show(none)})");
            var fed = new JobRules { MinGain = 0.5f, ApprovalFirst = true, CurrentApproval = 10f, ApprovalFloor = 28f, CurrentFood = 20f };
            JobSwap move = JobOptimizer.BestSwap(state, city, fed);
            Expect(move != null && move.IsMove && move.To == 3, $"with food to spare: a Citizen to Scribes for approval (got {Show(move)})");
        }

        // Approval 20 with a target of 28: the city grows Xavius (+4 next to another type, -3 as Citizen = +1) instead
        // of the planned Plain (-3).
        private static void LowApprovalCityGrowsXavius()
        {
            GameState state = JobWorld();
            state.Types["Xavius"] = Type("Xavius", 30, 3);
            CityState city = JobCity(state,
                Job(1, "Job01", 3, Pop(11, "Plain", 3f)),
                Job(2, "Job02", 2, Pop(21, "Plain", 3f), Pop(22, "Plain", 3f)));
            city.Growing = "Plain";
            city.ApprovalNet = 20f;
            city.FoodNet = 3f;
            city.TurnsToGrowth = 2f;
            city.Options.Add(new GrowOption("Plain", 2f));
            city.Options.Add(new GrowOption("Xavius", 2f));
            city.Counts["Xavius"] = 1;
            var options = new PlanOptions { ApprovalTarget = c => 28f };
            PlanResult plan = Planner.Plan(state, new GameSettings(), options);
            ExpectPick(plan, 1, "Xavius", PlanKind.Approval);
            Expect(Math.Abs(plan.Cities[1].ApprovalGain - 1f) < 0.01f, $"estimated +1 approval (got {plan.Cities[1].ApprovalGain})");
            city.ApprovalNet = 40f;
            PlanResult fine = Planner.Plan(state, new GameSettings(), options);
            Expect(fine.Cities[1].Kind != PlanKind.Approval, "above the target: normal plan");
        }

        // Only Citizen slots free: a Last Lord would cost -6 (job -3, its own -3), Plain -3: Plain it is.
        private static void LowApprovalCityAvoidsLastLordAsCitizen()
        {
            GameState state = JobWorld();
            state.Types["LastLord"] = Type("LastLord", 4, 0);
            CityState city = JobCity(state,
                Job(1, "Job01", 3, Pop(11, "Plain", 3f)),
                Job(2, "Job02", 1, Pop(21, "Plain", 3f)));
            city.Growing = "LastLord";
            city.ApprovalNet = 10f;
            city.FoodNet = 3f;
            city.TurnsToGrowth = 2f;
            city.Options.Add(new GrowOption("LastLord", 2f));
            city.Options.Add(new GrowOption("Plain", 2f));
            PlanResult plan = Planner.Plan(state, new GameSettings(), new PlanOptions { ApprovalTarget = c => 28f });
            ExpectPick(plan, 1, "Plain", PlanKind.Approval);
        }

        private static void ApprovalTargetsFollowSettings()
        {
            var state = NewState();
            state.ApprovalLevels["SettlementApproval_Neutral"] = 25f;
            state.ApprovalLevels["SettlementApproval_Happy"] = 60f;
            var city = new CityState { Guid = 7 };
            var settings = new GameSettings();
            Expect(float.IsNaN(ApprovalRule.Target(state, settings, city, "Off", 3f)), "Off: no target");
            Expect(ApprovalRule.Target(state, settings, city, "Content", 3f) == 28f, "Content + 3 = 28");
            settings.GetOrAddCity(7).MinApproval = "Happy";
            Expect(ApprovalRule.Target(state, settings, city, "Content", 3f) == 63f, "city override: Happy + 3 = 63");
            settings.GetOrAddCity(7).MinApproval = "Off";
            Expect(float.IsNaN(ApprovalRule.Target(state, settings, city, "Content", 3f)), "city override Off");
            Expect(ApprovalRule.Minimum(state, "Jubilant") == 85f, "missing data falls back to 85");
        }

        // Shapes taken from the game's English text: values wrapped in color and bold tags, yields named as
        // "[Icon] Word", links, icons standing alone, several icons in a row.
        private static void BonusTextIsPlain()
        {
            string Plain(string text, Func<string, string> localWord = null) => string.Join(" | ", BonusText.Lines(text, localWord));

            ExpectText(Plain("<c=FFFFFF><b>+2</b></c> [FoodColored] <a=GameFood>Food</a> on [PopulationCategory_02] Artisans"), "+2 Food on Artisans");
            ExpectText(Plain("+1 [DustColored] per [Dweller] Dweller"), "+1 Dust per Dweller");
            ExpectText(Plain("+1 [FoodColored][IndustryColored][DustColored][ScienceColored][CultureColored] on [Population] Population"),
                "+1 Food, Industry, Dust, Science, Influence on Population");
            ExpectText(Plain("+2 [FoodColored] on Tile producing [FoodColored] Food"), "+2 Food on Tile producing Food");
            ExpectText(Plain("+5 [PublicOrderColored]"), "+5 Approval");
            ExpectText(Plain("[PublicOrderColored] Empire Approval +10%"), "Empire Approval +10%");
            ExpectText(Plain("Unlocks <b>Sollusk Imperatium</b>\n+3 [CultureColored] Influence"), "Unlocks Sollusk Imperatium | +3 Influence");
            ExpectText(Plain("every 3 [Turn]\r\n[Turn] Turns left"), "every 3 turns | Turns left");
            ExpectText(Plain("+1 [Strategic01Colored] Titanium"), "+1 Titanium");
            ExpectText(Plain("[DoubleArrow] Adds +1 [ScienceColored] Science\n[TBD]\n"), "Adds +1 Science");
            ExpectText(Plain("+2​ [ScienceColored] Science ( [Population] )"), "+2 Science (Population)");
            ExpectText(Plain("[PopulationCategory_03]"), "Scribes");
            ExpectText(Plain(null), string.Empty);

            // In another language, the game's own words for the icons (the window reads them from the game).
            Func<string, string> french = icon => icon == "FoodColored" ? "Nourriture" : icon == "PopulationCategory_02" ? "Artisans" : null;
            ExpectText(Plain("+2 [FoodColored] Nourriture sur [PopulationCategory_02] Artisan", french), "+2 Nourriture sur Artisan");
            ExpectText(Plain("+2 [FoodColored]", french), "+2 Nourriture");
        }

        private static void ExpectText(string actual, string expected)
        {
            Expect(actual == expected, $"expected \"{expected}\", got \"{actual}\"");
        }

        private static GameState JobWorld()
        {
            var state = NewState(Type("Plain", 3, 0));
            state.JobEffects["LastLord"] = new List<JobEffect>
            {
                new JobEffect { Field = Yield.Approval, Sign = -1f, Constants = new[] { 3f }, RequiredTags = new[] { "Tag_Job01" } },
            };
            state.JobEffects["DaughterOfBor"] = new List<JobEffect>
            {
                new JobEffect { Field = Yield.Industry, Constants = new[] { 1f }, RequiredTags = new[] { "Tag_Job02" } },
            };
            state.JobEffects["Xavius"] = new List<JobEffect> { Mixing(Yield.Approval, +1f, 4f) };
            state.JobEffects["Sollusk"] = new List<JobEffect> { Mixing(Yield.Approval, -1f, 2f) };
            state.JobEffects["Foundling"] = new List<JobEffect> { Mixing(Yield.Science, +1f, 3f), Mixing(Yield.Approval, -1f, 2f) };
            return state;
        }

        // value x min(T - 1, 1): the game's "when the job has another population type" formula.
        private static JobEffect Mixing(int field, float sign, float value)
        {
            return new JobEffect { Field = field, Sign = sign, Ops = new[] { 10, 8, 10, 1, 10, 7, 2 }, Constants = new[] { value, 1f, 1f }, Properties = new[] { "TypeOfPopulationCount" } };
        }

        private static CityState JobCity(GameState state, params JobCategory[] jobs)
        {
            var city = new CityState { Guid = 1, Name = "City1", Jobs = new JobState() };
            city.Jobs.Categories.AddRange(jobs);
            state.Cities.Add(city);
            return city;
        }

        private static JobCategory Job(ulong guid, string name, int slots, params JobPop[] pops)
        {
            var job = new JobCategory { Guid = guid, Name = name, Slots = slots };
            // Like the game: Citizens 4 Food 1 Dust -3 Approval, Artisans 4 Industry 1 Dust -3 Approval, Scribes 2 Science.
            job.Base[Yield.Food] = guid == 1 ? 4f : 0f;
            job.Base[Yield.Industry] = guid == 2 ? 4f : 0f;
            job.Base[Yield.Money] = guid == 3 ? 0f : 1f;
            job.Base[Yield.Science] = guid == 3 ? 2f : 0f;
            job.Base[Yield.Approval] = guid == 3 ? 0f : -3f;
            job.Tags.Add("Tag_Job0" + guid);
            job.Pops.AddRange(pops);
            return job;
        }

        private static JobPop Pop(ulong guid, string type, float score) => new JobPop(guid, type, score);

        private static void Apply(CityState city, JobSwap swap)
        {
            JobCategory from = city.Jobs.Find(swap.From);
            JobCategory to = city.Jobs.Find(swap.To);
            JobPop pop = from.Pops.First(p => p.Guid == swap.Pop);
            JobPop partner = to.Pops.First(p => p.Guid == swap.Partner);
            from.Pops.Remove(pop);
            to.Pops.Remove(partner);
            to.Pops.Add(pop);
            from.Pops.Add(partner);
        }

        private static float Total(GameState state, CityState city)
        {
            float total = 0f;
            foreach (JobCategory job in city.Jobs.Categories)
            {
                float[] yields = JobOptimizer.Yields(state, job, job.Pops);
                for (int f = 0; f < Yield.Count; f++)
                {
                    total += city.Jobs.Weights[f] * yields[f];
                }
            }
            return total;
        }

        private static string Show(JobSwap swap) => swap == null ? "none" : $"{swap.PopType}#{swap.Pop} {swap.From}->{swap.To} <-> {swap.PartnerType}#{swap.Partner}, {JobOptimizer.Describe(swap.Delta)}, gain {swap.Gain:0.##}{(swap.TwoOrders ? ", two orders" : string.Empty)}";

        // ---- helpers ----

        private static GameState NewState(params PopType[] types)
        {
            var state = new GameState { GameId = "test", Turn = 10, IsHuman = true, CanAct = true };
            foreach (PopType type in types)
            {
                state.Types[type.Name] = type;
            }
            return state;
        }

        private static PopType Type(string name, int count, int level, bool presence = true, int[] thresholds = null)
        {
            return new PopType
            {
                Name = name,
                Count = count,
                Level = level,
                Thresholds = thresholds ?? new[] { 5, 15, 30 },
                PresenceMatters = presence,
                AvailableToEmpire = true,
            };
        }

        private static CityState AddCity(GameState state, ulong guid, string growing, float turns, params string[] options)
        {
            var city = new CityState
            {
                Guid = guid,
                Name = "City" + guid,
                Growing = growing,
                Population = 5,
                FoodNet = float.IsInfinity(turns) ? 0f : 3f,
                TurnsToGrowth = turns,
            };
            foreach (string option in options)
            {
                city.Options.Add(new GrowOption(option, turns));
            }
            state.Cities.Add(city);
            return city;
        }

        private static PlanResult Plan(GameState state, GameSettings settings = null)
        {
            return Planner.Plan(state, settings ?? new GameSettings(), new PlanOptions());
        }

        private static void ExpectPick(PlanResult plan, ulong city, string pop, PlanKind kind)
        {
            CityPlan cityPlan = plan.Cities[city];
            Expect(cityPlan.Pop == pop && cityPlan.Kind == kind,
                $"city {city}: expected {pop ?? "(leave)"} / {kind}, got {cityPlan.Pop ?? "(leave)"} / {cityPlan.Kind}");
        }

        private static void Expect(bool condition, string what)
        {
            checks++;
            if (!condition)
            {
                failures++;
                Console.WriteLine("    FAIL: " + what);
            }
        }

        private static void Run(string name, Action test)
        {
            int before = failures;
            try
            {
                test();
            }
            catch (Exception e)
            {
                failures++;
                checks++;
                Console.WriteLine($"    EXCEPTION: {e}");
            }
            Console.WriteLine((failures == before ? "ok    " : "FAIL  ") + name);
        }
    }
}
