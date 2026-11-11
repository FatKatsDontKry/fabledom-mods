using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // Commoners and nobles are particular about food as well as clothes (Jess, 2026-10-08): the half the Tailor package
    // gives commoners, to go with the garments nobles want.
    //  - The variety rule (Jess, 2026-10-10; it replaced the game's streak counter, which only ever asked for a second
    //    food): a commoner wants three kinds of food across their last four meals, a noble three of four to five of six
    //    by difficulty (CommonerVarietyKinds/Meals, NobleVariety). Fewer is the penalty, -10 commoners, -20 nobles, judged
    //    at each meal; until a fabling has eaten that many meals there is no penalty. Peasants are left alone, as the
    //    game did. The food split (Jess, 2026-10-13 and 16; ShelfSwaps): a town house shelves pastry, fish, egg, bread and
    //    pork (pastry replaces the game's vegetable shelf; vegetables are peasant food); the living quarters egg, bread,
    //    pastry, pork and grape juice (pastry replaces grapes), so five for nobles means every shelf of theirs stocked.
    //    Nobody eats grapes at home.
    //  - Shelves and errands to match: town house shelves are small enough to cycle (TownHouseShelves), a fabling's own
    //    meal pick avoids the two remembered kinds, and heads and palace staff bring a missing kind to any house holding
    //    as many kinds as its residents want or fewer, until it holds one more than they want (SpareKinds; 2026-11-11;
    //    it was every kind the shelf takes from 11-09, and fewer than wanted, then stop, before that), lowest priority, except for a house whose resident is already on the penalty.
    //  - A record keeps one meal more than the window, and the nobles can be judged afresh on request (JudgeNobles:
    //    Rivals, daily through Gastón's third date and when it moves the rule).
    //  - A sampler (HeadSampler, off by default, for tuning) logs once a day how the heads spend their time.
    [HarmonyPatch]
    internal static class Food
    {
        private static WorldObjectData condoData, townHouseData, palaceData, quartersData;

        // Private fields read on hot paths, bound once (a Traverse per call was the old way; it reflects every time).
        internal static readonly AccessTools.FieldRef<Needs, Worker> NeedsWorker = AccessTools.FieldRefAccess<Needs, Worker>("worker");
        private static readonly AccessTools.FieldRef<AttachedHousingMissingFood, Hub> FoodHub = AccessTools.FieldRefAccess<AttachedHousingMissingFood, Hub>("hub");
        private static readonly AccessTools.FieldRef<CanFindStorageWithEdible, Storage> EdibleHouse = AccessTools.FieldRefAccess<CanFindStorageWithEdible, Storage>("originStorage");
        private static readonly AccessTools.FieldRef<CanFindStorageWithEdible, Worker> EdibleWorker = AccessTools.FieldRefAccess<CanFindStorageWithEdible, Worker>("worker");

        // A behaviour-tree task's own object: its Owner is the Behavior component on the fabling, so no reflection is needed.
        internal static GameObject GameObjectOf(BehaviorDesigner.Runtime.Tasks.Task task) => task != null && task.Owner != null ? task.Owner.gameObject : null;

        // --- setup ---------------------------------------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            if (condoData != null) return;
            try
            {
                var all = Resources.FindObjectsOfTypeAll<WorldObjectData>();
                condoData = all.FirstOrDefault(d => d != null && d.name == "Condominium");
                townHouseData = all.FirstOrDefault(d => d != null && d.name == "Town house");
                palaceData = all.FirstOrDefault(d => d != null && d.name == "Palace main hall");   // its staff stock the living quarters
                quartersData = all.FirstOrDefault(d => d != null && d.name == "Palace living quarters");
                HookDormancy();
                if (!Plugin.FoodVariety.Value) return;
                if (condoData != null)
                {
                    int was = condoData.workerCapacity;
                    condoData.workerCapacity = Mathf.Max(was, Plugin.CondoHeads.Value);
                    Plugin.Log.LogInfo($"Tailor: Condominium heads of household {was} -> {condoData.workerCapacity}.");
                }
                ParseSwaps();
                ResourceData Res(string key) => __instance.resourceSoData.FirstOrDefault(r => r != null && r.key == key);
                AddExtraFoods(Res);
                foreach (var data in new[] { townHouseData, quartersData })
                {
                    var prefab = data != null ? Resources.Load<GameObject>(data.GetFullPath()) : null;
                    var storage = prefab != null ? prefab.GetComponent<Storage>() : null;
                    if (storage == null) { if (data != null) Plugin.Log.LogWarning($"Tailor: {data.name} prefab not found; its shelves are unchanged."); continue; }
                    var changed = Shelves(storage, data, false, Res);
                    if (changed.Count > 0) Plugin.Log.LogInfo($"Tailor: {data.name} food shelves: " + string.Join(", ", changed) + ".");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: food setup: " + ex.Message); }
        }

        // The food split (Jess, 2026-10-13; ShelfSwaps): a town house's vegetable shelf becomes pastry and the quarters'
        // grapes shelf becomes pastry. Prefabs are rekeyed at setup; standing houses after a load (below), where the stock
        // of the old kind stays edible but has no slot, so nothing more is brought and it is eaten down over a few days.
        private static readonly List<(string building, string from, string to)> swaps = new List<(string, string, string)>();
        private static void ParseSwaps()
        {
            swaps.Clear();
            foreach (var part in (Plugin.ShelfSwaps.Value ?? "").Split(','))
            {
                var a = part.Split(':'); if (a.Length != 2) continue;
                var b = a[1].Split('>'); if (b.Length != 2) continue;
                swaps.Add((a[0].Trim(), b[0].Trim(), b[1].Trim()));
            }
        }

        // One storage's shelves brought to the plan: slots rekeyed by ShelfSwaps, then sized (town houses by
        // TownHouseShelves, the quarters by QuartersShelves; a size given for a swapped-out kind applies to its
        // replacement). A standing house also gets a stored-amount record for the new kind, which the game only makes
        // when a storage is first set up. Returns what changed.
        private static List<string> Shelves(Storage st, WorldObjectData data, bool standing, System.Func<string, ResourceData> res)
        {
            var changed = new List<string>();
            if (st == null || st.resourcesCapacity == null || data == null) return changed;
            foreach (var (building, from, to) in swaps)
            {
                if (building != data.name) continue;
                var rd = res(to);
                if (rd == null) { Plugin.Log.LogWarning($"Tailor: shelf swap {from}>{to}: no resource called {to}."); continue; }
                foreach (var slot in st.resourcesCapacity)
                {
                    if (slot?.resourceAmountData?.resourceData == null || slot.resourceAmountData.resourceData.key != from) continue;
                    slot.resourceAmountData.resourceData = rd;
                    if (standing && st.resourcesStored != null && !st.resourcesStored.Any(r => r != null && r.key == to)) st.resourcesStored.Add(new SaveableResourceAmountData(to, 0));
                    changed.Add($"{from}->{to}");
                }
            }
            // shelves a house lacks (QuartersExtraShelves: the nobles' truffles, 2026-11-05): a slot added for a kind no slot holds
            var extraPlan = data == quartersData ? Plugin.QuartersExtraShelves.Value : null;
            if (!string.IsNullOrWhiteSpace(extraPlan))
            {
                foreach (var kv in ParseShelves(extraPlan))
                {
                    if (st.resourcesCapacity.Any(s => s?.resourceAmountData?.resourceData != null && s.resourceAmountData.resourceData.key == kv.Key)) continue;
                    var rd = res(kv.Key);
                    if (rd == null) continue;   // the resource is another plugin's and may not be loaded
                    // not retrievable, like the game's own food shelves in a dwelling (Jess, 2026-11-11: "Food in a dwelling place
                    // should be locked down"): a retrievable shelf puts the quarters on the game's list of stores to fetch from,
                    // and a granary worker carried 20 truffles off it
                    st.resourcesCapacity.Add(new StorageResource { resourceAmountData = new ResourceAmountData(rd, kv.Value), isRetrievable = false, showWarningIfFull = false });
                    if (standing && st.resourcesStored != null && !st.resourcesStored.Any(r => r != null && r.key == kv.Key)) st.resourcesStored.Add(new SaveableResourceAmountData(kv.Key, 0));
                    changed.Add($"+{kv.Key} {kv.Value}");
                }
            }
            var plan = data == townHouseData ? Plugin.TownHouseShelves.Value : data == quartersData ? Plugin.QuartersShelves.Value : null;
            if (!string.IsNullOrWhiteSpace(plan))
            {
                var sizes = ParseShelves(plan);
                foreach (var (building, from, to) in swaps)
                    if (building == data.name && sizes.ContainsKey(from) && !sizes.ContainsKey(to)) sizes[to] = sizes[from];
                foreach (var slot in st.resourcesCapacity)
                {
                    var key = slot?.resourceAmountData?.resourceData?.key;
                    if (key == null || !sizes.TryGetValue(key, out int amount) || slot.resourceAmountData.amount == amount) continue;
                    changed.Add($"{key} {slot.resourceAmountData.amount}->{amount}");
                    slot.resourceAmountData.amount = amount;
                }
            }
            if (OrderShelves(st, data)) changed.Add("reordered");
            return changed;
        }

        // The panel lists the shelves in the storage's own order. A town house's is given whole (Jess, 2026-10-16: egg, fish,
        // bread, pastry, pork, coal; kinds not named follow). The living quarters' names only its last shelves (Jess,
        // 2026-11-09: "coal is at the end and garments before that"): the foods keep their order, the truffle shelf added
        // after coal joins them, then garments, then coal. Called when the shelves are planned and again when the garment
        // shelf is added, which happens later (Garments.EnsureSlot). True when the order changed.
        internal static bool OrderShelves(Storage st, WorldObjectData data)
        {
            if (st == null || st.resourcesCapacity == null || data == null) return false;
            System.Func<string, int> rank;
            if (data == townHouseData && !string.IsNullOrWhiteSpace(Plugin.TownHouseOrder.Value))
            {
                var order = Plugin.TownHouseOrder.Value.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
                rank = key => { int i = order.IndexOf(key); return i < 0 ? order.Count : i; };
            }
            else if (data == quartersData && !string.IsNullOrWhiteSpace(Plugin.QuartersLastShelves.Value))
            {
                var last = Plugin.QuartersLastShelves.Value.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
                rank = key => last.IndexOf(key);   // -1 for every kind not named: first, in the order they stand
            }
            else return false;
            var before = st.resourcesCapacity.Select(x => x?.resourceAmountData?.resourceData?.key).ToList();
            var sorted = st.resourcesCapacity.Select((x, i) => (x, i)).OrderBy(q => rank(q.x?.resourceAmountData?.resourceData?.key)).ThenBy(q => q.i).Select(q => q.x).ToList();
            if (sorted.Select(x => x?.resourceAmountData?.resourceData?.key).SequenceEqual(before)) return false;
            st.resourcesCapacity.Clear();
            st.resourcesCapacity.AddRange(sorted);
            return true;
        }

        // The kinds whose shelves are ours and not the game's: garments, and the quarters' extra shelves (truffles). The
        // storage panel grows by the lines these add (StoragePanel).
        private static string addedFrom; private static HashSet<string> added;
        internal static HashSet<string> AddedKinds()
        {
            string plan = Plugin.QuartersExtraShelves.Value ?? "";
            if (added != null && plan == addedFrom) return added;
            addedFrom = plan;
            added = new HashSet<string>(ParseShelves(plan).Keys) { "garment" };
            return added;
        }

        // The prefab change does not reach houses the game spawns (Jess, 2026-10-21: a living quarters placed mid-session
        // had the grapes shelf), so every new town house and quarters is brought to the plan as it wakes, before the
        // storage makes its records a frame later; a loaded one gets its saved records and the game adds the missing
        // pastry record itself.
        [HarmonyPatch(typeof(Storage), "Awake"), HarmonyPostfix]
        private static void NewShelves(Storage __instance)
        {
            try
            {
                if (!Plugin.FoodVariety.Value || DataManager.Instance == null) return;
                var wo = __instance.GetComponent<WorldObject>();
                if (wo == null || wo.data == null || (wo.data != townHouseData && wo.data != quartersData)) return;
                ResourceData Res(string key) => DataManager.Instance.resourceSoData.FirstOrDefault(r => r != null && r.key == key);
                var changed = Shelves(__instance, wo.data, false, Res);
                if (changed.Count > 0 && newShelvesLogged.Add(wo.data.name)) Plugin.Log.LogInfo($"Tailor: a new {wo.data.name}'s shelves brought to the plan ({string.Join(", ", changed)}); further ones are not logged.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: new shelves: " + ex.Message); }
        }
        private static readonly HashSet<string> newShelvesLogged = new HashSet<string>();

        // Houses already standing keep their own shelves in the save, so the prefab changes above only reach new ones
        // (Jess, 2026-10-10: the panel still said 20/20): bring every standing town house's and the quarters' shelves to
        // the plan after a load.
        internal static void ResizeStandingShelves()
        {
            if (!Plugin.FoodVariety.Value || Storage.AllInstances == null || DataManager.Instance == null) return;
            ResourceData Res(string key) => DataManager.Instance.resourceSoData.FirstOrDefault(r => r != null && r.key == key);
            int houses = 0, slots = 0;
            var kinds = new Dictionary<string, int>();
            foreach (var st in Storage.AllInstances.ToList())
            {
                if (st == null || st.worldObject == null || (st.worldObject.data != townHouseData && st.worldObject.data != quartersData)) continue;
                var changed = Shelves(st, st.worldObject.data, true, Res);
                if (changed.Count == 0) continue;
                houses++; slots += changed.Count;
                foreach (var c in changed) { var k = c.Split(' ')[0]; kinds[k] = kinds.TryGetValue(k, out int n) ? n + 1 : 1; }
            }
            if (houses > 0) Plugin.Log.LogInfo($"Tailor: food shelves brought to the plan on {houses} standing houses ({slots} slots: {string.Join(", ", kinds.Select(k => k.Key + " x" + k.Value))}).");
        }

        private static Dictionary<string, int> ParseShelves(string s)
        {
            var d = new Dictionary<string, int>();
            foreach (var part in (s ?? "").Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && int.TryParse(kv[1].Trim(), out int n)) d[kv[0].Trim()] = n;
            }
            return d;
        }

        // --- the same-food penalty -----------------------------------------------------------------

        // What a fabling remembers: the kinds in each of their last few meals (Meals, oldest first, kept to the class's
        // count and one more) and whether the penalty is on. Saved with the garment state by unit guid.
        internal class Dish
        {
            public List<List<string>> Meals = new List<List<string>>();
            public bool Penalty;
            // every kind in the remembered meals, most recent last
            public List<string> Recent()
            {
                var order = new List<string>();
                foreach (var meal in Meals) foreach (var k in meal) { order.Remove(k); order.Add(k); }
                return order;
            }
        }
        // Nobles by difficulty (Jess, 2026-10-10, revised 2026-10-18): 3 of 5 on Calm, 4 of 6 on Classic, 4 of 5 on Challenge, 5 of 6 on Cruel; commoners 3 of 4 everywhere.
        // The config string is parsed once per difficulty band, and again if the setting changes in play (ConfigChanged).
        private static string nobleBand;
        private static (int kinds, int meals) nobleRule;
        // Another plugin may tighten the nobles' rule for a while (Rivals: Gastón's third date, by reflection on this field);
        // null is the config's rule.
        internal static (int kinds, int meals)? RuleOverride;
        private static (int kinds, int meals) NobleRule()
        {
            if (RuleOverride.HasValue) return RuleOverride.Value;
            string band = Winter.DifficultyBand();
            if (band == nobleBand) return nobleRule;
            (int kinds, int meals) rule = (4, 5);
            foreach (var part in (Plugin.NobleVariety.Value ?? "").Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || kv[0].Trim().ToLowerInvariant() != band) continue;
                var km = kv[1].Split('/');
                if (km.Length == 2 && int.TryParse(km[0].Trim(), out int k) && int.TryParse(km[1].Trim(), out int m)) { rule = (k, m); break; }
            }
            nobleRule = rule; nobleBand = band;
            return rule;
        }
        // The rule starts at Big town (Jess, 2026-10-17: not when the first commoner arrives); before that no meal is
        // recorded, so everyone begins with the full grace of unjudged meals, and no variety errand runs.
        // Asked on every happiness tick of every fabling, and the game's milestone check counts every worker, so the
        // answer is kept for the frame; the milestone name is parsed once.
        private static PopulationMilestone? fromMilestone;
        private static int ruleFrame = -1;
        private static bool ruleActive;
        internal static bool RuleActive()
        {
            if (Time.frameCount == ruleFrame) return ruleActive;
            ruleFrame = Time.frameCount;
            if (!Plugin.FoodVariety.Value || KingdomManager.Instance == null) return ruleActive = false;
            if (fromMilestone == null) fromMilestone = System.Enum.TryParse(Plugin.VarietyFromMilestone.Value, true, out PopulationMilestone from) ? from : PopulationMilestone.BIG_TOWN;
            return ruleActive = KingdomManager.Instance.GetCurrentPopulationMilestone() >= fromMilestone.Value;
        }
        // A setting edited in play is parsed afresh (Plugin.Awake subscribes this to the two config lines).
        internal static void ConfigChanged() { fromMilestone = null; nobleBand = null; ruleFrame = -1; }
        internal static int KindsWanted(FablingClass c) => c == FablingClass.NOBLE ? NobleRule().kinds : Plugin.CommonerVarietyKinds.Value;
        internal static int MealsCounted(FablingClass c) => c == FablingClass.NOBLE ? NobleRule().meals : Plugin.CommonerVarietyMeals.Value;
        private static int KindsWanted(Housing h) => KindsWanted(h != null && h.worldObject != null && h.worldObject.data != null ? h.worldObject.data.residentClass : FablingClass.COMMONER);
        private static string Guid(Worker w) => w != null && w.unit != null && w.unit.worldObject != null ? w.unit.worldObject.guid : null;
        private static Dish DishOf(Worker w, bool create)
        {
            string g = Guid(w);
            if (string.IsNullOrEmpty(g)) return null;
            if (!Garments.S.Dishes.TryGetValue(g, out var d) && create) Garments.S.Dishes[g] = d = new Dish();
            return d;
        }
        // Gastón's feast (Rivals, "Gastón's Table"): the variety requirements count as met for commoners and nobles while it
        // runs. The buff's key is Rivals' (theGourmetPrince + Feast); the meals keep being recorded, so the penalty comes
        // back as it stood once the feast ends. Asked on every happiness tick, so the answer is kept for the frame.
        private static int truceFrame = -1; private static bool truce;
        internal static bool FeastTruce()
        {
            if (Time.frameCount == truceFrame) return truce;
            truceFrame = Time.frameCount;
            try { return truce = DataManager.Instance != null && DataManager.Instance.IsBuffActivated("theGourmetPrinceFeast"); } catch { return truce = false; }
        }
        internal static bool Bored(Worker w)
        {
            if (!Plugin.SameFoodPenalty.Value || w == null || w.fablingClass == FablingClass.PEASANT || !RuleActive() || FeastTruce()) return false;
            var d = DishOf(w, false);
            return d != null && d.Penalty;
        }
        private static bool Bored(Needs n) => n != null && Bored(NeedsWorker(n));

        // Eat takes every reserved item of the meal in one pass: note what is going in, judge the meal coming out.
        private static readonly AccessTools.FieldRef<Eat, Worker> EatWorker = AccessTools.FieldRefAccess<Eat, Worker>("worker");
        private static readonly List<string> mealKinds = new List<string>();

        // Jess (2026-10-20): "I definitely saw a starving guy eat 1 homegrown and have his hunger gauge full." The eat
        // routine takes hunger down by each item's restoreHungerBy; this notes what the meal should restore and logs any
        // meal where hunger fell by more than that (MealWatch), with the name and the items.
        private static float mealHungerBefore, mealRestores;
        private static readonly List<string> mealItems = new List<string>();

        [HarmonyPatch(typeof(Eat), nameof(Eat.OnUpdate)), HarmonyPrefix]
        private static void MealBegins(Eat __instance)
        {
            mealKinds.Clear(); mealItems.Clear(); mealRestores = 0f; mealHungerBefore = -1f;
            try
            {
                var w = EatWorker(__instance);
                if (w == null || w.resourceReservations == null) return;
                if (w.needs != null) mealHungerBefore = w.needs.hunger;
                foreach (var r in w.resourceReservations)
                {
                    if (r == null) continue;
                    var data = DataManager.Instance.GetResourceDataFromKey(r.key);
                    if (data == null || data.resourceGroup != ResourceGroup.FOOD) continue;
                    if (Plugin.FoodVariety.Value && !mealKinds.Contains(r.key)) mealKinds.Add(r.key);
                    mealItems.Add(r.key + "(" + data.restoreHungerBy + ")"); mealRestores += data.restoreHungerBy;
                }
            }
            catch { mealKinds.Clear(); mealItems.Clear(); }
        }

        // Only a meal that was eaten counts: the game's Eat returns Failure without eating when the fabling has no housing,
        // and on an exception.
        [HarmonyPatch(typeof(Eat), nameof(Eat.OnUpdate)), HarmonyPostfix]
        private static void MealEaten(Eat __instance, BehaviorDesigner.Runtime.Tasks.TaskStatus __result)
        {
            try
            {
                if (__result != BehaviorDesigner.Runtime.Tasks.TaskStatus.Success) { mealKinds.Clear(); mealItems.Clear(); return; }
                var w = EatWorker(__instance);
                if (Plugin.MealWatch.Value && w != null && w.needs != null && mealHungerBefore >= 0f && mealItems.Count > 0)
                {
                    float fell = mealHungerBefore - w.needs.hunger;
                    if (fell > mealRestores + 1f)
                    {
                        string name; try { name = w.unit.GetLocalizedBirthName(); } catch { name = w.name; }
                        Plugin.Log.LogWarning($"Tailor: meal anomaly: {name} ({w.fablingClass}) ate {string.Join(", ", mealItems)} worth {mealRestores:0} but hunger fell {mealHungerBefore:0} -> {w.needs.hunger:0} ({fell:0}).");
                    }
                }
                if (mealKinds.Count > 0) RecordMeal(w, mealKinds);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: meal: " + ex.Message); }
            mealKinds.Clear(); mealItems.Clear();
        }

        // One meal on the record: the kinds eaten, judged against the class's rule.
        internal static void RecordMeal(Worker w, List<string> kinds)
        {
            if (w == null || kinds == null || kinds.Count == 0 || w.fablingClass == FablingClass.PEASANT || !RuleActive()) return;
            var d = DishOf(w, true);
            if (d == null) return;
            int meals = Mathf.Max(1, MealsCounted(w.fablingClass));
            d.Meals.Add(new List<string>(kinds));
            // one meal more than the window is remembered (Jess, 2026-11-09), so the record is whole the moment the window
            // grows by a meal (Gastón's third date); the judging still looks only at the window's meals
            while (d.Meals.Count > meals + 1) d.Meals.RemoveAt(0);
            d.Penalty = Judged(d, w.fablingClass);
        }

        // The penalty a record earns under the class's current rule: too few kinds across the window's last meals; none
        // until that many meals are on record.
        private static bool Judged(Dish d, FablingClass c)
        {
            int meals = Mathf.Max(1, MealsCounted(c)), want = KindsWanted(c);
            if (d == null || d.Meals.Count < meals) return false;
            return d.Meals.Skip(d.Meals.Count - meals).SelectMany(m => m).Distinct().Count() < want;
        }

        // Every noble's penalty worked out afresh from their record under the rule as it stands now. A meal judges its
        // eater, which is enough while the rule holds still; Rivals calls this (by reflection) each day of Gastón's third
        // date and the moment that date moves the rule, when a record judged at the last meal may be judged wrongly
        // (Jess, 2026-11-09: "we probably only need to judge the nobles every day during the quest, when it's a moving
        // target"). Returns how many nobles are on the penalty afterwards; -1 when it could not be done.
        private static int judgeLogs;
        internal static int JudgeNobles(string why)
        {
            try
            {
                if (Garments.S == null || Worker.Instances == null || !RuleActive()) return -1;
                int on = 0, turnedOn = 0, turnedOff = 0, nobles = 0;
                foreach (var w in Worker.Instances)
                {
                    if (w == null || w.fablingClass != FablingClass.NOBLE) continue;
                    var d = DishOf(w, false); if (d == null) continue;
                    nobles++;
                    bool now = Judged(d, FablingClass.NOBLE);
                    if (now && !d.Penalty) turnedOn++; else if (!now && d.Penalty) turnedOff++;
                    d.Penalty = now;
                    if (now) on++;
                }
                if (judgeLogs < 6 || Plugin.Verbose.Value)
                {
                    judgeLogs++;
                    Plugin.Log.LogInfo($"Tailor: {nobles} noble(s) judged afresh ({why}) under {KindsWanted(FablingClass.NOBLE)} kinds in {MealsCounted(FablingClass.NOBLE)} meals: {on} on the same-food penalty ({turnedOn} newly on, {turnedOff} newly off).");
                }
                return on;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: judging the nobles: " + ex.Message); return -1; }
        }

        // The kind on the shelf this fabling has gone longest without (never eaten lately first), else the given pick.
        internal static ResourceData BestDish(Worker w, Storage st, bool onlyRetrievable, ResourceData fallback)
        {
            if (w == null || st == null || w.fablingClass == FablingClass.PEASANT) return fallback;
            var d = DishOf(w, false);
            var recent = d != null ? d.Recent() : null;
            if (recent == null || recent.Count == 0 || fallback == null || !recent.Contains(fallback.key)) return fallback;
            ResourceData best = null; int bestRank = int.MaxValue;
            foreach (var r in st.resourcesStored)
            {
                if (r == null || !r.HasAvailable(1)) continue;
                if (onlyRetrievable && !st.IsResourceRetrievableAndUnlocked(r.key)) continue;
                var data = DataManager.Instance.GetResourceDataFromKey(r.key);
                if (data == null || data.resourceGroup != ResourceGroup.FOOD) continue;
                int rank = recent.IndexOf(r.key);   // -1 = not eaten lately; higher = more recent
                if (rank < bestRank) { bestRank = rank; best = data; }
            }
            return best != null && bestRank < recent.IndexOf(fallback.key) ? best : fallback;
        }

        // The TreeBudget plugin's dormancy feeds sleeping fablings outside Eat and offers two hooks (2026-10-12): steer
        // the pick and hear the meal. Set by reflection, so the Tailor needs no reference to that plugin.
        private static void HookDormancy()
        {
            try
            {
                // The dormancy lived in the TreeBudget plugin and moved into Fixes 0.4.0 (2026-10-15) under the same type
                // name, so the type decides, not the plugin GUID.
                var t = AccessTools.TypeByName("FabledomTreeBudget.Dormant");
                if (t == null) { Plugin.Log.LogInfo("Tailor: no dormancy patch loaded; the variety rule sees only meals eaten awake."); return; }
                AccessTools.Field(t, "PickFood")?.SetValue(null, (System.Func<Worker, Storage, ResourceData>)((w, st) => BestDish(w, st, false, st.GetFirstEdibleResource(onlyRetreivable: false))));
                AccessTools.Field(t, "MealFed")?.SetValue(null, (System.Action<Worker, List<string>>)RecordMeal);
                Plugin.Log.LogInfo("Tailor: hooked into TreeBudget's dormancy: dormant fablings' meals count for the variety rule.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: dormancy hooks: " + ex.Message); }
        }

        // The memory of fablings who are gone is dropped once a day (called from Garments.Daily).
        private static bool forgetWarned;
        internal static void ForgetTheDead()
        {
            try
            {
                if (Garments.S.Dishes.Count == 0 || Worker.Instances == null) return;
                var alive = new HashSet<string>(Worker.Instances.Select(Guid).Where(g => !string.IsNullOrEmpty(g)));
                foreach (var g in Garments.S.Dishes.Keys.Where(g => !alive.Contains(g)).ToList()) Garments.S.Dishes.Remove(g);
            }
            catch (System.Exception ex) { if (!forgetWarned) { forgetWarned = true; Plugin.Log.LogWarning("Tailor: forgetting the dead's dishes: " + ex.Message); } }
        }

        // -10 for commoners, -20 for nobles (Jess, 2026-10-09; the game's own figure was -5 for both)
        private static float Amount(Worker w) => w != null && w.fablingClass == FablingClass.NOBLE ? Plugin.SameFoodNoble.Value : Plugin.SameFoodCommoner.Value;

        // The game adds every impact and only then caps happiness at its maximum. The penalty has to join that sum
        // before the cap, like any other impactor, so a noble with desirability to spare absorbs it (Jess, 2026-10-21:
        // subtracting after the cap took 20 off a noble sitting at 145). It rides in on the start value together with
        // the garment impact (Garments.BeforeUpdateHappiness), so the game's own sum applies it. (Until 2026-10-22 this
        // was a second postfix redoing the whole sum, which only saw the garment impact because of the order the two
        // classes happened to be patched in.)
        internal static float Penalty(Worker w) => Bored(w) ? -Amount(w) : 0f;

        [HarmonyPatch(typeof(Needs), nameof(Needs.GetHappinessImpactors)), HarmonyPostfix]
        private static void ShowPenalty(Needs __instance, List<HappinessImpactor> __result)
        {
            var w = __result != null ? NeedsWorker(__instance) : null;
            if (w == null || !Bored(w)) return;
            __result.Add(new HappinessImpactor("Same food:", -Mathf.RoundToInt(Amount(w))));
        }

        // Agnes's feast wipes everyone's food memory (Jess, 2026-10-08), alongside its own hunger benefit: a feast is
        // variety enough. Other rulers' feasts leave it alone.
        [HarmonyPatch(typeof(DataManager), nameof(DataManager.ActivateBuff)), HarmonyPostfix]
        private static void FeastResetsStreaks(string key)
        {
            if (!Plugin.SameFoodPenalty.Value || key != "agnesFeast") return;
            int n = 0;
            foreach (var d in Garments.S.Dishes.Values)
                if (d != null && (d.Penalty || d.Meals.Count > 0)) { d.Penalty = false; d.Meals.Clear(); n++; }
            Plugin.Log.LogInfo($"Tailor: {key} began; {n} fablings' food memories cleared.");
        }

        // --- the second-food run -------------------------------------------------------------------

        private static string[] Foods = { "vegetable", "egg", "fish", "bread", "pork", "grapes", "grapeJuice", "pastry" };   // grapes and juice too (Jess, 2026-10-10); pastry (2026-10-13); ExtraFoods (truffles, 2026-11-05) joined at setup
        // Foods another plugin adds (Rivals' truffles): on the list once the resource exists; a house with no shelf for one never wants it.
        internal static void AddExtraFoods(System.Func<string, ResourceData> res)
        {
            var extra = (Plugin.ExtraFoods.Value ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length > 0 && !Foods.Contains(k)).ToList();
            var added = extra.Where(k => res(k) != null).ToList();
            if (added.Count > 0) { Foods = Foods.Concat(added).ToArray(); Plugin.Log.LogInfo("Tailor: foods on the variety list now: " + string.Join(", ", Foods) + "."); }
            foreach (var k in extra.Except(added)) Plugin.Log.LogInfo($"Tailor: extra food '{k}' is not a resource in this game; not on the list.");
        }
        private static int FoodKinds(Storage s) => Foods.Count(k => s.GetResourceStored(k) > 0);
        private static readonly HashSet<Storage> VarietyTargets = new HashSet<Storage>();
        internal static int VarietyRuns;
        private static readonly HashSet<Storage> HeldBackHouses = new HashSet<Storage>();   // FullLoads: houses whose run was held back today because the shelf was only spoken for
        // Diagnostics for the day (Jess, 2026-10-23: heads at 100% on food runs while the same houses sat at one kind):
        // houses skipped because no store holds a kind they lack, and trips that found none at the store they were sent to.
        private static readonly HashSet<Storage> NoStoreHouses = new HashSet<Storage>();
        private static int varietyWrongStore, wrongStoreLogged;

        // The kinds a house lacks: nothing on the shelf and room for some.
        private static List<string> MissingKinds(Storage st) => Foods.Where(k => st.GetResourceStored(k) == 0 && st.GetRemainingCapacity(k) > 0).ToList();

        // Whether a house is due a variety run (Jess, 2026-11-09: "variety runs start sooner"): the runs begin when the
        // shelf holds as many kinds as its residents want, or fewer (it was fewer only, so a house sat exactly on the
        // rule with nothing to spare and one emptied shelf put it under), and go on until it holds one kind more than they
        // want ([Food variety] SpareKinds; Jess, 2026-11-11: "fetch until there are the number the residents demand +1, if
        // they can. not until all the options are full"), or every kind its shelf takes if that is fewer. From 2026-11-09
        // to 11 they went on to every kind the shelf takes. Town houses and the living quarters alike. The houses being
        // filled are kept for the session; after a load the first test starts them again.
        private static readonly HashSet<Storage> ToppingUp = new HashSet<Storage>();
        private static int topUpLogs;
        private static bool WantsVariety(Housing h, Storage st)
        {
            if (h == null || st == null) return false;
            int kinds = FoodKinds(st), target = KindsWanted(h) + Mathf.Max(0, Plugin.VarietySpareKinds.Value);
            if (ToppingUp.Contains(st))
            {
                if (kinds < target && MissingKinds(st).Count > 0) return true;
                ToppingUp.Remove(st);
                if (topUpLogs < 6 || Plugin.Verbose.Value) { topUpLogs++; Plugin.Log.LogInfo($"Tailor: a {st.worldObject?.data?.name} holds {kinds} kind(s) ({(kinds >= target ? "its residents want " + KindsWanted(h) + ", and " + (kinds - KindsWanted(h)) + " to spare" : "every kind its shelf takes")}); its variety runs stop until it is down to {KindsWanted(h)} or fewer."); }
                return false;
            }
            if (kinds > KindsWanted(h) || MissingKinds(st).Count == 0) return false;
            ToppingUp.Add(st);
            if (topUpLogs < 6 || Plugin.Verbose.Value) { topUpLogs++; Plugin.Log.LogInfo($"Tailor: a {st.worldObject?.data?.name} holds {kinds} kind(s) and its residents want {KindsWanted(h)}; variety runs for it until it holds {target} (or every kind its shelf takes) (lacking {string.Join("/", MissingKinds(st))})."); }
            return true;
        }

        // The kinds some retrievable store in the kingdom holds right now, kept for the frame (asked per house per tick).
        private static int availFrame = -1;
        private static readonly HashSet<string> avail = new HashSet<string>();
        private static HashSet<string> KindsOnOffer()
        {
            if (Time.frameCount == availFrame) return avail;
            availFrame = Time.frameCount; avail.Clear();
            var stores = Storage.RetrievableInstances;
            if (stores == null) return avail;
            foreach (var st in stores)
            {
                if (st == null) continue;
                foreach (var k in Foods)
                    if (!avail.Contains(k) && st.GetResourceAvailable(k) > 0 && st.IsResourceRetrievableAndUnlocked(k)) avail.Add(k);
                if (avail.Count == Foods.Length) break;
            }
            return avail;
        }

        // Whether any store in the kingdom holds coal a head could fetch, kept for the frame. The variety run and the garment
        // run stand behind a coal run (Jess: starving, then coal, then variety, then garments), but a house short of coal with
        // no coal anywhere to fetch must not freeze them (2026-10-24, early spring: 84 of 125 town houses below 3 coal, the
        // coal makers' output taken as fast as made, the heads 0% on coal all season and the shelves drifting to one kind).
        private static int coalFrame = -1;
        private static bool coalOnOffer;
        internal static bool CoalOnOffer()
        {
            if (Time.frameCount == coalFrame) return coalOnOffer;
            coalFrame = Time.frameCount; coalOnOffer = false;
            var stores = Storage.RetrievableInstances;
            if (stores == null) return false;
            foreach (var st in stores)
                if (st != null && st.GetResourceAvailable("coal") > 0 && st.IsResourceRetrievableAndUnlocked("coal")) { coalOnOffer = true; break; }
            return coalOnOffer;
        }

        // A house whose resident is already on the penalty (Jess, 2026-10-10): its variety run comes before coal and garments.
        internal static bool OnPenalty(Housing h) =>
            h != null && h.residents != null && h.residents.Any(r => r != null && r.worker != null && Bored(r.worker.needs));
        internal static bool AnyOnPenalty(Hub hub)
        {
            if (hub == null || hub.activeAttachments == null) return false;
            foreach (var a in hub.activeAttachments)
            {
                var h = a != null ? a.GetComponent<Housing>() : null;
                if (h == null || h.GetStorage() == null || h.isFoodBeingDelivered || FoodKinds(h.GetStorage()) > KindsWanted(h)) continue;   // the run that outranks coal and garments is for a house at the rule or under it, not one being topped up beyond
                if (OnPenalty(h)) return true;
            }
            return false;
        }

        // When the game finds no house of this condo short of food, a head may still take a missing kind to a house holding
        // fewer kinds than its residents want: lowest priority, so not while any house of theirs is short of coal either,
        // except for a house whose resident is already on the penalty, which goes first (Jess, 2026-10-10: starving, then
        // variety, then garments).
        // The living quarters refill at the town-house threshold of 30 hunger, one bread, for a court of up to 20 (Jess,
        // 2026-10-10): raise the palace staff's trigger. The task instance belongs to that worker's tree, so the change sticks.
        [HarmonyPatch(typeof(AttachedHousingMissingFood), nameof(AttachedHousingMissingFood.OnUpdate)), HarmonyPrefix]
        private static void QuartersTrigger(AttachedHousingMissingFood __instance)
        {
            try
            {
                if (palaceData == null || __instance.amount >= Plugin.QuartersFoodTrigger.Value) return;
                var hub = FoodHub(__instance);
                if (hub != null && hub.worldObject != null && hub.worldObject.data == palaceData) __instance.amount = Plugin.QuartersFoodTrigger.Value;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: quarters trigger: " + ex.Message); }
        }

        // The game's trigger reads a kind as absent once every unit of it is reserved by residents about to eat (it counts
        // a kind only while one unit is unreserved), while room for a delivery is capacity less the physical stock. At a
        // meal rush a nearly full shelf therefore reads as empty and the head is sent with room for one item: the
        // 2026-10-23 autosave had five heads each carrying a single egg across town, and the heads' time on food runs
        // climbing toward 100%. With FullLoads the house is judged by what is physically on the shelf; the residents eat
        // what they reserved, the shelf is then truly short, and the head goes once with room for a full load.
        private static int PhysicalEdible(Storage st)
        {
            int n = 0;
            if (st == null || st.resourcesStored == null) return 0;
            foreach (var r in st.resourcesStored)
            {
                if (r == null || r.amount <= 0) continue;
                var d = DataManager.Instance.GetResourceDataFromKey(r.key);
                if (d != null && d.resourceGroup == ResourceGroup.FOOD) n += d.restoreHungerBy * r.amount;
            }
            return n;
        }

        [HarmonyPatch(typeof(AttachedHousingMissingFood), nameof(AttachedHousingMissingFood.OnUpdate)), HarmonyPrefix]
        private static bool PhysicalStock(AttachedHousingMissingFood __instance, ref BehaviorDesigner.Runtime.Tasks.TaskStatus __result)
        {
            if (!Plugin.FullLoads.Value) return true;
            try
            {
                var hub = FoodHub(__instance);
                if (hub == null || hub.activeAttachments == null) return true;
                int amount = __instance.amount;
                if (palaceData != null && hub.worldObject != null && hub.worldObject.data == palaceData) amount = Mathf.Max(amount, Plugin.QuartersFoodTrigger.Value);   // the quarters' own trigger, whichever prefix ran first
                foreach (var a in hub.activeAttachments)
                {
                    if (a == null || a.gridObject == null || a.gridObject.wo == null || a.gridObject.wo.data.isPerimeter) continue;
                    if (a.gridObject.constructable == null || !a.gridObject.constructable.isConstructionComplete) continue;
                    var h = a.GetComponent<Housing>();
                    if (h == null || !h.HasResidents() || h.isFoodBeingDelivered) continue;
                    var st = h.GetStorage();
                    if (st == null) continue;
                    if (PhysicalEdible(st) >= amount)
                    {
                        if (st.GetEdibleAmount() < amount) HeldBackHouses.Add(st);   // the game would have sent the head now
                        continue;
                    }
                    __instance.result.SetValue(h.gameObject);
                    __result = BehaviorDesigner.Runtime.Tasks.TaskStatus.Success;
                    return false;
                }
                __instance.result.SetValue(null);
                __result = BehaviorDesigner.Runtime.Tasks.TaskStatus.Failure;
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: physical stock: " + ex.Message); return true; }
        }

        // The errand is only ever offered to a carrier who is not hungry: the game's "nothing to fetch" failure is what lets
        // their own eat branch win, and on a big save there is always some house short of a kind (2026-10-12: a head of
        // condominium starved while running variety trips back to back).
        internal static bool Hungry(BehaviorDesigner.Runtime.Tasks.Task task)
        {
            try
            {
                var go = GameObjectOf(task);
                var w = go != null ? go.GetComponent<Worker>() : null;
                return w != null && w.needs != null && w.needs.config != null && w.needs.hunger >= w.needs.config.triggerHunger;
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(AttachedHousingMissingFood), nameof(AttachedHousingMissingFood.OnUpdate)), HarmonyPostfix]
        private static void SecondFood(AttachedHousingMissingFood __instance, ref BehaviorDesigner.Runtime.Tasks.TaskStatus __result)
        {
            if (!RuleActive()) return;   // the rule starts at Big town (Jess, 2026-10-17)
            if (__result == BehaviorDesigner.Runtime.Tasks.TaskStatus.Success || !Plugin.FoodVariety.Value || townHouseData == null) return;
            if (Hungry(__instance)) return;
            try
            {
                var hub = FoodHub(__instance);
                // condominium heads for their town houses, palace staff for the living quarters (Jess, 2026-10-10)
                if (hub == null || hub.worldObject == null || (hub.worldObject.data != condoData && hub.worldObject.data != palaceData)) return;
                var houses = hub.activeAttachments.Where(a => a != null && a.gridObject != null && a.gridObject.wo != null && !a.gridObject.wo.data.isPerimeter && a.gridObject.constructable != null && a.gridObject.constructable.isConstructionComplete)
                    .Select(a => a.GetComponent<Housing>()).Where(h => h != null).ToList();
                // a coal run outranks variety only when there is coal to be had (2026-10-24; see CoalOnOffer)
                bool coalShort = CoalOnOffer() && houses.Any(h => h.GetStorage() != null && h.GetStorage().GetResourceStored("coal") < 3 && !h.IsResourceBeingDelivered("coal"));
                foreach (var h in houses.OrderByDescending(OnPenalty))
                {
                    if (coalShort && !OnPenalty(h)) break;   // only a penalised house outranks a coal run
                    var st = h.GetStorage();
                    if (st == null || !h.HasResidents() || h.isFoodBeingDelivered) continue;
                    if (h.gridObject?.wo?.data != townHouseData && h.gridObject?.wo?.data?.residentClass != FablingClass.NOBLE) continue;
                    if (!WantsVariety(h, st)) continue;
                    var missing = MissingKinds(st);
                    if (missing.Count == 0) continue;
                    // only when some store holds a kind this house lacks: otherwise the game's search sends the head to the
                    // fullest store, which holds what the house already has, and the trip changes nothing (2026-10-23)
                    var onOffer = KindsOnOffer();
                    if (!missing.Any(onOffer.Contains)) { NoStoreHouses.Add(st); continue; }
                    VarietyTargets.Add(st);
                    __instance.result.SetValue(h.gameObject);
                    __result = BehaviorDesigner.Runtime.Tasks.TaskStatus.Success;
                    return;
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: second food: " + ex.Message); }
        }

        // On a variety run the store is the nearest one holding a kind the house lacks. The game's own search takes any
        // store with any food the house has room for and, within reach, the fullest of them, so a house with only pork was
        // sent to the pork store again and again (the 2026-10-23 log: heads at 100% on food, the same houses at one kind
        // for 30 days). Every other food run is left to the game.
        [HarmonyPatch(typeof(CanFindStorageWithEdible), "GetBestStorageSource"), HarmonyPrefix]
        private static bool StoreWithMissingKind(CanFindStorageWithEdible __instance, ref Transform __result)
        {
            try
            {
                var house = EdibleHouse(__instance);
                if (house == null || !VarietyTargets.Contains(house)) return true;
                var missing = MissingKinds(house);
                if (missing.Count == 0) { VarietyTargets.Remove(house); return true; }
                var worker = EdibleWorker(__instance);
                var from = worker != null ? worker.transform.position : house.transform.position;
                Storage best = null; float bestDist = float.MaxValue;
                var stores = Storage.RetrievableInstances;
                if (stores != null)
                    foreach (var st in stores)
                    {
                        if (st == null || st == house) continue;
                        if (!missing.Any(k => st.GetResourceAvailable(k) > 0 && st.IsResourceRetrievableAndUnlocked(k))) continue;
                        float d = (st.transform.position - from).sqrMagnitude;
                        if (d < bestDist) { best = st; bestDist = d; }
                    }
                if (best == null) { VarietyTargets.Remove(house); NoStoreHouses.Add(house); __result = null; return false; }   // the run fails; no walk
                __result = best.transform;
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: store with missing kind: " + ex.Message); return true; }
        }

        // On a variety run the head picks a kind the house does not have, preferring one its residents have not eaten lately;
        // every other food run is left to the game.
        [HarmonyPatch(typeof(CanFindStorageWithEdible), "ReserveResource"), HarmonyPrefix]
        private static bool DifferentFood(CanFindStorageWithEdible __instance, Storage pickupStorage)
        {
            try
            {
                var house = EdibleHouse(__instance);
                if (house == null || !VarietyTargets.Remove(house)) return true;
                var worker = EdibleWorker(__instance);
                var options = Foods.Where(k => house.GetResourceStored(k) == 0 && house.GetRemainingCapacity(k) > 0
                                               && pickupStorage.GetResourceAvailable(k) > 0 && pickupStorage.IsResourceRetrievableAndUnlocked(k)).ToList();
                if (options.Count == 0)
                {
                    // the proof line (Jess, 2026-10-23): a variety trip that cannot bring a missing kind; the game's pick runs
                    varietyWrongStore++;
                    if (wrongStoreLogged < 3)
                    {
                        wrongStoreLogged++;
                        string lacks = string.Join("/", MissingKinds(house));
                        string holds = string.Join("/", Foods.Where(k => pickupStorage.GetResourceAvailable(k) > 0));
                        if (Plugin.Verbose.Value) Plugin.Log.LogInfo($"Tailor: variety trip to a {house.worldObject?.data?.name} lacking {lacks} was sent to a {pickupStorage.worldObject?.data?.name} holding {(holds.Length > 0 ? holds : "no food")}; the game's pick runs instead.");
                    }
                    return true;
                }
                if (worker == null) return true;
                var home = house.worldObject != null ? house.worldObject.GetComponent<Housing>() : null;
                if (home != null && !WantsVariety(home, house)) return true;
                // the kind the residents have gone longest without comes first
                var lastSeen = new Dictionary<string, int>();
                if (home != null && home.residents != null)
                    foreach (var r in home.residents)
                    {
                        var d = r != null ? DishOf(r.worker, false) : null; if (d == null) continue;
                        var recent = d.Recent();
                        for (int i = 0; i < recent.Count; i++) { lastSeen.TryGetValue(recent[i], out int v); lastSeen[recent[i]] = Mathf.Max(v, i + 1); }
                    }
                int best = options.Min(k => lastSeen.TryGetValue(k, out int v) ? v : 0);
                options = options.Where(k => (lastSeen.TryGetValue(k, out int v) ? v : 0) == best).ToList();
                string key = options[Random.Range(0, options.Count)];
                int n = Mathf.Min(house.GetRemainingCapacity(key), pickupStorage.GetResourceAvailable(key), DataManager.Instance.GetResourceDataFromKey(key).GetCarryCapacity(worker.GetProfessionData()));
                if (n <= 0) return true;
                worker.ReserveResources(pickupStorage, key, n, alterGlobal: true);
                VarietyRuns++;
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: different food: " + ex.Message); return true; }
        }

        // --- a different dish than last time -----------------------------------------------------------

        // A fabling eating at home takes each item at random from the kinds on the shelf. While a fabling reserves its
        // meal, steer the pick to the kind on the shelf they have gone longest without (Jess, 2026-10-10).
        private static readonly AccessTools.FieldRef<ReserveFood, Worker> ReserveWorker = AccessTools.FieldRefAccess<ReserveFood, Worker>("worker");
        private static Worker eater;

        [HarmonyPatch(typeof(ReserveFood), nameof(ReserveFood.OnUpdate)), HarmonyPrefix]
        private static void EaterBegins(ReserveFood __instance) { try { eater = Plugin.FoodVariety.Value ? ReserveWorker(__instance) : null; } catch { eater = null; } }

        [HarmonyPatch(typeof(ReserveFood), nameof(ReserveFood.OnUpdate)), HarmonyPostfix]
        private static void EaterDone() => eater = null;

        [HarmonyPatch(typeof(Storage), nameof(Storage.GetFirstEdibleResource), new[] { typeof(bool) }), HarmonyPostfix]
        private static void DifferentDish(Storage __instance, bool onlyRetreivable, ref ResourceData __result)
        {
            try
            {
                if (eater == null || __result == null) return;
                __result = BestDish(eater, __instance, onlyRetreivable, __result);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: different dish: " + ex.Message); eater = null; }
        }

        // --- the sampler (HeadSampler, for tuning) -------------------------------------------------

        // Two groups, keyed "condo:..." and "palace:...": the Condominium heads and the Palace main hall's staff (Jess,
        // 2026-10-10: the palace workers were invisible until then).
        private static readonly Dictionary<string, int> Samples = new Dictionary<string, int>();
        private static string Group(Worker w)
        {
            if (w == null || w.workPlace == null || w.workPlace.worldObject == null) return null;
            var d = w.workPlace.worldObject.data;
            return d == condoData ? "condo" : (palaceData != null && d == palaceData) ? "palace" : null;
        }
        internal static IEnumerator Sample()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(5f);
                if (!Plugin.HeadSampler.Value || condoData == null || Worker.Instances == null || Time.timeScale <= 0f) continue;
                try
                {
                    foreach (var w in Worker.Instances)
                    {
                        string g = Group(w);
                        if (g == null) continue;
                        Samples.TryGetValue(g + ":all", out int a); Samples[g + ":all"] = a + 1;
                        string what = g + ":" + Doing(w);
                        Samples.TryGetValue(what, out int n); Samples[what] = n + 1;
                    }
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: sampler: " + ex.Message); }
            }
        }

        private static string Doing(Worker w)
        {
            var keys = new List<string>();
            if (w.resourceReservations != null) keys.AddRange(w.resourceReservations.Select(r => r.key));
            if (w.storage != null) keys.AddRange(w.storage.resourcesStored.Where(r => r.amount > 0).Select(r => r.key));
            if (keys.Contains("coal")) return "coal";
            if (keys.Contains("garment")) return "garments";
            if (keys.Any(k => Foods.Contains(k))) return "food";
            if (w.unit != null && w.unit.isInside) return "inside";
            return "idle";
        }

        // The variety day's log and counters (called from Garments.Daily).
        internal static void Daily()
        {
            if (Plugin.Verbose.Value && VarietyRuns + NoStoreHouses.Count + varietyWrongStore + HeldBackHouses.Count > 0)
                Plugin.Log.LogInfo($"Tailor: variety today: {VarietyRuns} run(s) carried a missing kind; {NoStoreHouses.Count} house(s) short of kinds no store holds; {varietyWrongStore} trip(s) found none at the store; {HeldBackHouses.Count} house(s) whose shelf was only spoken for, run held back; coal on offer: {(CoalOnOffer() ? "yes" : "no")}.");
            NoStoreHouses.Clear(); varietyWrongStore = 0; wrongStoreLogged = 0; HeldBackHouses.Clear();
            ToppingUp.RemoveWhere(st => st == null);   // houses torn down
            if (!Plugin.HeadSampler.Value || Samples.Count == 0) { VarietyRuns = 0; return; }
            string Line(string g)
            {
                if (!Samples.TryGetValue(g + ":all", out int all) || all == 0) return null;
                int count = Worker.Instances.Count(w => Group(w) == g);
                string Pct(string k) => (Samples.TryGetValue(g + ":" + k, out int n) ? 100f * n / all : 0f).ToString("0") + "%";
                return $"({count}): idle {Pct("idle")}, inside {Pct("inside")}, food {Pct("food")}, coal {Pct("coal")}, garments {Pct("garments")}";
            }
            int boredCommoners = 0, boredNobles = 0;
            foreach (var w in Worker.Instances)
                if (w != null && Bored(w.needs)) { if (w.fablingClass == FablingClass.NOBLE) boredNobles++; else boredCommoners++; }
            string condo = Line("condo"), palace = Line("palace");
            if (condo != null && Plugin.Verbose.Value) Plugin.Log.LogInfo($"Tailor: condo heads {condo}; second-food runs today {VarietyRuns}; same-food penalty on {boredCommoners} commoners, {boredNobles} nobles.");
            // where the penalised live and what their shelf holds, so a stuck streak can be read from the log (Jess, 2026-10-10)
            if (boredCommoners + boredNobles > 0)
            {
                var where = new Dictionary<string, int>();
                foreach (var w in Worker.Instances)
                {
                    if (w == null || !Bored(w.needs)) continue;
                    var h = w.resident != null && w.resident.HasHousing() ? w.resident.housing : null;
                    var st = h != null ? h.GetStorage() : null;
                    string key = h == null ? "homeless" : $"{h.worldObject.data.name} with {(st != null ? FoodKinds(st) : 0)} kind(s)" + (st != null && st.GetEdibleAmount() <= 0 ? ", empty" : "") + $", last ate {w.needs.previousEdibleConsumed}";
                    where.TryGetValue(key, out int n); where[key] = n + 1;
                }
                Plugin.Log.LogInfo("Tailor: penalised: " + string.Join("; ", where.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} in {kv.Key}")) + ".");
            }
            if (palace != null) Plugin.Log.LogInfo($"Tailor: palace staff {palace}.");
            Samples.Clear(); VarietyRuns = 0;
        }
    }
}
