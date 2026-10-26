using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // The Confectionery (Jess, 2026-10-13 to 15): cut content revived the way the Tailor was. The game files hold the
    // building data (a 2x2 workshop, one Confectioner, flour and eggs in, pastry out), the Confectioner's behaviour
    // tree (fetch within reach 15, work inside, like the Baker's) and pastry as a hidden food; the prefab has no body.
    // It wears the Pawnbroker's shell recut and recoloured by Jess (confectionery_shop.obj: the tower made a chimney;
    // confectionery_albedo/_normal/_roughness/_emission.png in plugins/icons), takes the Pawnbroker's footprint, ground
    // and decoration, smokes from the chimney with the Bakery's production smoke, and sits on the Food tab after the
    // Large chicken coop, unlocked at the 150 milestone. Pastry: 2 eggs + 2 flour -> 3 in 75 s, 50 hunger (as filed it
    // was 1 + 2 -> 1 at 30 hunger: 5 hunger a grain, a third of an egg's).
    [HarmonyPatch]
    internal static class Confectionery
    {
        private const string ShopPath = "structures/food production/confectionery";
        internal static WorldObjectData Data;
        private static GameObject prefab, pawnPrefab, bakeryPrefab;
        private static Material[] pawnMaterials, ownMaterials;
        private static Mesh shopMesh;
        private static bool ownLook, smokeWarned;

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            if (Data != null || !Plugin.ConfectioneryOn.Value) return;
            try
            {
                prefab = Tailor.FindPrefab(ShopPath, "Confectionery");
                pawnPrefab = Tailor.FindPrefab("structures/courting/pawnbroker", "Pawnbroker");
                bakeryPrefab = Tailor.FindPrefab("structures/food production/bakery", "Bakery");
                if (prefab == null) { Plugin.Log.LogError("Confectionery: prefab missing; the building stays cut."); return; }
                Data = prefab.GetComponent<WorldObject>().data;
                ResourceData Res(string key) => __instance.resourceSoData.FirstOrDefault(r => r != null && r.key == key);

                // Cost: the filed 25 wood / 10 stone unless the config says otherwise.
                var proto = Data.material?.FirstOrDefault();
                if (proto != null)
                {
                    var cost = new List<ResourceAmountData>();
                    foreach (var (key, amount) in new[] { ("wood", Plugin.ConfWood.Value), ("stone", Plugin.ConfStone.Value), ("plank", Plugin.ConfPlank.Value), ("stoneBrick", Plugin.ConfBrick.Value) })
                    {
                        var rd = Res(key); if (rd == null || amount <= 0) continue;
                        var e = (ResourceAmountData)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(proto, null);
                        e.resourceData = rd; e.amount = amount; cost.Add(e);
                    }
                    if (cost.Count > 0) Data.material = cost;
                }
                Data.isUnavailableAtStart = false;
                Data.produceResource = Data.resourceProduced != null;   // pastry is unlocked for the top bar when the first one finishes

                // The recipe (the asset is shared, so this is the recipe everywhere).
                var path = Data.resourceProduced;
                if (path != null)
                {
                    var egg = Res("egg"); var flour = Res("flour");
                    if (path.inputs != null)
                        foreach (var i in path.inputs)
                        {
                            if (i?.resourceData == null) continue;
                            if (i.resourceData == egg) i.amount = Plugin.PastryEggs.Value;
                            else if (i.resourceData == flour) i.amount = Plugin.PastryFlour.Value;
                        }
                    if (path.output != null) path.output.amount = Plugin.PastryBatch.Value;
                    path.productionTime = Plugin.PastrySeconds.Value;
                    Plugin.Log.LogInfo($"Confectionery: recipe {string.Join(" + ", (path.inputs ?? new List<ResourceAmountData>()).Select(i => i.amount + " " + i.resourceData?.key))} -> {path.output?.amount} pastry in {path.productionTime:0} s.");
                }
                // Pastry comes out of hiding.
                var pastry = Res("pastry");
                if (pastry != null)
                {
                    Traverse.Create(pastry).Field("hideFromGame").SetValue(false);
                    Traverse.Create(pastry).Field("tradeValue").SetValue(Plugin.PastryTradeValue.Value);
                    Traverse.Create(pastry).Field("restoreHungerBy").SetValue(Plugin.PastryHunger.Value);
                }
                else Plugin.Log.LogWarning("Confectionery: no pastry resource in the game data.");
                var icon = Icons.Load("confectionery_icon");
                if (icon != null) Data.iconSprite = icon;
                SizeStore(prefab.GetComponent<Storage>(), "the prefab");

                // The cut prefab's mesh holder is scaled 20 x 1 x 20: the placeholder's flat red square stretched over the
                // footprint. The placement ghost kept that under the new mesh (Jess, 2026-10-15: "it comes out HUGE"), so
                // put the holder at the Pawnbroker's transform on the prefab itself, which every instance then copies.
                var holder = prefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == "Mesh - Main");
                var pawnHolder = pawnPrefab != null ? pawnPrefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == "Mesh - Main") : null;
                if (holder != null)
                {
                    holder.transform.localPosition = pawnHolder != null ? pawnHolder.transform.localPosition : Vector3.zero;
                    holder.transform.localRotation = pawnHolder != null ? pawnHolder.transform.localRotation : Quaternion.identity;
                    holder.transform.localScale = pawnHolder != null ? pawnHolder.transform.localScale : Vector3.one;
                }
                CornerEntrance(prefab.transform);
                Desirability();
                if (pawnPrefab != null)
                {
                    Tailor.BorrowFootprint(prefab, pawnPrefab);
                    var mf = pawnPrefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.sharedMesh != null && m.sharedMesh.name == "pawnBroker_type1_var001");
                    pawnMaterials = mf != null ? mf.GetComponent<MeshRenderer>()?.sharedMaterials : null;
                }
                else Plugin.Log.LogWarning("Confectionery: Pawnbroker prefab not found; the building keeps its placeholder body.");
                Plugin.Log.LogInfo($"Confectionery: data set up ({Data.gridSize.x}x{Data.gridSize.z}, cost {string.Join(", ", (Data.material ?? new List<ResourceAmountData>()).Select(m => m.amount + " " + m.resourceData?.key))}, pastry {Plugin.PastryHunger.Value} hunger, pawnbroker {(pawnPrefab != null ? "found" : "missing")}, bakery {(bakeryPrefab != null ? "found" : "missing")}).");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Confectionery setup failed: " + ex); }
        }

        // The pastry store (Jess, 2026-10-20: "25 is not enough, and it's silly"): the cut data's output slot holds 25,
        // and a full output stops production until a shop or a head empties it. Sized on the prefab and, after a
        // load, on standing Confectioneries, which keep their own slots in the save.
        internal static void SizeStore(Storage st, string what)
        {
            try
            {
                if (st == null || st.resourcesCapacity == null || Plugin.PastryStore.Value <= 0) return;
                foreach (var slot in st.resourcesCapacity)
                {
                    if (slot?.resourceAmountData?.resourceData == null || slot.resourceAmountData.resourceData.key != "pastry") continue;
                    if (slot.resourceAmountData.amount == Plugin.PastryStore.Value) return;
                    Plugin.Log.LogInfo($"Confectionery: pastry store {slot.resourceAmountData.amount} -> {Plugin.PastryStore.Value} ({what}).");
                    slot.resourceAmountData.amount = Plugin.PastryStore.Value;
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Confectionery: pastry store: " + ex.Message); }
        }

        internal static void SizeStandingStores()
        {
            if (Data == null || Storage.AllInstances == null) return;
            foreach (var st in Storage.AllInstances.ToList())
                if (st != null && st.worldObject != null && st.worldObject.data == Data) SizeStore(st, "standing");
        }

        // --- the look ------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Constructable), "Awake"), HarmonyPostfix]
        private static void Dress(Constructable __instance)
        {
            var wo = __instance.GetComponent<WorldObject>();
            if (wo == null || Data == null || wo.data != Data) return;
            try
            {
                var root = __instance.transform;
                var waypoints = __instance.GetComponent<UnitWaypoints>();
                if (waypoints != null) Tailor.AddConstructionSpots(__instance, waypoints, Plugin.ConfConstructionSpots.Value);
                var mesh = ShopMesh();
                var main = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == "Mesh - Main");
                var pawnMain = pawnPrefab != null ? pawnPrefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == "Mesh - Main") : null;
                if (mesh != null && main != null)
                {
                    main.sharedMesh = mesh;
                    if (pawnMain != null)
                    {
                        main.transform.localPosition = pawnMain.transform.localPosition;
                        main.transform.localRotation = pawnMain.transform.localRotation;
                        main.transform.localScale = pawnMain.transform.localScale;
                    }
                    else main.transform.localScale = Vector3.one;
                    Tailor.CopySnow(main.gameObject, pawnMain);
                    var mr = main.GetComponent<MeshRenderer>();
                    if (mr != null && pawnMaterials != null) mr.sharedMaterials = ownLook ? OwnMaterials() : pawnMaterials;
                }
                // The Pawnbroker's window flower boxes, and its 2x2 construction poles (the cut prefab has 2x3 poles and no decoration mesh).
                Borrow(root, "Mesh - Decoration");
                Borrow(root, "Mesh - Construction");
                CornerEntrance(root);
                Smoke(__instance, main != null ? main.transform : root);
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Confectionery dressing failed: " + ex); }
        }

        // Desirability (Jess, 2026-10-16: "+20 radius 11. It is basically da bomb"; the Cafe is +10 at 12, the Carousel
        // +20 at 8). The cut data has no influence; the Bakery's (+5 at 10) is copied and retuned, and the prefab gets
        // the two components that apply and draw an area, copied from the Bakery's, since the cut prefab has neither.
        private static void Desirability() => GiveDesirability(Data, prefab, "Confectionery", Plugin.ConfDesirability.Value, Plugin.ConfDesirabilityRange.Value);

        // Desirability for one of our buildings: the Bakery's positive-area record (+5 at 10) copied and retuned, and the
        // two components that apply and draw an area copied onto the prefab from the Bakery's, since the cut prefabs have
        // neither. The Confectionery is +20 at 12 ("basically da bomb"), the Tailor +5 at 10 (Jess, 2026-10-16).
        private static readonly List<WorldObjectData> influenced = new List<WorldObjectData>();
        internal static void GiveDesirability(WorldObjectData data, GameObject target, string label, int effect, int range, GameObject componentDonor = null)
        {
            try
            {
                if (data == null || target == null || effect == 0 || range <= 0) return;
                if (bakeryPrefab == null) bakeryPrefab = Tailor.FindPrefab("structures/food production/bakery", "Bakery");
                AreaInfluenceData inf;
                if (data.influenceArea && data.areaInfluenceData != null)
                {
                    // the Sheep farm's cut data already carries its own record (-5 at 6); keep it, retuned
                    inf = data.areaInfluenceData;
                }
                else
                {
                    var donorData = bakeryPrefab != null ? bakeryPrefab.GetComponent<WorldObject>()?.data : null;
                    var src = donorData != null && donorData.influenceArea ? donorData.areaInfluenceData : null;
                    if (src == null) src = Resources.FindObjectsOfTypeAll<AreaInfluenceData>().FirstOrDefault(a => a != null && a.name == "Positive - Bakery");
                    if (src == null) { Plugin.Log.LogWarning(label + ": no positive influence to copy; no desirability."); return; }
                    inf = Object.Instantiate(src);
                    inf.name = (effect < 0 ? "Negative - " : "Positive - ") + label;
                }
                inf.worldObjectData = data;
                inf.range = range;
                Traverse.Create(inf).Field("effectValue").SetValue(effect);
                data.influenceArea = true;
                data.areaInfluenceData = inf;
                data.hideInfluenceInTooltip = false;
                var from = componentDonor != null && componentDonor.GetComponent<AreaInfluencer>() != null ? componentDonor : bakeryPrefab;
                if (from != null && from.GetComponent<AreaInfluencer>() != null)
                {
                    CopyComponent<AreaInfluencer>(from, target);
                    var shapes = CopyComponent<ShapesAreaAroundObject>(from, target);
                    if (shapes != null) shapes.rangeSelf = range;
                }
                if (!influenced.Contains(data)) influenced.Add(data);
                Plugin.Log.LogInfo($"{label}: desirability +{effect} in a radius of {range} (components {(target.GetComponent<AreaInfluencer>() != null ? "in place" : "missing")}).");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning(label + ": desirability: " + ex.Message); }
        }

        // The cells remember which influencers touch them, saved with the game, and an influencer only announces itself
        // when its building finishes. A Tailor or Confectionery standing before the desirability existed (2026-10-16) never did,
        // so the houses around it never heard of it: after a load, any finished one whose cells do not know it adds itself.
        internal static void InfluenceOnLoad()
        {
            if (influenced.Count == 0 || WorldObject.Instances == null) return;
            int added = 0;
            foreach (var wo in WorldObject.Instances.ToList())
            {
                try
                {
                    if (wo == null || wo.data == null || !influenced.Contains(wo.data)) continue;
                    var c = wo.GetComponent<Constructable>(); var inf = wo.GetComponent<AreaInfluencer>();
                    if (c == null || !c.isConstructionComplete || inf == null) continue;
                    var cells = Traverse.Create(inf).Method("GetAreaCells").GetValue<List<InfoCell>>();
                    if (cells == null || cells.Count == 0 || cells.Any(cell => cell != null && cell.areaInfluencerGuids != null && cell.areaInfluencerGuids.Contains(wo.guid))) continue;
                    inf.HandleAdd();
                    added++;
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning("Confectionery: influence on load: " + ex.Message); }
            }
            if (added > 0) Plugin.Log.LogInfo($"Tailor: {added} standing building(s) of ours now count for desirability.");
        }

        // A component of one prefab added to another with its serialized settings copied (references to other
        // components or objects are left alone; those are found at runtime by the component itself).
        private static T CopyComponent<T>(GameObject from, GameObject to) where T : Component
        {
            var src = from.GetComponent<T>();
            if (src == null) return null;
            var dst = to.GetComponent<T>() ?? to.AddComponent<T>();
            foreach (var f in typeof(T).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (f.IsInitOnly || typeof(Object).IsAssignableFrom(f.FieldType) || f.FieldType.IsSubclassOf(typeof(System.Delegate))) continue;
                try { f.SetValue(dst, f.GetValue(src)); } catch { }
            }
            return dst;
        }

        // The door is on the corner, so the corner square wants a road on both its sides (Jess, 2026-10-16: "let's make
        // placement super annoying"). The game needs every road arrow's cell to be road; the cut prefab carries two
        // arrows, both on the front edge (x +5 and -5 at z -15, cells of 10). The second one moves round to the east side
        // of the door cell (x +15, z -5), so the front-right square must have road to its south and to its east.
        private static void CornerEntrance(Transform root)
        {
            if (!Plugin.ConfCornerEntrance.Value) return;
            var arrows = root.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Arrow - Road connection")).OrderBy(t => t.name).ToList();
            if (arrows.Count < 2) { if (!cornerWarned) { cornerWarned = true; Plugin.Log.LogWarning("Confectionery: fewer than two road arrows on the prefab; the corner entrance needs two."); } return; }
            arrows[0].localPosition = new Vector3(5f, 0.02f, -15f);
            arrows[0].localRotation = Quaternion.identity;
            arrows[1].localPosition = new Vector3(15f, 0.02f, -5f);
            arrows[1].localRotation = Quaternion.Euler(0f, -90f, 0f);
        }
        private static bool cornerWarned;

        private static void Borrow(Transform root, string part)
        {
            if (pawnPrefab == null) return;
            var src = pawnPrefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == part);
            var dst = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == part);
            if (src == null || src.sharedMesh == null || dst == null) return;
            dst.sharedMesh = src.sharedMesh;
            var sr = src.GetComponent<MeshRenderer>(); var dr = dst.GetComponent<MeshRenderer>();
            if (sr != null && dr != null) dr.sharedMaterials = sr.sharedMaterials;
            dst.transform.localPosition = src.transform.localPosition;
            dst.transform.localRotation = src.transform.localRotation;
            dst.transform.localScale = src.transform.localScale;
            Tailor.CopySnow(dst.gameObject, src);
        }

        // The Bakery's production smoke (its VfxController with the particle systems, the smoke mesh and the window
        // light), cloned onto the chimney and wired to the Confectionery's production so it runs while pastry is made.
        // Jess (2026-10-15): "it's a big chimney", so SmokeScale makes it larger than the Bakery's.
        private static void Smoke(Constructable c, Transform parent)
        {
            if (bakeryPrefab == null || Plugin.SmokeScale.Value <= 0f || parent.Find("Fab chimney smoke") != null) return;
            var src = bakeryPrefab.GetComponentsInChildren<VfxController>(true).FirstOrDefault();
            if (src == null) { if (!smokeWarned) { smokeWarned = true; Plugin.Log.LogWarning("Confectionery: the Bakery has no production vfx to borrow."); } return; }
            // The Bakery's bundle is an empty root at the building's origin holding the smoke mesh (smoke_001, 9 units
            // up and 10 along, faded in and out by the controller through the _Turn_On/_Turn_Off shader floats, 17 s
            // to full), an unused particle system and a point light. So the root stays at the origin and the smoke mesh
            // itself moves to the flue (2026-10-15: moving the root put the smoke 38 units up and off the building).
            var go = Object.Instantiate(src.gameObject, parent);
            go.name = "Fab chimney smoke";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var flue = new Vector3(Plugin.SmokeX.Value, Plugin.SmokeY.Value, Plugin.SmokeZ.Value);
            int moved = 0;
            foreach (Transform child in go.transform)
            {
                if (child.name.StartsWith("smoke") || child.name.StartsWith("Smoke"))
                {
                    child.localPosition = flue;
                    child.localScale = child.localScale * Plugin.SmokeScale.Value;
                    moved++;
                }
                else if (child.GetComponent<Light>() != null) child.localPosition = new Vector3(flue.x, 5f, flue.z);   // a warm glow inside, under the chimney
            }
            if (moved == 0 && !smokeWarned) { smokeWarned = true; Plugin.Log.LogWarning("Confectionery: the Bakery's smoke bundle has no smoke child to move; the smoke may sit off the building."); }
            var pp = c.GetComponent<ProductionPlace>();
            if (pp != null)
            {
                var t = Traverse.Create(pp);
                t.Field("hasVfx").SetValue(true);
                t.Field("vfxController").SetValue(go.GetComponent<VfxController>());
            }
        }

        private static Mesh ShopMesh()
        {
            if (shopMesh != null) return shopMesh;
            shopMesh = Tailor.LoadObj("confectionery_shop.obj", "Fab confectionery shop", (t, tris, subCount) =>
            {
                // Jess's images are the Pawnbroker's block of atlas_002 plus a strip under it for the chimney's masonry
                // (tools/icons/confectionery/README.md): pixels 4099..5109 across and 3075..5201 down (the block, the masonry strip, then the awnings' own patch; 2026-10-21) on the 8192 sheet,
                // so the uv maps from that box to 0..1. Group 1 is the atlas submesh; group 0 the interior-mapped glass.
                if (subCount < 2 || Icons.LoadTexture("confectionery_albedo", false) == null) return;
                const float S = 8192f, x0 = 4099f, x1 = 5109f, y0 = 3075f, y1 = 5201f;
                float u0 = x0 / S, uw = (x1 - x0) / S, vb = 1f - y1 / S, vh = (y1 - y0) / S;
                var done = new HashSet<int>();
                foreach (var i in tris[1])
                    if (done.Add(i)) t[i] = new Vector2((t[i].x - u0) / uw, (t[i].y - vb) / vh);
                ownLook = true;
            });
            return shopMesh;
        }

        // The Pawnbroker's materials with the atlas one copied and given the Confectionery's textures; the Pawnbroker
        // itself keeps the shared sheet.
        private static Material[] OwnMaterials()
        {
            if (ownMaterials != null) return ownMaterials;
            ownMaterials = (Material[])pawnMaterials.Clone();
            try
            {
                int atlas = System.Array.FindIndex(pawnMaterials, m => m != null && m.name.StartsWith("atlas"));
                if (atlas < 0) atlas = 1;
                var m = new Material(pawnMaterials[atlas]) { name = "Fab confectionery atlas" };
                void Set(string prop, string file, bool linear)
                {
                    var tex = Icons.LoadTexture(file, linear);
                    if (tex != null && m.HasProperty(prop)) m.SetTexture(prop, tex);
                    else Plugin.Log.LogInfo($"Confectionery: own look: {prop} {(tex == null ? "has no " + file + ".png" : "is not on the material")}.");
                }
                Set("Texture2D_F1E595C5", "confectionery_albedo", false);
                Set("Texture2D_32A6F549", "confectionery_normal", true);
                Set("Texture2D_3A3EF09F", "confectionery_roughness", true);
                Set("_Emission", "confectionery_emission", false);
                ownMaterials[atlas] = m;
                Tailor.Seasonal(m);
                Plugin.Log.LogInfo("Confectionery: own look applied (atlas material copied with the Confectionery's textures).");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Confectionery: own look: " + ex.Message); ownMaterials = pawnMaterials; }
            return ownMaterials;
        }
    }
}
