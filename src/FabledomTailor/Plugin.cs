using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FabledomTailor
{
    // Sheep, wool and the Tailor: the game's cut clothing chain, switched on.
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "fab.fabledomtailor";
        public const string Name = "Fabledom Tailor";
        public const string Version = "0.9.1";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<int> SheepFarmWood, SheepFarmPlank, TailorPlank, TailorBrick, TailorWool, TailorConstructionSpots, TailorDesirability, TailorDesirabilityRange, SheepFarmDesirability, SheepFarmDesirabilityRange;
        internal static ConfigEntry<float> WoolTradeValue, GarmentTradeValue;
        internal static ConfigEntry<bool> TailorClothingLine, TailorBanner, SheepHerdsOnNewMaps, TailorMannequins, RequireHerd;
        internal static ConfigEntry<bool> GarmentDemand, WinterRule, GarmentErrands, GarmentBackgroundCarry;
        internal static ConfigEntry<int> GarmentCarryPerTrip;
        internal static ConfigEntry<string> FeastHoliday;
        internal static ConfigEntry<string> TradeWool, TradeGarments;
        internal static ConfigEntry<bool> PenWalkable, TradeMirrorGrapes;
        internal static ConfigEntry<string> SheepLetterMode, TownHouseShelves, ShelfSwaps, QuartersShelves, TownHouseOrder, VarietyFromMilestone;
        internal static ConfigEntry<bool> ConfectioneryOn, ConfCornerEntrance, UpgradePoolsOn, IntroLettersOn;
        internal static ConfigEntry<int> IntroReminderDays;
        internal static ConfigEntry<int> GarmentsFromUpgrade;
        internal static ConfigEntry<string> ConfGround;
        internal static ConfigEntry<int> ConfectioneryUnlockPopulation, ConfWood, ConfStone, ConfPlank, ConfBrick, ConfConstructionSpots, PastryEggs, PastryFlour, PastryBatch, PastryHunger, ConfDesirability, ConfDesirabilityRange, PastryStore;
        internal static ConfigEntry<float> PastrySeconds, PastryTradeValue, SmokeX, SmokeY, SmokeZ, SmokeScale;
        internal static ConfigEntry<bool> FoodVariety, SameFoodPenalty, HeadSampler, ChainLog, MealWatch, HappinessBreakdown, FullLoads;
        internal static ConfigEntry<bool> NobleLettersOn;
        internal static ConfigEntry<int> FashionGarments, FashionPastries, WardrobeGarments, WardrobePork, WardrobeEggs, PastriesQuestAmount;
        internal static ConfigEntry<int> CondoHeads, SheepFarmUnlockPopulation, TailorUnlockPopulation, QuartersFoodTrigger, CommonerVarietyKinds, CommonerVarietyMeals;
        internal static ConfigEntry<string> NobleVariety;
        internal static ConfigEntry<float> SameFoodCommoner, SameFoodNoble;
        internal static ConfigEntry<int> SheepLetterRepeatDays, SheepLetterRepeatDaysInNeed, SheepLetterNobility;
        internal static ConfigEntry<float> TradeMirrorShare, FrontSpotX, FrontSpotZ, PenSpotBack, SheepKeepAway, SheepGap;
        internal static ConfigEntry<int> WinterGrainPerDay, FarmGrainStore, WinterFillTo, WinterRunDaysEarly, BuildGraceDays;
        internal static ConfigEntry<float> WinterRecoveryCalm, WinterRecoveryClassic, WinterRecoveryChallenge, WinterRecoveryCruel;
        internal static ConfigEntry<float> TroughX, TroughZ, TroughWidth, TroughLength;
        internal static ConfigEntry<float> ShearSwingSeconds;
        internal static ConfigEntry<int> ShearSheepDays;
        internal static ConfigEntry<int> TroughFullAt;
        internal static ConfigEntry<bool> TroughGrain;
        internal static ConfigEntry<string> GarmentDays;
        internal static ConfigEntry<int> GarmentSharePerNoble, GarmentFreeNobles, GarmentShareCap, GarmentGraceDays, GarmentNobleStepDays, GarmentNoblePenalty, GarmentNobleMaxPenalty, GarmentStaffCarry, GarmentFallbackDays, GarmentCommonerPenalty, GarmentCommonerBonus, GarmentSlotNobles, GarmentSlotCommoners;
        internal static ConfigEntry<float> GarmentStockPerNoble;
        internal static ConfigEntry<float> MannequinX, MannequinZ, MannequinShiftX, Line2X, Line2Z, Line2Yaw;
        internal static ConfigEntry<string> MannequinSprite1, MannequinSprite2, Line2Variant, ShearAnimation, TailorGround;
        internal static ConfigEntry<string> CarryLikeResource;
        internal static ConfigEntry<bool> TailorSecondLine;
        internal static ConfigEntry<int> SheepFirstMin, SheepFirstMax;
        internal static ConfigEntry<float> SheepWanderMin, SheepWanderMax, SheepCentreShiftX, PenMinX, PenMaxX, PenMinZ, PenMaxZ;
        internal static ConfigEntry<float> LineX, LineZ, LineYaw, BannerX, BannerZ, CutZ;
        internal static ConfigEntry<int> WoolGrain, WoolBatch, GarmentWool;
        internal static ConfigEntry<float> WoolSeconds, GarmentSeconds;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            SheepFarmWood = Config.Bind("Sheep farm", "CostWood", 30, "Wood to build a Sheep farm.");
            SheepFarmPlank = Config.Bind("Sheep farm", "CostPlank", 10, "Planks to build a Sheep farm.");
            TailorPlank = Config.Bind("Tailor", "CostPlank", 30, "Planks to build a Tailor.");
            TailorBrick = Config.Bind("Tailor", "CostBrick", 10, "Stone bricks to build a Tailor.");
            TailorWool = Config.Bind("Tailor", "CostWool", 10, "Wool to build a Tailor (Jess, 2026-10-12; 0 = none).");
            TailorConstructionSpots = Config.Bind("Tailor", "ConstructionSpots", 3, "Construction spots on a Tailor site (the cut data gives it 1; the game's workshops have 3), so labourers fetch in parallel and builders stack.");
            WoolTradeValue = Config.Bind("Resources", "WoolTradeValue", 0.2f, "Trade value of wool: two grain are 0.12 and the farm sits idle all winter eating grain, so wool is dearer than flour (Jess, 2026-10-07).");
            GarmentTradeValue = Config.Bind("Resources", "GarmentTradeValue", 2.0f, "Trade value of a garment: the top of a two-stage chain and the scarce good, below armament (Jess, 2026-10-07: 1.2; 2026-10-24: 2.0, since the guilds price an upgrade by trade value and at 1.2 the Tinkers Guild asked 30 garments where it asks 9 armament). Rulers charge by it too.");
            RequireHerd = Config.Bind("Sheep farm", "RequireHerd", true, "A Sheep farm must be placed on a free wild sheep herd, like a Pigsty. False delivers a herd for free at placement (testing).");
            SheepHerdsOnNewMaps = Config.Bind("Sheep farm", "HerdsOnNewMaps", true, "Scatter wild sheep herds on new maps like pig herds (one per territory after the first).");
            SheepFirstMin = Config.Bind("Sheep farm", "HerdsInFirstTerritoryMin", 0, "Fewest sheep herds in the starting territory.");
            SheepFirstMax = Config.Bind("Sheep farm", "HerdsInFirstTerritoryMax", 1, "Most sheep herds in the starting territory.");
            SheepWanderMin = Config.Bind("Sheep farm", "SheepWanderMin", 2f, "Shortest wander a sheep makes from the pen's centre (game units; a tile is 10).");
            SheepWanderMax = Config.Bind("Sheep farm", "SheepWanderMax", 12f, "Longest wander a sheep makes from the flock's centre; the pen box below keeps them in.");
            SheepCentreShiftX = Config.Bind("Sheep farm", "SheepCentreShiftX", -4f, "Moves the centre the flock wanders around, across the farm (negative = left, seen from the barn side).");
            PenMinX = Config.Bind("Sheep farm", "PenMinX", -12.5f, "The pen box sheep wander in, in the farm's own units (the footprint spans -15..15; the barn is at the front, -z).");
            PenMaxX = Config.Bind("Sheep farm", "PenMaxX", 12.5f, "The pen box, right edge.");
            PenMinZ = Config.Bind("Sheep farm", "PenMinZ", -4f, "The pen box, front edge (the barn's back wall is at about -5.7; nothing nearer the front).");
            PenMaxZ = Config.Bind("Sheep farm", "PenMaxZ", 12.5f, "The pen box, back edge (the back fence is at about 14).");
            TailorClothingLine = Config.Bind("Tailor look", "ClothingLine", true, "Hang a clothing line by the Tailor.");
            TailorBanner = Config.Bind("Tailor look", "Banner", true, "A standing banner by the Tailor.");
            LineX = Config.Bind("Tailor look", "LineX", -9.2f, "First clothing line position across (negative = left): down the left side, half a unit in from where it hung (Jess, 2026-10-21: closer to the building).");
            LineZ = Config.Bind("Tailor look", "LineZ", -5.04f, "First clothing line position front to back; negative = toward the front.");
            LineYaw = Config.Bind("Tailor look", "LineYaw", 80f, "First clothing line turn in degrees (90 runs it down the side).");
            BannerX = Config.Bind("Tailor look", "BannerX", 7f, "Banner position across the front.");
            BannerZ = Config.Bind("Tailor look", "BannerZ", -6f, "Banner position; negative = toward the front.");
            TailorMannequins = Config.Bind("Tailor look", "Mannequins", true, "Two standees out front, showing off the wares.");
            MannequinX = Config.Bind("Tailor look", "MannequinX", 2.225f, "How far left and right of centre the mannequins stand.");
            MannequinShiftX = Config.Bind("Tailor look", "MannequinShiftX", 3.775f, "Both mannequins slide this far to the right (negative = left).");
            MannequinZ = Config.Bind("Tailor look", "MannequinZ", -7f, "Mannequin position; negative = toward the front.");
            MannequinSprite1 = Config.Bind("Tailor look", "MannequinSprite1", "noble_female", "Picture on the left mannequin (a sprite name from the game; empty keeps the puppet show's own).");
            MannequinSprite2 = Config.Bind("Tailor look", "MannequinSprite2", "noble_male", "Picture on the right mannequin.");
            TailorSecondLine = Config.Bind("Tailor look", "SecondClothingLine", true, "A second clothing line down the side of the yard.");
            Line2X = Config.Bind("Tailor look", "Line2X", 6.33f, "Second line position across (negative = left); it hangs at the left front.");
            Line2Z = Config.Bind("Tailor look", "Line2Z", -4.91f, "Second line position front to back; negative = toward the front.");
            Line2Yaw = Config.Bind("Tailor look", "Line2Yaw", 111f, "Second line turn in degrees.");
            Line2Variant = Config.Bind("Tailor look", "Line2Variant", "house_attachments_cloth_line_1", "Mesh for the second line: the game has house_attachments_cloth_line, _1 and _3 (different washing on the line).");
            TailorGround = Config.Bind("Tailor look", "Ground", "STONE_TILES", "Ground painted under the Tailor, which also clears the brush: STONE_TILES (paving), STONE_ROAD, GRAVEL_ROAD, DIRT (the Cafe's), GRASS (the Embassy's), GARDEN.");
            CutZ = Config.Bind("Tailor look", "CutFrontBeyond", -1f, "Pieces of the cafe mesh lying wholly in front of this line (the umbrella and tables) are removed.");
            // The cut data's recipes were 3 grain -> 1 wool in 30 s and 3 wool -> 1 garment in 20 s, which lets one tailor
            // eat the wool of four farms; these are the agreed numbers (see fabledom-mod-tailor.md).
            // The winter rule (Jess, 2026-10-05): see Winter.cs
            WinterRule = Config.Bind("Sheep farm", "WinterRule", true, "No shearing in winter; the sheep eat grain from the farm's store instead, and a hungry winter day costs recovery days in spring.");
            WinterGrainPerDay = Config.Bind("Sheep farm", "WinterGrainPerDay", 1, "Grain a farm's sheep eat each winter day.");
            WinterRecoveryCalm = Config.Bind("Sheep farm", "UnfedRecoveryDaysCalm", 0f, "Days of no shearing after winter for each winter day the sheep went hungry, on Calm.");
            WinterRecoveryClassic = Config.Bind("Sheep farm", "UnfedRecoveryDaysClassic", 1f, "The same on Classic (and any difficulty not otherwise named).");
            WinterRecoveryChallenge = Config.Bind("Sheep farm", "UnfedRecoveryDaysChallenge", 1.5f, "The same on Challenge.");
            WinterRecoveryCruel = Config.Bind("Sheep farm", "UnfedRecoveryDaysCruel", 2f, "The same on Cruel.");
            FarmGrainStore = Config.Bind("Sheep farm", "GrainStore", 50, "Grain a Sheep farm keeps in its store (raised to this if the game's own figure is lower).");
            SheepFarmUnlockPopulation = Config.Bind("Sheep farm", "UnlockAtPopulation", 120, "The population milestone that unlocks the Sheep farm (0 = open from the start).");
            SheepFarmDesirability = Config.Bind("Sheep farm", "Desirability", -5, "Desirability the Sheep farm gives the houses around it (Jess, 2026-10-16: -5, a pen; the Pigsty is -10; 0 = none).");
            SheepFarmDesirabilityRange = Config.Bind("Sheep farm", "DesirabilityRange", 8, "Radius of that desirability in cells (Jess, 2026-10-16: the same as the Pigsty's and the coop's, 8; the farm's bigger footprint already reaches further; the cut data said 6).");
            TailorUnlockPopulation = Config.Bind("Tailor", "UnlockAtPopulation", 150, "The population milestone that unlocks the Tailor (0 = open from the start).");
            TailorDesirability = Config.Bind("Tailor", "Desirability", 5, "Desirability the Tailor gives the houses around it (Jess, 2026-10-16; the Bakery is +5; 0 = none).");
            TailorDesirabilityRange = Config.Bind("Tailor", "DesirabilityRange", 10, "Radius of that desirability in cells (Jess, 2026-10-16: 10, like the game's other +5 buildings).");
            // The Confectionery (Jess, 2026-10-13 to 15): see Confectionery.cs
            ConfectioneryOn = Config.Bind("Confectionery", "Enabled", true, "Revive the Confectionery: pastry from eggs and flour, the nobles' fifth food.");
            ConfectioneryUnlockPopulation = Config.Bind("Confectionery", "UnlockAtPopulation", 150, "The population milestone that unlocks the Confectionery (Jess: 150, with grapes; 0 = open from the start).");
            ConfWood = Config.Bind("Confectionery", "CostWood", 0, "Wood to build a Confectionery (0 = none; with every cost line at 0 the filed cost of 25 wood and 10 stone applies).");
            ConfStone = Config.Bind("Confectionery", "CostStone", 0, "Stone to build a Confectionery (0 = none).");
            ConfPlank = Config.Bind("Confectionery", "CostPlank", 20, "Planks to build a Confectionery (Jess, 2026-10-15: 20 plank, 30 brick).");
            ConfBrick = Config.Bind("Confectionery", "CostBrick", 30, "Stone bricks to build a Confectionery.");
            ConfCornerEntrance = Config.Bind("Confectionery", "CornerEntrance", true, "The door is on the corner: the front-right square needs a road on both its south and its east side (Jess, 2026-10-16). Off: the cut data's two road squares along the front.");
            ConfDesirability = Config.Bind("Confectionery", "Desirability", 20, "Desirability the Confectionery gives the houses around it (Jess, 2026-10-16: +20, \"basically da bomb\"; the Cafe is +10, the Carousel +20; 0 = none).");
            ConfDesirabilityRange = Config.Bind("Confectionery", "DesirabilityRange", 12, "Radius of that desirability in cells (Jess, 2026-10-16: the Cafe's 12; the Carousel's is 8).");
            ConfGround = Config.Bind("Confectionery", "Ground", "STONE_TILES", "Ground painted under the Confectionery, which also clears the brush (Jess, 2026-10-15): STONE_TILES (paving), STONE_ROAD (cobbles), GRAVEL_ROAD, DIRT, GRASS, GARDEN; empty leaves the game's ground.");
            ConfConstructionSpots = Config.Bind("Confectionery", "ConstructionSpots", 3, "Construction spots on the site (the cut data gives it 1; the game's workshops have 3).");
            PastryEggs = Config.Bind("Confectionery", "PastryEggs", 2, "Eggs in a batch of pastry (Jess, 2026-10-15: 2 eggs + 2 flour -> 3 pastries; as filed 1 + 2 -> 1).");
            PastryFlour = Config.Bind("Confectionery", "PastryFlour", 2, "Flour in a batch of pastry.");
            PastryBatch = Config.Bind("Confectionery", "PastryBatch", 3, "Pastries a batch makes.");
            PastrySeconds = Config.Bind("Confectionery", "PastrySeconds", 75f, "Seconds a batch of 3 takes (Jess, 2026-10-15: 75; the Bakery makes a bread in 28 s; as filed a pastry took 20 s).");
            PastryHunger = Config.Bind("Confectionery", "PastryHunger", 50, "Hunger a pastry restores (as filed 30; bread and grape juice are 50, pork 70).");
            PastryStore = Config.Bind("Confectionery", "PastryStore", 60, "Pastries a Confectionery holds before it stops (Jess, 2026-10-20: the cut data's 25 is silly). A shop or a head emptying it keeps it going.");
            PastryTradeValue = Config.Bind("Confectionery", "PastryTradeValue", 2.7f, "Trade value of a pastry in the game's raw units, where the coin price is about three times this: bread is 2 (6 coin), grape juice 2.33 (7), pork 2.67 (8). 2.7 puts a pastry just over a pork. (Shipped as 8 for a few days, 2026-10-16 to 21, which traded at about 24 coin.)");
            SmokeX = Config.Bind("Confectionery", "SmokeX", 4.8f, "The chimney smoke's position across the building, in the shop mesh's own frame (the flue top from Jess's recut).");
            SmokeY = Config.Bind("Confectionery", "SmokeY", 24.5f, "The chimney smoke's height.");
            SmokeZ = Config.Bind("Confectionery", "SmokeZ", 1.6f, "The chimney smoke's position along the building.");
            SmokeScale = Config.Bind("Confectionery", "SmokeScale", 1.5f, "Size of the chimney smoke against the Bakery's (Jess: it's a big chimney; 0 = no smoke).");
            WinterFillTo = Config.Bind("Sheep farm", "WinterFillTo", 30, "At the start of winter (see WinterRunDaysEarly), a farm with less grain than this sends its worker for more at once, until it holds this much.");
            WinterRunDaysEarly = Config.Bind("Sheep farm", "WinterRunDaysEarly", 1, "Days before winter the grain run starts: 1 = the last day of autumn (Jess, 2026-10-26), so the ration is in the barn when the first winter day takes it; 0 = winter day 1.");
            BuildGraceDays = Config.Bind("Sheep farm", "BuildGraceDays", 5, "Days after a Sheep farm is finished during which the flock cannot go hungry, so a farm finished in winter has time to fetch grain.");

            ShearSheepDays = Config.Bind("Sheep farm", "ShearSheepDays", 3, "Days a sheep stands beside the shearer before another takes its place.");
            ShearSwingSeconds = Config.Bind("Sheep farm", "ShearSwingSeconds", 4f, "Seconds between the shearer's swings at the pen.");
            ShearAnimation = Config.Bind("Sheep farm", "ShearAnimation", "scytheCrops", "Animation the shearer plays at the pen spot (an arrival animation name from the game; empty keeps the pig keeper's).");
            // The trough's grain (see Trough.cs); positions in the farm mesh's own frame
            TroughGrain = Config.Bind("Sheep farm", "TroughGrain", false, "Show the farm's grain in its trough.");
            TroughX = Config.Bind("Sheep farm", "TroughX", 17.15f, "Centre of the grain heap across the trough.");
            TroughZ = Config.Bind("Sheep farm", "TroughZ", 12.05f, "Centre of the grain heap along the trough.");
            TroughWidth = Config.Bind("Sheep farm", "TroughWidth", 2.0f, "Width of the grain heap.");
            TroughLength = Config.Bind("Sheep farm", "TroughLength", 8.0f, "Length of the grain heap.");
            TroughFullAt = Config.Bind("Sheep farm", "TroughFullAt", 20, "Grain in store at which the trough shows heaped; from 1 below that it shows a little; 0 is bare.");

            // Garment demand (Jess, 2026-10-05): see Garments.cs
            GarmentDemand = Config.Bind("Garments", "Demand", true, "Nobles and a share of commoner houses want garments.");
            GarmentDays = Config.Bind("Garments", "Days", "calm:40/75,classic:35/60,challenge:30/60,cruel:25/55", "Days between garments, as noble/commoner by difficulty: for each noble in the Palace living quarters, and for each resident of a wanting commoner house (Jess, 2026-10-24: garments are slow, so the easier difficulties get longer intervals; it was 25/55 on every difficulty, the old NobleDays and CommonerDays lines).");
            GarmentSharePerNoble = Config.Bind("Garments", "SharePerNoble", 3, "Percent of commoner houses wanting garments, per noble over the free count.");
            GarmentFreeNobles = Config.Bind("Garments", "FreeNobles", 10, "Nobles that create no commoner demand.");
            GarmentShareCap = Config.Bind("Garments", "ShareCap", 30, "Most percent of commoner houses that ever want garments.");
            GarmentGraceDays = Config.Bind("Garments", "GraceDays", 5, "Days a household may go without before the penalty.");
            GarmentNoblePenalty = Config.Bind("Garments", "NoblePenalty", 10, "Happiness penalty for a noble going without.");
            GarmentNobleStepDays = Config.Bind("Garments", "NobleStepDays", 20, "Every this many further days without, a noble's penalty grows by NoblePenalty again.");
            GarmentNobleMaxPenalty = Config.Bind("Garments", "NobleMaxPenalty", 50, "The most a noble's garment penalty can reach.");
            GarmentErrands = Config.Bind("Garments", "RealErrands", true, "The palace staff and condominium heads carry garments to their houses on the coal errand when no house needs coal.");
            GarmentCarryPerTrip = Config.Bind("Garments", "CarryPerTrip", 3, "Garments carried per trip.");
            FeastHoliday = Config.Bind("Garments", "FeastHoliday", "giovanniFeast", "The feast whose traders clothe everyone for its 50 days (Jess, 2026-10-24: Giovanni's, the merchant): the garment clocks stand still, nobody is due or takes from a shelf, anyone going without is dressed on the first day and the penalty is off; no garments are added anywhere, and the Tailors' output over the feast refills the shelves. The game's buff keys: agnesFeast, farrahFeast, giovanniFeast, payneFeast, ramoneFeast, winifredFeast. Empty: no feast does this.");

            // Other rulers trade wool and garments (Jess, 2026-10-07): one entry per ruler and good, as
            // ruler=startMin-startMax/yearlyMin-yearlyMax/cap. Ruler keys: theHarvestPrincess (Agnes), theBardPrince (Ramone),
            // theTinkerPrincess (Farrah), theMerchantPrince (Giovanni), theWarriorPrincess (Winifred), theDarkKnight (Payne).
            // These lists are used only with MirrorGrapes off; Agnes sells the most wool and Ramone the most garments.
            FrontSpotX = Config.Bind("Sheep farm", "FrontSpotX", 9f, "Where the shearer stands in winter, across the farm (the door is at about -2; 9 is in front of the fence right of the barn, where the gate is).");
            FrontSpotZ = Config.Bind("Sheep farm", "FrontSpotZ", -8.5f, "Where the shearer stands in winter, front to back (the footprint front edge is -15, the barn front about -12, the fence right of it about -6).");
            PenSpotBack = Config.Bind("Sheep farm", "PenSpotBack", 0f, "How far the shearer's spot sits from the sheep's wander origin toward the back fence (negative = toward the barn).");
            SheepGap = Config.Bind("Sheep farm", "SheepGap", 2f, "How far from the shearer the sheep being sheared stands, toward the back fence.");
            SheepKeepAway = Config.Bind("Sheep farm", "SheepKeepAway", 1.5f, "Other sheep that come this close to a shearer at work are sent a few steps away, so they do not walk through them (0 = off).");
            PenWalkable = Config.Bind("Sheep farm", "PenWalkable", true, "Carve the Sheep farm's pen floor walkable (with a gap in the front fence beside the barn) so the shearer can work among the sheep.");
            SheepLetterMode = Config.Bind("Sheep letter", "Mode", "auto", "The letter offering a sheep herd (a copy of More piggies). auto: only when the Rivals plugin is not loaded, since stealing is the route then; on; off.");
            FoodVariety = Config.Bind("Food variety", "Enabled", true, "The food variety rule and what goes with it: town houses get smaller shelves that cycle (TownHouseShelves) with pastry on them (ShelfSwaps), Condominiums employ CondoHeads heads of household, heads and palace staff bring a kind of food a house is missing (lowest priority), and a fabling eats the kind they have gone longest without. Off: the game's own shelves and errands.");
            SameFoodPenalty = Config.Bind("Food variety", "SameFoodPenalty", true, "The variety rule's happiness penalty: a commoner or noble whose last few meals held too few kinds of food (CommonerVarietyKinds/Meals, NobleVariety) loses happiness (SameFoodCommoner/SameFoodNoble, below) until a varied meal. Peasants do not mind.");
            QuartersFoodTrigger = Config.Bind("Food variety", "QuartersFoodTrigger", 120, "The palace staff fetch food when the living quarters' shelves hold less than this much hunger in total (the game's 30 suits a town house, not a court).");
            FullLoads = Config.Bind("Food variety", "FullLoads", true, "A head of condominium or palace keeper goes for food when the shelves are physically short, not when the last items on them are spoken for by residents about to eat (the game's rule, which at a meal rush sent heads across town with room for a single egg; 2026-10-23). The residents still eat what they reserved; the run comes one meal later with room for a full load.");
            CondoHeads = Config.Bind("Food variety", "CondoHeads", 3, "Heads of household a Condominium employs (the game's is 3; 4 gives a spare pair of hands for food runs in towns with poor shops).");
            SameFoodCommoner = Config.Bind("Food variety", "SameFoodCommoner", 10f, "Happiness a commoner loses while on a same-food streak.");
            SameFoodNoble = Config.Bind("Food variety", "SameFoodNoble", 20f, "Happiness a noble loses while on a same-food streak.");
            VarietyFromMilestone = Config.Bind("Food variety", "FromMilestone", "BIG_TOWN", "The population milestone the variety rule starts at (Jess, 2026-10-17: Big town, 120). Before it no meal is judged and no variety errand runs. Names: HAMLET, SMALL_VILLAGE, VILLAGE, BIG_VILLAGE, SMALL_TOWN, TOWN, BIG_TOWN, SMALL_CITY, CITY, BIG_CITY, SMALL_KINGDOM, KINGDOM; ZERO = from the start.");
            CommonerVarietyKinds = Config.Bind("Food variety", "CommonerVarietyKinds", 3, "Kinds of food a commoner wants across their last CommonerVarietyMeals meals; fewer is the penalty.");
            CommonerVarietyMeals = Config.Bind("Food variety", "CommonerVarietyMeals", 4, "Meals a commoner is judged over.");
            NobleVariety = Config.Bind("Food variety", "NobleVariety", "calm:3/5,classic:4/6,challenge:4/5,cruel:5/6", "Kinds of food a noble wants across their last meals, as kinds/meals by difficulty (Jess, 2026-10-18: Calm and Classic loosened; before, calm:3/4,classic:4/5,challenge:4/5,cruel:5/6). The living quarters shelve egg, pork, bread, pastry and grape juice (ShelfSwaps), so 5 means all of them.");
            ShelfSwaps = Config.Bind("Food variety", "ShelfSwaps", "Town house:vegetable>pastry,Town house:grapes>pastry,Palace living quarters:grapes>pastry", "The food split (Jess, 2026-10-13 and 16): shelves rekeyed as building:old>new, so commoners eat pastry, fish, egg, bread and pork and nobles egg, bread, pastry, pork and grape juice; nobody eats grapes at home (the grapes>pastry town-house entry converts houses from the one-day grapes version). Standing houses are rekeyed after a load; stock of the old kind is eaten down. Empty keeps the game's shelves.");
            TownHouseShelves = Config.Bind("Food variety", "TownHouseShelves", "pastry:10,egg:15,fish:15,bread:10,pork:5", "Food a town house holds of each kind, small enough that shelves empty and the heads bring something else (Jess, 2026-10-10). A size given for a swapped-out kind (vegetable, grapes) applies to its replacement. Empty keeps the game's shelves (vegetable 30, egg/fish/bread/pork 20).");
            TownHouseOrder = Config.Bind("Food variety", "TownHouseOrder", "egg,fish,bread,pastry,pork,coal", "The order of a town house's shelves in its panel (Jess, 2026-10-16). Kinds not named keep their place after the named ones; empty keeps the game's order.");
            QuartersShelves = Config.Bind("Food variety", "QuartersShelves", "pastry:50", "Food the living quarters hold of each kind where it differs from the game's (egg/pork/bread/juice 50, grapes 100; the grapes shelf becomes pastry). Empty keeps the game's sizes.");
            MealWatch = Config.Bind("Diagnostics", "MealWatch", true, "Log any meal that lowers a fabling's hunger by more than the food eaten explains (Jess, 2026-10-20: a starving fabling seemed to fill up on one homegrown).");
            HappinessBreakdown = Config.Bind("Diagnostics", "HappinessBreakdown", true, "With each visitor roll, log what makes up the kingdom's average happiness: every panel impactor summed over all fablings, who sits at the cap, and the class averages (Jess, 2026-10-22: \"what's pulling it down?\").");
            NobleLettersOn = Config.Bind("Noble letters", "Enabled", true, "Three noble letters for the new goods, in the game's own noble-letter series (Jess, 2026-10-22): A noble of fashion (garments and pastries now), A noble requesting a wardrobe and A noble requesting pastries (quests with no deadline).");
            FashionGarments = Config.Bind("Noble letters", "FashionGarments", 150, "Garments A noble of fashion asks for.");
            FashionPastries = Config.Bind("Noble letters", "FashionPastries", 50, "Pastries A noble of fashion asks for.");
            WardrobeGarments = Config.Bind("Noble letters", "WardrobeGarments", 300, "Garments to make for A noble requesting a wardrobe.");
            WardrobePork = Config.Bind("Noble letters", "WardrobePork", 100, "Pork to produce for the wardrobe (skins for the gloves).");
            WardrobeEggs = Config.Bind("Noble letters", "WardrobeEggs", 200, "Eggs to produce for the wardrobe (feathers).");
            PastriesQuestAmount = Config.Bind("Noble letters", "PastriesQuest", 500, "Pastries to make for A noble requesting pastries.");
            HeadSampler = Config.Bind("Food variety", "HeadSampler", false, "For tuning: log once a day how the Condominium heads spend their time.");
            IntroLettersOn = Config.Bind("Letters", "Introductions", true, "Letters from the royal advisors that introduce the package as the kingdom grows (Jess, 2026-10-17): a warning at Town, a quest to build a Sheep farm at Big town, a reminder when the variety rule starts, a quest to build a Tailor and make 20 garments at Small city, and notices at the first and the 11th noble. Each once; a save already past them gets none.");
            IntroReminderDays = Config.Bind("Letters", "ReminderDaysAfterBigTown", 15, "Days after Big town before the reminder that the variety rule has started (Jess: 15).");
            SheepLetterRepeatDays = Config.Bind("Sheep letter", "RepeatDays", 275, "Days after the letter is answered before it can be drawn again (the pigs: 330).");
            SheepLetterRepeatDaysInNeed = Config.Bind("Sheep letter", "RepeatDaysInNeed", 110, "Days before the letter can come again when it was answered while someone was going without a garment (Jess: 110).");
            SheepLetterNobility = Config.Bind("Sheep letter", "MinNobility", 5, "Nobility needed before the letter can come; it also needs a Sheep farm.");
            TradeMirrorGrapes = Config.Bind("Ruler trade", "MirrorGrapes", true, "Every ruler sells wool in the amounts they sell grapes and garments in the amounts they sell grape juice (Jess, 2026-10-07). Off: the Wool and Garments lists below are used instead.");
            TradeMirrorShare = Config.Bind("Ruler trade", "MirrorShare", 0.5f, "With MirrorGrapes on: wool and garment amounts are this fraction of the grapes and juice amounts (Jess: half).");
            TradeWool = Config.Bind("Ruler trade", "Wool", "theHarvestPrincess=60-100/30-50/500, theBardPrince=20-50/10-20/200",
                "Who sells wool and how much: ruler=startMin-startMax/yearlyMin-yearlyMax/cap, comma-separated. Empty: nobody.");
            TradeGarments = Config.Bind("Ruler trade", "Garments", "theBardPrince=20-40/10-20/100, theHarvestPrincess=5-10/5-10/50",
                "Who sells garments and how much, same form.");
            GarmentBackgroundCarry = Config.Bind("Garments", "BackgroundCarry", false, "Fallback: move garments onto shelves without anyone carrying them (off: only real errands).");
            GarmentFallbackDays = Config.Bind("Garments", "FallbackAfterDays", 1, "Days a wanting house with a head of household may sit below half stock before a garment is carried in for them each day.");
            GarmentStaffCarry = Config.Bind("Garments", "StaffCarryPerDay", 3, "Garments each palace main hall worker brings to the living quarters per day while they restock.");
            GarmentCommonerPenalty = Config.Bind("Garments", "CommonerPenalty", 5, "Happiness penalty for a commoner going without.");
            GarmentSlotNobles = Config.Bind("Garments", "SlotLivingQuarters", 60, "The most garments the Palace living quarters ever hold.");
            GarmentStockPerNoble = Config.Bind("Garments", "StockPerNoble", 3f, "When the living quarters restock they fill to this many garments per noble in the kingdom (never past SlotLivingQuarters).");
            GarmentSlotCommoners = Config.Bind("Garments", "SlotCommonerHouse", 5, "Garments a wanting commoner house keeps in stock.");
            GarmentCommonerBonus = Config.Bind("Garments", "CommonerBonus", 5, "Happiness bonus for a commoner whose household is clothed.");
            ChainLog = Config.Bind("Diagnostics", "ChainLog", false, "For tuning: log one line a day on the wool chain (happiness, efficiency, yesterday's wool, garments and food, farms and tailors).");

            UpgradePoolsOn = Config.Bind("Resources", "UpgradePools", true, "Wool and garments in the guilds' upgrade costs (Jess, 2026-10-16): wool joins the Harvest Guild's pool and never the Tinkers Guild's; garments join the Tinkers Guild's and the Carousel's from the upgrade below. Off: the game's own pools, which would put both into the Tinkers Guild's from the first upgrade.");
            GarmentsFromUpgrade = Config.Bind("Resources", "GarmentsFromUpgrade", 5, "The first upgrade whose cost may ask for garments (Jess: the 5th).");
            CarryLikeResource = Config.Bind("Resources", "CarryLike", "", "Wool and garments are carried in the same amounts as this resource (its weight class). Empty leaves them as filed (NORMAL, 10 a trip); vegetables are LIGHT, 20 a trip.");
            WoolGrain = Config.Bind("Recipes", "WoolGrain", 3, "Grain a sheep farm turns into a batch of wool (Jess, 2026-10-24: 3 grain -> 2 wool in 87 s; it was 2 -> 1 in 65 s, so a wool now costs 1.5 grain instead of 2 and a farm makes half again as much a day; wool was the garment chain's bottleneck at 600 population).");
            WoolBatch = Config.Bind("Recipes", "WoolBatch", 2, "Wool a batch makes.");
            WoolSeconds = Config.Bind("Recipes", "WoolSeconds", 87f, "Seconds a batch of wool takes.");
            GarmentWool = Config.Bind("Recipes", "GarmentWool", 3, "Wool a tailor turns into one garment.");
            GarmentSeconds = Config.Bind("Recipes", "GarmentSeconds", 90f, "Seconds a tailor takes to make one garment.");
            VarietyFromMilestone.SettingChanged += (s, e) => Food.ConfigChanged();
            NobleVariety.SettingChanged += (s, e) => Food.ConfigChanged();
            GarmentDays.SettingChanged += (s, e) => Garments.ConfigChanged();
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            StartCoroutine(Shearer.Patrol());
            StartCoroutine(Food.Sample());
            Log.LogInfo("Fabledom Tailor loaded.");
        }
    }
}
