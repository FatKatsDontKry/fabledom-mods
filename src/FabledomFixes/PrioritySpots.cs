using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomFixes
{
    // A site takes as many labourers as it has construction spots, usually one, and its labourers are also the
    // ones who fetch its materials - so a one-spot site gets its materials one trip at a time by one fabling.
    // Jess (2026-10-12): prioritising a building should add construction spots, so more labourers fetch and
    // build it in parallel. Real spots, not shared ones: every count the game keeps (who holds the site, what is
    // in transit, whether anyone is still hammering) is per spot, so the game's own bookkeeping stays right.
    //
    // The spot is a copy of the site's existing construction spot, placed beside it. It is not part of the
    // building's saved components (that list is fixed in the editor), so it is never written to a save; it is
    // made again on load for a site saved as prioritised, before the labourers' reservations are resolved,
    // with the same stable identity, so a labourer who held it keeps it. Turning priority off removes the extra
    // spots that are free; one still held is removed once its labourer lets go.
    [HarmonyPatch]
    internal static class PrioritySpots
    {
        private const string Mark = " (priority spot ";

        private static UnitWaypoint Template(Constructable c)
        {
            var ws = c != null ? c.unitWaypoints : null;
            if (ws == null || ws.unitWaypoints == null) return null;
            foreach (var w in ws.unitWaypoints)
                if (w != null && w.type == UnitWaypoint.Type.CONSTRUCTION && !w.gameObject.name.Contains(Mark)) return w;
            return null;
        }

        internal static void Add(Constructable c)
        {
            try
            {
                if (c == null || c.isConstructionComplete) return;
                var template = Template(c);
                if (template == null) return;
                var list = c.unitWaypoints.unitWaypoints.ToList();
                int extra = Mathf.Clamp(Plugin.PriorityExtraSpots.Value, 0, 6);
                int made = 0;
                for (int i = 1; i <= extra; i++)
                {
                    string guid = template.guid + "-priority" + i;
                    if (list.Any(w => w != null && w.guid == guid)) continue;
                    var go = Object.Instantiate(template.gameObject, template.transform.parent);
                    go.name = template.gameObject.name + Mark + i + ")";
                    go.transform.position = template.transform.position + template.transform.right * (1.2f * i);
                    var wp = go.GetComponent<UnitWaypoint>();
                    wp.guid = guid;          // Awake gave it a fresh one; this one is the same after every load
                    wp.reservedBy = null;    // copied from the template; nobody holds the new spot
                    list.Add(wp);
                    made++;
                }
                if (made > 0)
                {
                    c.unitWaypoints.unitWaypoints = list.ToArray();
                    Plugin.Log.LogInfo($"[priority] {c.worldObject?.data?.name ?? c.name}: {made} extra construction spot{(made == 1 ? "" : "s")} added ({list.Count(w => w != null && w.type == UnitWaypoint.Type.CONSTRUCTION)} in all).");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[priority] adding spots: " + ex.Message); }
        }

        internal static void RemoveFree(Constructable c)
        {
            try
            {
                var ws = c != null ? c.unitWaypoints : null;
                if (ws == null || ws.unitWaypoints == null) return;
                var keep = new List<UnitWaypoint>();
                int removed = 0;
                foreach (var w in ws.unitWaypoints)
                {
                    if (w != null && w.gameObject.name.Contains(Mark) && w.reservedBy == null) { Object.Destroy(w.gameObject); removed++; continue; }
                    keep.Add(w);
                }
                if (removed > 0)
                {
                    ws.unitWaypoints = keep.ToArray();
                    Plugin.Log.LogInfo($"[priority] {c.worldObject?.data?.name ?? c.name}: {removed} extra spot{(removed == 1 ? "" : "s")} removed.");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[priority] removing spots: " + ex.Message); }
        }

        // The player's priority toggle on the site's panel.
        [HarmonyPatch(typeof(ConstructionPriority), nameof(ConstructionPriority.TogglePriority)), HarmonyPostfix]
        private static void AfterToggle(ConstructionPriority __instance)
        {
            Constructable c = null;
            try { c = Traverse.Create(__instance).Field("constructable").GetValue<Constructable>(); } catch { }
            if (c == null) c = __instance.GetComponentInParent<Constructable>();
            if (c == null) return;
            if (c.isPrioritized) Add(c); else RemoveFree(c);
        }

        // A held extra spot is let go when the labourer's claim clears: tidy up then if priority is off.
        [HarmonyPatch(typeof(Reserver), nameof(Reserver.ClearMainReservation)), HarmonyPrefix]
        private static void BeforeClearClaim(Reserver __instance)
        {
            try
            {
                var w = __instance.mainReservation;
                if (w == null || !w.gameObject.name.Contains(Mark)) return;
                var c = w.GetComponentInParent<Constructable>();
                if (c != null && !c.isPrioritized) Plugin.Instance.StartCoroutine(RemoveLater(c));
            }
            catch { }
        }

        private static IEnumerator RemoveLater(Constructable c)
        {
            yield return null;
            RemoveFree(c);
        }

        // A site saved as prioritised gets its spots back before the labourers' claims are resolved (the game does
        // that three frames after loading a reserver; this runs after two).
        [HarmonyPatch(typeof(Constructable), nameof(Constructable.OnLoad)), HarmonyPostfix]
        private static void AfterLoad(Constructable __instance)
        {
            if (__instance != null && __instance.isPrioritized) Plugin.Instance.StartCoroutine(AddAfter(__instance, 2));
        }

        private static IEnumerator AddAfter(Constructable c, int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
            if (c != null && c.isPrioritized) Add(c);
        }
    }
}
