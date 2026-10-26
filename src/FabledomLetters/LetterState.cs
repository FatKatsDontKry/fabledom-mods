using System.Collections.Generic;

namespace FabledomLetters
{
    internal enum GiftOutcome { None, Accepted, Declined, Missed }
    internal enum CureOutcome { None, Helped, Declined }

    // Everything the mod remembers per save. Stored in the save slot under "fabLetters",
    // which the base game never reads.
    internal class LetterState
    {
        public int Version = 2;

        public GiftOutcome Gift = GiftOutcome.None;

        public bool InvestDone;
        public int InvestAvailableDay;      // total-days value; 0 = no wait

        public bool MoveDone;
        public int MoveAvailableDay;

        public bool CardDone;
        public bool ProtectionDone;

        public CureOutcome Cure = CureOutcome.None;
        public int DemandAvailableDay;      // parents' demand (after declining the cure)
        public int ThanksAvailableDay;      // sister's thank-you (after helping)
        public string ThanksResource;       // resource picked for the thank-you letter now waiting

        public List<string> QuestsCompleted = new List<string>();
        public Dictionary<string, int> QuestAvailableDay = new Dictionary<string, int>();

        // The mod's own letters that are waiting; they are kept out of the game's save and re-sent on load.
        public List<string> PendingLetters = new List<string>();

        public int ExchangeDaysLeft;        // noble exchange buff, kept here instead of the game's buff list

        public int QuestPoolAtThreshold;    // quest letters still available when the population first reached the slowdown threshold
    }
}
