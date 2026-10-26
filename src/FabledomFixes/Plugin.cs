using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FabledomFixes
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "fab.fabledomfixes";
        public const string Name = "Fabledom Fixes";
        public const string Version = "0.5.1";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        // Tree budget (moved in from the Fabledom Tree Budget plugin, 0.4.0): see TreeBudget.cs and Dormant.cs.
        internal static ConfigEntry<bool> TreeBudgetEnabled;
        internal static ConfigEntry<int> TreesPerTick;
        internal static ConfigEntry<float> DecisionIntervalGameSeconds;
        internal static ConfigEntry<int> StatsEverySeconds;
        private float nextStats;
        internal static ConfigEntry<bool> FixGlobalSickness;
        internal static ConfigEntry<bool> FixCarry;
        internal static ConfigEntry<bool> VerboseCarryLog;
        internal static ConfigEntry<bool> FixOverflow;
        internal static ConfigEntry<bool> FixHousing;
        internal static ConfigEntry<bool> VerboseOverflowLog;
        internal static ConfigEntry<bool> GuardFalls;
        internal static ConfigEntry<bool> MoreBuilders;
        internal static ConfigEntry<float> ExtraBuilderShare;
        internal static ConfigEntry<bool> PriorityAddsSpots;
        internal static ConfigEntry<int> PriorityExtraSpots;
        internal static ConfigEntry<bool> MarketEmptyToggles;
        internal static ConfigEntry<bool> DischargedLookAgain;
        internal static ConfigEntry<float> MarketEmptyOffsetX, MarketEmptyOffsetY, MarketEmptyWidth, MarketEmptyArrowGap;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            FixGlobalSickness = Config.Bind("Fixes", "GlobalSicknessDebuff", true,
                "Make the Global sickness debuff actually double the daily sickness risk. " +
                "The game doubles a local copy but then tests the undoubled config value.");

            FixCarry = Config.Bind("Fixes", "KeepCarriedGoods", true,
                "Don't delete what a fabling is carrying when their task is cut off. Gatherers take the load " +
                "to their workplace before a break; haulers' loads snap back to where they were picked up.");
            VerboseCarryLog = Config.Bind("Fixes", "VerboseCarryLog", false,
                "Log every load the carry fix returns (useful while testing).");

            FixOverflow = Config.Bind("Fixes", "KeepOverflowingDeliveries", true,
                "Stop deliveries overflowing a full store. Fetch trips (marketeers, shopkeepers, production inputs, house coal and food) " +
                "are sized by the room left at the destination net of loads already on their way, and whatever still does not fit " +
                "is returned instead of deleted.");
            VerboseOverflowLog = Config.Bind("Fixes", "VerboseOverflowLog", false,
                "Log every fetch trimmed to the room left (useful while testing). Loads that did not fit are always logged.");

            GuardFalls = Config.Bind("Fixes", "GuardFalls", true,
                "Stop fablings falling through the world. Teleports and pathfinding moves that would drop a fabling at the map's " +
                "origin or below the ground are refused, and any fabling found under or off the map is put back at its home's door " +
                "(else the Embassy's, else the map's centre). A fallen fabling never gets a path and moves goods without walking.");

            FixHousing = Config.Bind("Fixes", "RehousePhantomTenants", true,
                "After a load and every so often, a fabling whose house is full and does not actually hold them is moved to a house " +
                "with room. Without this such fablings count toward the population while holding no bed, so housing looks full and " +
                "no newcomers of that class ever arrive.");

            MoreBuilders = Config.Bind("Fixes", "MoreBuildersBuildFaster", true,
                "The game builds every site at one flat rate however many labourers hammer on it. With this, each extra builder adds " +
                "ExtraBuilderShare of what the previous one added: at 0.5, two builders build at 1.5x, three at 1.75x, four at 1.875x.");
            ExtraBuilderShare = Config.Bind("Fixes", "ExtraBuilderShare", 0.5f,
                "Each extra builder's contribution as a share of the previous builder's (0.5 = halving, diminishing returns).");

            PriorityAddsSpots = Config.Bind("Fixes", "PriorityAddsConstructionSpots", true,
                "Prioritising a building adds construction spots to it, so more labourers fetch its materials and build it at once. " +
                "Turning priority off removes the extra spots once they are free.");
            PriorityExtraSpots = Config.Bind("Fixes", "PriorityExtraSpots", 1, "Extra construction spots a prioritised building gets (0-6).");

            MarketEmptyToggles = Config.Bind("Fixes", "MarketplaceEmptyToggles", true,
                "An Empty toggle under each Marketplace slot's dropdown, like the Granary's and the Town shop's Empty storage toggle. While a " +
                "slot's good is being emptied the marketeer stops restocking it and the granary and stockpile workers haul it out, that good " +
                "only. Without it, changing a slot's good deletes whatever the slot held, and so does losing a marketeer (Jess, 2026-10-24).");
            MarketEmptyOffsetX = Config.Bind("Fixes", "MarketplaceEmptyToggleOffsetX", 0f, "Nudge the slot toggles across in the panel, in pixels, if they sit badly.");
            MarketEmptyWidth = Config.Bind("Fixes", "MarketplaceEmptyToggleWidth", 120f, "Width in pixels of the toggle and its label, which sit at the right end of each slot's dropdown, left of the arrow.");
            MarketEmptyArrowGap = Config.Bind("Fixes", "MarketplaceEmptyToggleArrowGap", 28f, "Space in pixels left for the dropdown's arrow to the right of the toggle.");
            MarketEmptyOffsetY = Config.Bind("Fixes", "MarketplaceEmptyToggleOffsetY", 0f, "Nudge the slot toggles up or down in the panel, in pixels.");
            DischargedLookAgain = Config.Bind("Fixes", "DischargedPatientsLookAgain", true,
                "A patient who loses their bed while still sick (a doctor dismissed, the hospital closed) is sent back outside to look for another hospital. The game leaves them standing inside with their treatment stalled until the sickness kills them.");

            // Late-game frame time: decision trees and dormant unemployed fablings (fabledom-mods.md, Mod 5).
            TreeBudgetEnabled = Config.Bind("TreeBudget", "Enabled", true,
                "Cap the number of decision trees evaluated per tick and go round them in turn. Off = the game's behaviour (every tree, every tick), " +
                "which at 1,800+ fablings is 30-60 ms of every frame.");
            TreesPerTick = Config.Bind("TreeBudget", "TreesPerTick", 300,
                "Fewest trees evaluated per tick when Enabled. Towns with fewer trees than this are unaffected.");
            DecisionIntervalGameSeconds = Config.Bind("TreeBudget", "DecisionIntervalGameSeconds", 0.5f,
                "Longest a fabling waits between decisions, in game seconds (0 = no limit, TreesPerTick alone). The per-tick budget " +
                "is sized from this each tick, so the interval holds at any game speed or frame rate; the tree cost per frame then " +
                "grows with game speed, not with population. 1 s costs workers a few percent of their output (they pause that long " +
                "at each stop); 0.5 s halves that and doubles the tree cost.");
            StatsEverySeconds = Config.Bind("TreeBudget", "StatsEverySeconds", 60,
                "Log tree count, ticks per second, the dormant count and the unemployed share this often (0 = never).");
            if (TreesPerTick.Value < 1) TreesPerTick.Value = 1;
            if (DecisionIntervalGameSeconds.Value < 0f) DecisionIntervalGameSeconds.Value = 0f;
            FabledomTreeBudget.Dormant.Bind(Config);

            Boats.Bind(Config);

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(FabledomTreeBudget.TreeBudget));
            harmony.PatchAll(typeof(FabledomTreeBudget.Dormant));
            Log.LogInfo($"Tree budget {(TreeBudgetEnabled.Value ? $"ON, at least {TreesPerTick.Value} trees per tick, a decision at least every {DecisionIntervalGameSeconds.Value:0.##} game seconds" : "OFF")}; " +
                        $"dormant unemployed {(FabledomTreeBudget.Dormant.Enabled.Value ? $"ON, ambient pool {FabledomTreeBudget.Dormant.AmbientCount.Value}" : "OFF")}; " +
                        $"boats: crowded stop {(Boats.NoCrowdedStop.Value ? "off" : "game's")}, look-ahead {(Boats.LookAhead.Value > 0f ? Boats.LookAhead.Value.ToString("0.#") : "game's")}.");
            if (PriorityAddsSpots.Value)
            {
                harmony.PatchAll(typeof(PrioritySpots));
                Log.LogInfo($"Priority adds {PriorityExtraSpots.Value} construction spot(s).");
            }
            if (MoreBuilders.Value)
            {
                harmony.PatchAll(typeof(BuildersFix));
                Log.LogInfo($"More builders build faster: 2 at {BuildersFix.Multiplier(2):0.00}x, 3 at {BuildersFix.Multiplier(3):0.00}x.");
            }
            if (FixGlobalSickness.Value)
            {
                harmony.PatchAll(typeof(GlobalSicknessFix));
                Log.LogInfo("Global sickness fix applied.");
            }
            if (FixCarry.Value)
            {
                harmony.PatchAll(typeof(CarryFix));
                Log.LogInfo("Carry fix applied.");
            }
            if (FixOverflow.Value)
            {
                harmony.PatchAll(typeof(OverflowFix));
                Log.LogInfo("Overflow fix applied.");
            }
            if (FixHousing.Value)
            {
                harmony.PatchAll(typeof(HousingFix));
                StartCoroutine(HousingFix.Patrol());
                Log.LogInfo("Housing fix applied.");
            }
            if (GuardFalls.Value)
            {
                harmony.PatchAll(typeof(FallGuard));
                StartCoroutine(FallGuard.Patrol());
                Log.LogInfo("Fall guard applied.");
            }
            if (MarketEmptyToggles.Value)
            {
                harmony.PatchAll(typeof(MarketEmpty));
                Log.LogInfo("Marketplace empty toggles on.");
            }
            if (DischargedLookAgain.Value)
            {
                harmony.PatchAll(typeof(HospitalDischarge));
                Log.LogInfo("Discharged patients look again.");
            }
        }

        private void Update()
        {
            try { FabledomTreeBudget.Dormant.Tick(); }
            catch (System.Exception ex) { Log.LogWarning("[dormant] tick: " + ex); }

            try { Boats.Sweep(); }
            catch (System.Exception ex) { Log.LogWarning("[boats] sweep: " + ex.Message); }

            if (StatsEverySeconds.Value <= 0 || UnityEngine.Time.realtimeSinceStartup < nextStats) return;
            nextStats = UnityEngine.Time.realtimeSinceStartup + StatsEverySeconds.Value;
            try { FabledomTreeBudget.TreeBudget.LogStats(StatsEverySeconds.Value); FabledomTreeBudget.Dormant.LogStats(); Log.LogInfo("[" + Boats.Status() + "]"); }
            catch (System.Exception ex) { Log.LogWarning("[treebudget] stats: " + ex.Message); }
        }
    }
}
