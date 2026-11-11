# Page text (GitHub release / Nexus description)

**Luxuries: Food and Finery** (beta 0.9.2)

The upper classes get demanding. Nobles want a new garment every few weeks, commoners and nobles both want a varied table, and three buildings cut from Fabledom's files come back to supply them: the **Sheep farm** (wool from grain, no shearing in winter), the **Tailor** (garments from wool, in a purple shop with the washing out front) and the **Confectionery** (pastry from eggs and flour, the best food per grain in the game and the longest chain). Town houses and the living quarters shelve a wider spread of food in smaller amounts so it cycles; fablings eat what they have gone longest without; the heads of condominium and the palace staff fetch what a house is missing. The royal advisors write to introduce each step, nobles come asking for the new goods, and every ruler trades wool and garments.

**Luxuries is in beta (0.9.x):** expect balance changes, and keep a backup of your saves. Tested on the Steam version, game build 1.084. Works on existing saves. Made and tested by one person; please report anything odd at https://github.com/FatKatsDontKry/fabledom-mods/issues with your log attached (`BepInEx/LogOutput.log` in your Fabledom folder; Steam: right-click Fabledom, Manage, Browse local files).

**Install, in three steps:**

1. **Install BepInEx 5** if you do not have it (a free mod loader; skip if a `BepInEx` folder already sits next to `Fabledom.exe`). Download the Windows **x64** build of the latest **5.4** release from https://github.com/BepInEx/BepInEx/releases, the file named like `BepInEx_win_x64_5.4.23.x.zip`, and unzip it into the folder that holds `Fabledom.exe`.
2. **Run the game once** and close it. That first run creates the `BepInEx/plugins` folder.
3. **Install the mod:** unzip this download into `Fabledom/BepInEx/plugins/`, so `FabledomTailor.dll` and the `icons` folder sit side by side there.

Full instructions, what every config line does, and known things are in the README inside the zip.

**New in 0.9.2:**

- **Fixed:** the Tailor and the Confectionery lost their footprint every time a save loaded, so people walked through them, there was no paving under them, and the log showed an error each load.
- The Confectionery has its finished look.
- The Sheep farm fetches grain when its store falls below 5 and fills it to a winter's worth (45) all year. A saved config gets the new figures once.
- Food variety: the heads of condominium and the palace staff fetch a missing kind for a house only while it holds as many kinds as its residents want or fewer, and stock it to one kind over (`[Food variety] SpareKinds`, 1).
- The living quarters' and town houses' storage panels list garments, then coal, last, and grow to fit.
- Unused config lines removed and descriptions corrected.

**Download:** `Luxuries-Food-and-Finery-0.9.2.zip`
