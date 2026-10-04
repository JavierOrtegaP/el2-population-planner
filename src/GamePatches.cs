using System;
using System.Collections.Concurrent;
using Amplitude;
using Amplitude.Framework.Input;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Sandbox;
using Amplitude.Mercury.Simulation;
using Amplitude.Mercury.UI;
using Amplitude.UI.Interactables;
using HarmonyLib;

namespace PopulationPlanner
{
    internal readonly struct GrowthFailure
    {
        public readonly ulong City;
        public readonly string Pop;
        public readonly int Turn;

        public GrowthFailure(ulong city, string pop, int turn)
        {
            City = city;
            Pop = pop;
            Turn = turn;
        }
    }

    // A population the game offered but then refused to add (sandbox thread). The controller stops picking it in that
    // city for a while, so a city never keeps spending food on a population it can't get.
    [HarmonyPatch(typeof(DepartmentOfTheInterior), nameof(DepartmentOfTheInterior.AddPopulationToSettlement), new[] { typeof(StaticString), typeof(Settlement) })]
    internal static class GrowthFailurePatch
    {
        internal static readonly ConcurrentQueue<GrowthFailure> Failures = new ConcurrentQueue<GrowthFailure>();

        [HarmonyPostfix]
        private static void Postfix(bool __result, StaticString populationDefinitionName, Settlement settlement)
        {
            if (__result || Guard.HasFailed(typeof(GrowthFailurePatch)))
            {
                return;
            }
            try
            {
                Sandbox sandbox = SandboxManager.Sandbox;
                Empire empire = ReferenceEquals(settlement, null) ? null : settlement.Empire.Entity;
                // Only the empire's cities: those are what the mod picks populations for.
                if (sandbox == null || ReferenceEquals(empire, null) || empire.Index != sandbox.LocalEmpireIndex || settlement.SettlementStatus != Amplitude.Mercury.Data.Simulation.SettlementStatuses.City)
                {
                    return;
                }
                Failures.Enqueue(new GrowthFailure(settlement.GUID, populationDefinitionName.ToString(), sandbox.Turn));
                StateCapture.RequestRefresh();
            }
            catch (Exception e)
            {
                Guard.Fail(typeof(GrowthFailurePatch), e);
            }
        }
    }

    // The player picked a city's next population in the city screen (main thread).
    [HarmonyPatch(typeof(CityWindow_PopulationGroup), "NextPopDropList_SelectionChange")]
    internal static class ManualPickPatch
    {
        [HarmonyPostfix]
        private static void Postfix(IUIDropList dropList)
        {
            if (Guard.HasFailed(typeof(ManualPickPatch)))
            {
                return;
            }
            try
            {
                if (!(dropList?.SelectedEntry is AvailablePopulation entry) || ReferenceEquals(entry.PopulationDefinition, null))
                {
                    return;
                }
                ulong city = Snapshots.SettlementCursorSnapshot.PresentationData.SettlementGUID;
                Controller.Instance?.OnManualPick(city, entry.PopulationDefinition.Name.ToString());
            }
            catch (Exception e)
            {
                Guard.Fail(typeof(ManualPickPatch), e);
            }
        }
    }

    // The player dragged populations to another job in the population window (main thread): the mod leaves that city's
    // jobs alone until next turn.
    [HarmonyPatch(typeof(PopulationWindow), "DragDropCache_DragDropCompleted")]
    internal static class ManualJobMovePatch
    {
        [HarmonyPostfix]
        private static void Postfix(SimulationEntityGUID target)
        {
            if (Guard.HasFailed(typeof(ManualJobMovePatch)))
            {
                return;
            }
            try
            {
                Controller.Instance?.OnManualJobMove(target);
            }
            catch (Exception e)
            {
                Guard.Fail(typeof(ManualJobMovePatch), e);
            }
        }
    }

    // While the cursor is over the window, mouse presses and the wheel stop at the game's UI layer, so they don't also
    // click or zoom the map underneath. Releases always go through, so nothing stays half-pressed.
    [HarmonyPatch(typeof(UIInteractivityManager))]
    internal static class InputBlockPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("TryCatchLeftClickAction")]
        private static bool LeftClick(ButtonState buttonState, ref bool __result) => Pass(buttonState, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch("TryCatchRightClickAction")]
        private static bool RightClick(ButtonState buttonState, ref bool __result) => Pass(buttonState, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch("TryCatchMiddleClickAction")]
        private static bool MiddleClick(ButtonState buttonState, ref bool __result) => Pass(buttonState, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch("TryCatchMouseScrollAction")]
        private static bool Scroll(ref bool __result)
        {
            if (!PlannerWindow.MouseOver)
            {
                return true;
            }
            __result = true;
            return false;
        }

        private static bool Pass(ButtonState buttonState, ref bool __result)
        {
            if (buttonState != ButtonState.Down || !PlannerWindow.MouseOver)
            {
                return true;
            }
            __result = true;
            return false;
        }
    }
}
