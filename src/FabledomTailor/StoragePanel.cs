using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;
using UnityEngine.UI;

namespace FabledomTailor
{
    // A house's storage panel has a fixed set of rows; the garment slot is one more than the prefab planned for, so
    // a row is cloned when needed and the panel is stretched by a row's height.
    [HarmonyPatch]
    internal static class StoragePanel
    {
        private static readonly Dictionary<StorageInfo, float> baseHeights = new Dictionary<StorageInfo, float>();
        private static readonly Dictionary<StorageInfo, int> baseRows = new Dictionary<StorageInfo, int>();
        private static readonly Dictionary<StorageInfo, (float, float)> baseLe = new Dictionary<StorageInfo, (float, float)>();
        private static bool loggedGrow;

        [HarmonyPatch(typeof(StorageInfo), "UpdateUi"), HarmonyPrefix]
        private static void EnoughRows(StorageInfo __instance)
        {
            try
            {
                var tr = Traverse.Create(__instance);
                var storage = tr.Field("storage").GetValue<Storage>();
                var slots = tr.Field("storageSlots").GetValue<List<ResourceUiSlot>>();
                if (storage == null || slots == null || slots.Count == 0) return;
                if (!baseRows.ContainsKey(__instance)) baseRows[__instance] = slots.Count;
                while (slots.Count < storage.resourcesCapacity.Count)
                {
                    var last = slots[slots.Count - 1];
                    var copy = Object.Instantiate(last.gameObject, last.transform.parent);
                    copy.name = "Fab storage row " + slots.Count;
                    copy.transform.SetSiblingIndex(last.transform.GetSiblingIndex() + 1);
                    slots.Add(copy.GetComponent<ResourceUiSlot>());
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: storage rows: " + ex.Message); }
        }

        [HarmonyPatch(typeof(StorageInfo), "UpdateUi"), HarmonyPostfix]
        private static void Stretch(StorageInfo __instance)
        {
            try
            {
                var tr = Traverse.Create(__instance);
                var storage = tr.Field("storage").GetValue<Storage>();
                var slots = tr.Field("storageSlots").GetValue<List<ResourceUiSlot>>();
                if (storage == null || slots == null || slots.Count == 0) return;
                bool ours = storage.resourcesCapacity.Any(r => r != null && r.resourceAmountData != null && r.resourceAmountData.resourceData != null && r.resourceAmountData.resourceData.key == "garment");
                if (!ours) return;
                // The rows sit in a grid (3 across, 20 high) inside a slot with a fixed LayoutElement height. Our garment
                // row can push the grid onto a new line; grow the slot by one line's height for each such line.
                int visible = slots.Count(sl => sl.gameObject.activeSelf);
                int oursCount = storage.resourcesCapacity.Count(r => r != null && r.resourceAmountData != null && r.resourceAmountData.resourceData != null && r.resourceAmountData.resourceData.key == "garment");
                var grid = slots[0].transform.parent.GetComponent<GridLayoutGroup>();
                var gridRt = slots[0].transform.parent.GetComponent<RectTransform>();
                int columns = 3;
                float line = 20f;
                if (grid != null)
                {
                    line = grid.cellSize.y + grid.spacing.y;
                    if (grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount) columns = Mathf.Max(1, grid.constraintCount);
                    else if (gridRt != null && grid.cellSize.x + grid.spacing.x > 0) columns = Mathf.Max(1, Mathf.FloorToInt((gridRt.rect.width - grid.padding.horizontal + grid.spacing.x) / (grid.cellSize.x + grid.spacing.x)));
                }
                int linesNow = Mathf.CeilToInt(visible / (float)columns);
                int linesBefore = Mathf.CeilToInt(Mathf.Max(0, visible - oursCount) / (float)columns);
                float grow = Mathf.Max(0, linesNow - linesBefore) * line;
                var rt = __instance.GetComponent<RectTransform>();
                if (rt == null) return;
                if (!baseHeights.ContainsKey(__instance)) baseHeights[__instance] = rt.sizeDelta.y;
                var le = __instance.GetComponent<LayoutElement>();
                if (le != null)
                {
                    if (!baseLe.ContainsKey(__instance)) baseLe[__instance] = (le.minHeight, le.preferredHeight);
                    var (minH, prefH) = baseLe[__instance];
                    if (minH > 0) le.minHeight = minH + grow;
                    if (prefH > 0) le.preferredHeight = prefH + grow;
                    if (minH <= 0 && prefH <= 0) le.preferredHeight = baseHeights[__instance] + grow;
                }
                else rt.sizeDelta = new Vector2(rt.sizeDelta.x, baseHeights[__instance] + grow);
                if (grow > 0 && !loggedGrow) { loggedGrow = true; Plugin.Log.LogInfo($"Tailor: storage panel grown by {grow} ({visible} rows in {columns} columns, {linesNow} lines)."); }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: storage stretch: " + ex.Message); }
        }
    }
}
