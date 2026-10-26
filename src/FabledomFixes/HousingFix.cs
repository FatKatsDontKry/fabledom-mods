using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomFixes
{
    // Phantom tenants (Jess's save, 2026-10-06): a fabling whose record says it lives in a house that is already full.
    // On load the game refuses it ("Tried to add resident X to full housing") but the fabling keeps pointing at the
    // house, so it is counted in the population without holding a bed anywhere. Beds then look full kingdom-wide
    // (peasants 291/291 with two empty beds in a homestead), and no newcomers of that class can ever arrive.
    //
    // The fix: after a load and every so often, any resident whose house does not list them is moved to the first
    // house with room (or marked homeless, as the game does), and any house listing a resident who lives elsewhere
    // drops them. Each move is logged.
    internal static class HousingFix
    {
        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoad(DataManager __instance) => __instance.OnLoadComplete += () => Plugin.Instance.StartCoroutine(AfterLoad());

        private static IEnumerator AfterLoad()
        {
            for (int i = 0; i < 30; i++) yield return null;
            Sweep("after load");
        }

        internal static IEnumerator Patrol()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(90f);
                try { if (Worker.Instances != null && Worker.Instances.Count > 0) Sweep("patrol"); }
                catch (System.Exception ex) { Plugin.Log.LogWarning("[housing] patrol: " + ex.Message); }
            }
        }

        internal static int Sweep(string when)
        {
            int fixedCount = 0;
            try
            {
                // houses listing someone who lives elsewhere
                foreach (var h in Housing.Instances.ToList())
                {
                    if (h == null || h.residents == null) continue;
                    foreach (var r in h.residents.ToList())
                    {
                        if (r == null) { h.residents.Remove(r); fixedCount++; continue; }
                        if (r.housing != h)
                        {
                            h.residents.Remove(r);
                            fixedCount++;
                            Plugin.Log.LogWarning($"[housing] {Name(r)} was listed at {h.name} but lives at {(r.housing != null ? r.housing.name : "nowhere")}; dropped from the list ({when}).");
                        }
                    }
                }
                // residents pointing at a house that does not hold them
                foreach (var w in Worker.Instances.ToList())
                {
                    var r = w != null ? w.resident : null;
                    if (r == null || r.housing == null) continue;
                    if (r.housing.residents != null && r.housing.residents.Contains(r)) continue;
                    var wrong = r.housing;
                    r.housing = null;
                    var home = Housing.GetFirstAvailable(w.fablingClass);
                    r.AddToHousing(home, false);
                    fixedCount++;
                    Plugin.Log.LogWarning($"[housing] {Name(r)} ({w.fablingClass}) thought they lived at {wrong.name}, which is full; " + (home != null ? $"moved to {home.name}." : "no house has room, now homeless.") + $" ({when})");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[housing] sweep: " + ex.Message); }
            return fixedCount;
        }

        private static string Name(Resident r)
        {
            try { return r.unit.GetLocalizedBirthName(); } catch { return r.name; }
        }
    }
}
