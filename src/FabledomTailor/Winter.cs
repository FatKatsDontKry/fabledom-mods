using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // The winter rule (Jess, 2026-10-05): a Sheep farm shears nothing in winter and eats a grain a day from its own
    // store instead. Every winter day the store is empty costs recovery days of no shearing in spring (by difficulty,
    // see RecoveryPerHungryDay): the sheep are shut off while they recover, never killed. The grain store holds 50 so a
    // stocked farm goes through a winter.
    [HarmonyPatch]
    internal static class Winter
    {
        private const string Status = "fabSheepUnfed";
        internal static Dictionary<string, float> Debt => Garments.S.FarmDebt;   // recovery days owed, by farm guid

        private static bool IsFarm(ProductionPlace p) => p != null && Tailor.FarmData != null && p.worldObject != null && p.worldObject.data == Tailor.FarmData;
        private static bool IsWinter() => DateTimeManager.Instance != null && DateTimeManager.Instance.dateTimeData.currentSeason == Season.WINTER;

        // No shearing in winter, and none while the sheep are recovering from a hungry one.
        [HarmonyPatch(typeof(ProductionPlace), "IsProductionAllowed"), HarmonyPostfix]
        private static void NoWinterShearing(ProductionPlace __instance, ref bool __result)
        {
            if (!__result || !Plugin.WinterRule.Value || !IsFarm(__instance)) return;
            if (IsWinter()) { __result = false; return; }
            string guid = __instance.worldObject.guid;
            if (guid != null && Debt.TryGetValue(guid, out float days) && days > 0f) __result = false;
        }

        // The flock is shut off: winter, still recovering from a hungry one, or the farm's wool store is full (Jess,
        // 2026-10-08: nothing to shear into, so the shearer waits out front like in winter).
        internal static bool StandsInFront(WorldObject wo)
        {
            if (FullOfWool(wo)) return true;
            if (OutOfGrain(wo)) return true;   // no grain for the next wool: nothing is being made, so no shearing (Jess, 2026-10-24)
            if (!Plugin.WinterRule.Value) return false;
            if (IsWinter()) return true;
            return wo != null && wo.guid != null && Debt.TryGetValue(wo.guid, out float d) && d > 0f;
        }

        // The farm cannot start its next wool for want of grain (the game's own input check, which the winter top-up also
        // holds false until the store is filled). The shearer went on miming the shears over the flock while the farm stood
        // idle (Jess, 2026-10-24: "they go shear even if they're out of grain"); now he waits out front, as in winter, until
        // grain arrives. While he is away fetching it he is not at the spot anyway.
        internal static bool OutOfGrain(WorldObject wo)
        {
            var p = wo != null ? wo.GetComponent<ProductionPlace>() : null;
            try { return p != null && !p.HasEnoughInput(); } catch { return false; }
        }

        internal static bool FullOfWool(WorldObject wo)
        {
            var p = wo != null ? wo.GetComponent<ProductionPlace>() : null;
            try { return p != null && p.storage != null && !p.HasRoomForOutput(); } catch { return false; }
        }

        // Winter 1 (Jess, 2026-10-08): every farm with less than WinterFillTo grain goes for more straight away, even if a
        // full load would not fit. Once the farm holds that much, it is back to the game's own rule for the rest of winter.
        // The shearer only goes for grain when the farm cannot make its next wool ("not enough input"), and keeps shearing
        // the store down until then (Jess, 2026-10-08). While a farm is topping up, it reads as short, so the worker goes.
        [HarmonyPatch(typeof(ProductionPlace), nameof(ProductionPlace.HasEnoughInput)), HarmonyPostfix]
        private static void ShortUntilToppedUp(ProductionPlace __instance, ref bool __result)
        {
            if (!__result || !IsFarm(__instance) || __instance.worldObject.guid == null || !Garments.S.TopUp.Contains(__instance.worldObject.guid)) return;
            int grain = __instance.storage != null ? __instance.storage.GetResourceStored("grain") : 0;
            if (grain < Plugin.WinterFillTo.Value) __result = false;
        }

        [HarmonyPatch(typeof(ProductionPlace), nameof(ProductionPlace.CanFitMoreInput)), HarmonyPostfix]
        private static void TopUpForWinter(ProductionPlace __instance, ref bool __result)
        {
            if (__result || !IsFarm(__instance) || __instance.worldObject.guid == null || !Garments.S.TopUp.Contains(__instance.worldObject.guid)) return;
            int grain = __instance.storage != null ? __instance.storage.GetResourceStored("grain") : 0;
            if (grain < Plugin.WinterFillTo.Value) __result = true;
            else Garments.S.TopUp.Remove(__instance.worldObject.guid);
        }

        // A farm finished in winter (Jess, 2026-10-10): a few days of grace before hunger counts, and the worker is sent
        // for grain at once, as at the start of winter.
        [HarmonyPatch(typeof(Constructable), nameof(Constructable.ForceConstructionComplete)), HarmonyPostfix]
        private static void FarmFinished(Constructable __instance)
        {
            try
            {
                var wo = __instance.worldObject;
                if (wo == null || wo.data != Tailor.FarmData || string.IsNullOrEmpty(wo.guid) || Tailor.FarmData == null) return;
                if (Plugin.BuildGraceDays.Value > 0) Garments.S.Grace[wo.guid] = Plugin.BuildGraceDays.Value;
                var place = wo.GetComponent<ProductionPlace>();
                int grain = place != null && place.storage != null ? place.storage.GetResourceStored("grain") : 0;
                if (IsWinter() && grain < Plugin.WinterFillTo.Value) Garments.S.TopUp.Add(wo.guid);
                Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) finished with {grain} grain; {Plugin.BuildGraceDays.Value} day(s) of grace" + (IsWinter() ? $", fetching up to {Plugin.WinterFillTo.Value} for winter." : "."));
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: farm finished: " + ex.Message); }
        }

        // Loading a winter save: any farm short of grain is sent for more, the same rule as the first day of winter
        // (catches farms finished in winter before this rule existed, and saves made before the start of winter).
        internal static void TopUpOnLoad()
        {
            if (!Plugin.WinterRule.Value || Tailor.FarmData == null || !IsWinter()) return;
            foreach (var wo in WorldObject.Instances.ToList())
            {
                if (wo == null || wo.data != Tailor.FarmData || string.IsNullOrEmpty(wo.guid)) continue;
                var place = wo.GetComponent<ProductionPlace>();
                var constructable = wo.GetComponent<Constructable>();
                if (place == null || place.storage == null || constructable == null || !constructable.isConstructionComplete) continue;
                int grain = place.storage.GetResourceStored("grain");
                if (grain >= Plugin.WinterFillTo.Value || Garments.S.TopUp.Contains(wo.guid)) continue;
                Garments.S.TopUp.Add(wo.guid);
                Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) loaded in winter with {grain} grain; fetching up to {Plugin.WinterFillTo.Value}.");
            }
        }

        // Once a day: feed the sheep in winter, or run down the recovery in the other seasons.
        internal static void Daily()
        {
            if (!Plugin.WinterRule.Value || Tailor.FarmData == null) return;
            bool winter = IsWinter();
            var dtm = DateTimeManager.Instance;
            string season = dtm.dateTimeData.currentSeason.ToString();
            bool winterBegins = winter && Garments.S.LastSeason != season && Garments.S.LastSeason != "";
            Garments.S.LastSeason = season;
            // The run starts before the snow (Jess, 2026-10-26: "if it is winter 1, we should make it fall 30"): on the last
            // WinterRunDaysEarly days of autumn a farm under WinterFillTo goes for grain as it would on winter day 1, so the
            // ration is in the barn when the first winter day takes it. 0 = winter day 1, as before.
            int daysLeftInAutumn = dtm.GetDaysInSeason(Season.AUTUMN) - dtm.dateTimeData.currentDay;
            bool winterEve = !winter && dtm.dateTimeData.currentSeason == Season.AUTUMN && daysLeftInAutumn < Plugin.WinterRunDaysEarly.Value;
            if (!winter && !winterEve) Garments.S.TopUp.Clear();
            foreach (var wo in WorldObject.Instances.ToList())
            {
                if (wo == null || wo.data != Tailor.FarmData || string.IsNullOrEmpty(wo.guid)) continue;
                var place = wo.GetComponent<ProductionPlace>();
                var constructable = wo.GetComponent<Constructable>();
                if (place == null || constructable == null || !constructable.isConstructionComplete) continue;
                var sc = wo.statusController;
                Debt.TryGetValue(wo.guid, out float debt);
                Garments.S.Grace.TryGetValue(wo.guid, out int grace);
                if (grace > 0) { if (--grace > 0) Garments.S.Grace[wo.guid] = grace; else Garments.S.Grace.Remove(wo.guid); grace++; }   // grace counts this day
                if (winterEve && place.storage != null && place.storage.GetResourceStored("grain") < Plugin.WinterFillTo.Value && Garments.S.TopUp.Add(wo.guid))
                    Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) has {place.storage.GetResourceStored("grain")} grain with winter {daysLeftInAutumn + 1} day(s) off; fetching up to {Plugin.WinterFillTo.Value}.");
                if (winterBegins && place.storage != null && place.storage.GetResourceStored("grain") < Plugin.WinterFillTo.Value)
                {
                    Garments.S.TopUp.Add(wo.guid);
                    Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) starts winter with {place.storage.GetResourceStored("grain")} grain; fetching up to {Plugin.WinterFillTo.Value}.");
                }
                if (winter)
                {
                    var storage = place.storage;
                    int grain = storage != null ? storage.GetResourceStored("grain") : 0;
                    if (grain >= Plugin.WinterGrainPerDay.Value)
                    {
                        storage.Remove("grain", Plugin.WinterGrainPerDay.Value, alterGlobalStorage: false);
                        // fed: the marker stays while anything is owed, so a count from earlier in the winter is never hidden
                        // (Jess, 2026-10-24: a farm fed after three hungry days seemed to jump straight to 8 when it ran out again)
                        if (sc != null && debt <= 0f && sc.IsShowing(Status)) sc.Remove(Status);
                        else if (sc != null && debt > 0f && !sc.IsShowing(Status)) sc.Add(Status);
                    }
                    else if (grace > 0)
                    {
                        Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) has no grain, but is new; {grace - 1} day(s) of grace left.");
                    }
                    else
                    {
                        debt += RecoveryPerHungryDay();
                        if (debt > 0f && sc != null && !sc.IsShowing(Status)) sc.Add(Status);
                        Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) has no grain; the flock now owes {debt:0.#} recovery days ({RecoveryPerHungryDay():0.#} per hungry day on {DifficultyName()}).");
                    }
                }
                else if (debt > 0f)
                {
                    // recovery takes grain (Jess, 2026-10-10): a day without the ration is a day not recovered, and the
                    // worker is sent for grain meanwhile, as at the start of winter (shearing is off, so nothing else would)
                    var storage = place.storage;
                    int grain = storage != null ? storage.GetResourceStored("grain") : 0;
                    if (grain < Plugin.WinterFillTo.Value) Garments.S.TopUp.Add(wo.guid);
                    if (grain >= Plugin.WinterGrainPerDay.Value)
                    {
                        storage.Remove("grain", Plugin.WinterGrainPerDay.Value, alterGlobalStorage: false);
                        debt = Mathf.Max(0f, debt - 1f);
                        Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) recovering, {debt:0.#} day(s) left" + (debt <= 0f ? "; shearing resumes." : "."));
                    }
                    else Plugin.Log.LogInfo($"Tailor: Sheep farm ({wo.guid.Substring(0, 8)}) cannot recover without grain; {debt:0.#} day(s) still owed.");
                    if (debt <= 0f && sc != null && sc.IsShowing(Status)) sc.Remove(Status);
                    else if (debt > 0f && sc != null && !sc.IsShowing(Status)) sc.Add(Status);
                }
                if (debt > 0f) Debt[wo.guid] = debt; else Debt.Remove(wo.guid);
                Tailor.PlaceFarmSpots(wo.gameObject, StandsInFront(wo));
            }
        }

        // Recovery days owed per hungry winter day, by difficulty (Jess, 2026-10-06): none on Calm, 1 on Classic,
        // 1.5 on Challenge, 2 on Cruel. The game's difficulty config is matched by name; an unknown name counts as Classic.
        private static string difficultyLogged;
        internal static string DifficultyName()
        {
            try
            {
                var gd = DataManager.Instance.GetDifficultyConfig();
                string n = gd != null ? gd.name : "";
                if (difficultyLogged != n) { difficultyLogged = n; Plugin.Log.LogInfo($"Tailor: difficulty config is '{n}'."); }
                return n;
            }
            catch { return ""; }
        }
        // The band the mod's per-difficulty settings are keyed by: calm, classic, challenge or cruel (an unknown config
        // name counts as classic).
        internal static string DifficultyBand()
        {
            string n = DifficultyName().ToLowerInvariant();
            return n.Contains("calm") ? "calm" : n.Contains("cruel") ? "cruel" : n.Contains("challeng") ? "challenge" : "classic";
        }
        internal static float RecoveryPerHungryDay()
        {
            switch (DifficultyBand())
            {
                case "calm": return Plugin.WinterRecoveryCalm.Value;
                case "cruel": return Plugin.WinterRecoveryCruel.Value;
                case "challenge": return Plugin.WinterRecoveryChallenge.Value;
                default: return Plugin.WinterRecoveryClassic.Value;
            }
        }

        // The farm's own numbers on its marker (Jess, 2026-10-24: "how many days of recovery remain is the thing I want").
        // The status data is one asset shared by every farm, so the tooltip of the status slot is rewritten for the selected
        // farm when the slot is shown and again on each day change while it is hovered: in winter the days owed so far and
        // the grain in store, in recovery the days of no shearing left and whether there is grain to recover on.
        private static readonly AccessTools.FieldRef<StatusUiSlot, ShowTooltip> SlotTooltip = AccessTools.FieldRefAccess<StatusUiSlot, ShowTooltip>("tooltip");
        private static readonly AccessTools.FieldRef<StatusUiSlot, StatusData> SlotStatus = AccessTools.FieldRefAccess<StatusUiSlot, StatusData>("statusData");
        private static bool tipWarned;

        [HarmonyPatch(typeof(StatusUiSlot), nameof(StatusUiSlot.UpdateUi)), HarmonyPostfix]
        private static void FlockTip(StatusUiSlot __instance, StatusData statusData) => RewriteTip(__instance, statusData);

        [HarmonyPatch(typeof(StatusUiSlot), "UpdateTextWhileActive"), HarmonyPostfix]
        private static void FlockTipDaily(StatusUiSlot __instance) => RewriteTip(__instance, SlotStatus(__instance));

        private static void RewriteTip(StatusUiSlot slot, StatusData sd)
        {
            try
            {
                if (slot == null || sd == null || sd.key != Status || Tailor.FarmData == null) return;
                var selected = SelectionManager.GetFirstSelected();
                var wo = selected != null ? selected.GetComponent<WorldObject>() : null;
                if (wo == null || wo.data != Tailor.FarmData || string.IsNullOrEmpty(wo.guid)) return;
                var tip = SlotTooltip(slot);
                if (tip == null) return;
                var (title, body) = FlockText(wo);
                tip.SetTextFromScript(title, body);
            }
            catch (Exception ex) { if (!tipWarned) { tipWarned = true; Plugin.Log.LogWarning("Tailor: flock tooltip: " + ex.Message); } }
        }

        // Title and body for one farm's marker.
        internal static (string title, string body) FlockText(WorldObject wo)
        {
            Debt.TryGetValue(wo.guid, out float debt);
            int left = Mathf.CeilToInt(debt);
            var place = wo.GetComponent<ProductionPlace>();
            int grain = place != null && place.storage != null ? place.storage.GetResourceStored("grain") : 0;
            int ration = Plugin.WinterGrainPerDay.Value;
            string Days(int n) => n == 1 ? "1 day" : n + " days";
            if (IsWinter())
            {
                float rate = RecoveryPerHungryDay();
                if (grain >= ration && left > 0)
                    return ($"Flock owes {Days(left)} of recovery",
                        $"Fed today; grain in store: {grain}, the flock eats {ration} a day. Hungry days earlier this winter left {Days(left)} of no shearing owed, counted down one a day in spring. Each further hungry day adds {rate:0.#} {(rate == 1f ? "day" : "days")} (on {DifficultyName()}).");
                return ($"Hungry sheep: {Days(left)} of recovery owed",
                    $"The flock has no grain today. Each hungry winter day costs {rate:0.#} {(rate == 1f ? "day" : "days")} of no shearing in spring (on {DifficultyName()}). Owed so far: {Days(left)}. Grain in store: {grain}; the flock eats {ration} a day. Keep the farm stocked and the count stops growing.");
            }
            if (left > 0)
            {
                string grainLine = grain >= ration
                    ? $"Grain in store: {grain}; recovering costs {ration} a day."
                    : $"Grain in store: {grain}, not enough to recover on; today does not count until grain arrives.";
                return ($"Recovering flock: {Days(left)} of no shearing left",
                    $"The flock is recovering from a hungry winter. {Days(left)} to go, one a day. {grainLine}");
            }
            return (sd_title(), sd_body());
        }
        private static string sd_title() { var sd = DataManager.Instance != null ? DataManager.Instance.GetStatusDataFromKey(Status) : null; return sd != null ? sd.title.GetLocalizedString() : "Hungry sheep"; }
        private static string sd_body() { var sd = DataManager.Instance != null ? DataManager.Instance.GetStatusDataFromKey(Status) : null; return sd != null ? sd.description.GetLocalizedString() : ""; }

        // "Sheep unfed", cloned from the game's "missing coal" status with the grain's icon.
        internal static void SetupStatus(DataManager dm)
        {
            try
            {
                if (dm.statusSoData.Any(s => s != null && s.key == Status)) return;
                var coal = dm.GetStatusDataFromKey("missingCoal");
                var grain = dm.resourceSoData.FirstOrDefault(r => r != null && r.key == "grain");
                if (coal == null || grain == null) return;
                var st = UnityEngine.Object.Instantiate(coal);
                st.name = Status; st.key = Status;
                st.title = ModText.Make("Hungry sheep");
                st.description = ModText.Make("The flock has no grain. Each hungry winter day costs shearing in spring while they recover (more on harder difficulties). Keep the farm's grain store stocked through the winter.");
                // the game's own starving mark (a wheat ear on a disc) is the right picture for a hungry flock (Jess, 2026-10-07)
                var starving = dm.GetStatusDataFromKey("starving");
                st.icon = starving != null && !string.IsNullOrEmpty(starving.icon) ? starving.icon : Utils_Text.FontSprite(grain.GetSpriteKey());
                var sprite = starving != null && starving.sprite != null ? starving.sprite : Icons.Load("hungrysheep");
                if (sprite != null) st.sprite = sprite;
                // the ear from the starving mark, on the orange disc the game gives a building short of something (coal, a head
                // of household, storage): red discs are people in danger, and eight red ears over farms read as a famine (Jess)
                st.bgColor = coal.bgColor;
                dm.statusSoData.Add(st);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Tailor: sheep status: " + ex.Message); }
        }

        // The farm's grain store, sized for a winter (set on the prefab, so every farm gets it).
        internal static void SizeGrainStore(GameObject farmPrefab)
        {
            try
            {
                var storage = farmPrefab != null ? farmPrefab.GetComponent<Storage>() : null;
                var slot = storage != null ? storage.resourcesCapacity.FirstOrDefault(r => r != null && r.resourceAmountData != null && r.resourceAmountData.resourceData != null && r.resourceAmountData.resourceData.key == "grain") : null;
                if (slot == null) { Plugin.Log.LogWarning("Tailor: the Sheep farm prefab has no grain slot to size."); return; }
                if (slot.resourceAmountData.amount < Plugin.FarmGrainStore.Value) slot.resourceAmountData.amount = Plugin.FarmGrainStore.Value;
                Plugin.Log.LogInfo($"Tailor: Sheep farm grain store holds {slot.resourceAmountData.amount}.");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Tailor: grain store: " + ex.Message); }
        }
    }
}
