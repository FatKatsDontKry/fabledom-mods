using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomFixes
{
    // Construction advances at one flat rate per site while any labourer is hammering (Constructable.Update ->
    // Build(constructionSpeed * dt), gated by a single hasActiveBuilder flag). A second or third builder adds
    // nothing. Jess (2026-10-12): every extra builder should add half of what the previous one added, so two
    // builders build at 1.5x, three at 1.75x, four at 1.875x - more hands help, with diminishing returns.
    //
    // A site takes as many labourers as it has construction spots (one spot, one labourer), so this only shows on
    // buildings with two or more spots. The load-time readout below says how the town's buildings are made.
    [HarmonyPatch]
    internal static class BuildersFix
    {
        // Multiplier for n builders: 1 + s + s^2 + ... (n-1 terms), s = ExtraBuilderShare.
        internal static float Multiplier(int builders)
        {
            float s = Mathf.Clamp(Plugin.ExtraBuilderShare.Value, 0f, 0.99f);
            float m = 1f, term = 1f;
            for (int i = 1; i < builders; i++) { term *= s; m += term; }
            return m;
        }

        // Builders at a site: the labourers holding its construction spots who are currently hammering.
        private static int Builders(Constructable site)
        {
            int n = 0;
            try
            {
                foreach (var w in site.GetAllLaborers())
                    if (w != null && w.isBuilding) n++;
            }
            catch { }
            return n;
        }

        [HarmonyPatch(typeof(Constructable), nameof(Constructable.Build)), HarmonyPrefix]
        private static void MoreHands(Constructable __instance, ref float buildAmount)
        {
            if (__instance == null || __instance.isOneHitBuild || buildAmount <= 0f) return;
            int n = Builders(__instance);
            if (n > 1) buildAmount *= Multiplier(n);
        }

        // Readout after a load: construction spots per building type among the standing buildings, so we know
        // how many sites can take more than one builder at all.
        internal static void LogSpots()
        {
            try
            {
                var perType = new Dictionary<string, int>();
                var histogram = new SortedDictionary<int, int>();
                foreach (var c in Constructable.Instances)
                {
                    if (c == null || c.unitWaypoints == null || c.unitWaypoints.unitWaypoints == null || c.worldObject == null || c.worldObject.data == null) continue;
                    int spots = c.unitWaypoints.unitWaypoints.Count(w => w != null && w.type == UnitWaypoint.Type.CONSTRUCTION);
                    string name = c.worldObject.data.name;
                    if (!perType.ContainsKey(name)) perType[name] = spots;
                    histogram.TryGetValue(spots, out int k);
                    histogram[spots] = k + 1;
                }
                string h = string.Join(", ", histogram.Select(kv => $"{kv.Value} with {kv.Key}"));
                var multi = perType.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} {kv.Value}");
                Plugin.Log.LogInfo($"[builders] construction spots per building ({Constructable.Instances.Count} buildings): {h}. Types with 2+: {string.Join("; ", multi)}. " +
                                   $"Builder speed: 2 at {Multiplier(2):0.00}x, 3 at {Multiplier(3):0.00}x, 4 at {Multiplier(4):0.00}x.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[builders] spots readout: " + ex.Message); }
        }

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoad(DataManager __instance) => __instance.OnLoadComplete += () => Plugin.Instance.StartCoroutine(AfterLoad());

        private static System.Collections.IEnumerator AfterLoad()
        {
            for (int i = 0; i < 40; i++) yield return null;
            LogSpots();
            WriteInventory();
        }

        // The catalogue: every buildable type the game data holds, with its construction spots, written once per
        // load to mods\diagnostics\construction-spots.md (Jess, 2026-10-12: which buildings are one-spot).
        private static bool inventoryWritten;
        private static void WriteInventory()
        {
            if (inventoryWritten) return;
            inventoryWritten = true;
            try
            {
                var rows = new List<string>();
                var byCategory = new Dictionary<string, List<(string name, int spots, string[] mats, bool attachment)>>();
                foreach (var d in Resources.FindObjectsOfTypeAll<WorldObjectData>())
                {
                    if (d == null) continue;
                    GameObject prefab = null;
                    try { prefab = Resources.Load<GameObject>(d.GetFullPath()); } catch { }
                    if (prefab == null || prefab.GetComponent<Constructable>() == null) continue;
                    int spots = prefab.GetComponentsInChildren<UnitWaypoint>(true).Count(w => w != null && w.type == UnitWaypoint.Type.CONSTRUCTION);
                    // up to three materials, "amount key" each (Jess, 2026-10-12: the cost in three columns)
                    var mats = new string[3];
                    if (d.material != null)
                        for (int i = 0; i < 3 && i < d.material.Count; i++)
                        {
                            var m = d.material[i];
                            mats[i] = m != null && m.resourceData != null ? $"{m.amount} {m.resourceData.key}" : "";
                        }
                    string title; try { title = d.title.GetLocalizedString(); } catch { title = d.name; }
                    if (string.IsNullOrEmpty(title)) title = d.name;
                    string cat = d.buildingCategory.ToString();
                    if (!byCategory.ContainsKey(cat)) byCategory[cat] = new List<(string, int, string[], bool)>();
                    byCategory[cat].Add((title, spots, mats, d.isAttachment));
                }
                rows.Add("# Construction spots per buildable type");
                rows.Add("");
                rows.Add($"From the game data at load, {System.DateTime.Now:yyyy-MM-dd HH:mm}. Spots = how many labourers a site takes at once (each fetches materials and hammers).");
                rows.Add("");
                foreach (var cat in byCategory.Keys.OrderBy(k => k))
                {
                    rows.Add($"## {cat}");
                    rows.Add("");
                    rows.Add("| Building | Spots | Material 1 | Material 2 | Material 3 | Attachment |");
                    rows.Add("|---|---:|---|---|---|---|");
                    foreach (var r in byCategory[cat].OrderBy(r => r.spots).ThenBy(r => r.name))
                        rows.Add($"| {r.name} | {r.spots} | {r.mats[0] ?? ""} | {r.mats[1] ?? ""} | {r.mats[2] ?? ""} | {(r.attachment ? "yes" : "")} |");
                    rows.Add("");
                }
                int total = byCategory.Values.Sum(l => l.Count);
                int one = byCategory.Values.Sum(l => l.Count(r => r.spots <= 1));
                rows.Insert(3, $"{total} buildable types; {one} with one spot (or none), {total - one} with two or more.");
                string dir = @"C:\Claude Projects\Fabledom\mods\diagnostics";
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "construction-spots.md");
                System.IO.File.WriteAllLines(file, rows);
                Plugin.Log.LogInfo($"[builders] inventory written: {total} buildable types, {one} one-spot -> {file}");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[builders] inventory: " + ex.Message); }
        }
    }
}
