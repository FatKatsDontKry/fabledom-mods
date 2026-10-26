using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FabledomTailor
{
    // Wool and garments in the upgrade costs (Jess, 2026-10-16; fabledom-mod-new-rulers.md, "Upgrade cost pools").
    // The Harvest Guild, the Tinkers Guild and the Carousel each draw their next upgrade's cost from the unlocked,
    // unhidden resources: the Harvest Guild from food (plus tulips), the Tinkers Guild from produced industry goods,
    // the Carousel from almost anything. Once Luxuries unhides wool and garments, the game would put both into the
    // Tinkers pool from the first upgrade. Jess: wool is a raw agricultural good and belongs to the Harvest Guild;
    // garments are a luxury and join the Tinkers Guild and the Carousel only from the 5th upgrade. The three cost
    // methods are replaced with the same logic plus those rules (the game's are a shuffle, a filter and the first
    // three or five).
    [HarmonyPatch]
    internal static class UpgradePools
    {
        private static bool On => Plugin.UpgradePoolsOn.Value && Tailor.Garment != null;

        private static List<ResourceData> Candidates(System.Func<ResourceData, bool> keep)
        {
            var list = new List<ResourceData>(DataManager.Instance.resourceSoData);
            list.Shuffle();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var r = list[i];
                if (r == null || !DataManager.Instance.IsResourceUnlocked(r.key) || r.hideFromGame || !keep(r)) list.RemoveAt(i);
            }
            return list;
        }

        private static bool GarmentsAllowed(int upgradesDone) => upgradesDone + 1 >= Plugin.GarmentsFromUpgrade.Value;

        [HarmonyPatch(typeof(HarvestGuild), nameof(HarvestGuild.UpdateCosts)), HarmonyPrefix]
        private static bool Harvest()
        {
            if (!On) return true;
            try
            {
                var gd = DataManager.Instance.gameData;
                gd.harvestGuildCost.Clear();
                var list = Candidates(r => (r.resourceGroup == ResourceGroup.FOOD || r.key == "tulip" || r.key == "wool") && r.key != "homegrown");
                int n = Mathf.Clamp(list.Count, 0, 3);
                for (int i = 0; i < n; i++) gd.harvestGuildCost.Add(new SaveableResourceAmountData(list[i].key, Mathf.CeilToInt(gd.harvestGuildCurrentCost / list[i].tradeValue)));
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: harvest guild costs: " + ex.Message); return true; }
        }

        [HarmonyPatch(typeof(TinkersGuild), nameof(TinkersGuild.UpdateCosts)), HarmonyPrefix]
        private static bool Tinkers()
        {
            if (!On) return true;
            try
            {
                var gd = DataManager.Instance.gameData;
                gd.tinkersGuildCost.Clear();
                bool garments = GarmentsAllowed(gd.tinkersGuildBuffCount);
                var list = Candidates(r => r.resourceGroup != ResourceGroup.FOOD && !r.isCrop && r.isProduced && r.key != "wool" && (garments || r.key != "garment"));
                int n = Mathf.Clamp(list.Count, 0, 3);
                for (int i = 0; i < n; i++) gd.tinkersGuildCost.Add(new SaveableResourceAmountData(list[i].key, Mathf.CeilToInt(gd.tinkersGuildCurrentCost / list[i].tradeValue)));
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: tinkers guild costs: " + ex.Message); return true; }
        }

        [HarmonyPatch(typeof(Carousel), nameof(Carousel.UpdateCosts)), HarmonyPrefix]
        private static bool CarouselCosts()
        {
            if (!On) return true;
            try
            {
                var gd = DataManager.Instance.gameData;
                gd.carouselCost.Clear();
                bool garments = GarmentsAllowed(gd.carouselBuffCount - 1);   // the count starts at 1: the buff about to be bought
                var list = Candidates(r => r.key != "homegrown" && r.key != "coin" && r.key != "tulip" && r.key != "feastSupplies" && (garments || r.key != "garment"));
                int n = Mathf.Clamp(list.Count, 0, 5);
                for (int i = 0; i < n; i++) gd.carouselCost.Add(new SaveableResourceAmountData(list[i].key, Mathf.CeilToInt(gd.carouselBuffCurrentCost / list[i].tradeValue)));
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: carousel costs: " + ex.Message); return true; }
        }
    }
}
