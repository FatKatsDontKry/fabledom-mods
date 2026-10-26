using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FabledomLetters
{
    // How often letters come and which type is drawn.
    //  - Fixes the bad-letter draw (the game's "bad" roll searched the quest list).
    //  - Bad letters' weight grows by a difficulty-based bonus each draw that isn't bad.
    //  - From 250 population, the gap between letters slides from 14-21 days to 23-30 days as quest letters run out.
    //  - Beggar letters share one 150-day cooldown.
    [HarmonyPatch]
    internal static class LetterPacing
    {
        private static readonly AccessTools.FieldRef<WorldEventManager, int> BasicBonus = AccessTools.FieldRefAccess<WorldEventManager, int>("basicBonus");
        private static readonly AccessTools.FieldRef<WorldEventManager, int> QuestBonus = AccessTools.FieldRefAccess<WorldEventManager, int>("questBonus");
        private static readonly AccessTools.FieldRef<WorldEventManager, int> BadBonus = AccessTools.FieldRefAccess<WorldEventManager, int>("badBonus");

        internal static readonly string[] Beggars =
        {
            "villageBeggars1", "smallTownBeggars1", "townBeggars1", "bigTownBeggars1",
            "smallCityBeggars1", "cityBeggars1", "bigCityBeggars1",
        };
        // In the beggars series in the game data, but not beggars: they don't share the lock.
        private static readonly string[] NotBeggars = { "helpingABeginner", "pinocchioAndPuberty" };

        internal static int BadBonusForDifficulty()
        {
            switch (DataManager.Instance?.gameData?.gameDifficulty)
            {
                case GameDifficulty.CALM: return Plugin.BadBonusCalm.Value;
                case GameDifficulty.CLASSIC: return Plugin.BadBonusClassic.Value;
                case GameDifficulty.CHALLENGE: return Plugin.BadBonusChallenge.Value;
                default: return Plugin.BadBonusCruel.Value;
            }
        }

        // --- data edits (called from DataSetup) -------------------------------------------------

        internal static void SetupBeggars(DataManager dm)
        {
            foreach (var key in Beggars)
            {
                var d = dm.worldEventSoData.FirstOrDefault(e => e.key == key);
                if (d == null) { Plugin.Log.LogWarning("[letters] Beggar letter not found: " + key); continue; }
                d.belongsToSeries = true;
                d.seriesKey = "beggars";
                d.canRepeatSeries = true;
                d.daysBetweenSeriesRepeat = Plugin.BeggarCooldown.Value;
            }
            foreach (var key in NotBeggars)
            {
                var d = dm.worldEventSoData.FirstOrDefault(e => e.key == key);
                if (d != null) d.belongsToSeries = false;
            }
        }

        // --- which type of letter -------------------------------------------------------------
        // Same as the game's PickEventBasedOnClass, except the bad roll looks for bad letters and the
        // bad weight grows by the difficulty bonus. If no letter of the rolled type is available, the
        // game's fallback (any eligible letter) is kept.

        [HarmonyPatch(typeof(WorldEventManager), "PickEventBasedOnClass"), HarmonyPrefix]
        private static bool PickType(WorldEventManager __instance, List<WorldEventData> possibleEvents, ref WorldEventData __result)
        {
            var cfg = KingdomManager.Instance.kingdomConfig;
            ref int bB = ref BasicBonus(__instance);
            ref int bQ = ref QuestBonus(__instance);
            ref int bD = ref BadBonus(__instance);
            int badStep = BadBonusForDifficulty();

            int basic = cfg.worldEventClassStartValue[WorldEventClass.BASIC] + bB;
            int quest = cfg.worldEventClassStartValue[WorldEventClass.QUEST] + bQ;
            int bad = cfg.worldEventClassStartValue[WorldEventClass.BAD] + bD;
            int roll = Random.Range(0, basic + quest + bad + 1);

            WorldEventClass type;
            if (roll <= basic)
            {
                type = WorldEventClass.BASIC;
                bB = 0; bQ += cfg.worldEventClassBonus[WorldEventClass.QUEST]; bD += badStep;
            }
            else if (roll <= basic + quest)
            {
                type = WorldEventClass.QUEST;
                bB += cfg.worldEventClassBonus[WorldEventClass.BASIC]; bQ = 0; bD += badStep;
            }
            else
            {
                type = WorldEventClass.BAD;
                bB += cfg.worldEventClassBonus[WorldEventClass.BASIC]; bQ += cfg.worldEventClassBonus[WorldEventClass.QUEST]; bD = 0;
            }

            var ofType = possibleEvents.Where(e => e.worldEventClass == type).ToList();
            __result = ofType.Count > 0
                ? ofType[Random.Range(0, ofType.Count)]
                : possibleEvents[Random.Range(0, possibleEvents.Count)];   // the game's fallback
            Plugin.Log.LogInfo($"[letters] Draw: rolled {type} ({ofType.Count} of {possibleEvents.Count} eligible) -> {__result.key}" +
                               $" (weights next draw: basic {cfg.worldEventClassStartValue[WorldEventClass.BASIC] + bB}, " +
                               $"quest {cfg.worldEventClassStartValue[WorldEventClass.QUEST] + bQ}, bad {cfg.worldEventClassStartValue[WorldEventClass.BAD] + bD}; " +
                               $"{DataManager.Instance.gameData.gameDifficulty} bad +{badStep}).");
            return false;
        }

        // When the timer has run out but nothing qualifies, the game silently retries the next day.
        private static readonly AccessTools.FieldRef<WorldEventManager, int> DaysSince = AccessTools.FieldRefAccess<WorldEventManager, int>("daysSinceLastWorldEvent");
        private static readonly AccessTools.FieldRef<WorldEventManager, int> DaysWait = AccessTools.FieldRefAccess<WorldEventManager, int>("daysWaitUnitWorldEvent");

        [HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.PickDailyWorldEvent)), HarmonyPostfix]
        private static void AfterDailyPick(WorldEventManager __instance)
        {
            int since = DaysSince(__instance), wait = DaysWait(__instance);
            if (since <= wait) return;
            int waiting = Letters.WaitingCount();
            string why = waiting > KingdomManager.Instance.kingdomConfig.maxActiveWorldEvents
                ? $"{waiting} letters are already waiting"
                : $"nothing eligible yet (milestone {KingdomManager.Instance.GetCurrentPopulationMilestone()})";
            Plugin.Log.LogInfo($"[letters] Letter due {since - wait} day(s) ago, {why}; retrying tomorrow.");
        }

        // --- how often -------------------------------------------------------------------------

        // Quest-pool letters that can still arrive at some point in this game.
        internal static int QuestsRemaining()
        {
            var wem = WorldEventManager.Instance;
            var dm = DataManager.Instance;
            if (wem == null || dm == null) return 0;
            var handled = new HashSet<string>(Letters.HandledEvents(wem).Select(e => e.key));
            var milestone = KingdomManager.Instance.GetCurrentPopulationMilestone();
            bool tutorial = dm.gameData.isUsingTutorial;

            int total = 0, gone = 0;
            foreach (var e in dm.worldEventSoData)
            {
                if (!e.isDailyEvent || e.worldEventClass != WorldEventClass.QUEST) continue;
                if (tutorial && e.preventDuringTutorial) continue;   // can never come in this game: leave out
                total++;
                bool usedUp;
                if (Keys.SisterQuests.Contains(e.key))
                    usedUp = Letters.S.QuestsCompleted.Contains(e.key) || Letters.S.Cure == CureOutcome.Declined;
                else
                    usedUp = (handled.Contains(e.key) && !e.canRepeat) ||
                             (e.hasMaxPopulation && milestone > e.maxPopulationLimit);   // grown past it
                if (usedUp) gone++;
            }
            return total - gone;
        }

        [HarmonyPatch(typeof(KingdomConfig), nameof(KingdomConfig.GetRandomDaysBetweenWorldEvents)), HarmonyPostfix]
        private static void LetterGap(KingdomConfig __instance, ref int __result)
        {
            try
            {
                // The slowdown is measured against the quest letters still available when the population
                // first reached the threshold, so it starts at zero there instead of jumping.
                int pop = KingdomManager.Instance != null ? KingdomManager.Instance.GetTotalPopulationCount() : 0;
                float f = 0f;
                if (pop >= Plugin.SlowFromPopulation.Value)
                {
                    int remaining = QuestsRemaining();
                    if (Letters.S.QuestPoolAtThreshold <= 0)
                    {
                        Letters.S.QuestPoolAtThreshold = Mathf.Max(1, remaining);
                        Plugin.Log.LogInfo($"[letters] Population reached {Plugin.SlowFromPopulation.Value}: {remaining} quest letters left; letters slow down as these run out.");
                    }
                    f = Mathf.Clamp01(1f - (float)remaining / Letters.S.QuestPoolAtThreshold);
                }
                int minEarly = __instance.worldEventInterval.x, maxEarly = __instance.worldEventInterval.y - 1;   // 14-21
                int min = Mathf.RoundToInt(Mathf.Lerp(minEarly, Plugin.LateMinDays.Value, f));
                int max = Mathf.RoundToInt(Mathf.Lerp(maxEarly, Plugin.LateMaxDays.Value, f));
                __result = Random.Range(min, max + 1);
                Plugin.Log.LogInfo($"[letters] Next letter in {__result} days (population {pop}{(pop >= Plugin.SlowFromPopulation.Value ? $", quests used up {f:P0}" : $", slowdown starts at {Plugin.SlowFromPopulation.Value}")}, range {min}-{max}).");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[letters] Letter gap failed, keeping the game's: " + ex.Message); }
        }
    }
}
