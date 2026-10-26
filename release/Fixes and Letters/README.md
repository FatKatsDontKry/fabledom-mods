# Fabledom Fixes and Letters

Four Fabledom mods in one download: bug fixes and late-game performance for the base game, the family and sister letters put back on a chain that works, and two optional extras that ship switched off, Performance and Sickness. Each is its own DLL with its own config, so you can run any of them without the others.

By FatKatsDontKry (github.com/FatKatsDontKry). Fixes 0.5.1, Letters 0.2.1, Performance 0.4.0, Sickness 0.1.0. Made and tested by one person; see "Before you install" below.

## Fabledom Fixes

Bug fixes for things the base game gets wrong, mostly in large kingdoms. Every fix can be switched off in `fab.fabledomfixes.cfg`.

- **Carried goods are kept.** The game deletes what a fabling is carrying whenever its task is cut off (hunger, sickness, a job change, a demolished destination). The load now goes back to where it came from, the fabling's last workplace, or the nearest store with room.
- **Deliveries do not overflow.** Marketeers and other fetchers no longer bring more than their store has room for, and a load that does not fit is returned instead of vanishing.
- **Marketplace slots can be emptied, and dismissed one by one.** Each slot's dropdown gets an Empty storage toggle at its right end, like the Granary's and the Town shop's. While it is on the marketeer stops restocking that good and the granary and stockpile workers haul it out, that good only, until you turn it off. And dismissing a marketeer (the X on their portrait, or the overview menu) now removes that marketeer's own slot, the first portrait for the first good and so on, where the game always removed the fourth. Changing a slot's good, or removing it, still deletes whatever the slot holds, as the game does, so empty the slot first. The toggles are copied from the game's own; `MarketplaceEmptyToggleWidth`, `ArrowGap` and `OffsetX`/`Y` place them if they sit badly.
- **Phantom tenants.** A fabling whose record points at a house that is already full is rehoused on load instead of being counted in the population while holding no bed.
- **The global sickness debuff** applies as its tooltip says.
- **Discharged patients look again.** A patient who loses their bed while still sick (a doctor dismissed, the hospital closed) is sent back outside to find another hospital. The game leaves them standing inside with their treatment stalled, and the sickness kills them at 30 days with free beds next door.
- **Fall guard.** Fablings that fell through the world (they end up kilometres under the map, hauling goods by remote control) are put back on the ground.
- **More builders build faster.** Construction speed was one flat rate per site however many builders stood at it; each extra builder now adds half of what the previous one added.
- **Priority adds construction spots.** Prioritising a building adds a construction spot, so more labourers fetch its materials in parallel.
- **Boats.** Two late-game faults in how boats are driven by the pathfinding, found on a large water map.

**Late-game performance** (the parts that keep a 2,000-fabling kingdom playable):

- **Tree budget.** The game runs every fabling's behaviour tree every tick. The budget runs them in round-robin slices (`TreesPerTick`, 300), so each fabling still gets its turn, just not all of them every frame.
- **Dormant unemployed fablings.** An unemployed fabling's whole simulation is its hunger counter. Away from the camera they go dormant: no walker, no collider, no rendering, fed at home on a sweep. A pool of them (`AmbientCount`, 100) stays awake around the camera so streets look lived in, and anything that needs a fabling (a job, sickness, selection) wakes it at once. `[Dormant] Enabled` turns this off.

## Fabledom Letters

The base game's family and sister letters were designed as chains but never actually chained, so most of their quests could not come up. The mod puts them on a chain it controls, so the quests happen in order, and fixes the letter draw around them.

- **The family chain:** invest in family, the family move, the parents' demand, the credit card, on cooldowns you can set.
- **The sister chain:** her quests in order, and her thank-you gift (tulips and goods) which the base game could miss entirely.
- **The bad-letter draw** is fixed (the game's "bad" roll was searching the wrong list), and bad letters' weight grows by a difficulty-based bonus each draw that is not bad, so a long calm stretch ends.
- **Late-game pacing:** from 250 population the gap between letters slides from 14 to 21 days to 23 to 30 as the quest letters run out; the beggar letters share one 150-day cooldown.

**Existing saves:** on the first load the mod works out from the letters you have already answered where each chain stands, and clears the old permanent locks.

## Fabledom Performance (off by default)

Extra late-game speed for very large kingdoms, beyond what Fixes does. Off until you turn it on in `fab.fabledomperformance.cfg`, because it trades a little fidelity for frame time:

- **Walker ground check.** Every walking fabling casts a ray to the ground on every physics step; at 2,000 fablings that is about 10,000 raycasts a frame. The mod spaces those checks out, or turns them off so walkers follow the height of their path instead, with a little clipping on slopes.
- **Fast storage lookups.** The searches every hauler, head of household and shopkeeper runs across the town's stores are cached instead of recomputed each time.
- **Log quiet.** Known-harmless messages the game writes thousands of times are dropped before Unity formats them, and a repeated failed lookup is skipped.

No gameplay changes. If you see a fabling floating or sunk on a hill, that is the ground check; turn it back on.

## Fabledom Sickness (off by default)

The game's daily sickness roll ignores population: once a day there is a 23% chance that exactly one fabling falls ill, whether the town has 100 fablings or 2,000. So one hospital is always enough, and a Kingdom usually has nobody sick at all. With this on, sickness scales with population, so hospitals matter again. It also replaces the game's "Building a Hospital" letter with one that comes the first time anyone falls ill while you have no Hospital, in every game, with a 90-day, 10-nobility quest to build one (in tutorial games the letter alone, since the tutorial has its own step). Because a full hospital is the state that kills people, the mod also adds the warning the game never gives: once a staffed hospital exists and someone has been sick without a bed for `WarnNoBedAfterDays` (10) days, the royal advisors send a letter, and a daily notice nags after that.

Off until you turn it on in `fab.fabledomsickness.cfg`.

## Before you install

These mods were built and tested by one person, on their own saves, over several long games. They have not been through every situation the game can produce, and the Fixes change things deep in the game's hauling, housing and simulation code. If something looks wrong, a fabling stuck, goods vanishing, a letter that never comes, an error in `BepInEx/LogOutput.log`, please report it at https://github.com/FatKatsDontKry/fabledom-mods/issues with the log attached and what you were doing. That is how it gets fixed.

**Removing the mods.** Both keep their own data out of your save file where they can, and a save made with them loads without them. Letters you have already been sent stay answered.

## Installing

1. Install BepInEx 5 for Fabledom (the 64-bit x64 build), run the game once, and close it.
2. Copy `FabledomFixes.dll` and `FabledomLetters.dll` into `Fabledom/BepInEx/plugins/`. Either alone is fine. `FabledomPerformance.dll` and `FabledomSickness.dll` go in the same folder if you want them; they do nothing until switched on in their configs.
3. Start the game. The config files (`fab.fabledomfixes.cfg`, `fab.fabledomletters.cfg`, and one each for Performance and Sickness) are written in `BepInEx/config/` on the first run. To check they loaded, look near the top of `Fabledom/BepInEx/LogOutput.log` for lines beginning `Loading [Fabledom Fixes` and `Loading [Fabledom Letters`.

**Updating** is step 2 again, over the old files. A saved config keeps its values; if a default changes, the release notes say which lines to look at.

**Uninstalling** is deleting the DLLs you copied in (and their config files, if you like). See "Removing the mods" above for what happens to a save.

## What is in the zip

| File                       | What                                                                 |
| -------------------------- | -------------------------------------------------------------------- |
| `FabledomFixes.dll`        | the bug fixes and the late-game performance work                     |
| `FabledomLetters.dll`      | the family and sister letter chains and the letter draw              |
| `FabledomPerformance.dll`  | extra frame time for very large kingdoms; off until switched on      |
| `FabledomSickness.dll`     | sickness that scales with population; off until switched on          |
| `README.md`                | this file                                                            |

## Config

`fab.fabledomfixes.cfg`:

- `[Fixes]`: one switch per fix (`KeepCarriedGoods`, `KeepOverflowingDeliveries`, `RehousePhantomTenants`, `GlobalSicknessDebuff`, `GuardFalls`, `MoreBuildersBuildFaster` with `ExtraBuilderShare`, `PriorityAddsConstructionSpots` with `PriorityExtraSpots`, `MarketplaceEmptyToggles` with the four `MarketplaceEmptyToggle…` placement lines, `DischargedPatientsLookAgain`), and two verbose log switches, off.
- `[TreeBudget]`: `Enabled`, `TreesPerTick`, `StatsEverySeconds` (a log line of what the budget is doing).
- `[Dormant]`: `Enabled`, `AmbientCount`, and the timings of sleeping and waking. Leave the timings unless you are tuning.

`fab.fabledomletters.cfg`:

- `[Cooldowns]`: days between the family letters and the sister's quests.
- `[Letters]`, `[Parents demand]`, `[Sister thank-you]`: the amounts in each letter.
- `[Letter pacing]`: the bad-letter bonus by difficulty, the late-game gap and the population it starts at, the beggars' shared cooldown.

`fab.fabledomperformance.cfg`, `[Performance]`: `FastLookups`, `FixInfluenceAreaLookup`, `QuietPathfindingLog`, `MuteKnownSpam` with `MutedMessages`, `NoWarningStackTraces`, and the walker ground-check lines (on, off, or automatic above a population).

`fab.fabledomsickness.cfg`, `[Sickness]`: `Enabled`, `FablingsPerCasePerDay` (520: expected cases a day = population divided by this), `MaxCasesPerDay` (12), `GlobalSicknessMultiplier` (1.6), `WarnNoBed` with `WarnNoBedAfterDays` (10), and the log switches `LogDaily`, `LogWaiting` with `LogWaitingAfterDays`.

## Works with

Luxuries: Food and Finery, by FatKatsDontKry. The dormancy feeds sleeping fablings in a way that mod's food rule can see.
