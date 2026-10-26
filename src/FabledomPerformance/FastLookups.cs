using System.Collections.Generic;
using HarmonyLib;
using Nielsen;

namespace FabledomPerformance
{
    // The storage searches every hauler, head of household, shopkeeper and production worker runs (CanFindStorageWith
    // MissingResource / WithEdible / WithResource) walk every retrievable storage in the town and, for each, ask a few
    // small questions: how much of this key is here, is it retrievable, what resource is this key. In the game each
    // of those is a LINQ FirstOrDefault with a fresh closure, and the resource lookup is a linear scan of the resource
    // list. At 2,200 fablings the three searches cost ~17 ms a frame (profile 2026-10-12 11:50: 0.9 ms, 5.1 ms and
    // 1.3 ms a call) and spike to 280 ms when a new building's materials are fetched.
    //
    // Same answers, cheaper: a dictionary for the resource lookup (rebuilt if the list changes) and plain loops for
    // the storage questions. No allocation, no gameplay change.
    [HarmonyPatch]
    internal static class FastLookups
    {
        // --- DataManager.GetResourceDataFromKey ---------------------------------------------------------------------

        private static Dictionary<string, ResourceData> byKey;
        private static List<ResourceData> source;
        private static int sourceCount;

        [HarmonyPatch(typeof(DataManager), nameof(DataManager.GetResourceDataFromKey)), HarmonyPrefix]
        private static bool ResourceByKey(DataManager __instance, string key, ref ResourceData __result)
        {
            var list = __instance.resourceSoData;
            if (list == null) return true;
            if (byKey == null || !ReferenceEquals(source, list) || sourceCount != list.Count)
            {
                var d = new Dictionary<string, ResourceData>(list.Count * 2);
                foreach (var r in list)
                    if (r != null && r.key != null && !d.ContainsKey(r.key)) d[r.key] = r;   // first wins, as FirstOrDefault does
                byKey = d; source = list; sourceCount = list.Count;
            }
            __result = key != null && byKey.TryGetValue(key, out var found) ? found : null;
            return false;
        }

        // --- Storage: what is stored --------------------------------------------------------------------------------

        private static SaveableResourceAmountData Stored(List<SaveableResourceAmountData> stored, string key)
        {
            if (stored == null) return null;
            for (int i = 0; i < stored.Count; i++)
            {
                var s = stored[i];
                if (s != null && string.Equals(s.key, key)) return s;
            }
            return null;
        }

        [HarmonyPatch(typeof(Storage), nameof(Storage.GetResourceStored)), HarmonyPrefix]
        private static bool GetResourceStored(Storage __instance, string key, ref int __result)
        {
            __result = Stored(__instance.resourcesStored, key)?.amount ?? 0;
            return false;
        }

        [HarmonyPatch(typeof(Storage), nameof(Storage.GetResourceAvailable)), HarmonyPrefix]
        private static bool GetResourceAvailable(Storage __instance, string key, ref int __result)
        {
            __result = Stored(__instance.resourcesStored, key)?.GetAvailable() ?? 0;
            return false;
        }

        [HarmonyPatch(typeof(Storage), nameof(Storage.GetResourceReserved)), HarmonyPrefix]
        private static bool GetResourceReserved(Storage __instance, string key, ref int __result)
        {
            __result = Stored(__instance.resourcesStored, key)?.reserved ?? 0;
            return false;
        }

        [HarmonyPatch(typeof(Storage), nameof(Storage.HasAmountStored)), HarmonyPrefix]
        private static bool HasAmountStored(Storage __instance, string key, int amount, ref bool __result)
        {
            __result = Stored(__instance.resourcesStored, key)?.HasAvailable(amount) ?? false;
            return false;
        }

        // --- Storage: what it may hold --------------------------------------------------------------------------------

        private static StorageResource Capacity(List<StorageResource> capacities, string key)
        {
            if (capacities == null) return null;
            for (int i = 0; i < capacities.Count; i++)
            {
                var c = capacities[i];
                if (c != null && c.resourceAmountData != null && c.resourceAmountData.resourceData != null
                    && string.Equals(c.resourceAmountData.resourceData.key, key)) return c;
            }
            return null;
        }

        [HarmonyPatch(typeof(Storage), nameof(Storage.IsResourceRetrievable)), HarmonyPrefix]
        private static bool IsResourceRetrievable(Storage __instance, string key, ref bool __result)
        {
            __result = Capacity(__instance.resourcesCapacity, key)?.isRetrievable ?? false;
            return false;
        }

        [HarmonyPatch(typeof(Storage), nameof(Storage.IsResourceRetrievableAndUnlocked)), HarmonyPrefix]
        private static bool IsResourceRetrievableAndUnlocked(Storage __instance, string key, ref bool __result)
        {
            var c = Capacity(__instance.resourcesCapacity, key);
            __result = c != null && !__instance.IsResourceLocked(c.resourceAmountData.resourceData.key) && c.isRetrievable;
            return false;
        }

        // --- the global stock -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Utils_Resources), nameof(Utils_Resources.GetAvailableResourceFromKey)), HarmonyPrefix]
        private static bool GetAvailableResourceFromKey(List<SaveableResourceAmountData> resources, string key, ref SaveableResourceAmountData __result)
        {
            __result = Stored(resources, key);
            return false;
        }

        [HarmonyPatch(typeof(Utils_Resources), nameof(Utils_Resources.GetAvailableAmountFromKey)), HarmonyPrefix]
        private static bool GetAvailableAmountFromKey(List<SaveableResourceAmountData> resources, string key, ref int __result)
        {
            __result = Stored(resources, key)?.amount ?? 0;
            return false;
        }

        // The game's version throws if the key is not in the global list at all; "not there" is answered as "no" here.
        [HarmonyPatch(typeof(Utils_Resources), nameof(Utils_Resources.HasInGlobalStorage)), HarmonyPrefix]
        private static bool HasInGlobalStorage(string key, int amount, ref bool __result)
        {
            var gd = DataManager.Instance != null ? DataManager.Instance.gameData : null;
            __result = gd != null && (Stored(gd.globalResources, key)?.HasAvailable(amount) ?? false);
            return false;
        }

        [HarmonyPatch(typeof(Utils_Resources), nameof(Utils_Resources.GetGlobalAmountFromKey)), HarmonyPrefix]
        private static bool GetGlobalAmountFromKey(string key, ref int __result)
        {
            var gd = DataManager.Instance != null ? DataManager.Instance.gameData : null;
            __result = gd != null ? (Stored(gd.globalResources, key)?.amount ?? 0) : 0;
            return false;
        }
    }
}
