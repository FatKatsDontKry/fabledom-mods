# Fabledom mods

Mods for [Fabledom](https://store.steampowered.com/app/1651560/Fabledom/) by FatKatsDontKry, built on BepInEx 5 and Harmony. Made and tested by one person; please report anything odd in [Issues](https://github.com/FatKatsDontKry/fabledom-mods/issues) with your `BepInEx/LogOutput.log` attached.

## Downloads

Both are on the [Releases](https://github.com/FatKatsDontKry/fabledom-mods/releases) page. Each zip carries a README with the full install steps, every config line, and known things.

| Download | What |
| --- | --- |
| **Luxuries: Food and Finery** | The upper classes get demanding: garments, food variety, and three buildings cut from the game's files come back to supply them (the Sheep farm, the Tailor, the Confectionery). |
| **Fixes and Letters** | Four mods in one zip, each its own DLL: bug fixes for large kingdoms and the Marketplace, the family and sister letter chains, and two optional extras (Performance, Sickness) off by default. |

**Luxuries is in beta (0.9.x):** expect balance changes, and keep a backup of your saves. Fixes and Letters are earlier still (0.x) and change things deep in the game's hauling and housing code; the same advice applies.

**Tested on the Steam version, game build 1.084** (the number at the bottom of the game's Settings screen). The game is also sold on the Epic store; the mods are untested there and the install steps below assume Steam's folder. A game patch that changes the build number may break a mod until it is updated; the build number is the reference point when it does.

### Installing

**Requires BepInEx 5**, a free mod loader for Unity games. It is not in the zips; install it once and every BepInEx mod uses it.

1. **Find the game folder.** In Steam, right-click Fabledom, Manage, Browse local files. It is the folder that holds `Fabledom.exe`.
2. **Install BepInEx 5** (skip if a `BepInEx` folder already sits next to `Fabledom.exe`). Download the Windows **x64** build of the latest **5.4** release from the [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases), the file named like `BepInEx_win_x64_5.4.23.x.zip` (not 6.x, not x86, not IL2CPP). Unzip it into the folder that holds `Fabledom.exe` and run the game once, then close it. That first run creates the `BepInEx/plugins` and `BepInEx/config` folders.
3. **Install the mod** by unzipping the download into `Fabledom/BepInEx/plugins/`. The zip's README says exactly which files should end up where.

The mods work on existing saves; the READMEs say what happens to a save if a mod is removed.

### When something goes wrong

The log is `BepInEx/LogOutput.log` in your Fabledom folder (Steam: right-click Fabledom, Manage, Browse local files). It is rewritten every time the game starts, so copy it before starting the game again. Attach it to a [bug report](https://github.com/FatKatsDontKry/fabledom-mods/issues/new/choose) with the mod and its version, the game build, and what you were doing.

## What is here

| Folder | What |
| --- | --- |
| `src/` | The source of every shipped plugin, one C# project each: `FabledomTailor` (Luxuries), `FabledomFixes`, `FabledomLetters`, `FabledomPerformance`, `FabledomSickness`. |
| `release/` | The README and page text of each download. The zips themselves are on Releases, not in the repository. |
| `tools/` | `package_release.py`, Tools for building and packaging the downloads; players need none of them. |

## Rights

No license is granted for this code yet; you may download and use the releases. The Tailor's and Confectionery's meshes and textures, and the other images under `tools/icons/`, are derived from Fabledom's own files and remain the property of Grenaa Games.

## Building

For developers; players need none of this.

1. Install the .NET SDK (any version that builds `net472` projects) and have Fabledom installed with BepInEx 5.
2. Set `GameDir` in `src/Directory.Build.props` to your Fabledom folder. The projects reference the game's assemblies from there and do not copy them.
3. Build a plugin:

```
dotnet build src/FabledomTailor/FabledomTailor.csproj -c Release
```

Every project except Fixes copies its DLL into `BepInEx/plugins` after the build, so close the game first. `FabledomFixes` parks its DLL in `BepInEx/plugins-off` instead; copy it into `plugins` by hand with the game closed.

4. Package a download: `python tools/package_release.py luxuries` or `python tools/package_release.py fixes`. The script refuses to zip a DLL that differs from its last build.
