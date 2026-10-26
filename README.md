# Fabledom mods

Mods for [Fabledom](https://store.steampowered.com/app/1651560/Fabledom/) by FatKatsDontKry, built on BepInEx 5 and Harmony. Made and tested by one person; please report anything odd in [Issues](https://github.com/FatKatsDontKry/fabledom-mods/issues) with your `BepInEx/LogOutput.log` attached.

## Downloads

Both are on the [Releases](https://github.com/FatKatsDontKry/fabledom-mods/releases) page. Each zip carries a README with the full install steps, every config line, and known things.

| Download | What |
| --- | --- |
| **Luxuries: Food and Finery** | The upper classes get demanding: garments, food variety, and three buildings cut from the game's files come back to supply them (the Sheep farm, the Tailor, the Confectionery). |
| **Fixes and Letters** | Four mods in one zip, each its own DLL: bug fixes for large kingdoms and the Marketplace, the family and sister letter chains, and two optional extras (Performance, Sickness) off by default. |

**Requires** BepInEx 5 (x64, 5.4.x). Install by unzipping into `Fabledom/BepInEx/plugins/`. The mods work on existing saves; the READMEs say what happens to a save if a mod is removed.

## What is here

| Folder | What |
| --- | --- |
| `src/` | The source of every shipped plugin, one C# project each: `FabledomTailor` (Luxuries), `FabledomFixes`, `FabledomLetters`, `FabledomPerformance`, `FabledomSickness`. |
| `release/` | The README and page text of each download. The zips themselves are on Releases, not in the repository. |
| `tools/` | `package_release.py`, which builds a release zip from the installed DLLs and checks each against its build, and the icons and textures the Luxuries zip ships. `check_harmony_overloads.py` scans the patches for ambiguous by-name targets (it needs the game's decompiled code beside it, which is not included). |

## Building

1. Install the .NET SDK (any version that builds `net472` projects) and have Fabledom installed with BepInEx 5.
2. Set `GameDir` in `src/Directory.Build.props` to your Fabledom folder. The projects reference the game's assemblies from there and do not copy them.
3. Build a plugin:

```
dotnet build src/FabledomTailor/FabledomTailor.csproj -c Release
```

Every project except Fixes copies its DLL into `BepInEx/plugins` after the build, so close the game first. `FabledomFixes` parks its DLL in `BepInEx/plugins-off` instead; copy it into `plugins` by hand with the game closed.

4. Package a download: `python tools/package_release.py luxuries` or `python tools/package_release.py fixes`. The script refuses to zip a DLL that differs from its last build.

## Rights

No license is granted for this code yet; you may download and use the releases. The Tailor's and Confectionery's meshes and textures, and the other images under `tools/icons/`, are derived from Fabledom's own files and remain the property of Grenaa Games.
