using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FabledomLetters
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "fab.fabledomletters";
        public const string Name = "Fabledom Letters";
        public const string Version = "0.2.1";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<int> InvestCooldown, MoveCooldown, QuestCooldown;
        internal static ConfigEntry<int> CardMaxCoin;
        internal static ConfigEntry<int> InvestBCoin, ExchangeHappiness, ExchangeDays;
        internal static ConfigEntry<int> DemandDays, DemandNobility, DemandFamilyAid;
        internal static ConfigEntry<int> ThanksDays, ThankYouTulips, ThankYouBase;
        internal static ConfigEntry<int> BadBonusCalm, BadBonusClassic, BadBonusChallenge, BadBonusCruel;
        internal static ConfigEntry<int> LateMinDays, LateMaxDays, BeggarCooldown, SlowFromPopulation;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            var enabled = Config.Bind("General", "Enabled", true,
                "Replace the royal family and big sister letter series with the Fabledom Letters rules.");

            InvestCooldown = Config.Bind("Cooldowns", "InvestInFamilyDays", 90, "Days before Invest in family can come again after it is declined.");
            MoveCooldown = Config.Bind("Cooldowns", "FamilyMoveDays", 90, "Days before Family move can come again after it is declined.");
            QuestCooldown = Config.Bind("Cooldowns", "SisterQuestDays", 120, "Days before a sister quest letter comes back after it is declined or the quest fails.");

            CardMaxCoin = Config.Bind("Letters", "CreditCardMaxCoin", 150, "Family credit card only comes while you hold this much coin or less.");
            InvestBCoin = Config.Bind("Letters", "InvestExchangeCoin", 200, "Invest in family, second option: coin paid (not scaled).");
            ExchangeHappiness = Config.Bind("Letters", "InvestExchangeHappiness", 10, "Invest in family, second option: happiness lost while the noble youth is away.");
            ExchangeDays = Config.Bind("Letters", "InvestExchangeDays", 90, "Invest in family, second option: days the noble youth is away.");

            DemandDays = Config.Bind("Parents demand", "Days", 240, "After declining Finding your sister's cure, the parents' demand letter can come again this many days after the last one (it joins the parents' letter draw).");
            DemandNobility = Config.Bind("Parents demand", "NobilityCost", 20, "Nobility paid to settle the demand.");
            DemandFamilyAid = Config.Bind("Parents demand", "FamilyAidIncrease", 20, "Permanent family aid increase if you don't pay.");

            ThanksDays = Config.Bind("Sister thank-you", "Days", 240, "After helping find her cure, your sister's thank-you letter can come again this many days after the last one (it joins the sister's letter draw).");
            ThankYouTulips = Config.Bind("Sister thank-you", "Tulips", 50, "Tulips in each thank-you.");
            ThankYouBase = Config.Bind("Sister thank-you", "ResourceBase", 40, "Base amount of the random resource, multiplied by the game's milestone price multiplier.");

            BadBonusCalm = Config.Bind("Letter pacing", "BadBonusCalm", 4, "How much the chance of a bad letter grows with each letter that isn't bad (Calm).");
            BadBonusClassic = Config.Bind("Letter pacing", "BadBonusClassic", 10, "Same, Classic.");
            BadBonusChallenge = Config.Bind("Letter pacing", "BadBonusChallenge", 13, "Same, Challenge.");
            BadBonusCruel = Config.Bind("Letter pacing", "BadBonusCruel", 20, "Same, Cruel (the game's own value).");
            LateMinDays = Config.Bind("Letter pacing", "LateGameMinDays", 23, "Shortest gap between letters once all quest letters are used up (the game's is 14).");
            LateMaxDays = Config.Bind("Letter pacing", "LateGameMaxDays", 30, "Longest gap between letters once all quest letters are used up (the game's is 21). The gap slides between the two as quests run out.");
            SlowFromPopulation = Config.Bind("Letter pacing", "SlowdownFromPopulation", 250, "Letters keep the game's 14-21 day gap until the population reaches this; from then on the gap follows how many quest letters are used up.");
            BeggarCooldown = Config.Bind("Letter pacing", "BeggarSharedCooldownDays", 150, "After any beggar letter, no beggar letter for this many days (the game's is 90).");

            if (!enabled.Value)
            {
                Log.LogInfo("Fabledom Letters is disabled in the config.");
                return;
            }
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("Fabledom Letters loaded.");
        }
    }
}
