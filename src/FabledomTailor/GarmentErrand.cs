using System.Linq;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // Real carrying for garments (Jess, 2026-10-05). The palace staff's and the condominium head's trees have one
    // errand for a resource their attached houses are short of, with the resource (coal) baked into four steps:
    //   AttachedHousingMissingResource(coal) -> CanFindStorageWithResource(coal) -> ...BeingDelivered(coal) -> pickup, carry, stash
    // When no house is short of coal, the same steps are pointed at garments for one trip, so the worker reserves
    // garments at a store, walks there, picks them up and carries them to the house, exactly as they do coal.
    [HarmonyPatch]
    internal static class GarmentErrand
    {
        private const string Key = "garment";
        private static ResourceData coalData;   // looked up once; the game's lookup walks the resource list

        [HarmonyPatch(typeof(AttachedHousingMissingResource), nameof(AttachedHousingMissingResource.OnUpdate)), HarmonyPostfix]
        private static void AfterCheck(AttachedHousingMissingResource __instance, ref TaskStatus __result)
        {
            try
            {
                if (!Plugin.GarmentDemand.Value || !Plugin.GarmentErrands.Value) return;
                var garment = Tailor.Garment;
                var coal = __instance.resourceData != null && __instance.resourceData.key != Key ? __instance.resourceData : (coalData != null ? coalData : coalData = DataManager.Instance.GetResourceDataFromKey("coal"));
                if (garment == null || coal == null) return;
                if (__result == TaskStatus.Success && Food.CoalOnOffer()) { Point(__instance, coal, 0); return; }   // the game's own errand (coal) runs; keep the steps on it
                // a house short of coal with no coal anywhere to fetch (2026-10-24): the coal run cannot be served, so it does not
                // stand in the garments' way; with no garment house to serve the steps stay on coal and the game's step fails as it would
                if (Food.Hungry(__instance)) { Point(__instance, coal, 0); return; }        // a hungry carrier eats first (2026-10-12)
                var hub = __instance.workPlace != null && __instance.workPlace.Value != null ? __instance.workPlace.Value.GetComponent<Hub>() : null;
                if (hub == null) return;
                if (Food.AnyOnPenalty(hub)) { Point(__instance, coal, 0); return; }   // a variety run for a penalised house comes first (Jess, 2026-10-10)
                int share = Garments.Share();
                foreach (var a in hub.activeAttachments)
                {
                    if (a == null || a.gridObject == null || a.gridObject.wo == null || a.gridObject.wo.data.isPerimeter) continue;
                    if (a.gridObject.constructable == null || !a.gridObject.constructable.isConstructionComplete) continue;
                    var h = a.GetComponent<Housing>();
                    if (h == null || !Garments.Wants(h, share)) continue;
                    var storage = h.GetStorage();
                    var slot = storage != null ? storage.GetResourceCapacity(Key) : null;
                    if (slot == null) continue;
                    int stored = storage.GetResourceStored(Key), cap = slot.resourceAmountData.amount;
                    if (stored * 2 >= cap || h.IsResourceBeingDelivered(Key)) continue;
                    int want = Mathf.Clamp(cap - stored, 1, Plugin.GarmentCarryPerTrip.Value);
                    Point(__instance, garment, want);
                    __instance.result.SetValue(h.gameObject);
                    __result = TaskStatus.Success;
                    return;
                }
                Point(__instance, coal, 0);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: garment errand: " + ex.Message); }
        }

        // Point the errand's other steps in this worker's tree at the resource: the search, the two delivery marks.
        private static void Point(AttachedHousingMissingResource check, ResourceData resource, int amount)
        {
            var owner = check.Owner;
            if (owner == null) return;
            foreach (var t in owner.FindTasks<CanFindStorageWithResource>())
                if (t.origin != null && t.origin.Name == "housing") { t.resourceData = resource; if (amount > 0) t.minAmount = amount; else if (resource.key != Key) t.minAmount = check.amount; }
            foreach (var t in owner.FindTasks<AddToResourcesBeingDelivered>())
                if (t.housing != null && t.housing.Name == "housing") t.resourceData = resource;
            foreach (var t in owner.FindTasks<ResetIsResourceBeingDeliveredOnEnd>())
                if (t.housing != null && t.housing.Name == "housing") t.resourceData = resource;
        }
    }
}
