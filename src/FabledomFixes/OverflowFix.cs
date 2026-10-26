using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomFixes
{
    // Deliveries overflowing a full store (Jess's brief, 2026-10-06). Two faults, worst at the Marketplace:
    //
    //  1. The generic "fetch what my store is missing" step (CanFindStorageWithMissingResource, used by marketeers,
    //     shopkeepers and every production place fetching inputs) sizes the pickup by what the source has and what the
    //     worker can carry, and never by the room left at the destination. A market with room for 5 coal sends a
    //     marketeer for a full load; the stash step stores 5 and deletes the rest. The two sibling steps (fixed
    //     resource, food) do cap by room.
    //  2. Nothing counts loads already on their way, so several workers see the same 5 of room and all set off.
    //
    // The fix keeps an "incoming" ledger: when a worker commits to a destination the load is pledged to it, room is
    // judged net of pledges, pickups are capped to that net room, and a pledge is released when the load is stashed or
    // the worker gives it up. A pledge only counts while its worker still holds the reservation or the goods, so a
    // stale one can never make a store look full for good. As a backstop, when a load still does not fit, the part
    // that fits is stored and the rest is handed to the carry fix to return rather than deleted.
    internal static class OverflowFix
    {
        private class Pledge { public Storage To; public string Key; public int Amount; public float Until; }   // Until: a gatherer promise, kept alive by gather ticks
        private static readonly Dictionary<Worker, Pledge> Pledges = new Dictionary<Worker, Pledge>();

        private static readonly AccessTools.FieldRef<CanFindStorageWithMissingResource, Worker> MissingWorker = AccessTools.FieldRefAccess<CanFindStorageWithMissingResource, Worker>("worker");
        private static readonly AccessTools.FieldRef<CanFindStorageWithMissingResource, Storage> MissingOrigin = AccessTools.FieldRefAccess<CanFindStorageWithMissingResource, Storage>("originStorage");
        private static readonly AccessTools.FieldRef<CanFindStorageWithResource, Worker> ResourceWorker = AccessTools.FieldRefAccess<CanFindStorageWithResource, Worker>("worker");
        private static readonly AccessTools.FieldRef<CanFindStorageWithResource, Storage> ResourceOrigin = AccessTools.FieldRefAccess<CanFindStorageWithResource, Storage>("originStorage");
        private static readonly AccessTools.FieldRef<CanFindStorageWithEdible, Worker> EdibleWorker = AccessTools.FieldRefAccess<CanFindStorageWithEdible, Worker>("worker");
        private static readonly AccessTools.FieldRef<CanFindStorageWithEdible, Storage> EdibleOrigin = AccessTools.FieldRefAccess<CanFindStorageWithEdible, Storage>("originStorage");
        private static readonly AccessTools.FieldRef<StashResource, Worker> StashWorker = AccessTools.FieldRefAccess<StashResource, Worker>("worker");

        private static readonly Type CapacitiesStorageType = typeof(CanFindStorageWithMissingResource).GetNestedType("ResourceCapacitiesStorage", BindingFlags.NonPublic);
        private static readonly FieldInfo CapacityField = CapacitiesStorageType?.GetField("resourceCapacity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo CapacityObjectField = CapacitiesStorageType?.GetField("gameObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // --- the ledger -----------------------------------------------------------------------

        // Loads pledged to this store for this good by workers who still hold them (reserved at the source, or in hand).
        internal static int Incoming(Storage to, string key)
        {
            if (to == null) return 0;
            int sum = 0;
            List<Worker> dead = null;
            foreach (var kv in Pledges)
            {
                var w = kv.Key; var p = kv.Value;
                if (w == null || p.To == null) { if (dead == null) dead = new List<Worker>(); dead.Add(w); continue; }
                if (p.To != to || p.Key != key) continue;
                if (Holds(w, p)) sum += p.Amount; else { if (dead == null) dead = new List<Worker>(); dead.Add(w); }
            }
            if (dead != null) foreach (var w in dead) Pledges.Remove(w);
            return sum;
        }

        private static bool Holds(Worker w, Pledge p)
        {
            try
            {
                if (Time.time < p.Until) return true;
                if (w.resourceReservations != null && w.resourceReservations.Any(r => r != null && r.key == p.Key && r.amount > 0)) return true;
                return w.storage != null && w.storage.GetResourceStored(p.Key) > 0;
            }
            catch { return false; }
        }

        internal static int NetRoom(Storage to, string key) => to == null ? 0 : to.GetRemainingCapacity(key) - Incoming(to, key);

        private static void Pledge_(Worker w, Storage to, string key, int amount)
        {
            if (w == null || to == null || amount <= 0) { if (w != null) Pledges.Remove(w); return; }
            Pledges[w] = new Pledge { To = to, Key = key, Amount = amount };
        }

        internal static void Release(Worker w) { if (w != null) Pledges.Remove(w); }

        // --- 1. the generic fetch: judge room net of pledges, cap the pickup, pledge ------------------

        // The destination's "what is missing" list, with pledged loads taken off; an entry with no net room goes, so a
        // store that is already being refilled is not chosen again.
        [HarmonyPatch(typeof(CanFindStorageWithMissingResource), "GetOriginStorageCapacities"), HarmonyPostfix]
        private static void NetOfPledges(object __result)
        {
            try
            {
                if (!(__result is IList list) || CapacityField == null || CapacityObjectField == null) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var entry = list[i];
                    var cap = (ResourceCapacity)CapacityField.GetValue(entry);
                    var go = CapacityObjectField.GetValue(entry) as GameObject;
                    var to = go != null ? go.GetComponent<Storage>() : null;
                    int pledged = Incoming(to, cap.key);
                    if (pledged <= 0) continue;
                    int net = cap.amount - pledged;
                    if (net <= 0) list.RemoveAt(i);
                    else CapacityField.SetValue(entry, new ResourceCapacity(cap.key, net));
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] net-of-pledges: " + ex.Message); }
        }

        [HarmonyPatch(typeof(CanFindStorageWithMissingResource), "ReserveResource"), HarmonyPostfix]
        private static void CapGenericPickup(CanFindStorageWithMissingResource __instance, Storage pickupStorage)
        {
            try
            {
                var worker = MissingWorker(__instance);
                var to = __instance.alternateOrigin != null && __instance.alternateOrigin.Value != null ? __instance.alternateOrigin.Value.GetComponent<Storage>() : null;
                if (to == null) to = MissingOrigin(__instance);
                CapAndPledge(worker, pickupStorage, to, "fetch");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] generic pickup: " + ex.Message); }
        }

        // The fixed-resource fetch (house coal, farm grain...) already caps by room; it only needs the pledges.
        [HarmonyPatch(typeof(CanFindStorageWithResource), nameof(CanFindStorageWithResource.OnUpdate)), HarmonyPrefix]
        private static bool SkipWhenRefillUnderWay(CanFindStorageWithResource __instance, ref TaskStatus __result)
        {
            try
            {
                var worker = ResourceWorker(__instance);
                if (worker != null && worker.resourceReservations != null && worker.resourceReservations.Count > 0) return true;   // already committed
                var to = ResourceOrigin(__instance);
                if (to == null || __instance.resourceData == null) return true;
                if (to.GetRemainingCapacity(__instance.resourceData.key) > 0 && NetRoom(to, __instance.resourceData.key) <= 0)
                {
                    __result = TaskStatus.Failure;
                    return false;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] fixed fetch: " + ex.Message); }
            return true;
        }

        [HarmonyPatch(typeof(CanFindStorageWithResource), "ReserveResource"), HarmonyPostfix]
        private static void CapFixedPickup(CanFindStorageWithResource __instance, Storage pickupStorage)
        {
            try { CapAndPledge(ResourceWorker(__instance), pickupStorage, ResourceOrigin(__instance), "fixed fetch"); }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] fixed pickup: " + ex.Message); }
        }

        [HarmonyPatch(typeof(CanFindStorageWithEdible), "ReserveResource"), HarmonyPostfix]
        private static void CapFoodPickup(CanFindStorageWithEdible __instance, Storage pickupStorage)
        {
            try { CapAndPledge(EdibleWorker(__instance), pickupStorage, EdibleOrigin(__instance), "food fetch"); }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] food pickup: " + ex.Message); }
        }

        // The reservation the step just made is trimmed to the destination's net room and pledged to it.
        private static void CapAndPledge(Worker worker, Storage from, Storage to, string what)
        {
            if (worker == null || to == null || worker.resourceReservations == null || worker.resourceReservations.Count == 0) return;
            var r = worker.resourceReservations[worker.resourceReservations.Count - 1];
            if (r == null || string.IsNullOrEmpty(r.key)) return;
            int room = Mathf.Max(0, NetRoom(to, r.key));
            if (r.amount > room)
            {
                int cut = r.amount - room;
                try { (from ?? r.storage)?.ClearReserve(r.key, cut, r.alterGlobal); } catch { }
                if (Plugin.VerboseOverflowLog.Value)
                    Plugin.Log.LogInfo($"[overflow] {worker.name} ({worker.GetProfessionKey()}): {what} of {r.key} for {to.name} cut from {r.amount} to {room} (room {to.GetRemainingCapacity(r.key)}, {Incoming(to, r.key)} already on its way).");
                r.amount = room;
            }
            Pledge_(worker, to, r.key, r.amount);
        }

        // --- 2. releasing ------------------------------------------------------------------------

        // A reservation cleared before the pickup (the task was cut off) ends the pledge; one cleared by the pickup
        // itself leaves the goods in the worker's hands and the pledge stands.
        [HarmonyPatch(typeof(Worker), nameof(Worker.ClearResourceReservations)), HarmonyPostfix]
        private static void AfterReservationsCleared(Worker __instance)
        {
            try
            {
                if (__instance == null || !Pledges.TryGetValue(__instance, out var p)) return;
                if (__instance.storage == null || __instance.storage.GetResourceStored(p.Key) <= 0) Pledges.Remove(__instance);
            }
            catch { }
        }

        // --- 2b. gatherers: the gate and the gather itself judge the workplace's room net of other pledges --------
        // A woodcutter leaves when the camp has room for 1, chops a full load, and comes back to a camp the others have
        // filled; the game then deleted the load (5 wood a trip, over 2,000 trips in a few days of Jess's save).

        private static readonly AccessTools.FieldRef<StorageHasRoomForResource, Storage> GateStorage = AccessTools.FieldRefAccess<StorageHasRoomForResource, Storage>("_storage");
        private static readonly AccessTools.FieldRef<GatherResource, GatherableBase> GatherTarget = AccessTools.FieldRefAccess<GatherResource, GatherableBase>("gatherable");
        private static readonly AccessTools.FieldRef<GatherResource, Worker> GatherWorker = AccessTools.FieldRefAccess<GatherResource, Worker>("worker");
        private static readonly AccessTools.FieldRef<GatherResourceCrop, GatherableBase> CropTarget = AccessTools.FieldRefAccess<GatherResourceCrop, GatherableBase>("gatherable");
        private static readonly AccessTools.FieldRef<GatherResourceCrop, Worker> CropWorker = AccessTools.FieldRefAccess<GatherResourceCrop, Worker>("worker");
        private const float PromiseSeconds = 120f;

        // Room net of everyone else's pledges (this worker's own pledge is not held against them).
        private static int RoomForMe(Worker w, Storage to, string key)
        {
            if (to == null) return 0;
            int others = Incoming(to, key);
            if (Pledges.TryGetValue(w, out var mine) && mine.To == to && mine.Key == key && Holds(w, mine)) others -= mine.Amount;
            return to.GetRemainingCapacity(key) - Mathf.Max(0, others);
        }

        [HarmonyPatch(typeof(StorageHasRoomForResource), nameof(StorageHasRoomForResource.OnUpdate)), HarmonyPostfix]
        private static void GateNetOfPledges(StorageHasRoomForResource __instance, ref TaskStatus __result)
        {
            try
            {
                if (__result != TaskStatus.Success || __instance.resourceData == null) return;
                var to = GateStorage(__instance);
                var go = Traverse.Create(__instance).Field("gameObject").GetValue<GameObject>();
                var w = go != null ? go.GetComponent<Worker>() : null;
                if (to == null || w == null) return;
                string key = __instance.resourceData.key;
                int room = RoomForMe(w, to, key);
                if (room < Mathf.Max(1, __instance.amount)) { __result = TaskStatus.Failure; return; }
                // promise the load I am about to fetch, so the next worker through this gate sees less room
                int load = Mathf.Min(room, Mathf.Max(1, __instance.resourceData.GetCarryCapacity(w.GetProfessionData())));
                Pledges[w] = new Pledge { To = to, Key = key, Amount = load, Until = Time.time + PromiseSeconds };
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] gate: " + ex.Message); }
        }

        // Stop gathering once the load matches the room left; keep the pledge equal to what is in hand.
        private static bool StopAtRoom(Worker w, GatherableBase g)
        {
            if (w == null || g == null || g.data == null) return false;
            string key = g.data.key;
            Storage to = null;
            if (Pledges.TryGetValue(w, out var mine) && mine.Key == key) to = mine.To;
            if (to == null) to = w.workPlace != null ? w.workPlace.storage : null;
            if (to == null) return false;
            int carried = w.storage != null ? w.storage.GetResourceStored(key) : 0;
            int room = RoomForMe(w, to, key);
            Pledges[w] = new Pledge { To = to, Key = key, Amount = Mathf.Max(carried, Mathf.Min(room, carried + 1)), Until = Time.time + PromiseSeconds };
            return carried >= room;
        }

        [HarmonyPatch(typeof(GatherResource), nameof(GatherResource.OnUpdate)), HarmonyPrefix]
        private static bool GatherToRoom(GatherResource __instance, ref TaskStatus __result)
        {
            try
            {
                if (!StopAtRoom(GatherWorker(__instance), GatherTarget(__instance))) return true;
                __result = TaskStatus.Success;
                return false;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] gather: " + ex.Message); return true; }
        }

        [HarmonyPatch(typeof(GatherResourceCrop), nameof(GatherResourceCrop.OnUpdate)), HarmonyPrefix]
        private static bool HarvestToRoom(GatherResourceCrop __instance, ref TaskStatus __result)
        {
            try
            {
                var w = CropWorker(__instance);
                if (!StopAtRoom(w, CropTarget(__instance))) return true;
                try { w.unit.reserver.ClearMainReservation(); } catch { }
                __result = TaskStatus.Success;
                return false;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] harvest: " + ex.Message); return true; }
        }

        // --- losses, summarised once a minute instead of one line per trip ---------------------------------
        private static readonly Dictionary<string, int> LostAmount = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> LostTrips = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> LostWhere = new Dictionary<string, string>();
        private static float lastLossLog;

        private static void CountLoss(string key, int amount, string where)
        {
            LostAmount[key] = (LostAmount.TryGetValue(key, out var a) ? a : 0) + amount;
            LostTrips[key] = (LostTrips.TryGetValue(key, out var t) ? t : 0) + 1;
            LostWhere[key] = where;
            if (Time.time - lastLossLog < 60f) return;
            lastLossLog = Time.time;
            var parts = LostAmount.Select(kv => $"{kv.Value} {kv.Key} in {LostTrips[kv.Key]} trip(s) at a full {LostWhere[kv.Key]}").ToList();
            Plugin.Log.LogWarning("[overflow] lost, nowhere to put it: " + string.Join("; ", parts) + ".");
            LostAmount.Clear(); LostTrips.Clear(); LostWhere.Clear();
        }

        // --- 3. the backstop: what does not fit is returned, not deleted -----------------------------

        private static Worker overflowWorker; private static string overflowKey; private static int overflowAmount; private static Storage overflowStore;

        [HarmonyPatch(typeof(StashResource), nameof(StashResource.OnStart)), HarmonyPrefix, HarmonyPriority(Priority.High)]
        private static void BeforeStash(StashResource __instance)
        {
            overflowWorker = null;
            try
            {
                var worker = StashWorker(__instance);
                var target = __instance.storage != null ? __instance.storage.Value : null;
                if (worker == null || target == null) return;
                var construction = target.GetComponent<Constructable>();
                if (construction != null && construction.isUnderConstruction) return;   // building sites take everything
                var store = target.GetComponent<Storage>() ?? target.GetComponentInParent<Storage>();
                var carry = worker.storage.GetFirstStoredResource();
                if (store == null || carry == null || carry.amount <= 0) return;
                int room = Mathf.Max(0, store.GetRemainingCapacity(carry.key));
                if (room >= carry.amount) return;
                int over = carry.amount - room;
                // keep the overflow aside so the game stores only what fits
                worker.storage.Remove(carry.key, over, alterGlobalStorage: false);
                overflowWorker = worker; overflowKey = carry.key; overflowAmount = over; overflowStore = store;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] before stash: " + ex.Message); overflowWorker = null; }
        }

        [HarmonyPatch(typeof(StashResource), nameof(StashResource.OnStart)), HarmonyPostfix, HarmonyPriority(Priority.High)]
        private static void AfterStash(StashResource __instance)
        {
            try
            {
                var worker = StashWorker(__instance);
                if (overflowWorker != null && overflowWorker == worker)
                {
                    worker.storage.Store(overflowKey, overflowAmount, alterGlobalStorage: false);
                    int lost = CarryFix.ReturnCarry(worker, "store full", logLoss: false);
                    if (lost > 0) CountLoss(overflowKey, lost, overflowStore.name.Replace("(Clone)", ""));
                    else if (Plugin.VerboseOverflowLog.Value)
                        Plugin.Log.LogInfo($"[overflow] {worker.name} ({worker.GetProfessionKey()}): {overflowAmount} {overflowKey} did not fit in {overflowStore.name} ({Incoming(overflowStore, overflowKey)} pledged); returned elsewhere.");
                }
                Release(worker);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[overflow] after stash: " + ex.Message); }
            finally { overflowWorker = null; }
        }
    }
}
