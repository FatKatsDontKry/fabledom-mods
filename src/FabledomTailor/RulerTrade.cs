using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // Other rulers trade wool and garments (Jess, 2026-10-07). Each ruler's goods are a list on their RulerData
    // (starting stock, yearly increase, cap); the realm's live stock is a parallel list on the realm, refilled each
    // year by index, so new goods are appended to the END of both lists in the same order. A loaded game whose
    // realms predate this gets the missing entries appended with a starting stock on the first daily tick.
    [HarmonyPatch]
    internal static class RulerTrade
    {
        private static bool dataDone, savesDone;

        private static IEnumerable<(string ruler, string key, Vector2Int start, Vector2Int yearly, int cap)> Wanted()
        {
            if (Plugin.TradeMirrorGrapes.Value)
            {
                // wool sells like grapes and garments like grape juice, ruler by ruler (Jess, 2026-10-07)
                foreach (var ruler in DataManager.Instance.rulerSoData.Where(r => r != null && r.tradingResources != null))
                {
                    var grapes = ruler.tradingResources.FirstOrDefault(t => t != null && t.resourceData != null && t.resourceData.key == "grapes");
                    var juice = ruler.tradingResources.FirstOrDefault(t => t != null && t.resourceData != null && t.resourceData.key == "grapeJuice");
                    float f = Mathf.Max(0.05f, Plugin.TradeMirrorShare.Value);
                    Vector2Int Scale(Vector2Int v) => new Vector2Int(Mathf.RoundToInt(v.x * f), Mathf.Max(1, Mathf.RoundToInt(v.y * f)));
                    if (grapes != null && !grapes.hide && grapes.capacity > 0) yield return (ruler.key, "wool", Scale(grapes.startingAmount), Scale(grapes.yearlyIncreaseAmount), Mathf.Max(1, Mathf.RoundToInt(grapes.capacity * f)));
                    if (juice != null && !juice.hide && juice.capacity > 0) yield return (ruler.key, "garment", Scale(juice.startingAmount), Scale(juice.yearlyIncreaseAmount), Mathf.Max(1, Mathf.RoundToInt(juice.capacity * f)));
                }
                yield break;
            }
            foreach (var w in Parse(Plugin.TradeWool.Value, "wool")) yield return w;
            foreach (var w in Parse(Plugin.TradeGarments.Value, "garment")) yield return w;
        }

        // "ruler=startMin-startMax/yearlyMin-yearlyMax/cap, ..."
        private static IEnumerable<(string ruler, string key, Vector2Int start, Vector2Int yearly, int cap)> Parse(string text, string key)
        {
            foreach (var part in (text ?? "").Split(','))
            {
                var m = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"^(\w+)\s*=\s*(\d+)\s*-\s*(\d+)\s*/\s*(\d+)\s*-\s*(\d+)\s*/\s*(\d+)$");
                if (!m.Success) { if (part.Trim().Length > 0) Plugin.Log.LogWarning($"Tailor: trade entry not understood: '{part.Trim()}'."); continue; }
                int G(int i) => int.Parse(m.Groups[i].Value);
                yield return (m.Groups[1].Value, key, new Vector2Int(G(2), G(3)), new Vector2Int(G(4), G(5)), G(6));
            }
        }

        // Runs after the Tailor's own data setup (the same DataManager.Awake postfix, lower priority).
        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void AddToRulers(DataManager __instance)
        {
            if (dataDone) return;
            dataDone = true;
            try
            {
                var added = new List<string>();
                foreach (var w in Wanted())
                {
                    var ruler = __instance.rulerSoData.FirstOrDefault(r => r != null && r.key == w.ruler);
                    var res = __instance.resourceSoData.FirstOrDefault(r => r != null && r.key == w.key);
                    if (ruler == null || res == null) { Plugin.Log.LogWarning($"Tailor: trade: ruler '{w.ruler}' or resource '{w.key}' not found."); continue; }
                    if (ruler.tradingResources == null) ruler.tradingResources = new List<TradingAmountData>();
                    // the game's own lists already carry wool and garment entries, hidden with zero amounts (found
                    // 2026-10-07): fill those in rather than appending, so the realm's stock list stays aligned
                    var existing = ruler.tradingResources.FirstOrDefault(t => t != null && t.resourceData == res);
                    if (existing != null)
                    {
                        existing.startingAmount = w.start; existing.yearlyIncreaseAmount = w.yearly; existing.capacity = w.cap; existing.hide = false;
                        added.Add($"{w.key} at {w.ruler} (entry filled in)");
                        continue;
                    }
                    ruler.tradingResources.Add(new TradingAmountData { resourceData = res, startingAmount = w.start, yearlyIncreaseAmount = w.yearly, capacity = w.cap, hide = false });
                    added.Add($"{w.key} at {w.ruler}");
                }
                if (added.Count > 0) Plugin.Log.LogInfo("Tailor: rulers now trade " + string.Join(", ", added) + ".");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: ruler trade data: " + ex.Message); }
        }

        // A realm from an older save has no stock entry for the new goods; append one per missing good, in the
        // ruler's list order, so the yearly refill (which pairs the two lists by index) stays aligned.
        internal static void EnsureSaves()
        {
            if (savesDone || DataManager.Instance == null || DataManager.Instance.gameData == null) return;
            try
            {
                var realms = DataManager.Instance.gameData.realmDatas;
                if (realms == null || realms.Count == 0) return;
                savesDone = true;
                foreach (var realm in realms)
                {
                    if (realm == null || string.IsNullOrEmpty(realm.rulerKey)) continue;
                    var ruler = DataManager.Instance.GetRulerDataFromKey(realm.rulerKey);
                    if (ruler == null || ruler.tradingResources == null) continue;
                    for (int i = 0; i < ruler.tradingResources.Count; i++)
                    {
                        var t = ruler.tradingResources[i];
                        if (t == null || t.resourceData == null) continue;
                        var have = realm.availableResources.FirstOrDefault(a => a != null && a.key == t.resourceData.key);
                        if (have != null)
                        {
                            // an entry the game made while the good was hidden holds nothing: give it a starting stock once
                            if (have.amount <= 0 && !t.hide && t.startingAmount.y > 0 && (t.resourceData.key == "wool" || t.resourceData.key == "garment"))
                            {
                                have.amount = Random.Range(t.startingAmount.x, t.startingAmount.y);
                                Plugin.Log.LogInfo($"Tailor: {realm.rulerKey} now offers {have.amount} {t.resourceData.key}.");
                            }
                            continue;
                        }
                        if (i != realm.availableResources.Count)
                        {
                            Plugin.Log.LogWarning($"Tailor: trade: {realm.rulerKey}'s stock list is out of step with the ruler's goods at '{t.resourceData.key}' (index {i} vs {realm.availableResources.Count}); not adding.");
                            break;
                        }
                        int amount = Random.Range(t.startingAmount.x, t.startingAmount.y);
                        realm.availableResources.Add(new SaveableResourceAmountData(t.resourceData.key, amount));
                        Plugin.Log.LogInfo($"Tailor: {realm.rulerKey} now offers {amount} {t.resourceData.key}.");
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: ruler trade stock: " + ex.Message); }
        }
    }
}
