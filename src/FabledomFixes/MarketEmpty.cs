using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Nielsen;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FabledomFixes
{
    // Marketplace: an Empty toggle per slot (Jess, 2026-10-24). The Marketplace has four slots, each a dropdown choosing the
    // good it holds, and no way to empty one: changing a slot's good deletes whatever the slot held (Market.AlterActiveCapacity
    // removes the capacity, and Storage.RemoveCapacityFromList removes the stock with it, out of the kingdom's totals), and so
    // does losing a marketeer. The Granary, the Stockpile and the Town shop have an "Empty storage" toggle; this gives each
    // market slot one. While a slot's good is being emptied, the marketeer stops restocking it and the granary and stockpile
    // workers haul it out, as they do for a store being emptied, taking only that good; the heads keep fetching from it
    // meanwhile. The toggle stays on until it is turned off, like the game's. Nothing lingers: changing the slot's good still
    // does what the game does, so the toggle is how you empty a slot first.
    internal static class MarketEmpty
    {
        private const string SaveKey = "fabFixesMarketEmpty";
        private static readonly Dictionary<string, HashSet<string>> emptying = new Dictionary<string, HashSet<string>>();   // market guid -> goods being emptied

        private static readonly AccessTools.FieldRef<CanFindStorageWithMissingResource, Worker> WorkerOf = AccessTools.FieldRefAccess<CanFindStorageWithMissingResource, Worker>("worker");
        private static readonly AccessTools.FieldRef<CanFindStorageWithMissingResource, Storage> OriginOf = AccessTools.FieldRefAccess<CanFindStorageWithMissingResource, Storage>("originStorage");
        private static readonly System.Reflection.MethodInfo CapsMethod = AccessTools.Method(typeof(CanFindStorageWithMissingResource), "GetOriginStorageCapacities");
        private static readonly System.Reflection.MethodInfo OriginsMethod = AccessTools.Method(typeof(CanFindStorageWithMissingResource), "GetAllOriginStorages");
        private static readonly Type EntryType = AccessTools.Inner(typeof(CanFindStorageWithMissingResource), "ResourceCapacitiesStorage");
        private static readonly System.Reflection.FieldInfo CapacityField = EntryType != null ? AccessTools.Field(EntryType, "resourceCapacity") : null;
        private static readonly System.Reflection.FieldInfo CapacityObjectField = EntryType != null ? AccessTools.Field(EntryType, "gameObject") : null;
        private static bool warned;
        private static void Warn(string what, Exception ex) { if (!warned) { warned = true; Plugin.Log.LogWarning("[market] " + what + ": " + ex.Message); } }

        // --- the state ------------------------------------------------------------------------------------------------

        private static string GuidOf(Storage st) => st != null && st.worldObject != null ? st.worldObject.guid : null;
        internal static HashSet<string> KeysOf(Storage st) { var g = GuidOf(st); return g != null && emptying.TryGetValue(g, out var keys) ? keys : null; }
        internal static bool IsEmptying(Storage st, string key) { var keys = KeysOf(st); return keys != null && key != null && keys.Contains(key); }

        internal static void Set(Market market, string key, bool on)
        {
            var st = market != null ? market.GetComponent<Storage>() : null;
            string g = GuidOf(st);
            if (g == null || string.IsNullOrEmpty(key)) return;
            if (!emptying.TryGetValue(g, out var keys)) emptying[g] = keys = new HashSet<string>();
            bool changed = on ? keys.Add(key) : keys.Remove(key);
            if (keys.Count == 0) emptying.Remove(g);
            if (changed) Plugin.Log.LogInfo($"[market] {g.Substring(0, 8)}: {(on ? "emptying" : "no longer emptying")} {key} ({st.GetResourceStored(key)} on the stall).");
        }

        // A slot that changes its good, or a marketeer leaving, drops the good from the list (the game has already deleted its stock).
        [HarmonyPatch(typeof(Market), nameof(Market.AlterActiveCapacity)), HarmonyPostfix]
        private static void SlotChanged(Market __instance) => Reconcile(__instance);

        [HarmonyPatch(typeof(Market), "HandleWorkerRemoved"), HarmonyPostfix]
        private static void WorkerLeft(Market __instance) => Reconcile(__instance);

        private static void Reconcile(Market market)
        {
            try
            {
                var st = market != null ? market.GetComponent<Storage>() : null;
                var keys = KeysOf(st);
                if (keys == null) return;
                var active = new HashSet<string>(new[] { market.activeCapacity1Key, market.activeCapacity2Key, market.activeCapacity3Key, market.activeCapacity4Key }.Where(k => !string.IsNullOrEmpty(k)));
                foreach (var gone in keys.Where(k => !active.Contains(k)).ToList()) Set(market, gone, false);
            }
            catch (Exception ex) { Warn("reconcile", ex); }
        }

        // --- dismissing a marketeer takes their slot's good, not the last slot's ------------------------------------
        // The game numbers the slots by the worker count: dismiss anyone and slot N goes, whatever it holds (Jess, 2026-10-24:
        // "it always fires the last person and the fourth good on the list is what is eliminated"). The marketeers' portraits
        // and the overview menu both dismiss through Worker.QuitProfession, so before the game acts, the dismissed worker's
        // slot (their position among the workers) is swapped into the last slot, and the game's removal takes that good.
        // Sorting (isSort) moves workers between buildings and is left alone.
        [HarmonyPatch(typeof(Worker), nameof(Worker.QuitProfession)), HarmonyPrefix]
        private static void SlotFollowsTheWorker(Worker __instance, bool isSort)
        {
            try
            {
                if (isSort || __instance == null || __instance.workPlace == null) return;
                var market = __instance.workPlace.GetComponent<Market>();
                if (market == null) return;
                var workers = __instance.workPlace.GetWorkers();
                int n = workers != null ? workers.Count : 0, k = workers != null ? workers.IndexOf(__instance) : -1;
                if (n <= 1 || k < 0 || k == n - 1) return;
                var keys = new[] { market.activeCapacity1Key, market.activeCapacity2Key, market.activeCapacity3Key, market.activeCapacity4Key };
                string mine = keys[k]; keys[k] = keys[n - 1]; keys[n - 1] = mine;
                market.activeCapacity1Key = keys[0]; market.activeCapacity2Key = keys[1]; market.activeCapacity3Key = keys[2]; market.activeCapacity4Key = keys[3];
                var st = market.GetComponent<Storage>();
                int left = st != null && !string.IsNullOrEmpty(mine) ? st.GetResourceStored(mine) : 0;
                Plugin.Log.LogInfo($"[market] marketeer {k + 1} dismissed: slot {k + 1}'s good ({mine}) goes with them" + (left > 0 && keys.Take(n - 1).All(x => x != mine) ? $"; {left} on the stall are lost, as the game does (empty the slot first)" : "."));
            }
            catch (Exception ex) { Warn("dismiss", ex); }
        }

        // --- the marketeer: no restocking of a good being emptied --------------------------------------------------

        [HarmonyPatch(typeof(CanFindStorageWithMissingResource), "GetOriginStorageCapacities"), HarmonyPostfix]
        private static void NotWhileEmptying(CanFindStorageWithMissingResource __instance, object __result)
        {
            try
            {
                if (emptying.Count == 0 || !(__result is IList list) || list.Count == 0 || CapacityField == null) return;
                var origin = OriginOf(__instance);
                if (origin == null || origin.GetComponent<Market>() == null) return;
                var keys = KeysOf(origin);
                if (keys == null || keys.Count == 0) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var cap = (ResourceCapacity)CapacityField.GetValue(list[i]);
                    if (keys.Contains(cap.key)) list.RemoveAt(i);
                }
            }
            catch (Exception ex) { Warn("restock filter", ex); }
        }

        // --- the haulers: a granary or stockpile worker takes the good out ------------------------------------------

        // Set by the search below for the reservation that follows it, for this one worker's search.
        private static Storage steerStore, steerOrigin;
        private static string steerKey;
        private static int steerRoom;

        [HarmonyPatch(typeof(CanFindStorageWithMissingResource), "GetBestStorageSource"), HarmonyPrefix]
        private static bool FromAnEmptyingMarket(CanFindStorageWithMissingResource __instance, ref Transform __result)
        {
            steerStore = null; steerOrigin = null; steerKey = null; steerRoom = 0;
            try
            {
                if (emptying.Count == 0 || CapsMethod == null || OriginsMethod == null || CapacityField == null || CapacityObjectField == null) return true;
                // only the stores' own fetchers (granary, stockpile) haul for an emptied store, as in the game
                var origins = OriginsMethod.Invoke(__instance, null) as List<Storage>;
                if (origins == null || !origins.Any(o => o != null && (o.isGeneralStorage || o.granary != null || o.stockpileLot != null))) return true;
                if (origins.Any(o => o != null && o.GetComponent<Market>() != null)) return true;   // a market does not haul for another
                var caps = CapsMethod.Invoke(__instance, null) as IList;   // what the origin is missing, with room, the game's own list (the overflow fix nets off pledges)
                if (caps == null || caps.Count == 0) return true;
                var worker = WorkerOf(__instance);
                var from = worker != null ? worker.transform.position : origins[0].transform.position;
                float bestDist = float.MaxValue;
                foreach (var st in Storage.RetrievableInstances)
                {
                    if (st == null) continue;
                    var keys = KeysOf(st);
                    if (keys == null || keys.Count == 0) continue;
                    float d = (st.transform.position - from).sqrMagnitude;
                    if (d >= bestDist) continue;
                    foreach (var entry in caps)
                    {
                        var cap = (ResourceCapacity)CapacityField.GetValue(entry);
                        if (!keys.Contains(cap.key) || cap.amount <= 0) continue;
                        if (st.GetResourceAvailable(cap.key) <= 0 || !st.IsResourceRetrievableAndUnlocked(cap.key)) continue;
                        var go = CapacityObjectField.GetValue(entry) as GameObject;
                        var origin = go != null ? go.GetComponent<Storage>() : null;
                        if (origin == null || origin == st) continue;
                        steerStore = st; steerOrigin = origin; steerKey = cap.key; steerRoom = cap.amount; bestDist = d;
                        break;
                    }
                }
                if (steerStore == null) return true;
                __result = steerStore.transform;
                return false;
            }
            catch (Exception ex) { Warn("haul search", ex); steerStore = null; return true; }
        }

        [HarmonyPatch(typeof(CanFindStorageWithMissingResource), "ReserveResource"), HarmonyPrefix]
        private static bool ReserveTheEmptyingGood(CanFindStorageWithMissingResource __instance, Storage pickupStorage)
        {
            if (steerStore == null || pickupStorage != steerStore || steerKey == null || steerOrigin == null) return true;
            var st = steerStore; var origin = steerOrigin; string key = steerKey; int room = steerRoom;
            steerStore = null; steerOrigin = null; steerKey = null; steerRoom = 0;
            try
            {
                var worker = WorkerOf(__instance);
                if (worker == null) return true;
                var data = DataManager.Instance.GetResourceDataFromKey(key);
                int n = Mathf.Min(room, st.GetResourceAvailable(key), data != null ? data.GetCarryCapacity(worker.GetProfessionData()) : room);
                if (n <= 0) return true;
                if (__instance.alternateOrigin != null) __instance.alternateOrigin.Value = origin.gameObject;
                worker.ReserveResources(st, key, n, alterGlobal: true);
                return false;
            }
            catch (Exception ex) { Warn("haul reservation", ex); return true; }
        }

        // --- the panel: an Empty toggle under each slot's dropdown ---------------------------------------------------

        private static readonly AccessTools.FieldRef<MarketContainer, Market> PanelMarket = AccessTools.FieldRefAccess<MarketContainer, Market>("market");
        private static readonly AccessTools.FieldRef<MarketContainer, WorkPlace> PanelWork = AccessTools.FieldRefAccess<MarketContainer, WorkPlace>("workPlace");
        private static readonly AccessTools.FieldRef<EmptyStorage, Toggle> EmptyToggle = AccessTools.FieldRefAccess<EmptyStorage, Toggle>("toggle");
        private static readonly Dictionary<MarketContainer, Toggle[]> toggles = new Dictionary<MarketContainer, Toggle[]>();
        private static Toggle template;
        private static GameObject templateRow;
        private static bool noTemplateLogged, madeLogged;

        [HarmonyPatch(typeof(MarketContainer), "UpdateFullUi"), HarmonyPostfix]
        private static void AfterFullUi(MarketContainer __instance) { Ensure(__instance); Refresh(__instance); }

        [HarmonyPatch(typeof(MarketContainer), "UpdateStorageUi"), HarmonyPostfix]
        private static void AfterStorageUi(MarketContainer __instance) => Refresh(__instance);

        private static void Ensure(MarketContainer panel)
        {
            try
            {
                if (panel == null || toggles.ContainsKey(panel)) return;
                if (template == null)
                {
                    // the game's own "Empty storage" toggle, as the Granary's and the Stockpile's panels use it
                    var source = Resources.FindObjectsOfTypeAll<EmptyStorage>().FirstOrDefault(e => e != null && EmptyToggle(e) != null);
                    template = source != null ? EmptyToggle(source) : null;
                    // The toggle is a circle; its "Empty storage" label and the tooltip sit beside it on the row the EmptyStorage
                    // component owns (Jess's screenshots, 2026-10-24: the first build copied the circle alone). Copy the row when it
                    // is just that, one toggle and its label, so the label reads in the game's own words and language.
                    var row = source != null ? source.gameObject : null;
                    if (row != null && row.GetComponentsInChildren<Toggle>(true).Length == 1 && row.GetComponentsInChildren<CustomDropDown>(true).Length == 0 && row.GetComponentsInChildren<TMP_Text>(true).Length <= 2)
                        templateRow = row;
                }
                if (template == null) { if (!noTemplateLogged) { noTemplateLogged = true; Plugin.Log.LogWarning("[market] no Empty-storage toggle found to copy; the market slots get none."); } return; }
                var tr = Traverse.Create(panel);
                var made = new Toggle[4];
                for (int i = 0; i < 4; i++)
                {
                    var dd = tr.Field("dropDown" + (i + 1)).GetValue<CustomDropDown>();
                    if (dd == null) continue;
                    // on the dropdown's own line, as its child at the right end beside the arrow, so the panel grows no rows
                    // (Jess, 2026-10-24: "the marketplace menu is getting really long")
                    var go = UnityEngine.Object.Instantiate(templateRow != null ? templateRow : template.gameObject, dd.transform);
                    go.name = "Fab empty slot " + (i + 1);
                    var t = go.GetComponent<Toggle>() ?? go.GetComponentInChildren<Toggle>(true);
                    if (t == null) { UnityEngine.Object.Destroy(go); continue; }
                    // The toggle's event carries the game's own wiring: the call into EmptyStorage, which the clone must not make,
                    // and the call that draws the check mark (Jess, 2026-10-24: replacing the whole event lost the mark). Switch off
                    // only the EmptyStorage calls, then drop the component.
                    var ev = t.onValueChanged;
                    for (int k = 0; k < ev.GetPersistentEventCount(); k++)
                    {
                        var target = ev.GetPersistentTarget(k);
                        if (target == null || target is EmptyStorage) ev.SetPersistentListenerState(k, UnityEngine.Events.UnityEventCallState.Off);
                    }
                    foreach (var es in go.GetComponentsInChildren<EmptyStorage>(true)) UnityEngine.Object.DestroyImmediate(es);   // the label's localizer stays
                    if (templateRow == null)
                    {
                        // no row to copy: give the lone circle the game's label, copied from beside the original toggle
                        var srcLabel = template.transform.parent != null ? template.transform.parent.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(l => !l.transform.IsChildOf(template.transform)) : null;
                        if (srcLabel != null)
                        {
                            var lbl = UnityEngine.Object.Instantiate(srcLabel.gameObject, go.transform);
                            lbl.name = "Fab empty label";
                            var lrt = lbl.GetComponent<RectTransform>(); var trt = go.GetComponent<RectTransform>();
                            if (lrt != null && trt != null) { lrt.anchorMin = lrt.anchorMax = new Vector2(1f, 0.5f); lrt.pivot = new Vector2(0f, 0.5f); lrt.anchoredPosition = new Vector2(6f, 0f); }
                        }
                    }
                    int slot = i;
                    t.onValueChanged.AddListener(on => OnToggle(panel, slot, on));
                    // The row's width came from the Stockpile panel it was copied out of; on its own it has none and the label wrapped a
                    // letter per line. Fixed size, anchored to the dropdown's right edge, left of the arrow; the label runs on one line.
                    var rt = go.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        float height = rt.rect.height > 0f ? Mathf.Min(rt.rect.height, 28f) : 24f;
                        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
                        rt.sizeDelta = new Vector2(Plugin.MarketEmptyWidth.Value, height);
                        rt.anchoredPosition = new Vector2(-Plugin.MarketEmptyArrowGap.Value + Plugin.MarketEmptyOffsetX.Value, Plugin.MarketEmptyOffsetY.Value);
                        rt.localScale = Vector3.one;
                    }
                    var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                    le.ignoreLayout = true;   // a child of the dropdown; no layout group may move it
                    foreach (var tmp in go.GetComponentsInChildren<TMP_Text>(true)) { tmp.enableWordWrapping = false; tmp.overflowMode = TextOverflowModes.Overflow; }
                    made[i] = t;
                }
                toggles[panel] = made;
                if (!madeLogged) { madeLogged = true; Plugin.Log.LogInfo($"[market] Empty toggles added to the Marketplace panel ({made.Count(t => t != null)} of 4)."); }
            }
            catch (Exception ex) { Warn("panel toggles", ex); }
        }

        private static void Refresh(MarketContainer panel)
        {
            try
            {
                if (panel == null || !toggles.TryGetValue(panel, out var made)) return;
                var market = PanelMarket(panel);
                var work = PanelWork(panel);
                int workers = work != null ? work.GetWorkers().Count : 0;
                var st = market != null ? market.GetComponent<Storage>() : null;
                string[] keys = market != null ? new[] { market.activeCapacity1Key, market.activeCapacity2Key, market.activeCapacity3Key, market.activeCapacity4Key } : new string[4];
                for (int i = 0; i < 4; i++)
                {
                    var t = made[i];
                    if (t == null) continue;
                    bool show = market != null && i < workers && !string.IsNullOrEmpty(keys[i]);
                    t.gameObject.SetActive(show);
                    if (show) t.SetIsOnWithoutNotify(IsEmptying(st, keys[i]));
                }
            }
            catch (Exception ex) { Warn("panel refresh", ex); }
        }

        private static void OnToggle(MarketContainer panel, int slot, bool on)
        {
            try
            {
                var market = PanelMarket(panel);
                if (market == null) return;
                string key = slot == 0 ? market.activeCapacity1Key : slot == 1 ? market.activeCapacity2Key : slot == 2 ? market.activeCapacity3Key : market.activeCapacity4Key;
                Set(market, key, on);
                Refresh(panel);   // two slots holding the same good share one state
            }
            catch (Exception ex) { Warn("toggle", ex); }
        }

        // --- save and load -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPrefix]
        private static void BeforeSave()
        {
            try
            {
                if (SaveMaster.GetActiveSlot() < 0) return;
                SaveMaster.SetString(SaveKey, string.Join(";", emptying.Select(kv => kv.Key + "=" + string.Join(",", kv.Value))));
            }
            catch (Exception ex) { Warn("save", ex); }
        }

        [HarmonyPatch(typeof(DataManager), "Load"), HarmonyPostfix]
        private static void AfterLoad(int slot)
        {
            emptying.Clear();
            if (slot < 0) return;
            try
            {
                string s = SaveMaster.GetString(SaveKey);
                if (string.IsNullOrEmpty(s)) return;
                foreach (var part in s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=');
                    if (kv.Length != 2) continue;
                    var keys = new HashSet<string>(kv[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                    if (keys.Count > 0) emptying[kv[0]] = keys;
                }
                if (emptying.Count > 0) Plugin.Log.LogInfo($"[market] {emptying.Count} market(s) emptying a slot, from the save.");
            }
            catch (Exception ex) { Warn("load", ex); }
        }

        [HarmonyPatch(typeof(WorldEventManager), "Start"), HarmonyPrefix]
        private static void NewGame(WorldEventManager __instance)
        {
            try { if (!Traverse.Create(__instance).Field("isLoaded").GetValue<bool>()) emptying.Clear(); } catch { }
        }
    }
}
