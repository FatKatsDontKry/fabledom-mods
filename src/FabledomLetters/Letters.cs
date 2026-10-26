using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomLetters
{
    // The rules: which family/sister letter may be drawn, and what each answer unlocks.
    [HarmonyPatch]
    internal static class Letters
    {
        internal static LetterState S = new LetterState();
        internal static bool NeedsMigration;

        private static readonly AccessTools.FieldRef<WorldEventManager, List<SaveableWorldEventData>> ActiveEvents =
            AccessTools.FieldRefAccess<WorldEventManager, List<SaveableWorldEventData>>("activeWorldEvents");
        internal static readonly AccessTools.FieldRef<WorldEventManager, List<SaveableWorldEventData>> HandledEvents =
            AccessTools.FieldRefAccess<WorldEventManager, List<SaveableWorldEventData>>("handeledWorldEvents");

        internal static int Today => DateTimeManager.Instance.GetTotalDaysTranspired();
        internal static PopulationMilestone Milestone => KingdomManager.Instance.GetCurrentPopulationMilestone();

        internal static bool IsWaiting(string key) =>
            WorldEventManager.Instance != null && ActiveEvents(WorldEventManager.Instance).Any(e => e.key == key);

        internal static int WaitingCount() =>
            WorldEventManager.Instance == null ? 0 : ActiveEvents(WorldEventManager.Instance).Count;

        private static bool AnyWaiting(HashSet<string> keys) =>
            WorldEventManager.Instance != null && ActiveEvents(WorldEventManager.Instance).Any(e => keys.Contains(e.key));

        private static bool SisterQuestRunning() =>
            Keys.QuestOf.Values.Any(q =>
            {
                var sq = QuestManager.Instance?.GetSaveableQuestData(q);
                return sq != null && sq.IsActive() && !sq.IsExpired();
            });

        private static bool Done(string questLetter) => S.QuestsCompleted.Contains(questLetter);

        private static bool CooledDown(string letter) =>
            !S.QuestAvailableDay.TryGetValue(letter, out var day) || Today >= day;

        // True if the mod's rules allow this letter right now. The game's own conditions
        // (milestone, nobility, coin, buildings, land) are still checked afterwards.
        internal static bool Allowed(string key)
        {
            if (IsWaiting(key)) return false;
            if (Keys.FamilyLane.Contains(key) && AnyWaiting(Keys.FamilyLane)) return false;
            if (Keys.SisterLane.Contains(key))
            {
                if (AnyWaiting(Keys.SisterLane) || SisterQuestRunning()) return false;   // one sister letter or quest at a time
                if (S.Cure == CureOutcome.Declined) return false;
                if (IsWaiting(Keys.Cure)) return false;
                if (S.Cure == CureOutcome.Helped && DataManager.Instance.IsBuffActivated("globalSickness")) return false;
            }

            switch (key)
            {
                case Keys.Gift:
                    return S.Gift == GiftOutcome.None;
                case Keys.Invest:
                    return !S.InvestDone && (S.Gift == GiftOutcome.Declined || S.Gift == GiftOutcome.Missed) && Today >= S.InvestAvailableDay;
                case Keys.Move:
                    return S.InvestDone && !S.MoveDone && Today >= S.MoveAvailableDay;
                case Keys.Card:
                    return !S.CardDone && (S.Gift == GiftOutcome.Accepted || S.Gift == GiftOutcome.Missed);
                case Keys.Protection:
                    return S.CardDone && !S.ProtectionDone;
                case Keys.Cure:
                    return S.MoveDone && S.Cure == CureOutcome.None && !AnyWaiting(Keys.SisterLane);
                case Keys.Q1:
                    return !Done(Keys.Q1) && CooledDown(Keys.Q1);
                case Keys.Q2:
                case Keys.Q3:
                    return Done(Keys.Q1) && !Done(key) && CooledDown(key);
                case Keys.Q4:
                    return (Done(Keys.Q2) || Done(Keys.Q3)) && !Done(Keys.Q4) && CooledDown(Keys.Q4);
                case Keys.Demand:
                    return S.Cure == CureOutcome.Declined && Today >= S.DemandAvailableDay;
                case Keys.Thanks:
                    return S.Cure == CureOutcome.Helped && Today >= S.ThanksAvailableDay;
            }
            return true;
        }

        // --- the draw ---------------------------------------------------------------------------

        [HarmonyPatch(typeof(WorldEventManager), "IsEventUnavailable",
            new[] { typeof(List<SaveableWorldEventData>), typeof(WorldEventData) }), HarmonyPrefix]
        private static bool BeforeAvailabilityCheck(ref List<SaveableWorldEventData> transpiredEvents, WorldEventData eventToCheck, ref bool __result)
        {
            if (eventToCheck == null || !Keys.All.Contains(eventToCheck.key)) return true;
            if (!Allowed(eventToCheck.key))
            {
                __result = true;
                return false;
            }
            // Answered letters normally never come back; for ours the rules above decide that.
            transpiredEvents = transpiredEvents.Where(e => !Keys.All.Contains(e.key)).ToList();
            return true;
        }

        // --- answers ------------------------------------------------------------------------------

        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPrefix]
        private static void BeforeAnswer(SaveableWorldEventData saveableWorldEvent, List<WorldEventActionData> actionDatas, int actionIndex)
        {
            // A sister quest accepted again after failing: clear the old expired record so it restarts cleanly.
            var a = actionDatas[actionIndex];
            if (Keys.SisterQuests.Contains(saveableWorldEvent.key) && a.startsQuest && a.quest != null)
            {
                var old = QuestManager.Instance.GetSaveableQuestData(a.quest.key);
                if (old != null && !old.IsActive()) QuestManager.Instance.RemoveQuest(a.quest.key);
            }
        }

        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPostfix]
        private static void AfterAnswer(SaveableWorldEventData saveableWorldEvent, List<WorldEventActionData> actionDatas, int actionIndex)
        {
            string key = saveableWorldEvent.key;
            if (!Keys.All.Contains(key)) return;
            var a = actionDatas[actionIndex];
            if (a.worldEventAction == WorldEventAction.POSTPONE) return;
            bool decline = a.worldEventAction == WorldEventAction.DECLINE;
            S.PendingLetters.Remove(key);

            switch (key)
            {
                case Keys.Gift:
                    S.Gift = decline ? GiftOutcome.Declined : GiftOutcome.Accepted;
                    break;
                case Keys.Invest:
                    if (decline) S.InvestAvailableDay = Today + Plugin.InvestCooldown.Value;
                    else S.InvestDone = true;
                    break;
                case Keys.Move:
                    if (decline) S.MoveAvailableDay = Today + Plugin.MoveCooldown.Value;
                    else S.MoveDone = true;
                    break;
                case Keys.Card:
                    S.CardDone = true;
                    break;
                case Keys.Protection:
                    S.ProtectionDone = true;
                    break;
                case Keys.Cure:
                    S.Cure = decline ? CureOutcome.Declined : CureOutcome.Helped;
                    if (decline) S.DemandAvailableDay = Today + Plugin.DemandDays.Value;
                    else S.ThanksAvailableDay = Today + Plugin.ThanksDays.Value;
                    break;
                case Keys.Demand:
                    S.DemandAvailableDay = Today + Plugin.DemandDays.Value;
                    break;
                case Keys.Thanks:
                    Gifts.DeliverThanks();
                    S.ThanksAvailableDay = Today + Plugin.ThanksDays.Value;
                    break;
                default:   // sister quest letters
                    if (decline) S.QuestAvailableDay[key] = Today + Plugin.QuestCooldown.Value;
                    break;
            }
            Plugin.Log.LogInfo($"[letters] Answered {key} (option {actionIndex + 1}{(decline ? ", decline" : "")}).");
        }

        // The mod's own letters are kept out of the game's save, so remember which are waiting.
        [HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.TriggerEvent)), HarmonyPrefix]
        private static void BeforeLetterSent(string key)
        {
            if (key == Keys.Thanks && S.ThanksResource == null) Gifts.PickThanksResource();
            if (Keys.Custom.Contains(key) && !S.PendingLetters.Contains(key)) S.PendingLetters.Add(key);
        }

        // --- quests -------------------------------------------------------------------------------

        private static string LetterForQuest(string questKey) =>
            Keys.QuestOf.FirstOrDefault(kv => kv.Value == questKey).Key;

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.CompleteQuest)), HarmonyPostfix]
        private static void AfterQuestCompleted(string key)
        {
            var letter = LetterForQuest(key);
            if (letter == null || S.QuestsCompleted.Contains(letter)) return;
            S.QuestsCompleted.Add(letter);
            Plugin.Log.LogInfo($"[letters] Sister quest completed: {letter}.");
        }

        // A quest fails when its deadline counts down to 0 (the game handles that only in the UI).
        private static readonly List<string> DeadlineWatch = new List<string>();

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.UpdateDeadlines)), HarmonyPrefix]
        private static void BeforeDeadlines()
        {
            DeadlineWatch.Clear();
            foreach (var q in Keys.QuestOf.Values)
            {
                var sq = QuestManager.Instance.GetSaveableQuestData(q);
                if (sq != null && !sq.isCompleted && sq.deadline > 0) DeadlineWatch.Add(q);
            }
        }

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.UpdateDeadlines)), HarmonyPostfix]
        private static void AfterDeadlines()
        {
            foreach (var q in DeadlineWatch)
            {
                var sq = QuestManager.Instance.GetSaveableQuestData(q);
                if (sq != null && !sq.isCompleted && sq.deadline == 0) QuestFailed(q);
            }
        }

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.FailQuest)), HarmonyPostfix]
        private static void AfterFailQuest(string questKey) => QuestFailed(questKey);

        private static void QuestFailed(string questKey)
        {
            var letter = LetterForQuest(questKey);
            if (letter == null) return;
            S.QuestAvailableDay[letter] = Today + Plugin.QuestCooldown.Value;
            Plugin.Log.LogInfo($"[letters] Sister quest failed: {letter}; back in {Plugin.QuestCooldown.Value} days.");
        }

        // --- noble exchange happiness (stateless: nothing extra is written into the save) -------

        [HarmonyPatch(typeof(KingdomManager), nameof(KingdomManager.GetHappinessStartValue)), HarmonyPostfix]
        private static void ExchangeHappiness(ref float __result)
        {
            if (DataManager.Instance != null && DataManager.Instance.IsBuffActivated(Keys.ExchangeBuff))
                __result = Mathf.Max(0f, __result - Plugin.ExchangeHappiness.Value);
        }
    }
}
