using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomFixes
{
    // Carried goods are deleted (worker.storage.RemoveAll) whenever a fabling's task is cut off:
    // the eat/sick/profession-change branches fall back to DropCarry when no stockpile or granary
    // has room for the whole load, ResetWorker wipes it, and a demolished destination wipes it.
    //
    // This fix:
    //  - remembers where a hauler picked its load up (PickupResource), until the load is stashed;
    //  - remembers the hub a harvester is taken away from mid-shift (ResetProfession), so a reassigned
    //    woodcutter walks the logs in hand back to the old camp instead of dropping them (Jess, 2026-10-09:
    //    shuffling workers for short walks to work should not cost resources);
    //  - before a break, sends a gatherer back to its own workplace if the load fits there,
    //    and snaps a hauler's load straight back to where it came from;
    //  - wherever the game would delete a load, returns it instead: origin, then workplace,
    //    then the nearest stockpile, granary or marketplace with room. Anything left is lost (and logged).
    internal static class CarryFix
    {
        private static readonly ConditionalWeakTable<Worker, Storage> Origins = new ConditionalWeakTable<Worker, Storage>();
        private static readonly ConditionalWeakTable<Worker, Storage> LastHubs = new ConditionalWeakTable<Worker, Storage>();

        private static readonly AccessTools.FieldRef<PickupResource, Worker> PickupWorker = AccessTools.FieldRefAccess<PickupResource, Worker>("worker");
        private static readonly AccessTools.FieldRef<PickupResource, Storage> PickupStorage = AccessTools.FieldRefAccess<PickupResource, Storage>("pickupStorage");
        private static readonly AccessTools.FieldRef<StashResource, Worker> StashWorker = AccessTools.FieldRefAccess<StashResource, Worker>("worker");
        private static readonly AccessTools.FieldRef<DropCarry, Worker> DropWorker = AccessTools.FieldRefAccess<DropCarry, Worker>("worker");
        private static readonly AccessTools.FieldRef<ResetWorker, Worker> ResetWorkerWorker = AccessTools.FieldRefAccess<ResetWorker, Worker>("worker");
        private static readonly AccessTools.FieldRef<WorkerTransportResource, Worker> TransportWorker = AccessTools.FieldRefAccess<WorkerTransportResource, Worker>("worker");
        private static readonly AccessTools.FieldRef<CanFindStorageWithRoomForCarry, Worker> FindWorker = AccessTools.FieldRefAccess<CanFindStorageWithRoomForCarry, Worker>("worker");

        // --- remember / forget the origin ---------------------------------------------------

        [HarmonyPatch(typeof(PickupResource), nameof(PickupResource.OnStart)), HarmonyPostfix]
        private static void AfterPickup(PickupResource __instance)
        {
            var worker = PickupWorker(__instance);
            var from = PickupStorage(__instance);
            if (worker == null || from == null || worker.storage.IsEmpty()) return;
            Origins.Remove(worker);
            Origins.Add(worker, from);
        }

        [HarmonyPatch(typeof(StashResource), nameof(StashResource.OnStart)), HarmonyPostfix]
        private static void AfterStash(StashResource __instance)
        {
            var worker = StashWorker(__instance);
            if (worker != null && worker.storage.IsEmpty()) { Origins.Remove(worker); LastHubs.Remove(worker); }
        }

        // A harvester gathers straight into their hands (Worker.Gather), so there is no pickup origin; when they are
        // moved to another job the workplace is cleared before the tree gets the "professionChanged" event. Keep the
        // hub they are leaving while they still carry something from it.
        [HarmonyPatch(typeof(Worker), nameof(Worker.ResetProfession)), HarmonyPrefix]
        private static void BeforeResetProfession(Worker __instance)
        {
            if (__instance == null || __instance.storage == null || __instance.storage.IsEmpty()) return;
            var hub = WorkplaceStorage(__instance);
            if (hub == null) return;
            LastHubs.Remove(__instance);
            LastHubs.Add(__instance, hub);
        }

        private static Storage LastHub(Worker worker) => LastHubs.TryGetValue(worker, out var hub) && Usable(hub) ? hub : null;

        // --- before a break: hub for gatherers, snap-back for haulers -----------------------

        [HarmonyPatch(typeof(CanFindStorageWithRoomForCarry), nameof(CanFindStorageWithRoomForCarry.OnUpdate)), HarmonyPrefix]
        private static bool BeforeBreakDelivery(CanFindStorageWithRoomForCarry __instance, ref TaskStatus __result)
        {
            var worker = FindWorker(__instance);
            var carry = worker?.storage.GetFirstStoredResource();
            if (carry == null) return true;

            // Hauler: the task was cut off, so the goods go straight back where they came from.
            if (Origins.TryGetValue(worker, out var origin) && Usable(origin))
            {
                ReturnCarry(worker, "task interrupted");
                if (worker.storage.IsEmpty())
                {
                    __result = TaskStatus.Failure;   // nothing left to carry; tree skips the walk
                    return false;
                }
            }

            // Gatherer (or a hauler whose origin is gone): walk the load to the hub they were just moved away from,
            // else to their own workplace, if it fits.
            foreach (var hub in new[] { LastHub(worker), WorkplaceStorage(worker) })
            {
                if (hub == null || hub.GetRemainingCapacity(carry.key) < carry.amount) continue;
                __instance.returnedStorage.Value = hub.gameObject;
                __result = TaskStatus.Success;
                return false;
            }

            return true;   // the game's own stockpile/granary search
        }

        // --- the places where the game deletes a load -----------------------------------------

        [HarmonyPatch(typeof(DropCarry), nameof(DropCarry.OnUpdate)), HarmonyPrefix]
        private static void BeforeDropCarry(DropCarry __instance) => ReturnCarry(DropWorker(__instance), "drop");

        [HarmonyPatch(typeof(ResetWorker), nameof(ResetWorker.OnUpdate)), HarmonyPrefix]
        private static void BeforeResetWorker(ResetWorker __instance)
        {
            if (__instance.resetStorage) ReturnCarry(ResetWorkerWorker(__instance), "reset");
        }

        [HarmonyPatch(typeof(WorkerTransportResource), "HandleDestinationGone"), HarmonyPrefix]
        private static void BeforeDestinationGone(WorkerTransportResource __instance) => ReturnCarry(TransportWorker(__instance), "destination gone");

        // --- returning goods -----------------------------------------------------------------

        internal static int ReturnCarry(Worker worker, string reason, bool logLoss = true)
        {
            if (worker == null || worker.storage == null) return 0;
            var carry = worker.storage.GetFirstStoredResource();
            if (carry == null || carry.amount <= 0) { Origins.Remove(worker); return 0; }

            string key = carry.key;
            int left = carry.amount;

            Origins.TryGetValue(worker, out var origin);
            left = Put(worker, origin, key, left, "origin");
            left = Put(worker, LastHub(worker), key, left, "last workplace");
            left = Put(worker, WorkplaceStorage(worker), key, left, "workplace");
            while (left > 0)
            {
                var nearest = Storage.AllInstances
                    .Where(s => Usable(s) && IsDropOffPoint(s, key) && s.GetRemainingCapacity(key) > 0)
                    .OrderBy(s => (s.transform.position - worker.transform.position).sqrMagnitude)
                    .FirstOrDefault();
                if (nearest == null) break;
                int before = left;
                left = Put(worker, nearest, key, left, "nearest stockpile/granary/market");
                if (left == before) break;
            }

            if (left > 0)
            {
                // what could not be placed anywhere is gone, as the game would have deleted the whole load
                worker.storage.Remove(key, left, alterGlobalStorage: false);
                if (logLoss) Plugin.Log.LogWarning($"[carry] {worker.name}: {left} {key} lost ({reason}) - {WhyNoRoom(worker, origin, key)}");
            }
            Origins.Remove(worker);
            LastHubs.Remove(worker);
            OverflowFix.Release(worker);
            return left;
        }

        private static int Put(Worker worker, Storage target, string key, int amount, string label)
        {
            if (amount <= 0 || !Usable(target)) return amount;
            int n = Mathf.Min(amount, target.GetRemainingCapacity(key));
            if (n <= 0) return amount;

            worker.storage.Remove(key, n, worker.storage.IsResourceRetrievableAndUnlocked(key));
            target.Store(key, n, target.IsResourceRetrievable(key));
            if (Plugin.VerboseCarryLog.Value)
                Plugin.Log.LogInfo($"[carry] {worker.name}: {n} {key} returned to {label} ({target.name}).");
            return amount - n;
        }

        // Besides origin and workplace, a load may only go to a stockpile or granary that allows it,
        // or a marketplace (which only has room for the goods it is set to sell). Anything else is lost.
        private static bool IsDropOffPoint(Storage s, string key)
        {
            if (s.stockpileLot != null) return s.stockpileLot.IsResourceAllowed(key);
            if (s.granary != null) return s.granary.IsResourceAllowed(key);
            return s.GetComponent<Market>() != null;
        }

        // Only runs when a load is lost: says which places were full and which don't take this resource.
        private static string WhyNoRoom(Worker worker, Storage origin, string key)
        {
            var parts = new List<string>();
            // a destroyed Storage compares equal to null in Unity, so the plain check used to drop this part silently
            if ((object)origin == null) parts.Add("no origin recorded");
            else if (!origin) parts.Add("origin gone (destroyed)");
            else parts.Add(Usable(origin) ? $"origin {origin.name} full" : "origin unusable");
            var last = LastHub(worker);
            if (last != null) parts.Add($"last workplace {last.name} full");
            var wp = worker.workPlace;
            if (wp == null || wp.storage == null) parts.Add("no workplace");
            else parts.Add(Usable(wp.storage) ? $"workplace {wp.storage.name} {(wp.storage.GetRemainingCapacity(key) > 0 ? "has room" : "full")}" : "workplace unusable");

            void Count(string label, IEnumerable<Storage> all, System.Func<Storage, bool> allows)
            {
                var list = all.Where(Usable).ToList();
                if (list.Count == 0) { parts.Add($"no {label}s"); return; }
                var taking = list.Where(allows).ToList();
                int room = taking.Sum(s => Mathf.Max(0, s.GetRemainingCapacity(key)));
                parts.Add($"{taking.Count} {label}s take {key} ({room} room), {list.Count - taking.Count} don't");
            }
            Count("stockpile", Storage.AllInstances.Where(s => s.stockpileLot != null), s => s.stockpileLot.IsResourceAllowed(key));
            Count("granary", Storage.AllInstances.Where(s => s.granary != null), s => s.granary.IsResourceAllowed(key));
            Count("marketplace", Storage.AllInstances.Where(s => s.GetComponent<Market>() != null), s => s.GetRemainingCapacity(key) > 0);
            return string.Join("; ", parts) + ".";
        }

        private static Storage WorkplaceStorage(Worker worker)
        {
            var wp = worker.workPlace;
            return wp != null && Usable(wp.storage) ? wp.storage : null;
        }

        private static bool Usable(Storage s)
        {
            if (s == null || s.gridObject == null) return false;
            var g = s.gridObject;
            if (g.destroyable != null && g.destroyable.isBeingDestroyed) return false;
            return g.constructable == null || g.constructable.isConstructionComplete;
        }
    }
}
