using System;
using HarmonyLib;
using Nielsen;

namespace FabledomFixes
{
    // A patient who loses their bed while still sick is left standing inside the hospital with their behaviour tree
    // parked in the treatment wait. The game discharges on its own (a doctor dismissed drops the capacity, and the
    // surplus patients are removed from the end of the list; closing the building removes them all) but never sends
    // anyone back out, so the discharged keep "treated" days frozen while "sick" days count to the death at 30, with
    // free beds in the next hospital. Jess, 2026-10-25: four commoners died this way after a hospital was moved; the
    // same shape as Henry Myers on 2026-10-18.
    //
    // After every discharge of a still-sick patient: outside, idle, and the tree restarted from the root, where the
    // sick branch finds the nearest hospital with room and walks there (treated days carry over; the game keeps them).
    // Recovery also goes through RemovePatient, with isSick already false, and is left alone.
    internal static class HospitalDischarge
    {
        private static readonly AccessTools.FieldRef<Hospital, WorkPlace> WorkPlaceOf = AccessTools.FieldRefAccess<Hospital, WorkPlace>("workPlace");

        [HarmonyPatch(typeof(Hospital), nameof(Hospital.RemovePatient)), HarmonyPostfix]
        private static void AfterDischarge(Hospital __instance, Worker worker)
        {
            try
            {
                if (worker == null || worker.needs == null || !worker.needs.isSick) return;
                var u = worker.unit;
                if (u == null || u.health == null || u.health.isDead || u.isOnMission) return;
                u.GoOutside();
                worker.SetIdleAnimation();
                if (u.btc != null) u.btc.Restart();
                string name; try { name = u.GetLocalizedBirthName(); } catch { name = worker.name; }
                int doctors = -1; try { doctors = WorkPlaceOf(__instance).GetWorkers().Count; } catch { }
                Plugin.Log.LogInfo($"[hospital] {name} lost their bed still sick ({worker.needs.daysTreated:0} day(s) treated, {worker.needs.daysSick:0} untreated; the hospital now has {doctors} doctor(s) for {__instance.GetPatientCount()}/{__instance.GetCurrentCapacity()} patients); sent out to find another bed.");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[hospital] discharge: " + ex.Message); }
        }
    }
}
