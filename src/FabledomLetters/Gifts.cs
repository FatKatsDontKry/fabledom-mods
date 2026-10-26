using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomLetters
{
    // The sister's thank-you gift, and the daily checks that run before the game draws a letter.
    [HarmonyPatch]
    internal static class Gifts
    {
        private static readonly string[] ThanksResources = { "plank", "stoneBrick", "coal", "iron", "grain" };

        private static int Scale()
        {
            var table = WorldMapGameplayManager.Instance?.diplomacyConfig?.populationMilestoneScalingEffects;
            return table != null && table.TryGetValue(Letters.Milestone, out var m) ? m : 1;
        }

        private static string Title(string key, int n) =>
            $"{n} {DataManager.Instance.GetResourceDataFromKey(key)?.title.GetLocalizedString() ?? key}";

        internal static void PickThanksResource()
        {
            var options = ThanksResources.Where(k => DataManager.Instance.GetResourceDataFromKey(k) != null).ToList();
            Letters.S.ThanksResource = options.Count > 0 ? options[Random.Range(0, options.Count)] : null;
        }

        internal static string ThanksDescription()
        {
            string s = Title("tulip", Plugin.ThankYouTulips.Value);
            var res = Letters.S.ThanksResource;
            if (res != null) s += " and " + Title(res, Plugin.ThankYouBase.Value * Scale());
            return s;
        }

        internal static void DeliverThanks()
        {
            int tulips = Deposit("tulip", Plugin.ThankYouTulips.Value);
            var res = Letters.S.ThanksResource;
            int amount = res != null ? Deposit(res, Plugin.ThankYouBase.Value * Scale()) : 0;
            Plugin.Log.LogInfo($"[letters] Sister's thank-you delivered: {tulips} tulip, {amount} {res} (×{Scale()}).");
            Letters.S.ThanksResource = null;
        }

        // Puts goods into storages with room; returns how much fit.
        private static int Deposit(string key, int amount)
        {
            int left = amount;
            foreach (var st in Storage.GetStoragesWithRoom(key, 1))
            {
                if (left <= 0) break;
                int n = Mathf.Min(left, st.GetRemainingCapacity(key));
                if (n <= 0) continue;
                st.Store(key, n, alterGlobalStorage: true);
                left -= n;
            }
            if (left > 0) Plugin.Log.LogWarning($"[letters] No room for {left} {key} from the thank-you gift.");
            return amount - left;
        }

        // --- daily -----------------------------------------------------------------------------

        [HarmonyPatch(typeof(WorldEventManager), "HandleStandardWorldEventsOnDayChange"), HarmonyPrefix]
        private static void BeforeDay()
        {
            if (GameManager.Instance.IsSandboxMode(isCreativeMode: true)) return;
            try
            {
                if (Letters.NeedsMigration) { Migration.Run(); Letters.NeedsMigration = false; }
                CheckGiftMissed();
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[letters] Daily step failed: " + ex); }
        }

        // Big sister's gift only comes at Small village; if the draw never picked it, it was missed.
        internal static void CheckGiftMissed()
        {
            var s = Letters.S;
            if (s.Gift == GiftOutcome.None && Letters.Milestone > PopulationMilestone.SMALL_VILLAGE && !Letters.IsWaiting(Keys.Gift))
            {
                s.Gift = GiftOutcome.Missed;
                Plugin.Log.LogInfo("[letters] Big sister's gift never came; both the Invest and the credit card paths stay open.");
            }
        }
    }
}
