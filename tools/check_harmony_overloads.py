"""For every [HarmonyPatch(typeof(T), "name" | nameof(T.name))] in the Tailor mod that gives no parameter types,
count the declarations of that method on T in the decompiled game code. More than one = ambiguous for Harmony."""
import os, re, io, glob, sys
ROOT = r"C:\Claude Projects\Fabledom\mods"
SRC = os.path.join(ROOT, "src", sys.argv[1] if len(sys.argv) > 1 else "FabledomTailor")   # project folder name as the one argument
DEC = os.path.join(ROOT, "decompiled")

attr = re.compile(r'\[HarmonyPatch\(typeof\((?P<type>[\w.]+)\),\s*(?:"(?P<name>\w+)"|nameof\((?P<type2>[\w.]+)\.(?P<name2>\w+)\))(?P<rest>[^\]]*)\]')
patches = []
for f in glob.glob(os.path.join(SRC, "*.cs")):
    for m in attr.finditer(io.open(f, encoding="utf-8-sig").read()):
        name = m.group("name") or m.group("name2")
        explicit = "new" in m.group("rest") or "typeof" in m.group("rest")
        patches.append((os.path.basename(f), m.group("type").split(".")[-1], name, explicit))

files = {}
for dirpath, _, names in os.walk(DEC):
    for n in names:
        if n.endswith(".cs"): files.setdefault(n[:-3], []).append(os.path.join(dirpath, n))

decl = lambda name: re.compile(r'^\s*(?:public|private|protected|internal)[\w\s<>,\[\]]*?\b' + re.escape(name) + r'\s*\(', re.M)
print(f"{len(patches)} patch attributes")
for fname, t, name, explicit in sorted(set(patches)):
    srcs = files.get(t, [])
    n = sum(len(decl(name).findall(io.open(p, encoding="utf-8-sig").read())) for p in srcs)
    flag = "AMBIGUOUS" if (n > 1 and not explicit) else ("explicit" if explicit else ("MISSING?" if n == 0 else "ok"))
    if flag != "ok": print(f"  {flag:10s} {fname:20s} {t}.{name}  ({n} declaration(s) in {len(srcs)} file(s))")
