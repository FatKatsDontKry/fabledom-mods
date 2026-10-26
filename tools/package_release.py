"""Build a release zip for one of the packages, from the files the game actually runs (the deployed plugin and
icons), checked against the build output and the source icons so nothing stale ships.

Usage:  python package_release.py luxuries
Writes mods/release/<package folder>/<zip name>-<version>.zip and lists what went in."""
import hashlib, io, os, re, sys, zipfile

MODS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLUGINS = r"C:\Program Files (x86)\Steam\steamapps\common\Fabledom\BepInEx\plugins"
PACKAGES = {
    "luxuries": dict(
        folder="Luxuries - Food and Finery", zipname="Luxuries-Food-and-Finery",
        dll="FabledomTailor.dll", project="FabledomTailor",
        icons=["tailor_albedo.png", "tailor_normal.png", "tailor_roughness.png", "tailor_emission.png", "tailor_fabric.png", "tailor_icon.png",
               "confectionery_albedo.png", "confectionery_normal.png", "confectionery_roughness.png", "confectionery_emission.png", "confectionery_icon.png",
               "nogarments.png", "hungrysheep.png"],
        icon_sources={"confectionery_": os.path.join(MODS, "tools", "icons", "confectionery")},
    ),
    "fixes": dict(
        folder="Fixes and Letters", zipname="Fixes-and-Letters",
        dll="FabledomFixes.dll", project="FabledomFixes",
        # the other three DLLs in the download, each checked against its own build the same way
        more_dlls=[("FabledomLetters.dll", "FabledomLetters"), ("FabledomPerformance.dll", "FabledomPerformance"), ("FabledomSickness.dll", "FabledomSickness")],
        icons=[], icon_sources={},
    ),
}


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""): h.update(chunk)
    return h.hexdigest()


def version_of(project):
    src = io.open(os.path.join(MODS, "src", project, "Plugin.cs"), encoding="utf-8").read()
    return re.search(r'Version = "([^"]+)"', src).group(1)


def main(name):
    p = PACKAGES[name]
    version = version_of(p["project"])
    rel = os.path.join(MODS, "release", p["folder"])
    out = os.path.join(rel, f"{p['zipname']}-{version}.zip")
    dll_live = os.path.join(PLUGINS, p["dll"])
    dll_built = os.path.join(MODS, "src", p["project"], "bin", "Release", p["dll"])   # Directory.Build.props drops the framework folder
    problems = []
    if not os.path.exists(dll_live): problems.append("deployed dll missing: " + dll_live)
    elif os.path.exists(dll_built) and md5(dll_live) != md5(dll_built): problems.append("deployed dll differs from the last build (deploy first, or the build is newer than what was tested)")
    files = [(p["dll"], dll_live)]
    for dll, project in p.get("more_dlls", []):
        live = os.path.join(PLUGINS, dll)
        built = os.path.join(MODS, "src", project, "bin", "Release", dll)
        if not os.path.exists(live): problems.append("deployed dll missing: " + live); continue
        if os.path.exists(built) and md5(live) != md5(built): problems.append(f"{dll}: deployed differs from the last build")
        files.append((dll, live))
    for icon in p["icons"]:
        live = os.path.join(PLUGINS, "icons", icon)
        srcdir = next((d for pre, d in p["icon_sources"].items() if icon.startswith(pre)), os.path.join(MODS, "tools", "icons"))
        src = os.path.join(srcdir, icon)
        if not os.path.exists(live): problems.append("deployed icon missing: " + icon); continue
        if os.path.exists(src) and md5(src) != md5(live): problems.append(f"{icon}: the deployed copy differs from tools/icons (copy the right one in first)")
        files.append(("icons/" + icon, live))
    files.append(("README.md", os.path.join(rel, "README.md")))
    if problems:
        print("NOT PACKAGED:"); [print("  -", q) for q in problems]; sys.exit(1)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for arc, path in files: z.write(path, arc)
    print("wrote", out, f"({os.path.getsize(out) // 1024} KB)")
    for arc, path in files: print(f"  {arc:40s} {os.path.getsize(path) // 1024:6d} KB  {md5(path)[:12]}")


if __name__ == "__main__":
    for name in (sys.argv[1:] or ["luxuries"]): main(name)
