using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace FabledomLetters
{
    // First load of a save without Fabledom Letters data: work out the flags from the letters
    // already answered, and clear the old permanent series locks.
    internal static class Migration
    {
        private static readonly AccessTools.FieldRef<WorldEventManager, List<string>> ActiveSeries =
            AccessTools.FieldRefAccess<WorldEventManager, List<string>>("activeSeries");
        private static readonly AccessTools.FieldRef<WorldEventManager, List<SaveableWorldEventSeries>> HandledSeries =
            AccessTools.FieldRefAccess<WorldEventManager, List<SaveableWorldEventSeries>>("handeledSeries");

        internal static void Run()
        {
            var wem = WorldEventManager.Instance;
            var s = Letters.S;
            var handled = Letters.HandledEvents(wem);
            int today = Letters.Today;

            // Base-game option order (before the mod added options).
            List<int> Answers(string key) => handled.Where(e => e.key == key && e.actionTakenIndex >= 0).Select(e => e.actionTakenIndex).ToList();

            if (Answers(Keys.Gift).Count > 0) s.Gift = GiftOutcome.Accepted;               // base gift had only Accept
            var invest = Answers(Keys.Invest);
            if (invest.Contains(0)) s.InvestDone = true;
            else if (invest.Contains(1)) s.InvestAvailableDay = today + Plugin.InvestCooldown.Value;
            var move = Answers(Keys.Move);
            if (move.Contains(0) || move.Contains(1)) s.MoveDone = true;
            else if (move.Contains(2)) s.MoveAvailableDay = today + Plugin.MoveCooldown.Value;
            if (Answers(Keys.Card).Count > 0) s.CardDone = true;
            if (Answers(Keys.Protection).Any(i => i <= 2)) s.ProtectionDone = true;
            var cure = Answers(Keys.Cure);
            if (cure.Count > 0)
            {
                s.Cure = cure.Contains(0) ? CureOutcome.Helped : CureOutcome.Declined;
                if (s.Cure == CureOutcome.Declined) s.DemandAvailableDay = today + Plugin.DemandDays.Value;
                else s.ThanksAvailableDay = today + Plugin.ThanksDays.Value;
            }

            // Sister quests: accepted ones are done unless still running or failed.
            foreach (var letter in Keys.SisterQuests)
            {
                var answers = Answers(letter);
                if (answers.Count == 0) continue;
                if (!answers.Contains(0)) { s.QuestAvailableDay[letter] = today + Plugin.QuestCooldown.Value; continue; }
                if (!Keys.QuestOf.TryGetValue(letter, out var q)) continue;
                var sq = QuestManager.Instance.GetSaveableQuestData(q);
                if (sq == null || sq.isCompleted) s.QuestsCompleted.Add(letter);           // completed (claimed records are removed)
                else if (sq.IsExpired()) s.QuestAvailableDay[letter] = today + Plugin.QuestCooldown.Value;   // failed
                // else: still in progress
            }
            if (s.QuestsCompleted.Contains(Keys.Q2) || s.QuestsCompleted.Contains(Keys.Q3) || s.QuestsCompleted.Contains(Keys.Q4))
                if (!s.QuestsCompleted.Contains(Keys.Q1)) s.QuestsCompleted.Add(Keys.Q1);

            Gifts.CheckGiftMissed();

            // Old permanent locks no longer apply.
            int removed = HandledSeries(wem).RemoveAll(x => x.seriesKey == "royalFamily" || x.seriesKey == "bigSisterQuest");
            ActiveSeries(wem).RemoveAll(x => x == "royalFamily" || x == "bigSisterQuest");

            Plugin.Log.LogInfo($"[letters] Existing save converted ({removed} old series locks cleared): " + Newtonsoft.Json.JsonConvert.SerializeObject(s));
        }
    }
}
