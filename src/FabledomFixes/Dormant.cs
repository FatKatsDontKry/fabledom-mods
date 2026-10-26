using System;
using System.Collections.Generic;
using BehaviorDesigner.Runtime.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using Lowscope.Saving;
using Nielsen;
using UnityEngine;

using FabledomFixes;

namespace FabledomTreeBudget   // type names kept: the Tailor finds FabledomTreeBudget.Dormant by name
{
    // Dormant unemployed fablings.
    //
    // An unemployed fabling's whole simulation is its hunger counter: happiness is computed from state (hunger,
    // housing, tax, home area, cold, sickness, prison), free-time visits add nothing, and food is eaten from its
    // own house storage. Everything else it does - wandering, sitting, chatting - is cosmetic and costs a decision
    // tree evaluation every tick plus a walker in the pathfinding update.
    //
    // So: keep an ambient pool of the AmbientCount unemployed nearest the camera running normally, and put the
    // rest inside at home. Going dormant = stop the tree, clear reservations, teleport to the house entry, GoInside
    // (the game's own "hidden in a building" state: no walker, no collider, no rendering). Dormant fablings are fed
    // from their house storage in a sweep that mirrors the Eat task, so the head of household keeps restocking as
    // usual and starvation still works. Needs.Update is untouched and keeps ticking hunger.
    //
    // They wake (GoOutside, fresh tree) when hired, made sick, sent on a mission, when their house is destroyed or
    // changes, when they die (the tree processes the death), or when the camera settles near them. Wakes from game
    // events are immediate; camera-driven sleeps and wakes are staggered over frames.
    //
    // The set of dormant fablings is saved under the mod's own key ("fabDormant", their guids) beside the game's
    // data, which the game never reads. On load exactly those are re-adopted, or released if the feature is now
    // off; a save made without this mod carries no key and nothing is touched.
    [HarmonyPatch]
    public static class Dormant
    {
        // Hooks for other mods (the Tailor's food-variety rule): how to pick the next item from the house shelf for a
        // dormant fabling, and what a dormant fabling just ate (distinct resource keys of the meal). Null = default.
        public static Func<Worker, Storage, ResourceData> PickFood;
        public static Action<Worker, List<string>> MealFed;

        // For other mods: is this fabling asleep, and wake it because it has something to do (a garment errand, say).
        // A woken fabling starts a fresh tree at its door and is not put back to sleep for GraceSeconds.
        public static bool IsDormant(Unit u) => u != null && dormant.Contains(u);
        public static void WakeFor(Unit u, string reason) => WakeIfDormant(u, string.IsNullOrEmpty(reason) ? "other mod" : reason);

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> AmbientCount, GraceSeconds, SleepPerFrame, WakePerFrame;
        internal static ConfigEntry<float> SettleSeconds, ReselectSeconds, Hysteresis, FeedSeconds, SettleDistance;

        private static readonly HashSet<Unit> dormant = new HashSet<Unit>();
        private static readonly Dictionary<Unit, Housing> dormantHome = new Dictionary<Unit, Housing>();
        private static readonly Dictionary<Unit, float> awakeSince = new Dictionary<Unit, float>();
        private static readonly Queue<Unit> toSleep = new Queue<Unit>();
        private static readonly Queue<Unit> toWake = new Queue<Unit>();
        private static readonly HashSet<Unit> queued = new HashSet<Unit>();

        private static CameraController camera;
        private static float nextReselect, nextFeed;
        private static Vector3 settleAnchor; private static float settleAnchorTime = -1f; private static bool settled;
        private static float loadedAt = -1f; private static bool warnedNoPool;
        private static bool adoptPending;
        private static int adoptFrame;
        private const string SaveKey = "fabDormant";
        private static HashSet<string> savedGuids;   // from the save being loaded; consumed by Adopt

        // Counters for the periodic log line.
        private static int slept, woken, fedFablings, fedMeals, lastHomeless, lastAwakePool;
        private static int sweepHungry, sweepNoFood, sweepNoStorage;   // from the latest feed sweep
        private static float sweepHungerSum, sweepTrigger;
        private static readonly Dictionary<string, int> wakeReasons = new Dictionary<string, int>();

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Dormant", "Enabled", true,
                "Put unemployed fablings outside the ambient pool to sleep inside their homes. They keep eating from the house " +
                "and wake when hired, sick, sent on a mission or when the camera comes near. Off = the game's behaviour.");
            AmbientCount = config.Bind("Dormant", "AmbientCount", 100,
                "How many unemployed fablings nearest the camera stay fully simulated (wandering, sitting, chatting).");
            SettleSeconds = config.Bind("Dormant", "CameraSettleSeconds", 2f,
                "Only re-pick the pool when the camera has moved less than CameraSettleDistance in this many seconds, so panning doesn't churn it (a slow follow still counts as settled).");
            SettleDistance = config.Bind("Dormant", "CameraSettleDistance", 10f,
                "Camera movement (world units) within CameraSettleSeconds that still counts as settled. 2026-10-12: with \"not moved at all\" the pool was never picked while the camera followed a boat.");
            ReselectSeconds = config.Bind("Dormant", "ReselectSeconds", 5f,
                "While the camera is still, re-pick the pool this often (awake fablings wander; newcomers arrive).");
            Hysteresis = config.Bind("Dormant", "Hysteresis", 1.5f,
                "A pool member is only put to sleep once it is this many times farther away than the pool's edge.");
            GraceSeconds = config.Bind("Dormant", "GraceSeconds", 20,
                "A fabling must have been awake this long before it can be put to sleep (lets newcomers walk in, hires settle).");
            SleepPerFrame = config.Bind("Dormant", "SleepPerFrame", 40, "Camera-driven sleeps processed per frame.");
            WakePerFrame = config.Bind("Dormant", "WakePerFrame", 5, "Camera-driven wakes processed per frame (each loads a fresh tree).");
            FeedSeconds = config.Bind("Dormant", "FeedSeconds", 3f,
                "How often dormant fablings are fed from their house storage (the game's own needs tick is 3 s).");
        }

        // ---- per frame -----------------------------------------------------------------------------------------

        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (adoptPending && Time.frameCount - adoptFrame > 60 && GameRunning()) { adoptPending = false; Adopt(); }

            if (!Enabled.Value)
            {
                // Switched off with fablings still asleep: wake them a few per frame, never all at once.
                if (dormant.Count > 0 && GameRunning())
                {
                    toSleep.Clear();
                    if (toWake.Count == 0) foreach (var u in dormant) if (!queued.Contains(u)) { toWake.Enqueue(u); queued.Add(u); }
                    ProcessQueues(now);
                }
                return;
            }
            if (!GameRunning()) return;
            HookCamera(now);
            ProcessQueues(now);
            UpdateSettle(now);
            if (!warnedNoPool && loadedAt > 0f && now - loadedAt > 60f && dormant.Count == 0 && toSleep.Count == 0)
            {
                warnedNoPool = true;
                Plugin.Log.LogWarning($"[dormant] a minute after load and nobody is asleep: camera {(camera == null ? "not found" : settled ? "settled" : "still moving")}, pool check pending.");
            }
            if (camera != null && settled && now >= nextReselect)
            {
                nextReselect = now + Mathf.Max(1f, ReselectSeconds.Value);
                try { Reselect(now); }
                catch (Exception ex) { Plugin.Log.LogWarning("[dormant] reselect: " + ex); }
            }
            if (now >= nextFeed)
            {
                nextFeed = now + Mathf.Max(0.5f, FeedSeconds.Value);
                try { Feed(); }
                catch (Exception ex) { Plugin.Log.LogWarning("[dormant] feed: " + ex); }
            }
        }

        private static bool GameRunning()
        {
            return DataManager.Instance != null && DataManager.Instance.isLoadComplete && Time.timeScale > 0f
                && Worker.Instances != null && Worker.Instances.Count > 0
                && GameManager.Instance != null && GameManager.Instance.GetMainState() == MainState.PLAYING;
        }

        private static void HookCamera(float now)
        {
            if (camera != null) return;   // Unity null: also true once the scene's camera rig was destroyed
            camera = UnityEngine.Object.FindObjectOfType<CameraController>();
            if (camera == null) return;
            settleAnchor = camera.transform.position; settleAnchorTime = now; settled = false;
        }

        // Settled = the rig has moved less than SettleDistance over the last SettleSeconds. Checked once per window.
        private static void UpdateSettle(float now)
        {
            if (camera == null) return;
            float window = Mathf.Max(0.5f, SettleSeconds.Value);
            if (settleAnchorTime < 0f) { settleAnchor = camera.transform.position; settleAnchorTime = now; return; }
            if (now - settleAnchorTime < window) return;
            Vector3 p = camera.transform.position;
            settled = (p - settleAnchor).magnitude < Mathf.Max(0f, SettleDistance.Value);
            settleAnchor = p; settleAnchorTime = now;
        }

        private static void ProcessQueues(float now)
        {
            int n = 0;
            while (toSleep.Count > 0 && n++ < SleepPerFrame.Value)
            {
                var u = toSleep.Dequeue();
                queued.Remove(u);
                if (u != null && !dormant.Contains(u) && CanSleepNow(u.worker, now)) Sleep(u);
            }
            n = 0;
            while (toWake.Count > 0 && n++ < WakePerFrame.Value)
            {
                var u = toWake.Dequeue();
                queued.Remove(u);
                if (u != null && dormant.Contains(u)) Wake(u, "camera");
            }
        }

        // ---- who sleeps ---------------------------------------------------------------------------------------

        // Unemployed, allied, alive, housed-or-not, and in no state the tree must handle (sick, imprisoned, mission, hiding).
        private static bool IsIdleCandidate(Worker w)
        {
            if (w == null || w.unit == null || w.needs == null || w.resident == null) return false;
            if (w.fablingClass == FablingClass.FAIRY || w.HasProfession()) return false;
            var u = w.unit;
            if (u.isOnMission || u.health == null || u.health.isDead || w.isHiding) return false;
            if (w.needs.isSick || w.needs.isImprisoned) return false;
            return u.worldObject != null && u.worldObject.team == Team.ALLY;
        }

        private static bool CanSleepNow(Worker w, float now)
        {
            if (!IsIdleCandidate(w) || !w.resident.HasHousing()) return false;
            var u = w.unit;
            if (u.isInside || u.vehicle != null) return false;
            if (w.storage != null && !w.storage.IsEmpty()) return false;    // carrying something: let the tree deliver it
            if (!awakeSince.TryGetValue(u, out var since) || now - since < GraceSeconds.Value) return false;
            return w.resident.housing.unitWaypoints != null;
        }

        private struct Cand { public Unit u; public float d; public bool dormant; }   // d is a squared distance

        private static void Reselect(float now)
        {
            Vector3 focus = camera.transform.position;   // the rig sits on the ground at the point the camera looks at
            var cands = new List<Cand>(Worker.Instances.Count);
            int homeless = 0;
            float hysteresis = Mathf.Max(1f, Hysteresis.Value);

            foreach (var w in Worker.Instances)
            {
                if (w == null || w.unit == null) continue;
                var u = w.unit;
                bool isDormant = dormant.Contains(u);
                if (isDormant)
                {
                    // house changed under them (destroyed and rehoused, or moved by the housing fix): wake, they'd reappear in the wrong place
                    if (!dormantHome.TryGetValue(u, out var home) || home != w.resident.housing) { Wake(u, "home changed"); continue; }
                    // the game re-enabled the tree behind our back (e.g. the load sequence): a dormant fabling's tree stays stopped
                    if (u.btc != null && u.btc.current != null && u.btc.current.ExecutionStatus == TaskStatus.Running)
                    {
                        try { u.btc.current.DisableBehavior(false); if (u.reserver != null) { u.reserver.ClearAllActive(); u.reserver.ClearAllPrevious(); } }
                        catch (Exception ex) { Plugin.Log.LogWarning($"[dormant] re-stopping {Name(u)}: {ex.Message}"); }
                    }
                }
                else
                {
                    if (!IsIdleCandidate(w)) { awakeSince.Remove(u); continue; }
                    if (!w.resident.HasHousing()) { homeless++; continue; }
                    if (!awakeSince.ContainsKey(u)) awakeSince[u] = now;
                }
                Vector3 p = u.transform.position;
                float dx = p.x - focus.x, dz = p.z - focus.z;
                cands.Add(new Cand { u = u, d = dx * dx + dz * dz, dormant = isDormant });
            }
            lastHomeless = homeless;

            cands.Sort((a, b) => a.d.CompareTo(b.d));
            int n = Mathf.Max(0, AmbientCount.Value);
            float edge = cands.Count > n && n > 0 ? cands[n - 1].d : float.PositiveInfinity;
            float farEdge = edge * hysteresis * hysteresis;   // squared, like d
            if (n == 0) edge = farEdge = -1f;   // pool of zero: everyone sleeps

            int awake = 0;
            foreach (var c in cands) if (!c.dormant) awake++;

            // Sleep pass, farthest first: beyond the hysteresis edge always; beyond the pool edge when the pool is over-full.
            for (int i = cands.Count - 1; i >= 0; i--)
            {
                var c = cands[i];
                if (c.d <= edge) break;
                if (c.dormant || queued.Contains(c.u)) continue;
                if (c.d > farEdge || awake > n)
                {
                    if (!CanSleepNow(c.u.worker, now)) continue;   // stays awake and counted; tried again next round
                    toSleep.Enqueue(c.u); queued.Add(c.u); awake--;
                }
            }
            // Wake pass, nearest first, while there is room in the pool.
            for (int i = 0; i < cands.Count && awake < n; i++)
            {
                var c = cands[i];
                if (c.d > edge) break;
                if (!c.dormant || queued.Contains(c.u)) continue;
                toWake.Enqueue(c.u); queued.Add(c.u); awake++;
            }
            lastAwakePool = awake;
        }

        // ---- transitions ----------------------------------------------------------------------------------------

        private static void Sleep(Unit u)
        {
            var w = u.worker;
            var housing = w.resident.housing;
            try
            {
                try { w.ResetPartner(); } catch { }
                // Full stop, never pause: pause=false is what reaches DestroyBehavior and runs the tasks' OnEnd cleanup.
                // (The no-argument DisableBehavior uses the prefab's pauseWhenDisabled flag, which we can't see.)
                if (u.btc != null && u.btc.current != null) u.btc.current.DisableBehavior(false);
                if (u.reserver != null) { u.reserver.ClearAllActive(); u.reserver.ClearAllPrevious(); }
                w.ClearResourceReservations(alterGlobal: false);
                w.isHandlingHunger = false;
                var entry = housing.unitWaypoints.GetEntry();
                u.ai.TeleportWithRaycastHeight(entry != null ? entry.transform.position : housing.transform.position);
                u.GoInside();
                dormant.Add(u);
                dormantHome[u] = housing;
                awakeSince.Remove(u);
                slept++;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[dormant] couldn't put {Name(u)} to sleep: {ex.Message}; waking it back up.");
                dormant.Add(u);
                Wake(u, "sleep failed");
            }
        }

        private static void Wake(Unit u, string reason)
        {
            if (!dormant.Remove(u)) return;
            dormantHome.Remove(u);
            if (u == null) return;
            try
            {
                u.GoOutside();
                if (u.worker != null) u.worker.SetIdleAnimation();
                if (u.btc != null && u.btc.current != null) u.btc.current.EnableBehavior();   // fresh start from the root
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[dormant] waking {Name(u)} ({reason}): {ex.Message}"); }
            awakeSince[u] = Time.realtimeSinceStartup;
            woken++;
            wakeReasons.TryGetValue(reason, out int k);
            wakeReasons[reason] = k + 1;
            // Rare wakes are worth a line each (camera wakes are routine and only counted).
            if (reason != "camera") Plugin.Log.LogInfo($"[dormant] woke {Name(u)} ({reason}) at {u.transform.position:F0}");
        }

        private static void WakeIfDormant(Unit u, string reason)
        {
            if (u != null && dormant.Contains(u)) Wake(u, reason);
        }

        // ---- feeding ---------------------------------------------------------------------------------------------

        // Mirrors ReserveFood + Eat: take edibles from the house storage until hunger is at or under eatUntil.
        // Another mod can steer the pick (PickFood) and hear about the meal (MealFed).
        private static readonly List<string> mealKinds = new List<string>();

        private static void Feed()
        {
            sweepHungry = sweepNoFood = sweepNoStorage = 0;
            sweepHungerSum = 0f;
            if (dormant.Count == 0) return;
            foreach (var u in new List<Unit>(dormant))
            {
                if (u == null) { dormant.Remove(u); dormantHome.Remove(u); continue; }
                var w = u.worker;
                var needs = w != null ? w.needs : null;
                if (needs == null || needs.config == null) continue;
                sweepHungerSum += needs.hunger;
                sweepTrigger = needs.config.triggerHunger;
                if (needs.hunger <= needs.config.triggerHunger) continue;
                sweepHungry++;
                var housing = w.resident.housing;
                if (housing == null) { Wake(u, "no housing"); continue; }
                var storage = housing.GetStorage();
                if (storage == null) { sweepNoStorage++; continue; }
                if (!storage.HasFood()) { sweepNoFood++; continue; }

                int items = 0;
                mealKinds.Clear();
                while (needs.hunger > needs.config.eatUntil && items < 50 && storage.HasFood())
                {
                    var pick = PickFood;
                    var food = pick != null ? pick(w, storage) : storage.GetFirstEdibleResource(onlyRetreivable: false);
                    if (food == null) break;
                    needs.hunger -= food.restoreHungerBy;
                    storage.Remove(food.key, 1, alterGlobalStorage: false);
                    if (w.fablingClass != FablingClass.PEASANT && food.key == needs.previousEdibleConsumed)
                        needs.sameEdibleAmount += food.restoreHungerBy;
                    else
                        needs.sameEdibleAmount = 0;
                    needs.previousEdibleConsumed = food.key;
                    if (!mealKinds.Contains(food.key)) mealKinds.Add(food.key);
                    items++;
                }
                if (items == 0) continue;
                if (needs.hunger < needs.config.triggerHunger) needs.timeHungry = 0f;
                if (needs.hunger < needs.config.triggerStarving) needs.HideStarvingWarnings();
                fedFablings++;
                fedMeals += items;
                var fed = MealFed;
                if (fed != null) { try { fed(w, new List<string>(mealKinds)); } catch (Exception ex) { Plugin.Log.LogWarning("[dormant] MealFed hook: " + ex.Message); } }
            }
        }

        // ---- save and load -----------------------------------------------------------------------------------------

        // The dormant set goes into the save slot under the mod's own key, which the game never reads.
        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPrefix]
        private static void BeforeSave()
        {
            try
            {
                if (SaveMaster.GetActiveSlot() < 0) return;
                var guids = new List<string>();
                foreach (var u in dormant)
                    if (u != null && u.worldObject != null && !string.IsNullOrEmpty(u.worldObject.guid)) guids.Add(u.worldObject.guid);
                SaveMaster.SetString(SaveKey, string.Join(";", guids));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[dormant] save: " + ex.Message); }
        }

        [HarmonyPatch(typeof(DataManager), "Load"), HarmonyPostfix]
        private static void AfterLoad(int slot)
        {
            savedGuids = null;
            if (slot < 0) return;
            try
            {
                string s = SaveMaster.GetString(SaveKey);
                if (string.IsNullOrEmpty(s)) return;   // a save without this mod's data: nothing is ours
                savedGuids = new HashSet<string>(s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[dormant] load: " + ex.Message); }
        }

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoad(DataManager __instance) => __instance.OnLoadComplete += OnLoadComplete;

        private static void OnLoadComplete()
        {
            dormant.Clear(); dormantHome.Clear(); awakeSince.Clear();
            toSleep.Clear(); toWake.Clear(); queued.Clear(); wakeReasons.Clear();
            camera = null; settleAnchorTime = -1f; settled = false;
            loadedAt = Time.realtimeSinceStartup; warnedNoPool = false;
            adoptPending = true;
            adoptFrame = Time.frameCount;
            nextReselect = nextFeed = 0f;
        }

        // Fablings saved while dormant come back "inside" with a live tree doing invisible free time. Re-adopt exactly
        // the ones the save lists as ours, or release them if the feature is now off. Nothing else is touched.
        private static void Adopt()
        {
            var ours = savedGuids;
            savedGuids = null;
            if (ours == null || ours.Count == 0) return;
            int adopted = 0, released = 0;
            foreach (var w in Worker.Instances)
            {
                if (w == null || w.unit == null || w.unit.worldObject == null || !ours.Contains(w.unit.worldObject.guid)) continue;
                var u = w.unit;
                if (!Enabled.Value || !IsIdleCandidate(w) || !u.isInside || !w.resident.HasHousing())
                {
                    // feature off, or the fabling is no longer in a state we'd keep asleep: hand it back to the game
                    if (!u.isInside) continue;
                    try { u.GoOutside(); w.SetIdleAnimation(); u.btc.Restart(); released++; }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[dormant] release {Name(u)}: {ex.Message}"); }
                    continue;
                }
                try
                {
                    if (u.btc != null && u.btc.current != null) u.btc.current.DisableBehavior(false);
                    if (u.reserver != null) { u.reserver.ClearAllActive(); u.reserver.ClearAllPrevious(); }
                    w.ClearResourceReservations(alterGlobal: false);
                    w.isHandlingHunger = false;
                    dormant.Add(u);
                    dormantHome[u] = w.resident.housing;
                    adopted++;
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[dormant] adopt {Name(u)}: {ex.Message}"); }
            }
            Plugin.Log.LogInfo($"[dormant] after load: save listed {ours.Count} dormant fablings; {adopted} re-adopted, {released} released (feature {(Enabled.Value ? "on" : "off")}).");
        }

        // ---- wake triggers from the game -----------------------------------------------------------------------------

        [HarmonyPatch(typeof(Worker), nameof(Worker.StartProfession)), HarmonyPrefix]
        private static void BeforeHire(Worker __instance) => WakeIfDormant(__instance != null ? __instance.unit : null, "hired");

        [HarmonyPatch(typeof(Needs), nameof(Needs.MakeSick)), HarmonyPrefix]
        private static void BeforeSick(Needs __instance) => WakeIfDormant(__instance.GetComponent<Unit>(), "sick");

        [HarmonyPatch(typeof(Unit), nameof(Unit.SendOnMission)), HarmonyPrefix]
        private static void BeforeMission(Unit __instance) => WakeIfDormant(__instance, "mission");

        [HarmonyPatch(typeof(Resident), nameof(Resident.HandleHousingDestroyed)), HarmonyPrefix]
        private static void BeforeHouseGone(Resident __instance) => WakeIfDormant(__instance.unit, "house destroyed");

        [HarmonyPatch(typeof(Resident), nameof(Resident.RemoveFromHousing)), HarmonyPrefix]
        private static void BeforeLeaveHouse(Resident __instance) => WakeIfDormant(__instance.unit, "left house");

        [HarmonyPatch(typeof(Health), nameof(Health.Die)), HarmonyPrefix]
        private static void BeforeDie(Health __instance)
        {
            var u = __instance.GetComponent<Unit>();
            if (u != null) WakeIfDormant(u, "died");   // the tree's own death branch must run
        }

        [HarmonyPatch(typeof(Unit), nameof(Unit.RemoveFromGameplay)), HarmonyPrefix]
        private static void BeforeRemove(Unit __instance)
        {
            dormant.Remove(__instance); dormantHome.Remove(__instance); awakeSince.Remove(__instance);
        }

        // The game restarted a tree itself (mission return, repair): that unit is awake whatever we thought.
        [HarmonyPatch(typeof(BehaviorTreeController), nameof(BehaviorTreeController.Restart)), HarmonyPostfix]
        private static void AfterRestart(BehaviorTreeController __instance)
        {
            var u = __instance.GetComponent<Unit>();
            if (u != null && dormant.Remove(u)) { dormantHome.Remove(u); awakeSince[u] = Time.realtimeSinceStartup; }
        }

        // ---- reporting -------------------------------------------------------------------------------------------------

        internal static void LogStats()
        {
            if (!Enabled.Value && dormant.Count == 0) return;
            string reasons = "";
            foreach (var kv in wakeReasons) reasons += $" {kv.Key}={kv.Value}";
            bool feast = false;
            try { feast = DataManager.Instance != null && DataManager.Instance.IsBuffActivated("agnesFeast"); } catch { }
            float avgHunger = dormant.Count > 0 ? sweepHungerSum / dormant.Count : 0f;
            Plugin.Log.LogInfo($"[dormant] dormant {dormant.Count}, ambient pool {lastAwakePool}/{AmbientCount.Value}, homeless awake {lastHomeless}; " +
                               $"since last: slept {slept}, woke {woken} ({reasons.Trim()}), fed {fedMeals} meals to {fedFablings} fablings; " +
                               $"last sweep: avg hunger {avgHunger:0.0} (trigger {sweepTrigger:0}), hungry {sweepHungry}, of which no food at home {sweepNoFood}, no storage {sweepNoStorage}" +
                               $"{(feast ? ", FEAST ACTIVE (hunger paused)" : "")}; queued sleep {toSleep.Count}, wake {toWake.Count}");
            slept = woken = fedFablings = fedMeals = 0;
            wakeReasons.Clear();
        }

        private static string Name(Unit u)
        {
            try { return u.GetLocalizedBirthName(); } catch { return u != null ? u.name : "?"; }
        }
    }
}
