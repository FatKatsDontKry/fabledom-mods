# Luxuries: Food and Finery

A Fabledom mod. The upper classes are more demanding: nobles want new clothes, and commoners and nobles both want a varied table. It revives three buildings cut from the game's files, the Sheep farm, the Tailor and the Confectionery, and adds wool, garments and pastry to go with them.

By FatKatsDontKry (github.com/FatKatsDontKry). Version 0.9.1, a beta.

## Before you install

**Made and tested by one person**, on their own saves. It has run through several long games on every difficulty, but not through every situation the game can produce. If something looks wrong, a building that does nothing, a fabling that will not eat, red text in the game's log, please report it at https://github.com/FatKatsDontKry/fabledom-mods/issues with the log file (`Fabledom/BepInEx/LogOutput.log`) attached and what you were doing at the time. That is how it gets fixed.

**Existing saves work.** The mod rekeys the food shelves of standing houses when a save loads: the Town house vegetable shelf becomes pastry, the living quarters' grapes shelf becomes pastry. Whatever was on the old shelf stays edible and is eaten down; nothing is lost. Buildings already finished are picked up as they are. The advisors' introduction letters are only sent for milestones you have not yet passed.

**Removing the mod.** A save made with the mod loads without it: the mod keeps its own letters and quests out of the save file. Garments, wool and pastry left on shelves become orphaned goods the game ignores, and houses keep working. Demolish the Tailor, Sheep farms and Confectioneries before removing it if you want a clean save.

**Back up your saves** before trying any mod. Fabledom keeps them in `C:\Users\<you>\AppData\LocalLow\Grenaa Games\Fabledom\` (paste `%LOCALAPPDATA%Low\Grenaa Games\Fabledom` into the Explorer address bar).

## Installing

The mod runs on BepInEx 5, a free mod loader for Unity games. It is not included in the zip; install it once and every BepInEx mod uses it.

**1. Find the game folder.** In Steam, right-click Fabledom, Manage, Browse local files. The folder holds `Fabledom.exe`.

**2. Install BepInEx 5** (skip if you already have a `BepInEx` folder next to `Fabledom.exe`):

- Download the Windows **x64** build of the latest **5.4** release from github.com/BepInEx/BepInEx/releases. The file is named like `BepInEx_win_x64_5.4.23.x.zip`. Not the 6.x previews, not the x86 build, not the IL2CPP builds.
- Unzip it **into the game folder**, so that `winhttp.dll`, `doorstop_config.ini` and a `BepInEx` folder sit next to `Fabledom.exe`.
- Start the game once and close it again. BepInEx creates its `plugins` and `config` folders on that first run. A console window may open alongside the game the first time; that is normal.

**3. Install the mod.** Unzip the mod's zip and copy its contents into `Fabledom/BepInEx/plugins/`, so you have:

```
Fabledom/BepInEx/plugins/FabledomTailor.dll
Fabledom/BepInEx/plugins/icons/   (the mod's images)
```

The `icons` folder must sit next to the dll under `plugins`; the mod reads its textures and menu icons from there.

**4. Start the game.** The mod writes its config file, `Fabledom/BepInEx/config/fab.fabledomtailor.cfg`, on the first run. To check it loaded, look near the top of `Fabledom/BepInEx/LogOutput.log` for a line beginning `Loading [Fabledom Tailor`.

**Updating** is step 3 again, over the old files. A saved config keeps its values; if a default changes, the release notes say which lines to look at.

**Uninstalling** is deleting `FabledomTailor.dll` and the `icons` folder (and the config file, if you like). If you have Rivals installed, remove it too: it needs this mod. See "Removing the mod" above for what happens to a save.

## What is in the zip

| File                 | What                                                                                                                          |
| -------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `FabledomTailor.dll` | the mod                                                                                                                       |
| `icons/`             | thirteen images: the Tailor's and the Confectionery's textures and menu icons, the clothes-line fabrics, and the roof markers |
| `README.md`          | this file                                                                                                                     |

## What it adds

**Sheep farm** (unlocks at the 120 population milestone, on the Food tab after the Pigsty). Takes the nearest wild sheep herd, like the Pigsty does with pigs; the map generator scatters sheep herds on new maps. The shearer makes wool from grain. No shearing in winter: the sheep eat from the farm's grain store instead, and a hungry winter costs recovery days in spring. A farm finished in winter gets five days of grace to fetch grain. Hover the farm's "Hungry sheep" marker for its own count: in winter the recovery days owed so far, in spring the days of no shearing left, and the grain in store either way. A Sheep farm takes 5 desirability off the houses around it (radius 8), like the Pigsty.

**Tailor** (unlocks at 150, Resource production tab). Makes garments from wool. Its look is its own: a purple shop with clothes lines, a banner and two mannequins out front. It gives +5 desirability to the houses around it (radius 10).

**Confectionery** (unlocks at 150, Food tab after the Large chicken coop). Makes pastry from eggs and flour: 2 eggs and 2 flour become 3 pastries, 50 hunger each. The door is on the corner, so the front-right square needs a road on both its south and its east side. Pastry is the best food per grain in the game, and the longest chain. The Confectionery gives +20 desirability (radius 12, the Cafe's reach), and pastry trades at about 8 coin, a little above pork.

**Garments.** Nobles want a new garment every 25 days on Cruel, 30 on Challenge, 35 on Classic and 40 on Calm; once you have more than 10 nobles, a share of commoner houses want one too (3% of houses per noble over 10, up to 30%), every 55 days on Cruel, 60 on Challenge and Classic, 75 on Calm. A house that has gone without shows a "no garments" marker on its roof, and after five days of grace the resident is unhappy: a noble -10, growing by another 10 every 20 days without, up to -50; a commoner -5. A commoner whose household is clothed gets +5. The palace staff and the heads of condominium fetch garments along with food. The Town shop can be set to sell garments, which is how they reach commoner houses, and it can sell pastry too, so the heads of condominium have it close at hand. Giovanni's feast is a wardrobe holiday: for its 50 days his traders clothe everyone, the garment clocks stand still, anyone going without is dressed on the first day, and nothing is taken from the shelves, so the Tailors' output goes to restocking. A counter-play for a letter that wants 150 garments at once.

**Food variety.** A commoner wants 3 different kinds of food across their last 4 meals; a noble 3 of 5 on Calm, 4 of 6 on Classic, 4 of 5 on Challenge, 5 of 6 on Cruel. Fewer is a happiness penalty (-10 commoners, -20 nobles) until the next varied meal. To make this fair, the shelves change: Town houses hold pastry, fish, egg, bread and pork in small amounts so they cycle; the living quarters hold egg, bread, pastry, pork and grape juice. Fablings eat the kind they have gone longest without, and the heads of condominium and palace staff fetch a missing kind for any house that is short, after any coal run, unless there is no coal anywhere to fetch. Agnes's feast resets everyone's streak.

**The royal advisors write.** Six letters introduce all this as your kingdom grows: a warning at Town that commoners will soon want variety, a quest at Big town to build a Sheep farm, a reminder when the variety rule starts, a quest at Small city to build a Tailor and make 20 garments, a word when your first noble arrives, and another when your 11th does. The quests work like the game's own advisor quests: 90 days, 10 nobility. Each letter comes once; a save already past that point never gets it.

**The sheep letter.** With no sheep herd on the map, a letter offers one for nobility. It comes again 275 days after an answer, or 110 if someone was going without a garment at the time.

**Three noble letters.** Nobles come asking for the new goods, in the game's own way and in the game's own noble-letter rotation (they need two free beds in the living quarters, like the game's). *A noble of fashion* wants 150 garments and 50 pastries on the spot and moves in. *A noble requesting a wardrobe* and *A noble requesting pastries* cost 10 nobility to accept and set a quest with no deadline, 300 garments with 100 pork and 200 eggs, or 500 pastries, and the noble arrives when it is done. The amounts are config lines.

**Ruler trade.** Every ruler sells wool in half the amounts they sell grapes, and garments in half the amounts of grape juice.

## Config

Everything is in `fab.fabledomtailor.cfg`. The sections, and the lines most people would touch:

- `[Sheep farm]`: build cost, `UnlockAtPopulation` (120), the winter rule and its grain figures, `RequireHerd` (a farm needs a free wild herd, as a Pigsty does), `HerdsOnNewMaps`, `Desirability` (-5) and its range.
- `[Tailor]`: build cost (30 plank, 10 brick, 10 wool), `UnlockAtPopulation` (150), `ConstructionSpots` (3), `Desirability` (+5) and its range.
- `[Tailor look]`: the clothes lines, banner, mannequins and their positions; `Ground` (what the plot is paved with).
- `[Confectionery]`: `Enabled`, `UnlockAtPopulation` (150), cost (20 plank, 30 brick), `CornerEntrance`, the pastry recipe (`PastryEggs`, `PastryFlour`, `PastryBatch`, `PastrySeconds`, `PastryHunger`), `PastryTradeValue`, `Desirability` (+20) and its range, the chimney smoke's place and size.
- `[Recipes]`: wool (3 grain make 2 in 87 s) and garments (3 wool, 90 s).
- `[Garments]`: `Demand` (off turns the whole garment side off), `Days` (noble/commoner days between garments by difficulty), `SharePerNoble`, `FreeNobles`, `ShareCap`, `GraceDays` (5), the penalties, `FeastHoliday` (the feast that clothes everyone; giovanniFeast).
- `[Food variety]`: `Enabled`, `SameFoodPenalty`, `CommonerVarietyKinds`/`Meals` (3 of 4), `NobleVariety` by difficulty, `ShelfSwaps` and `TownHouseShelves` (which kinds the houses shelve and how many), `QuartersFoodTrigger`, `CondoHeads` (3).
- `[Sheep letter]`: `Mode`, `RepeatDays` (275), `RepeatDaysInNeed` (110), `MinNobility`.
- `[Letters]`: `Introductions` (the advisors' letters, on), `ReminderDaysAfterBigTown` (15).
- `[Noble letters]`: `Enabled`, and the amounts each letter asks for.
- `[Resources]`: `UpgradePools` (wool in the Harvest Guild's upgrade costs, garments in the Tinkers Guild's and Carousel's from the 5th upgrade), trade values of wool and garments.
- `[Ruler trade]`: `MirrorGrapes`, `MirrorShare`, or your own lists per ruler.
- `[Diagnostics]`: `MealWatch` and `HappinessBreakdown` are on and write a line to the log only when something is off or once per visitor roll; `ChainLog` and `[Food variety] HeadSampler` are off and write a lot. All exist for tuning and can be switched off.

## Known things

- A Town house shelf that shrinks under a delivery already on its way loses that one delivery; the log notes it.
- The chimney smoke takes about twenty seconds to appear after a batch starts, and only runs while one is in progress.
- On Cruel with a full court the palace staff are busy; if the quarters run short, raise `QuartersFoodTrigger` (200 to 250).
- Noble letters, the game's and the mod's, only arrive on days the game has no other letter to send, so they are rare in a young kingdom and common in an old one. That is the game's rule.

## Works with

Fabledom Fixes and Letters, by FatKatsDontKry. With the Fixes dormancy on, sleeping fablings' meals still count for the variety rule.
