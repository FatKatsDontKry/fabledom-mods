using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FabledomLetters
{
    // The mod's state is saved under its own key in the save slot ("fabLetters"), which the base game
    // never reads. Anything the mod adds to the game's own lists (its letter, buff, notification) is
    // taken out before the game writes its data and put back afterwards, so a save stays loadable
    // without the mod; the mod re-creates those from its own state on load.
    [HarmonyPatch]
    internal static class SaveLoad
    {
        private const string SaveKey = "fabLetters";

        private static List<KeyValue<string, int>> strippedBuffs;

        // --- saving -----------------------------------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPrefix]
        private static void BeforeGameDataSave(DataManager __instance)
        {
            if (SaveMaster.GetActiveSlot() < 0) return;
            var gd = __instance.gameData;

            strippedBuffs = gd.activeBuffsNew.Where(b => b.key == Keys.ExchangeBuff).ToList();
            Letters.S.ExchangeDaysLeft = strippedBuffs.Count > 0 ? strippedBuffs[0].value : 0;
            gd.activeBuffsNew.RemoveAll(b => b.key == Keys.ExchangeBuff);

            SaveMaster.SetString(SaveKey, JsonConvert.SerializeObject(Letters.S));
        }

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPostfix]
        private static void AfterGameDataSave(DataManager __instance)
        {
            var gd = __instance.gameData;
            if (strippedBuffs != null) gd.activeBuffsNew.AddRange(strippedBuffs);
            strippedBuffs = null;
        }

        // The mod's own letters are removed from the letter manager's saved lists; PendingLetters re-sends them on load.
        [HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.OnSave)), HarmonyPostfix]
        private static void AfterLetterSave(ref string __result)
        {
            try
            {
                var o = JObject.Parse(__result);
                foreach (var listName in new[] { "savedActiveWorldEvents", "savedHandeledWorldEvents" })
                {
                    if (o[listName] is JArray arr)
                        foreach (var item in arr.Where(t => Keys.Custom.Contains((string)t["key"])).ToList())
                            item.Remove();
                }
                __result = o.ToString(Formatting.None);
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Couldn't clean letter save: " + ex.Message); }
        }

        // --- loading ----------------------------------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Load"), HarmonyPostfix]
        private static void AfterGameDataLoad(int slot)
        {
            if (slot < 0) return;
            string json = SaveMaster.GetString(SaveKey);
            if (string.IsNullOrEmpty(json))
            {
                Letters.S = new LetterState();
                Letters.NeedsMigration = true;
                Plugin.Log.LogInfo("[letters] No Fabledom Letters data in this save; it will be worked out from the letters already answered.");
                return;
            }
            try
            {
                Letters.S = JsonConvert.DeserializeObject<LetterState>(json) ?? new LetterState();
                Letters.NeedsMigration = false;
                Plugin.Log.LogInfo("[letters] Loaded: " + json);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[letters] Couldn't read saved letter data, starting over: " + ex.Message);
                Letters.S = new LetterState();
                Letters.NeedsMigration = true;
            }
        }

        // A new game (the letter manager starts without having loaded a save): start from a clean state,
        // so nothing carries over from a save loaded earlier in the same session.
        [HarmonyPatch(typeof(WorldEventManager), "Start"), HarmonyPrefix]
        private static void BeforeLetterManagerStart(WorldEventManager __instance)
        {
            if (Traverse.Create(__instance).Field("isLoaded").GetValue<bool>()) return;
            Letters.S = new LetterState();
            Letters.NeedsMigration = false;
            Plugin.Log.LogInfo("[letters] New game: letter state reset.");
        }

        // After everything has loaded: put back the buff and the mod's waiting letters.
        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoadComplete(DataManager __instance)
        {
            __instance.OnLoadComplete += AfterLoadComplete;
        }

        // The game shows slots for every live letter a few frames after loading; re-sending ours before that
        // would give them two slots, so wait until the game has drawn its own first.
        private static void AfterLoadComplete() => Plugin.Instance.StartCoroutine(AfterLoadSettled());

        private static System.Collections.IEnumerator AfterLoadSettled()
        {
            for (int i = 0; i < 20; i++) yield return null;
            try
            {
                var dm = DataManager.Instance;
                if (Letters.S.ExchangeDaysLeft > 0 && !dm.IsBuffActivated(Keys.ExchangeBuff))
                {
                    dm.ActivateBuff(Keys.ExchangeBuff);
                    int i = dm.gameData.activeBuffsNew.FindIndex(b => b.key == Keys.ExchangeBuff);
                    if (i >= 0) dm.gameData.activeBuffsNew[i] = new KeyValue<string, int>(Keys.ExchangeBuff, Letters.S.ExchangeDaysLeft);
                }
                foreach (var key in Letters.S.PendingLetters.ToList())
                    if (!Letters.IsWaiting(key)) WorldEventManager.Instance.TriggerEvent(key, resetDailyCounter: false);

                var scale = WorldMapGameplayManager.Instance?.diplomacyConfig?.populationMilestoneScalingEffects;
                if (scale != null)
                    Plugin.Log.LogInfo("[letters] Milestone price multipliers: " + string.Join(", ", scale.Select(kv => kv.Key + "=" + kv.Value)));
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[letters] After-load step failed: " + ex); }
        }
    }
}
