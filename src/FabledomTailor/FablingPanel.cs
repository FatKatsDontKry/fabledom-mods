using HarmonyLib;
using Nielsen;
using TMPro;
using UnityEngine;

namespace FabledomTailor
{
    // A garments line in the fabling's panel, beside the game's "no coal at home" warning: when the next garment is
    // due, or how long they have gone without.
    [HarmonyPatch]
    internal static class FablingPanel
    {
        private static TMP_Text Line(WorkerExtraInfo panel)
        {
            var coal = Traverse.Create(panel).Field("noCoalAtHomeWarnign").GetValue<TMP_Text>();
            if (coal == null) return null;
            var existing = coal.transform.parent.Find("Fab garments");
            if (existing != null) return existing.GetComponent<TMP_Text>();
            var go = Object.Instantiate(coal.gameObject, coal.transform.parent);
            go.name = "Fab garments";
            go.transform.SetSiblingIndex(coal.transform.GetSiblingIndex() + 1);
            foreach (var mb in go.GetComponents<MonoBehaviour>()) if (mb != null && !(mb is TMP_Text) && mb.GetType().Name.Contains("Locali")) mb.enabled = false;
            return go.GetComponent<TMP_Text>();
        }

        // What the line says for this fabling, or null for nothing.
        internal static string Text(Worker w)
        {
            try
            {
                if (!Plugin.GarmentDemand.Value || w == null || w.resident == null || !w.resident.HasHousing()) return null;
                var h = w.resident.housing;
                if (!Garments.Wants(h, Garments.Share())) return null;
                string rg = w.unit != null && w.unit.worldObject != null ? w.unit.worldObject.guid : null;
                if (rg == null || !Garments.S.Residents.TryGetValue(rg, out var c)) return null;
                string glyph = Tailor.Garment != null ? Utils_Text.FontSprite(Tailor.Garment.GetSpriteKey()) + " " : "";
                if (Garments.Holiday())
                {
                    int left = Garments.HolidayDaysLeft();
                    return left > 0 ? $"{glyph}Dressed by the feast's traders, {left} day{(left == 1 ? "" : "s")} to go" : $"{glyph}Dressed by the feast's traders";
                }
                if (c.Unclothed > 0) return $"{glyph}Without a garment for {c.Unclothed} day{(c.Unclothed == 1 ? "" : "s")}";
                int due = Mathf.Max(0, Mathf.CeilToInt(Garments.IntervalDays(h) - c.Days));
                return $"{glyph}Next garment in {due} day{(due == 1 ? "" : "s")}";
            }
            catch { return null; }
        }

        [HarmonyPatch(typeof(WorkerExtraInfo), "UpdateUi"), HarmonyPostfix]
        private static void AfterUpdateUi(WorkerExtraInfo __instance)
        {
            try
            {
                var w = Traverse.Create(__instance).Field("worker").GetValue<Worker>();
                var line = Line(__instance);
                if (line == null) return;
                string text = Text(w);
                line.text = text ?? "";
                line.gameObject.SetActive(text != null);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: fabling panel: " + ex.Message); }
        }

        [HarmonyPatch(typeof(WorkerExtraInfo), nameof(WorkerExtraInfo.ResetUi)), HarmonyPostfix]
        private static void AfterResetUi(WorkerExtraInfo __instance)
        {
            var line = Line(__instance);
            if (line != null) line.gameObject.SetActive(false);
        }

        // The panel only shows when there is something to say; a garments line counts.
        [HarmonyPatch(typeof(WorkerExtraInfo), nameof(WorkerExtraInfo.HasExtraInfo)), HarmonyPostfix]
        private static void AfterHasExtraInfo(Worker worker, ref bool __result)
        {
            if (!__result && Text(worker) != null) __result = true;
        }
    }
}
