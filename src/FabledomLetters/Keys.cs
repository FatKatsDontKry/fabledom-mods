using System.Collections.Generic;

namespace FabledomLetters
{
    internal static class Keys
    {
        public const string Gift = "bigSistersGift";
        public const string Invest = "investInFamily";
        public const string Move = "familyMove";
        public const string Card = "familyCreditCard";
        public const string Protection = "familyProtection";
        public const string Cure = "findingYourSisterCure";

        public const string Q1 = "buildingNewHouses";
        public const string Q2 = "buildingNewStructures";
        public const string Q3 = "buildingNewShops";
        public const string Q4 = "buyingNewTerritory";

        // Added by the mod.
        public const string Demand = "fabParentsDemand";
        public const string Thanks = "fabSisterThanks";
        public const string ExchangeBuff = "fabNobleExchange";

        public static readonly HashSet<string> Custom = new HashSet<string> { Demand, Thanks };
        public static readonly HashSet<string> FamilyLane = new HashSet<string> { Gift, Invest, Move, Card, Protection, Cure, Demand };
        public static readonly HashSet<string> SisterLane = new HashSet<string> { Q1, Q2, Q3, Q4, Thanks };
        public static readonly HashSet<string> SisterQuests = new HashSet<string> { Q1, Q2, Q3, Q4 };
        public static readonly HashSet<string> All = new HashSet<string> { Gift, Invest, Move, Card, Protection, Cure, Demand, Q1, Q2, Q3, Q4, Thanks };

        // Sister quest letter key -> the quest it starts (read from the game data at startup).
        public static readonly Dictionary<string, string> QuestOf = new Dictionary<string, string>();
    }
}
