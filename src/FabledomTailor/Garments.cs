using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // Garment demand (Jess, 2026-10-05). Demand lives in the house, not the fabling:
    //  - the Palace living quarters always want garments: one per noble living there every 25 days on Cruel, 30 on
    //    Challenge, 35 on Classic, 40 on Calm (Days);
    //  - a share of commoner houses want them, set by the noble count (3% per noble over 10, capped at 30%); each
    //    commoner house has a fixed rank 0-99 from its guid and wants garments while its rank is below the share,
    //    so nothing is tracked when houses or commoners come and go; a wanting house takes one per resident every 55 days
    //    on Cruel, 60 on Challenge and Classic, 75 on Calm (Days);
    //  - a wanting house gets a garment slot in its storage, which the head of household stocks like coal;
    //  - each resident keeps their own clock and takes a garment from the house's slot when due; none there means that
    //    resident goes without, "no garments" on the house, and a happiness penalty for them alone (nobles -10 after
    //    the 5 grace days and another -10 every 20 days, up to -50; commoners -5 after the same 5) until one arrives;
    //    a clothed commoner in a wanting house gets +5.
    [HarmonyPatch]
    internal static class Garments
    {
        internal class Clock { public float Days; public int Unclothed; }
        internal class State
        {
            public int Version = 2;   // 2: clocks per resident (by unit guid), not per house
            public Dictionary<string, Clock> Residents = new Dictionary<string, Clock>();
            public HashSet<string> Filling = new HashSet<string>();   // noble houses currently restocking garments
            public Dictionary<string, float> FarmDebt = new Dictionary<string, float>();   // sheep farms: recovery days owed after a hungry winter (fractional by difficulty)
            public int Share = -1;      // yesterday's wanting share, for the log
            public bool SheepLetterOffered;   // the guaranteed sheep letter on the first garment penalty has been sent
            public int Taken;           // garments taken by houses, all time (the readout later)
            public HashSet<string> TopUp = new HashSet<string>();   // sheep farms to fill to WinterFillTo grain at the start of winter
            public Dictionary<string, int> Grace = new Dictionary<string, int>();   // sheep farms: days of no-hunger grace left after being built
            public Dictionary<string, int> QuartersSince = new Dictionary<string, int>();   // wanting houses: the day their garment shelf first existed
            public Dictionary<string, Food.Dish> Dishes = new Dictionary<string, Food.Dish>();   // commoners and nobles: the last two kinds eaten and whether the penalty is on
            public string LastSeason = "";
            // the introduction letters (IntroLetters.cs): sent once each, the ones still waiting, the day Big town was
            // reached, our quests with their progress (cut from the game's save), and whether an older save was caught up
            public HashSet<string> IntroSent = new HashSet<string>();
            public HashSet<string> IntroPending = new HashSet<string>();
            public int BigTownDay = -1;
            public Dictionary<string, string> IntroQuests = new Dictionary<string, string>();
            public bool IntroCaughtUp;
            public Dictionary<string, string> NobleQuests = new Dictionary<string, string>();   // the noble letters' quests, with progress
            public List<string> NobleHandled = new List<string>();                            // answered noble letters (their repeat timers)
            public HashSet<string> NoblePending = new HashSet<string>();                        // noble letters waiting for an answer
        }
        internal static State S = new State();
        private static bool loggedSlot;
        private const string SaveKey = "fabTailor";
        private const string Status = "fabNoGarments";
        private const string Key = "garment";

        // --- what a house wants --------------------------------------------------------------------------

        // Counting the nobles walks every worker, and this is asked per tick by the garment errand and per refresh by the
        // fabling panel, so the answer is kept for the day; the daily step asks afresh.
        private static int shareDay = -1, shareCache;
        internal static int Share()
        {
            int today = DateTimeManager.Instance != null ? DateTimeManager.Instance.GetTotalDaysTranspired() : -1;
            if (today >= 0 && today == shareDay) return shareCache;
            int nobles = 0;
            try { nobles = Worker.GetCount(FablingClass.NOBLE); } catch { }
            shareCache = Mathf.Clamp(Plugin.GarmentSharePerNoble.Value * Mathf.Max(0, nobles - Plugin.GarmentFreeNobles.Value), 0, Plugin.GarmentShareCap.Value);
            shareDay = today;
            return shareCache;
        }

        // A fixed rank 0-99 per house, from its guid (the sign bit is masked off rather than Abs'd: Abs leaves int.MinValue negative).
        private static int Rank(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return 99;
            unchecked { int h = 17; foreach (char c in guid) h = h * 31 + c; return (h & 0x7fffffff) % 100; }
        }

        private static bool Complete(Housing h) => h != null && h.gridObject != null && h.gridObject.constructable != null && h.gridObject.constructable.isConstructionComplete;

        internal static bool Wants(Housing h, int share)
        {
            if (!Complete(h) || h.worldObject == null || h.worldObject.data == null) return false;
            var cls = h.worldObject.data.residentClass;
            if (cls == FablingClass.NOBLE) return true;
            if (cls == FablingClass.COMMONER) return Rank(h.worldObject.guid) < share;
            return false;
        }

        // Days between garments by difficulty (Jess, 2026-10-24: 40/75 Calm, 35/60 Classic, 30/60 Challenge, 25/55 Cruel;
        // garments are slow, and 25/55 everywhere made the easier difficulties as tight as Cruel). Parsed once per band.
        private static string daysBand;
        private static (int noble, int commoner) daysParsed;
        internal static (int noble, int commoner) DaysByDifficulty()
        {
            string band = Winter.DifficultyBand();
            if (band == daysBand) return daysParsed;
            (int noble, int commoner) d = (25, 55);
            foreach (var part in (Plugin.GarmentDays.Value ?? "").Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || kv[0].Trim().ToLowerInvariant() != band) continue;
                var nc = kv[1].Split('/');
                if (nc.Length == 2 && int.TryParse(nc[0].Trim(), out int n) && int.TryParse(nc[1].Trim(), out int c) && n > 0 && c > 0) { d = (n, c); break; }
            }
            daysParsed = d; daysBand = band;
            return d;
        }
        internal static void ConfigChanged() { daysBand = null; }
        internal static float IntervalDays(Housing h) { var d = DaysByDifficulty(); return h.worldObject.data.residentClass == FablingClass.NOBLE ? d.noble : d.commoner; }

        // --- the slot ---------------------------------------------------------------------------------------

        private static void EnsureSlot(Housing h)
        {
            var storage = h.GetStorage();
            var garment = Tailor.Garment;
            if (storage == null || garment == null) return;
            if (storage.resourcesCapacity.Any(r => r != null && r.resourceAmountData != null && r.resourceAmountData.resourceData == garment)) return;
            int capacity = h.worldObject.data.residentClass == FablingClass.NOBLE ? Plugin.GarmentSlotNobles.Value : Plugin.GarmentSlotCommoners.Value;
            storage.resourcesCapacity.Add(new StorageResource { resourceAmountData = new ResourceAmountData(garment, capacity), isRetrievable = false, showWarningIfFull = false });
            if (storage.resourcesStored.All(r => r.key != Key)) storage.resourcesStored.Add(new SaveableResourceAmountData(Key, 0));
            // every wanting house gets its slot again on each load, so this is said once a session
            if (!loggedSlot) { loggedSlot = true; Plugin.Log.LogInfo($"Tailor: garment shelves set up in wanting houses (living quarters {Plugin.GarmentSlotNobles.Value}, commoner houses {Plugin.GarmentSlotCommoners.Value})."); }
        }

        // The living quarters' shelf is sized to the court each day; the game fetches whenever a slot is below its
        // capacity, so the staff keep it filled on their own.
        private static void TendNobleStock(Housing h)
        {
            var storage = h.GetStorage();
            var slot = storage != null ? storage.GetResourceCapacity(Key) : null;
            if (slot == null) return;
            int nobles = Worker.GetCount(FablingClass.NOBLE);
            // the shelf grows with the court: 3 per noble, never past the slot's ceiling; the palace staff keep it
            // stocked under their own errand rules (Jess, 2026-10-06: no start or stop line of ours)
            int full = Mathf.Clamp(Mathf.CeilToInt(Plugin.GarmentStockPerNoble.Value * nobles), 1, Plugin.GarmentSlotNobles.Value);
            if (slot.resourceAmountData.amount != full)
            {
                Plugin.Log.LogInfo($"Tailor: the living quarters' garment shelf now holds {full} ({nobles} nobles).");
                slot.resourceAmountData.amount = full;
            }
            S.Filling.Remove(h.worldObject.guid);
            int stored = storage.GetResourceStored(Key);
            // optional background carry by the staff (off by default; the errand does the real walking)
            if (stored < full && Plugin.GarmentBackgroundCarry.Value)
            {
                int staff = 0;
                try { var hub = h.attachment != null ? h.attachment.GetHub() : null; var wp = hub != null ? hub.GetComponent<WorkPlace>() : null; staff = wp != null ? wp.GetWorkers().Count : 0; } catch { }
                if (staff <= 0) return;
                int want = Mathf.Min(full - stored, staff * Plugin.GarmentStaffCarry.Value);
                want = TakeFromStores(want);
                if (want <= 0) return;
                storage.Store(Key, want, alterGlobalStorage: false);
                Plugin.Log.LogInfo($"Tailor: the palace staff brought {want} garments to the living quarters ({stored + want} on hand).");
            }
        }

        // A head of household whose errands never pick garments up (the condominium's, it seems) leaves the slot
        // empty for good. If a wanting house with a head has had an empty slot for a few days while the stores hold
        // garments, one is carried across in the background each day until the slot is half full.
        private static readonly Dictionary<string, int> emptyDays = new Dictionary<string, int>();
        private static void TendHouseStock(Housing h)
        {
            var storage = h.GetStorage();
            var slot = storage != null ? storage.GetResourceCapacity(Key) : null;
            if (slot == null || !Plugin.GarmentBackgroundCarry.Value) return;
            string guid = h.worldObject.guid;
            int stored = storage.GetResourceStored(Key), cap = slot.resourceAmountData.amount;
            bool hasHead = h.workPlace != null ? h.workPlace.HasWorker() : (h.attachment?.GetHub()?.GetComponent<WorkPlace>()?.HasWorker() ?? false);
            if (stored * 2 >= cap || !hasHead || InStores() <= 0) { emptyDays.Remove(guid); return; }
            emptyDays.TryGetValue(guid, out int days);
            emptyDays[guid] = ++days;
            if (days < Plugin.GarmentFallbackDays.Value) return;
            if (TakeFromStores(1) <= 0) return;
            storage.Store(Key, 1, alterGlobalStorage: false);
            Plugin.Log.LogInfo($"Tailor: a garment carried to {h.worldObject.data.name} ({guid.Substring(0, 8)}) whose head had not fetched any ({stored + 1}/{cap}).");
        }

        // Only the kingdom's stores supply the carries: stockpiles, granaries, markets and the Tailor's own output,
        // never another house's shelf or the quarters.
        private static bool IsStore(Storage st) => st != null && st.worldObject != null && st.worldObject.data != null
            && (st.isGeneralStorage || st.worldObject.data == Tailor.TailorData);
        private static int InStores() => Storage.AllInstances.Where(IsStore).Sum(st => st.GetResourceAvailable(Key));

        private static int TakeFromStores(int want)
        {
            int taken = 0;
            foreach (var st in Storage.AllInstances.Where(IsStore).OrderByDescending(st => st.GetResourceAvailable(Key)).ToList())
            {
                if (taken >= want) break;
                int n = Mathf.Min(want - taken, st.GetResourceAvailable(Key));
                if (n <= 0) continue;
                st.Remove(Key, n, alterGlobalStorage: true);
                taken += n;
            }
            return taken;
        }

        // --- the daily step ---------------------------------------------------------------------------------

        [HarmonyPatch(typeof(WorldEventManager), "HandleStandardWorldEventsOnDayChange"), HarmonyPrefix]
        private static void Daily()
        {
            if (GameManager.Instance == null || GameManager.Instance.IsSandboxMode(isCreativeMode: true)) return;
            try { RulerTrade.EnsureSaves(); } catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: ruler trade: " + ex.Message); }
            try { SheepLetter.Daily(); } catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: sheep letter: " + ex.Message); }
            try { IntroLetters.Daily(); } catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: introduction letters: " + ex.Message); }
            try { Step(); }
            catch (Exception ex) { Plugin.Log.LogError("Tailor: garment step failed: " + ex); }
            try { Winter.Daily(); }
            catch (Exception ex) { Plugin.Log.LogError("Tailor: winter step failed: " + ex); }
            try { Tailor.PaveAll("daily"); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: paving: " + ex.Message); }
        }

        // One line a day for the wool chain (Jess, 2026-10-07: watch production change after marrying Ramone):
        // happiness, the efficiency it gives (the canon's bands), yesterday's wool and garments, farms and tailors.
        // Only with [Diagnostics] ChainLog on.
        private static void LogChain()
        {
            if (!Plugin.ChainLog.Value) return;
            try
            {
                int hap = KingdomManager.Instance.GetAverageWorkerHappiness();
                int band = (hap / 10) * 10;
                int eff = hap < 40 ? 80 : hap < 50 ? 90 : band <= 100 ? band + 50 : 150 + (band - 100) / 2;
                var days = DataManager.Instance.gameData.producedResourcesPerDay;
                var last = days != null && days.Count > 0 ? days[days.Count - 1] : null;
                int wool = last?.dailyGraphDatas.FirstOrDefault(d => d.key == "wool")?.value ?? 0;
                int garments = last?.dailyGraphDatas.FirstOrDefault(d => d.key == "garment")?.value ?? 0;
                int farms = 0, tailors = 0;
                foreach (var wo in WorldObject.Instances) { if (wo == null || wo.data == null) continue; if (wo.data == Tailor.FarmData) farms++; else if (wo.data == Tailor.TailorData) tailors++; }
                string food = last != null ? string.Join(", ", new[] { "vegetable", "fish", "bread", "egg", "pork", "grain" }.Select(k => k + " +" + (last.dailyGraphDatas.FirstOrDefault(d => d.key == k)?.value ?? 0))) : "";
                string debts = S.FarmDebt.Count > 0 ? "; recovering farms " + string.Join(", ", S.FarmDebt.Select(kv => $"{kv.Key.Substring(0, 8)}:{kv.Value:0.#}d")) : "";
                Plugin.Log.LogInfo($"Tailor: chain day {DateTimeManager.Instance.GetTotalDaysTranspired()}: happiness {hap} (cap {KingdomManager.Instance.GetMaxHappiness()}), efficiency about {eff}%, yesterday wool +{wool}, garments +{garments}; farms {farms}, tailors {tailors}; food yesterday {food}{debts}.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: chain log: " + ex.Message); }
        }

        // Shelves exist from the moment a game is loaded, not from the first daily tick (Jess, 2026-10-07: the living
        // quarters' slot appeared two days in). Sizes only; the clocks still move once a day.
        internal static void PrepareShelves()
        {
            try
            {
                if (!Plugin.GarmentDemand.Value || Tailor.Garment == null) return;
                int share = Share();
                foreach (var h in Housing.Instances.ToList())
                {
                    if (h == null || h.worldObject == null || !Wants(h, share)) continue;
                    EnsureSlot(h);
                    if (h.worldObject.data.residentClass == FablingClass.NOBLE) TendNobleStock(h);
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: shelves after load: " + ex.Message); }
        }

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoad(DataManager __instance) => __instance.OnLoadComplete += () => Plugin.Instance.StartCoroutine(AfterLoad());
        private static System.Collections.IEnumerator AfterLoad()
        {
            for (int i = 0; i < 30; i++) yield return null;
            PrepareShelves();
            try { Food.ResizeStandingShelves(); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: shelves: " + ex.Message); }
            try { Confectionery.InfluenceOnLoad(); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: confectionery influence: " + ex.Message); }
            try { Confectionery.SizeStandingStores(); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: confectionery store: " + ex.Message); }
            try { IntroLetters.AfterLoad(); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: introduction letters: " + ex.Message); }
            try { NobleLetters.AfterLoad(); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: noble letters: " + ex.Message); }
            yield return new UnityEngine.WaitForSecondsRealtime(3f);   // after the ground textures are set up, so the paint sticks
            try { Tailor.SeasonalPending(); } catch { }
            try { Tailor.PaveAll("after load"); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: paving: " + ex.Message); }
            try { Winter.TopUpOnLoad(); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: winter top-up: " + ex.Message); }
        }

        private static void Step()
        {
            LogChain();
            if (!Plugin.GarmentDemand.Value || Tailor.Garment == null) return;
            shareDay = -1;   // a fresh count once a day
            int share = Share();
            if (share != S.Share) { Plugin.Log.LogInfo($"Tailor: {share}% of commoner houses want garments now ({Worker.GetCount(FablingClass.NOBLE)} nobles)."); S.Share = share; }
            int wanting = 0, residentsWanting = 0, unclothed = 0;
            bool holiday = Holiday();
            HolidayLog(holiday);
            var seen = new HashSet<string>();
            foreach (var h in Housing.Instances.ToList())
            {
                if (!Wants(h, share)) continue;
                string guid = h.worldObject.guid;
                if (string.IsNullOrEmpty(guid)) continue;
                seen.Add(guid);
                EnsureSlot(h);
                if (h.worldObject.data.residentClass == FablingClass.NOBLE) TendNobleStock(h); else TendHouseStock(h);
                if (h.residents.Count == 0) continue;
                wanting++;
                float interval = IntervalDays(h);
                var storage = h.GetStorage();
                bool anyWithout = false;
                foreach (var res in h.residents.ToList())
                {
                    string rg = res != null && res.unit != null && res.unit.worldObject != null ? res.unit.worldObject.guid : null;
                    if (string.IsNullOrEmpty(rg)) continue;
                    seen.Add(rg);
                    residentsWanting++;
                    // a new clock starts part-way through, so a household's residents do not all come due the same day,
                    // but in the first half only: a newcomer's first garment is at least half an interval away, time
                    // enough for the shelf to be stocked (Jess, 2026-10-10)
                    if (!S.Residents.TryGetValue(rg, out var c)) S.Residents[rg] = c = new Clock { Days = UnityEngine.Random.Range(0f, interval * 0.5f) };
                    if (!holiday) c.Days += 1f;   // the feast holiday: the clocks stand still (Jess, 2026-10-24)
                    if (!holiday && c.Days >= interval)
                    {
                        if (storage != null && storage.GetResourceStored(Key) > 0)
                        {
                            storage.Remove(Key, 1, alterGlobalStorage: false);
                            c.Days -= interval;
                            c.Unclothed = 0;
                            S.Taken++;
                        }
                        else
                        {
                            c.Days = interval;   // stays due until a garment turns up
                            c.Unclothed++;
                        }
                    }
                    if (c.Unclothed > 0) { anyWithout = true; unclothed++; }
                    // the first real penalty (past the grace days) brings the sheep letter once, when that letter exists
                    if (!S.SheepLetterOffered && c.Unclothed >= Plugin.GarmentGraceDays.Value) SheepLetter.OnFirstGarmentPenalty();
                }
                // the marker also shows while the shelf is bare, whatever the clocks say (Jess, 2026-10-10: a bare shelf is
                // the warning, not the penalty), once the shelf is GraceDays old so the carriers have had time to stock it
                bool bare = false;
                if (storage != null)
                {
                    int today = DateTimeManager.Instance != null ? DateTimeManager.Instance.GetTotalDaysTranspired() : 0;
                    if (!S.QuartersSince.TryGetValue(guid, out int since)) S.QuartersSince[guid] = since = today;
                    bare = storage.GetResourceStored(Key) <= 0 && today - since >= Plugin.GarmentGraceDays.Value;
                }
                bool show = !holiday && (anyWithout || bare);   // the traders are dressing everyone
                var sc = h.gridObject.wo.statusController;
                if (show && !sc.IsShowing(Status)) sc.Add(Status);
                else if (!show && sc.IsShowing(Status)) sc.Remove(Status);
            }
            // residents no longer in a wanting house (moved, died, fewer nobles): forget their clocks
            foreach (var g in S.Residents.Keys.Where(g => !seen.Contains(g)).ToList()) S.Residents.Remove(g);
            // and a house that stopped wanting loses its status
            foreach (var h in Housing.Instances)
                if (h != null && h.gridObject?.wo?.statusController != null && !Wants(h, share) && h.gridObject.wo.statusController.IsShowing(Status)) h.gridObject.wo.statusController.Remove(Status);
            if (DateTimeManager.Instance != null && DateTimeManager.Instance.dateTimeData.currentDay % 10 == 0)
                Plugin.Log.LogInfo($"Tailor: garments wanted by {residentsWanting} fablings in {wanting} houses; {unclothed} going without; {S.Taken} taken so far.");
        }

        // --- the feast holiday (Jess, 2026-10-24; fabledom-mod-tailor.md, "Giovanni's feast: a wardrobe holiday") -----
        // For the days of the feast named in FeastHoliday (Giovanni's: his traders clothe the court and the town houses),
        // the garment clocks stand still, nobody is due or takes from a shelf, the going-without is cleared on the first
        // day and the penalty is off. Nothing is added to shelves or stores: the goods come and go with the caravan, and
        // the Tailors' output over the feast refills the shelves. The holiday follows the game's own buff, so a feast cut
        // short ends it, and a save loaded mid-feast keeps it without a flag of our own. A counter-play, too, for a letter
        // that wants 150 garments at once.
        private static int holidayFrame = -1;
        private static bool holidayCached;
        internal static bool Holiday()
        {
            if (Time.frameCount == holidayFrame) return holidayCached;
            holidayFrame = Time.frameCount;
            try
            {
                string key = (Plugin.FeastHoliday.Value ?? "").Trim();
                return holidayCached = key.Length > 0 && Plugin.GarmentDemand.Value && DataManager.Instance != null && DataManager.Instance.gameData != null && DataManager.Instance.IsBuffActivated(key);
            }
            catch { return holidayCached = false; }
        }
        internal static int HolidayDaysLeft()
        {
            try { string key = (Plugin.FeastHoliday.Value ?? "").Trim(); return key.Length > 0 && DataManager.Instance != null ? DataManager.Instance.GetBuffDaysRemaining(key) : 0; }
            catch { return 0; }
        }
        private static bool holidayWas;
        private static void HolidayLog(bool holiday)
        {
            if (holiday == holidayWas) return;
            holidayWas = holiday;
            Plugin.Log.LogInfo(holiday
                ? $"Tailor: the feast '{(Plugin.FeastHoliday.Value ?? "").Trim()}' is on: garment clocks paused, nobody goes without ({HolidayDaysLeft()} day(s) to go)."
                : "Tailor: the feast is over; the garment clocks run again.");
        }

        // The first day: everyone going without is dressed by the traders, as if a fresh garment, and the markers come down.
        [HarmonyPatch(typeof(DataManager), nameof(DataManager.ActivateBuff)), HarmonyPostfix]
        private static void FeastDressesEveryone(string key)
        {
            try
            {
                string want = (Plugin.FeastHoliday.Value ?? "").Trim();
                if (want.Length == 0 || key != want || !Plugin.GarmentDemand.Value) return;
                int dressed = 0;
                foreach (var c in S.Residents.Values)
                    if (c != null && c.Unclothed > 0) { c.Unclothed = 0; c.Days = 0f; dressed++; }
                int cleared = 0;
                foreach (var h in Housing.Instances)
                {
                    var sc = h != null && h.gridObject != null && h.gridObject.wo != null ? h.gridObject.wo.statusController : null;
                    if (sc != null && sc.IsShowing(Status)) { sc.Remove(Status); cleared++; }
                }
                holidayFrame = -1;
                Plugin.Log.LogInfo($"Tailor: {key} began: {dressed} fabling(s) going without were dressed by the traders, {cleared} marker(s) taken down; the garment clocks pause for the feast.");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Tailor: feast holiday: " + ex.Message); }
        }

        // --- happiness ------------------------------------------------------------------------------------------

        // The penalty or bonus for this fabling's house, if it wants garments.
        internal static int Impact(Worker w)
        {
            try
            {
                if (!Plugin.GarmentDemand.Value || w == null || w.resident == null || !w.resident.HasHousing()) return 0;
                if (Holiday()) return 0;   // dressed by the traders for the feast
                var h = w.resident.housing;
                string rg = w.unit != null && w.unit.worldObject != null ? w.unit.worldObject.guid : null;
                if (h.worldObject == null || rg == null || !S.Residents.TryGetValue(rg, out var c)) return 0;
                bool noble = h.worldObject.data.residentClass == FablingClass.NOBLE;
                if (noble)
                {
                    // -10 after the grace days, another -10 every 20 days without, up to -50 (Jess, 2026-10-05)
                    if (c.Unclothed < Plugin.GarmentGraceDays.Value) return 0;
                    int steps = 1 + (c.Unclothed - Plugin.GarmentGraceDays.Value) / Mathf.Max(1, Plugin.GarmentNobleStepDays.Value);
                    return -Mathf.Min(Plugin.GarmentNobleMaxPenalty.Value, steps * Plugin.GarmentNoblePenalty.Value);
                }
                if (c.Unclothed >= Plugin.GarmentGraceDays.Value) return -Plugin.GarmentCommonerPenalty.Value;
                return c.Unclothed == 0 ? Plugin.GarmentCommonerBonus.Value : 0;
            }
            catch { return 0; }
        }

        // The game sums its factors from a start value and clamps the result to the cap. Our factors have to join the sum
        // before that clamp (a -5 on someone at 135 raw should be absorbed by the surplus, not taken off the capped 100),
        // so they ride in on the start value for the duration of this one fabling's update: the garment impact and the
        // same-food penalty (Food.Penalty) together, so the game's own sum applies both and nothing is recomputed.
        private static float pendingImpact;

        [HarmonyPatch(typeof(Needs), "UpdateHappiness"), HarmonyPrefix]
        private static void BeforeUpdateHappiness(Needs __instance)
        {
            var w = Food.NeedsWorker(__instance);
            pendingImpact = Impact(w) + Food.Penalty(w);
        }

        [HarmonyPatch(typeof(Needs), "UpdateHappiness"), HarmonyPostfix]
        private static void AfterUpdateHappiness() => pendingImpact = 0f;

        [HarmonyPatch(typeof(KingdomManager), nameof(KingdomManager.GetHappinessStartValue)), HarmonyPostfix]
        private static void StartValueWithGarments(ref float __result) => __result += pendingImpact;

        [HarmonyPatch(typeof(Needs), nameof(Needs.GetHappinessImpactors)), HarmonyPostfix]
        private static void AfterImpactors(Needs __instance, ref List<HappinessImpactor> __result)
        {
            var w = Food.NeedsWorker(__instance);
            int impact = Impact(w);
            if (impact != 0 && __result != null) __result.Add(new HappinessImpactor("Garments:", impact));
        }

        // --- the status on the house ------------------------------------------------------------------------

        // "No garments", cloned from the game's "missing coal" status with the garment's icon.
        internal static void SetupStatus(DataManager dm)
        {
            try
            {
                if (dm.statusSoData.Any(s => s != null && s.key == Status)) return;
                var coal = dm.GetStatusDataFromKey("missingCoal");
                var garment = Tailor.Garment;
                if (coal == null || garment == null) { Plugin.Log.LogWarning("Tailor: no 'missingCoal' status or garment data to build the garment status from."); return; }
                var st = UnityEngine.Object.Instantiate(coal);
                st.name = Status; st.key = Status;
                st.title = ModText.Make("No garments");
                st.description = ModText.Make("The household is due a new garment and has none. Garments come from a Tailor; the head of the household fetches them.");
                // the status icon is a text glyph; the coal one is a font character, ours is the garment's sprite tag
                st.icon = Utils_Text.FontSprite(garment.GetSpriteKey());
                // the overhead marker is a sprite: nogarments.png from the icons folder, else the garment's own resource sprite
                var sprite = Icons.Load("nogarments") ?? Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(sp => sp != null && sp.name.ToLowerInvariant().Contains("garment"));
                if (sprite != null) st.sprite = sprite;
                Plugin.Log.LogInfo($"Tailor: garment status set up (marker {(sprite != null ? sprite.name : "coal's")}).");
                dm.statusSoData.Add(st);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Tailor: garment status: " + ex.Message); }
        }

        // What is pulling the average down (Jess, 2026-10-22: "what's pulling it down? out of winter, still at 89"): every
        // fabling's happiness impactors (the panel's own list, our Same food line included) summed over the kingdom, as the
        // average contribution and how many carry each; plus the start value, who sits at the cap, and the class averages.
        private static void LogHappinessBreakdown(KingdomManager km)
        {
            var sum = new Dictionary<string, float>(); var cnt = new Dictionary<string, int>();
            var byClass = new Dictionary<FablingClass, (float total, int n)>();
            int n = 0, capped = 0, floored = 0; float max = km.GetMaxHappiness();
            foreach (var w in Worker.Instances)
            {
                if (w == null || w.needs == null) continue;
                n++;
                if (w.needs.happiness >= max) capped++;
                if (w.needs.happiness <= 0f) floored++;
                var c = byClass.TryGetValue(w.fablingClass, out var t) ? t : (total: 0f, n: 0);
                byClass[w.fablingClass] = (c.Item1 + w.needs.happiness, c.Item2 + 1);
                var list = w.needs.GetHappinessImpactors();
                if (list == null) continue;
                foreach (var imp in list)
                {
                    string k = (imp.title ?? "?").TrimEnd(':', ' ');
                    sum[k] = (sum.TryGetValue(k, out var v) ? v : 0f) + imp.impact;
                    cnt[k] = (cnt.TryGetValue(k, out var q) ? q : 0) + 1;
                }
            }
            if (n == 0) return;
            var parts = sum.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / n:+0.0;-0.0} avg ({cnt[kv.Key]} fablings, {kv.Value / Mathf.Max(cnt[kv.Key], 1):+0.0;-0.0} each)");
            var classes = byClass.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key.ToString().ToLowerInvariant()} {kv.Value.total / Mathf.Max(kv.Value.n, 1):0.0} ({kv.Value.n})");
            Plugin.Log.LogInfo($"Tailor: happiness breakdown over {n} fablings: start {km.GetHappinessStartValue():0}, cap {max:0}; {string.Join("; ", parts)}; at the cap {capped}, at zero {floored}; by class {string.Join(", ", classes)}.");
        }

        // Jess (2026-10-18): 78 happiness on the bar and only 2 of 3 visitors wanted to stay. The roll reads the same
        // average the bar shows, at the day change; this logs what it saw, so the next roll can be checked. (The attribute
        // must sit directly on this method: in 0.9.0 as first built it had drifted onto the helper above, whose parameter
        // Harmony could not match, and the whole plugin failed to patch.)
        [HarmonyPatch(typeof(KingdomManager), "CalculateInterestedVisitors"), HarmonyPostfix]
        private static void LogVisitors(KingdomManager __instance, int visitors, int __result)
        {
            try
            {
                var info = __instance.GetCurrentPopulationUnlockInfo();
                int sick = Worker.Instances != null ? Worker.Instances.Count(w => w != null && w.needs != null && w.needs.isSick) : 0;
                Plugin.Log.LogInfo($"Tailor: visitors rolled: {__result} of {visitors} want to stay at average happiness {__instance.GetAverageWorkerHappiness()} (thresholds {string.Join("/", info.minAverageHappiness)}; {sick} sick, {__instance.GetTotalPopulationCount()} fablings).");
                if (Plugin.HappinessBreakdown.Value) LogHappinessBreakdown(__instance);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: visitor log: " + ex.Message); }
        }

        // --- save and load --------------------------------------------------------------------------------------

        // The state goes into the file right before the file is written, after the game has synced its own savers: the
        // quest and letter managers' OnSave is where IntroLetters and NobleLetters move our quests and letter records into
        // the state. A hook on DataManager.Save ran before that sync (the game fires it first), so the file used to hold
        // the previous save's quests and noble letters (fixed 2026-10-22).
        [HarmonyPatch(typeof(Lowscope.Saving.Core.SaveFileUtility), nameof(Lowscope.Saving.Core.SaveFileUtility.WriteSave)), HarmonyPrefix]
        private static void BeforeWrite(Lowscope.Saving.Data.SaveGame saveGame)
        {
            if (saveGame == null || SaveMaster.GetActiveSlot() < 0) return;
            try { saveGame.Set($"SVar-{SaveKey}", JsonConvert.SerializeObject(S), "Global"); } catch (Exception ex) { Plugin.Log.LogWarning("Tailor: save: " + ex.Message); }
        }

        [HarmonyPatch(typeof(DataManager), "Load"), HarmonyPostfix]
        private static void AfterLoad(int slot)
        {
            if (slot < 0) return;
            string json = SaveMaster.GetString(SaveKey);
            if (string.IsNullOrEmpty(json)) { S = new State(); return; }
            try { S = JsonConvert.DeserializeObject<State>(json) ?? new State(); }
            catch (Exception ex) { Plugin.Log.LogWarning("Tailor: couldn't read saved garment data, starting over: " + ex.Message); S = new State(); }
        }

        [HarmonyPatch(typeof(WorldEventManager), "Start"), HarmonyPrefix]
        private static void NewGame(WorldEventManager __instance)
        {
            if (Traverse.Create(__instance).Field("isLoaded").GetValue<bool>()) return;
            S = new State();
        }
    }
}
