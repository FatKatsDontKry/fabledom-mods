using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // Switches on the cut clothing chain: Sheep farm and Tailor in the build menu, wool and garments visible,
    // a sheep herd spawned when a farm is placed, and the Tailor dressed as the cafe with its furniture cut away.
    [HarmonyPatch]
    internal static class Tailor
    {
        private const string FarmPath = "structures/resource production/sheep farm";
        private const string TailorPath = "structures/resource production/tailor";
        private const string HerdPath = "Animals/Sheep herd";
        private const string CafePath = "structures/amenities/cafe";

        internal static WorldObjectData FarmData, TailorData;
        private static GameObject farmPrefab, tailorPrefab, cafePrefab, linePrefab, bannerPrefab, puppetPrefab;
        private static Mesh cutCafeMesh;
        private static Material[] cafeMaterials;

        internal static GameObject FindPrefab(string path, string name)
        {
            var go = Resources.Load<GameObject>(path);
            if (go != null) return go;
            foreach (var g in Resources.LoadAll<GameObject>("Structures"))
                if (g != null && string.Equals(g.name, name, System.StringComparison.OrdinalIgnoreCase)) return g;
            return null;
        }

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void Setup(DataManager __instance)
        {
            if (FarmData != null) return;
            try
            {
                farmPrefab = FindPrefab(FarmPath, "Sheep farm");
                tailorPrefab = FindPrefab(TailorPath, "Tailor");
                cafePrefab = FindPrefab(CafePath, "Cafe");
                linePrefab = FindPrefab("structures/basic/homestead/clothing line", "Clothing line");
                bannerPrefab = FindPrefab("structures/decoration/standing vertical flag", "Standing vertical flag");
                puppetPrefab = FindPrefab("structures/amenities/puppet show", "Puppet show");
                if (farmPrefab == null || tailorPrefab == null) { Plugin.Log.LogError($"Tailor: prefabs missing (farm {farmPrefab != null}, tailor {tailorPrefab != null})."); return; }
                FarmData = farmPrefab.GetComponent<WorldObject>().data;
                TailorData = tailorPrefab.GetComponent<WorldObject>().data;

                ResourceData Res(string key) => __instance.resourceSoData.FirstOrDefault(r => r != null && r.key == key);
                List<ResourceAmountData> Cost(WorldObjectData d, params (string key, int amount)[] items)
                {
                    var proto = d.material?.FirstOrDefault() ?? FarmData.material?.FirstOrDefault();
                    var list = new List<ResourceAmountData>();
                    foreach (var (key, amount) in items)
                    {
                        var rd = Res(key); if (rd == null || proto == null) continue;
                        var e = (ResourceAmountData)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(proto, null);
                        e.resourceData = rd; e.amount = amount; list.Add(e);
                    }
                    return list;
                }

                // Sheep farm: real cost, needs a Farm (it eats grain).
                FarmData.material = Cost(FarmData, ("wood", Plugin.SheepFarmWood.Value), ("plank", Plugin.SheepFarmPlank.Value));
                var farm = Resources.FindObjectsOfTypeAll<WorldObjectData>().FirstOrDefault(w => w != null && w.name == "Farm");
                if (farm != null) { FarmData.hasPriorStructureRequirement = true; FarmData.priorStructureRequirement = farm; }
                FarmData.isUnavailableAtStart = false;
                // the cut data never had these flags set, so the game neither unlocked wool and garments when the
                // buildings finished nor listed them in the top bar: the wool "vanished" into a storage nobody could see
                FarmData.produceResource = FarmData.resourceProduced != null;
                TailorData.produceResource = TailorData.resourceProduced != null;
                // Recipes to the agreed numbers (the assets are shared, so this changes the recipe everywhere).
                void Recipe(ResourcePathData path, int input, int output, float seconds, string what)
                {
                    if (path == null) return;
                    if (path.inputs != null && path.inputs.Count > 0) path.inputs[0].amount = input;
                    if (path.output != null && output > 0) path.output.amount = output;
                    path.productionTime = seconds;
                    Plugin.Log.LogInfo($"Tailor: {what} recipe set to {input} {(path.inputs != null && path.inputs.Count > 0 ? path.inputs[0].resourceData.key : "?")} -> {path.output.amount} {path.output.resourceData.key} in {seconds:0} s.");
                }
                Recipe(FarmData.resourceProduced, Plugin.WoolGrain.Value, Plugin.WoolBatch.Value, Plugin.WoolSeconds.Value, "wool");
                Recipe(TailorData.resourceProduced, Plugin.GarmentWool.Value, 1, Plugin.GarmentSeconds.Value, "garment");
                // Wool and garments carry like food (Jess, 2026-10-06): the cut data left them in a class nobody carries 17 of
                try
                {
                    var veg = __instance.resourceSoData.FirstOrDefault(r => r != null && r.key == Plugin.CarryLikeResource.Value);
                    foreach (var key in new[] { "wool", "garment" })
                    {
                        var rd = __instance.resourceSoData.FirstOrDefault(r => r != null && r.key == key);
                        if (rd != null && veg != null) { var was = rd.weight; rd.weight = veg.weight; Plugin.Log.LogInfo($"Tailor: {key} carry class {was} -> {rd.weight} (like {veg.key})."); }
                    }
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: carry class: " + ex.Message); }
                Garments.SetupStatus(__instance);
                Winter.SetupStatus(__instance);
                Winter.SizeGrainStore(farmPrefab);
                if (cafePrefab != null) BorrowFootprint(tailorPrefab, cafePrefab);
                // Like the Pigsty, a farm needs a free wild herd (the testing shortcut delivered one for free; see RequireHerd)
                FarmData.hasAvailableWorldObjectRequirement = Plugin.RequireHerd.Value;
                if (Plugin.RequireHerd.Value)
                {
                    // the requirement must name the sheep herd (the cut data may leave it blank)
                    var herdWo = Resources.Load<GameObject>(HerdPath)?.GetComponent<WorldObject>();
                    if (FarmData.availableWorldObjectRequirement == null && herdWo != null) FarmData.availableWorldObjectRequirement = herdWo.data;
                    Plugin.Log.LogInfo($"Tailor: Sheep farm requires a free herd ({(FarmData.availableWorldObjectRequirement != null ? FarmData.availableWorldObjectRequirement.name : "none set!")}).");
                }

                // Tailor: real cost, needs a Sheep farm.
                TailorData.material = Plugin.TailorWool.Value > 0
                    ? Cost(TailorData, ("plank", Plugin.TailorPlank.Value), ("stoneBrick", Plugin.TailorBrick.Value), ("wool", Plugin.TailorWool.Value))
                    : Cost(TailorData, ("plank", Plugin.TailorPlank.Value), ("stoneBrick", Plugin.TailorBrick.Value));
                TailorData.hasPriorStructureRequirement = true;
                TailorData.priorStructureRequirement = FarmData;
                TailorData.isUnavailableAtStart = false;
                // Menu icon (Jess, 2026-10-09): the purple shop, tailor_icon.png in the icons folder; the cut data's own icon stays when it is missing.
                var tailorIcon = Icons.Load("tailor_icon");
                if (tailorIcon != null) TailorData.iconSprite = tailorIcon;
                Confectionery.GiveDesirability(TailorData, tailorPrefab, "Tailor", Plugin.TailorDesirability.Value, Plugin.TailorDesirabilityRange.Value);   // +5 at 10 (Jess, 2026-10-16)
                // the Sheep farm's cut data has a -5 at 6 record but no area components; -5 at 8 like the Pigsty and the coop (Jess, 2026-10-16)
                if (pigstyPrefab == null) pigstyPrefab = FindPrefab("structures/food production/pigsty", "Pigsty");
                Confectionery.GiveDesirability(FarmData, farmPrefab, "Sheep farm", Plugin.SheepFarmDesirability.Value, Plugin.SheepFarmDesirabilityRange.Value, pigstyPrefab);
                MilestoneUnlocks();

                // Wool and garments come out of hiding.
                foreach (var (key, value) in new[] { ("wool", Plugin.WoolTradeValue.Value), ("garment", Plugin.GarmentTradeValue.Value) })
                {
                    var rd = Res(key); if (rd == null) continue;
                    Traverse.Create(rd).Field("hideFromGame").SetValue(false);
                    Traverse.Create(rd).Field("tradeValue").SetValue(value);
                    Traverse.Create(rd).Field("restoreHungerBy").SetValue(0);
                }
                Plugin.Log.LogInfo($"Tailor: data set up (farm {FarmData.gridSize.x}x{FarmData.gridSize.z}, tailor {TailorData.gridSize.x}x{TailorData.gridSize.z}, cafe {(cafePrefab != null ? "found" : "missing")}, line {(linePrefab != null ? "found" : "missing")}, banner {(bannerPrefab != null ? "found" : "missing")}).");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor setup failed: " + ex); }
        }

        [HarmonyPatch(typeof(BuildMenuContainer), "Awake"), HarmonyPrefix]
        private static void AddToBuildMenu(BuildMenuContainer __instance)
        {
            if (FarmData == null) return;
            // the farm sits with the Pigsty and the coops under Food production (Jess: livestock belongs with the farms);
            // the Tailor stays under Resource production. The tab a button shows under is the data's category.
            var food = Traverse.Create(__instance).Field("foodProduction").GetValue<List<WorldObjectData>>();
            var resources = Traverse.Create(__instance).Field("resourceProduction").GetValue<List<WorldObjectData>>();
            if (food != null && !food.Contains(FarmData))
            {
                // the tab lists buildings in unlock order, so the farm (120) goes right after the Pigsty (90), ahead of the Grape farm (150)
                int afterPigsty = food.FindIndex(d => d != null && d.name == "Pigsty") + 1;
                if (afterPigsty > 0) food.Insert(afterPigsty, FarmData); else food.Add(FarmData);
                FarmData.buildingCategory = BuildingCategory.FOOD_PRODUCTION;
            }
            if (resources != null && !resources.Contains(TailorData)) { resources.Add(TailorData); TailorData.buildingCategory = BuildingCategory.RESOURCE_PRODUCTION; }
            if (food != null && Confectionery.Data != null && !food.Contains(Confectionery.Data))
            {
                // after eggs (Jess, 2026-10-15): the Confectionery eats them, so it follows the Large chicken coop
                int afterEggs = food.FindIndex(d => d != null && d.name == "Large chicken coop") + 1;
                if (afterEggs > 0) food.Insert(afterEggs, Confectionery.Data); else food.Add(Confectionery.Data);
                Confectionery.Data.buildingCategory = BuildingCategory.FOOD_PRODUCTION;
            }
        }

        // --- sheep herds on new maps, scattered like pig herds ---------------------------------------

        [HarmonyPatch(typeof(RealmGenerator), "InitRealmConfigData"), HarmonyPostfix]
        private static void ScatterSheep(RealmGenerator __instance)
        {
            if (!Plugin.SheepHerdsOnNewMaps.Value) return;
            try
            {
                var data = __instance.data;
                if (data == null || data.scatteredObjects == null) return;
                var herdGo = Resources.Load<GameObject>(HerdPath);
                var herdData = herdGo != null ? herdGo.GetComponent<WorldObject>()?.data : null;
                if (herdData == null) { Plugin.Log.LogWarning("Tailor: sheep herd data not found for scattering."); return; }
                if (data.scatteredObjects.Any(c => c != null && c.worldObjectData == herdData)) return;
                var pigs = data.scatteredObjects.FirstOrDefault(c => c != null && c.worldObjectData != null && c.worldObjectData.name == "Pig herd");
                if (pigs == null) { Plugin.Log.LogWarning("Tailor: no pig herd config to copy for sheep."); return; }
                var sheep = Object.Instantiate(pigs);
                sheep.name = "Sheep herd (mod)";
                sheep.worldObjectData = herdData;
                sheep.firstTerritoryAmount = new Vector2Int(Plugin.SheepFirstMin.Value, Plugin.SheepFirstMax.Value);
                sheep.secondTerritoryAmount = new Vector2Int(1, 1);
                sheep.lastTerritoryAmount = new Vector2Int(1, 1);
                data.scatteredObjects = data.scatteredObjects.Concat(new[] { sheep }).ToArray();
                Plugin.Log.LogInfo("Tailor: sheep herds added to the map generator.");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor: scattering sheep failed: " + ex); }
        }

        // --- the farm needs a herd: deliver one that arrives when the pen is built --------------------

        // Placement: if no wild herd is free, create one, reserved for this farm, but keep it off the map until
        // construction finishes (the game then moves it to the pen, which is when the sheep appear).
        [HarmonyPatch(typeof(AnimalHerdFarm), "HandlePlacementComplete"), HarmonyPrefix]
        private static bool EnsureHerd(AnimalHerdFarm __instance)
        {
            try
            {
                var wo = __instance.GetComponent<WorldObject>();
                if (wo == null || wo.data != FarmData) return true;
                var tr = Traverse.Create(__instance);
                var herdData = tr.Field("animalHerdData").GetValue<WorldObjectData>();
                if (herdData == null || Plugin.RequireHerd.Value) return true;   // the game claims a wild herd, or refuses the placement
                bool free = AnimalHerd.Instances.Any(h => h != null && h.gridObject != null && h.gridObject.wo.data.id == herdData.id && !h.isReserverd && h.gridObject.wo.IsInsideUnlockedTerritory());
                if (free) return true;   // the game claims the wild one
                var origin = tr.Field("wanderOrigin").GetValue<Transform>();
                var at = origin != null ? origin.position : __instance.transform.position;
                var go = SaveMaster.SpawnSavedPrefab(InstanceSource.Resources, herdData.GetFullPath());
                if (go == null) { Plugin.Log.LogWarning("Tailor: couldn't spawn a sheep herd at " + herdData.GetFullPath()); return true; }
                go.transform.position = at;
                var herd = go.GetComponent<AnimalHerd>();
                herd.isReserverd = true;
                herd.isFromWorldEvent = true;        // so the sheep are spawned when it is first put on the grid
                tr.Field("animalHerd").SetValue(herd);
                Plugin.Log.LogInfo("Tailor: a sheep herd is on its way for the new farm.");
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor: herd delivery failed: " + ex); return true; }
        }

        // Placement: the game reserves the first free herd it finds; take the nearest one instead (sheep and pigs alike),
        // so a farm pulls in the herd beside it rather than one across the map.
        [HarmonyPatch(typeof(AnimalHerdFarm), "HandlePlacementComplete"), HarmonyPrefix]
        private static bool NearestHerd(AnimalHerdFarm __instance)
        {
            try
            {
                var tr = Traverse.Create(__instance);
                var herdData = tr.Field("animalHerdData").GetValue<WorldObjectData>();
                if (herdData == null) return true;
                var at = __instance.transform.position;
                var herd = AnimalHerd.Instances
                    .Where(h => h != null && h.gridObject != null && h.gridObject.wo.data.id == herdData.id && !h.isReserverd && h.gridObject.wo.IsInsideUnlockedTerritory())
                    .OrderBy(h => (h.transform.position - at).sqrMagnitude)
                    .FirstOrDefault();
                if (herd == null) return true;   // the game's own line then throws as it would have anyway
                herd.isReserverd = true;
                tr.Field("animalHerd").SetValue(herd);
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: nearest herd pick failed: " + ex.Message); return true; }
        }

        // Cancelled placement: a delivered herd that never reached the map is thrown away.
        [HarmonyPatch(typeof(AnimalHerdFarm), "HandlePlacementCancelled"), HarmonyPrefix]
        private static void DropUndeliveredHerd(AnimalHerdFarm __instance)
        {
            var herd = Traverse.Create(__instance).Field("animalHerd").GetValue<AnimalHerd>();
            if (herd != null && !AnimalHerd.Instances.Contains(herd)) Object.Destroy(herd.gameObject);
        }

        // Construction done: the game moves the herd to the pen; make sure the sheep exist (a save made
        // mid-construction loads the herd without them).
        [HarmonyPatch(typeof(AnimalHerdFarm), "HandleConstructionComplete"), HarmonyPostfix]
        private static void HerdArrives(AnimalHerdFarm __instance)
        {
            try
            {
                var herd = Traverse.Create(__instance).Field("animalHerd").GetValue<AnimalHerd>();
                if (herd == null) return;
                if (herd.animals.Count == 0) Traverse.Create(herd).Method("SpawnAnimals").GetValue();
                if (!AnimalHerd.Instances.Contains(herd)) AnimalHerd.Instances.Add(herd);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: herd arrival check failed: " + ex.Message); }
        }

        // The shearer's and tailor's own behaviour trees expect work spots the cut prefabs lack: copy them from the
        // Pigsty and the Stone masonry (AddWorkSpots, below).
        private static GameObject pigstyPrefab, masonryPrefab;
        private static bool spotsLoggedFarm, spotsLoggedTailor;

        // The stub's footprint parts, borrowed from the Cafe (same 2x2): the obstacle box the pathfinder respects, the
        // navigation tag surfaces, and the ground texture that is painted under a finished building and clears the brush.
        internal static void BorrowFootprint(GameObject tailor, GameObject cafe)
        {
            try
            {
                var tcc = tailor.GetComponent<ColliderController>(); var ccc = cafe.GetComponent<ColliderController>();
                var tWo = tailor.GetComponent<WorldObject>(); var cWo = cafe.GetComponent<WorldObject>();
                if (tWo.data.gridSize.x != cWo.data.gridSize.x || tWo.data.gridSize.z != cWo.data.gridSize.z) { Plugin.Log.LogWarning("Tailor: footprints differ; the Cafe's colliders are not borrowed."); return; }
                if (tcc != null && ccc != null)
                {
                    var ct = Traverse.Create(ccc); var tt = Traverse.Create(tcc);
                    foreach (var name in new[] { "obstacleSurfacesContainer", "tagSurfacesContainer" })
                    {
                        var src = ct.Field(name).GetValue<GameObject>();
                        if (src == null) continue;
                        var old = tt.Field(name).GetValue<GameObject>();
                        if (old != null) Object.DestroyImmediate(old, true);
                        var copy = Object.Instantiate(src, tailor.transform);
                        copy.name = src.name; copy.transform.localPosition = src.transform.localPosition; copy.transform.localRotation = src.transform.localRotation; copy.transform.localScale = src.transform.localScale;
                        tt.Field(name).SetValue(copy);
                    }
                    tt.Field("addObstacle").SetValue(ct.Field("addObstacle").GetValue<bool>());
                    tt.Field("modifyTags").SetValue(ct.Field("modifyTags").GetValue<bool>());
                }
                var tg = tailor.GetComponent<AlterGroundTexture>(); var cg = cafe.GetComponent<AlterGroundTexture>();
                if (tg != null && cg != null)
                {
                    foreach (var f in typeof(AlterGroundTexture).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    {
                        if (f.FieldType.IsSubclassOf(typeof(Component))) continue;   // its own grid object, attachment and so on
                        if (f.FieldType == typeof(GameObject))
                        {
                            // The Cafe's ground mesh and foundation are children of its prefab: give the Tailor its own copy
                            // at the same place. Copying the "has a ground mesh" switch without the mesh left the Tailor's
                            // Awake throwing on a null (2026-10-08), so the ground was never painted and the brush stayed.
                            var src = f.GetValue(cg) as GameObject;
                            if (src == null) { f.SetValue(tg, null); continue; }
                            var parentPath = src.transform.parent != null && src.transform.parent != cafe.transform ? src.transform.parent.name : null;
                            var parent = parentPath != null ? tailor.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == parentPath) ?? tailor.transform : tailor.transform;
                            var existing = parent.Find(src.name);
                            var copy = existing != null ? existing.gameObject : Object.Instantiate(src, parent);
                            copy.name = src.name; copy.transform.localPosition = src.transform.localPosition; copy.transform.localRotation = src.transform.localRotation; copy.transform.localScale = src.transform.localScale;
                            f.SetValue(tg, copy);
                            continue;
                        }
                        f.SetValue(tg, f.GetValue(cg));
                    }
                }
                Plugin.Log.LogInfo("Tailor: footprint and ground borrowed from the Cafe.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: footprint: " + ex.Message); }
        }

        // Where the shearer stands: in the pen by the sheep while shearing, out front like the pig keeper when the
        // flock is shut off (winter, recovering from a hungry one, or a full wool store). The pen spot carries the shearing pose.
        internal static void PlaceFarmSpots(GameObject farm, bool front)
        {
            try
            {
                var waypoints = farm.GetComponent<UnitWaypoints>();
                var parent = waypoints != null ? Traverse.Create(waypoints).Field("waypointParent").GetValue<GameObject>() : null;
                if (parent == null) return;
                var origin = parent.transform.Find("Sheep wander origin");
                for (int n = 0; ; n++)
                {
                    var spot = parent.transform.Find("Fab work spot " + n);
                    if (spot == null) break;
                    if (front)
                    {
                        // in winter and while recovering: in front of the fence right of the barn, where the gate is
                        // (Jess, 2026-10-07), extra workers side by side along the fence; config numbers for nudging
                        spot.localPosition = new Vector3(Plugin.FrontSpotX.Value + 2f * n, 0f, Plugin.FrontSpotZ.Value);
                    }
                    else if (origin != null)
                    {
                        // in the pen, by the sheep's wander origin; the pen floor is carved walkable (PenWalk)
                        spot.localPosition = origin.localPosition + new Vector3(2f * n, 0f, Plugin.PenSpotBack.Value);   // a little toward the back fence (Jess, 2026-10-08)
                    }
                    var wp = spot.GetComponent<UnitWaypoint>();
                    if (wp != null && !front && !string.IsNullOrEmpty(Plugin.ShearAnimation.Value)) wp.animOnArrival = Plugin.ShearAnimation.Value;
                    else if (wp != null && front && pigstyAnimations.TryGetValue(n, out var anim)) wp.animOnArrival = anim;
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: farm spots: " + ex.Message); }
        }
        private static readonly System.Collections.Generic.Dictionary<int, string> pigstyAnimations = new System.Collections.Generic.Dictionary<int, string>();

        [HarmonyPatch(typeof(Constructable), "Awake"), HarmonyPostfix]
        private static void AddWorkSpots(Constructable __instance)
        {
            var wo = __instance.GetComponent<WorldObject>();
            if (wo == null || FarmData == null) return;
            bool farm = wo.data == FarmData, tailor = wo.data == TailorData;
            if (!farm && !tailor) return;
            try
            {
                if (pigstyPrefab == null) pigstyPrefab = FindPrefab("structures/food production/pigsty", "Pigsty");
                if (masonryPrefab == null) masonryPrefab = FindPrefab("structures/resource production/stone masonry", "Stone masonry");
                var source = farm ? pigstyPrefab : masonryPrefab;
                var waypoints = __instance.GetComponent<UnitWaypoints>();
                var parent = waypoints != null ? Traverse.Create(waypoints).Field("waypointParent").GetValue<GameObject>() : null;
                if (source == null || parent == null) return;
                if (tailor) AddConstructionSpots(__instance, waypoints, Plugin.TailorConstructionSpots.Value);
                if (parent.transform.Find("Fab work spot 0") != null) return;
                int n = 0;
                foreach (var w in source.GetComponentsInChildren<UnitWaypoint>(true))
                {
                    if (w.name.Contains("Entry") || w.name.Contains("Construction")) continue;
                    var copy = Object.Instantiate(w.gameObject, parent.transform);
                    copy.name = "Fab work spot " + n;
                    // the farm's spot sits at its sheep origin in the shearing seasons and out front, where the Pigsty's
                    // keeper stands, in winter (PlaceFarmSpots); the tailor's just inside the shop front
                    if (farm) pigstyAnimations[n] = w.animOnArrival;
                    var origin = parent.transform.Find("Sheep wander origin");
                    copy.transform.localPosition = farm && origin != null ? origin.localPosition + new Vector3(2f * n, 0f, 0f) : new Vector3(-2f + 2f * n, 0f, -2f);
                    copy.transform.localRotation = Quaternion.identity;
                    n++;
                }
                if (waypoints != null)
                {
                    // keep the construction clones, which live beside the original waypoint rather than under the parent
                    var extra = waypoints.unitWaypoints != null ? waypoints.unitWaypoints.Where(w => w != null && w.gameObject.name.StartsWith("Fab construction")).ToList() : new List<UnitWaypoint>();
                    waypoints.unitWaypoints = parent.GetComponentsInChildren<UnitWaypoint>(true).Union(extra).ToArray();
                }
                if (farm) PlaceFarmSpots(__instance.gameObject, Winter.StandsInFront(wo));
                // every building gets its spots again on each load, so this is said once a session per kind
                if (farm ? !spotsLoggedFarm : !spotsLoggedTailor)
                {
                    if (farm) spotsLoggedFarm = true; else spotsLoggedTailor = true;
                    Plugin.Log.LogInfo($"Tailor: {(farm ? "Sheep farm" : "Tailor")} given {n} work spot(s) from {source.name}.");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor: work spots failed: " + ex); }
        }

        // The Tailor's cut data gives its site one construction spot; the game's workshops have three (Jess, 2026-10-12).
        // Clone the site's construction waypoint beside itself with a stable guid, the way the Fixes mod's priority
        // spots do, so a labourer's saved claim on one survives a load.
        internal static void AddConstructionSpots(Constructable c, UnitWaypoints waypoints, int want)
        {
            try
            {
                want = Mathf.Clamp(want, 1, 6);
                if (waypoints == null || waypoints.unitWaypoints == null || want <= 1) return;
                UnitWaypoint template = null;
                foreach (var w in waypoints.unitWaypoints)
                    if (w != null && w.type == UnitWaypoint.Type.CONSTRUCTION && !w.gameObject.name.Contains("Fab construction")) { template = w; break; }
                if (template == null) return;
                var list = waypoints.unitWaypoints.ToList();
                int have = list.Count(w => w != null && w.type == UnitWaypoint.Type.CONSTRUCTION), made = 0;
                for (int i = 1; have + made < want && i <= 5; i++)
                {
                    string guid = template.guid + "-fab" + i;
                    if (list.Any(w => w != null && w.guid == guid)) continue;
                    var go = Object.Instantiate(template.gameObject, template.transform.parent);
                    go.name = "Fab construction spot " + i;
                    go.transform.position = template.transform.position + template.transform.right * (1.2f * ((i + 1) / 2) * (i % 2 == 1 ? 1f : -1f));
                    var wp = go.GetComponent<UnitWaypoint>();
                    wp.guid = guid;
                    wp.reservedBy = null;
                    list.Add(wp);
                    made++;
                }
                if (made > 0) waypoints.unitWaypoints = list.ToArray();
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: construction spots: " + ex.Message); }
        }

        // Garments on the market stalls and in the town shop: the stall options are lists on the buildings, so add
        // the garment to each as it wakes (the lists are serialised on the prefab, which can be reloaded between scenes).
        // The two resources are assets that outlive scenes, so they are looked up once; the game's own lookup by key walks
        // the whole list, and the garment errand asks for this every tick. (Unity's null check catches an unloaded asset.)
        private static ResourceData garmentData, pastryData;
        private static ResourceData Resource(ref ResourceData cache, string key)
        {
            if (cache != null) return cache;
            return cache = DataManager.Instance != null ? DataManager.Instance.resourceSoData.FirstOrDefault(r => r != null && r.key == key) : null;
        }
        internal static ResourceData Garment => Resource(ref garmentData, "garment");

        [HarmonyPatch(typeof(Market), "Awake"), HarmonyPrefix]
        private static void GarmentsAtTheMarket(Market __instance)
        {
            var g = Garment;
            if (g != null && __instance.options != null && !__instance.options.Contains(g)) __instance.options.Add(g);
            var w = DataManager.Instance != null ? DataManager.Instance.resourceSoData.FirstOrDefault(r => r != null && r.key == "wool") : null;
            if (w != null && __instance.options != null && !__instance.options.Contains(w)) __instance.options.Add(w);   // the market stores everything
            var p = Pastry;
            if (p != null && __instance.options != null && !__instance.options.Contains(p)) __instance.options.Add(p);
        }

        // Pastry too (Jess, 2026-10-20): the Town shop can sell it, so the heads can fetch it from a shop beside the
        // condominium rather than from the Confectionery across town; and the market stores it like any food.
        internal static ResourceData Pastry => Resource(ref pastryData, "pastry");

        [HarmonyPatch(typeof(Shop), "Awake"), HarmonyPrefix]
        private static void GarmentsInTheShop(Shop __instance)
        {
            var g = Garment;
            if (g != null && __instance.options != null && !__instance.options.Contains(g)) __instance.options.Add(g);
            var p = Pastry;
            if (p != null && __instance.options != null && !__instance.options.Contains(p)) __instance.options.Add(p);
        }

        // Population milestones (Jess, 2026-10-09): the Sheep farm unlocks with the 120 milestone and the Tailor with the
        // 150 one (UnlockAtPopulation). Being on a milestone's reward list is all the game needs: the button shows locked with "Reach
        // milestone ..." like the Pigsty's until then (do not set isUnavailableAtStart: that hides a building outright).
        // 0 in the config keeps one open from the start.
        private static readonly List<(WorldObjectData data, PopulationUnlockInfo at)> milestoneUnlocks = new List<(WorldObjectData, PopulationUnlockInfo)>();
        // The kingdom's config (and its milestones) is only loaded once the KingdomManager wakes, after the data is set up.
        [HarmonyPatch(typeof(KingdomManager), "Awake"), HarmonyPostfix]
        private static void MilestonesWhenKingdomWakes(KingdomManager __instance) => MilestoneUnlocks(__instance.kingdomConfig);

        internal static void MilestoneUnlocks(KingdomConfig cfg = null)
        {
            if (FarmData == null) return;
            int before = milestoneUnlocks.Count;
            try
            {
                if (cfg == null) cfg = Resources.FindObjectsOfTypeAll<KingdomConfig>().FirstOrDefault();
                if (cfg == null) return;   // too early; the KingdomManager hook sets them
                var infos = typeof(KingdomConfig).GetFields().Where(f => f.FieldType == typeof(PopulationUnlockInfo)).Select(f => f.GetValue(cfg) as PopulationUnlockInfo).Where(i => i != null && i.rewardData != null).ToList();
                void Gate(WorldObjectData data, int population)
                {
                    if (data == null || population <= 0 || milestoneUnlocks.Any(m => m.data == data)) return;
                    var at = infos.FirstOrDefault(i => i.population == population);
                    if (at == null) { Plugin.Log.LogWarning($"Tailor: no population milestone at {population}; {data.name} stays open from the start."); return; }
                    if (!at.rewardData.worldObjectRewards.Contains(data)) at.rewardData.worldObjectRewards.Add(data);
                    at.rewardData.hasWorldObjectRewards = true;
                    milestoneUnlocks.Add((data, at));
                }
                Gate(FarmData, Plugin.SheepFarmUnlockPopulation.Value);
                Gate(TailorData, Plugin.TailorUnlockPopulation.Value);
                Gate(Confectionery.Data, Plugin.ConfectioneryUnlockPopulation.Value);   // null until its own setup has run; the KingdomManager hook comes back for it
                if (milestoneUnlocks.Count > before) Plugin.Log.LogInfo("Tailor: unlocks at milestones: " + string.Join(", ", milestoneUnlocks.Select(m => $"{m.data.name} at {m.at.population}")) + ".");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: milestone unlocks: " + ex.Message); }
        }

        // A farm or tailor already finished in an older save never unlocked its product; do it when it loads.
        [HarmonyPatch(typeof(Constructable), nameof(Constructable.OnLoad)), HarmonyPostfix]
        private static void UnlockProductOnLoad(Constructable __instance)
        {
            try
            {
                var wo = __instance.GetComponent<WorldObject>();
                if (wo == null || FarmData == null || (wo.data != FarmData && wo.data != TailorData && (Confectionery.Data == null || wo.data != Confectionery.Data))) return;
                if (!__instance.isConstructionComplete || wo.data.resourceProduced == null) return;
                string key = wo.data.resourceProduced.output.resourceData.key;
                if (!DataManager.Instance.IsResourceUnlocked(key)) { DataManager.Instance.UnlockResource(key); Plugin.Log.LogInfo($"Tailor: {key} unlocked for the top bar."); }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: unlock on load: " + ex.Message); }
        }

        // Sheep wander with the simple agent: a random point around the herd's origin, walked to in a straight line.
        // Jess (2026-10-08): centre them 2 units further left, keep them in the pen, never in the barn. Points are chosen
        // in the farm's own frame (barn at the front, -z) inside the pen box.
        private static readonly Dictionary<Vector3, Transform> FarmByOrigin = new Dictionary<Vector3, Transform>();
        private static Transform FarmFor(Vector3 origin)
        {
            if (FarmByOrigin.TryGetValue(origin, out var t) && t != null) return t;
            foreach (var wo in WorldObject.Instances)
            {
                if (wo == null || wo.data != FarmData) continue;
                var wps = wo.GetComponent<UnitWaypoints>();
                var parent = wps != null ? Traverse.Create(wps).Field("waypointParent").GetValue<GameObject>() : null;
                var o = parent != null ? parent.transform.Find("Sheep wander origin") : null;
                var at = o != null ? o.position : wo.transform.position;
                if (Vector3.Distance(new Vector3(at.x, 0f, at.z), new Vector3(origin.x, 0f, origin.z)) < 4f) { FarmByOrigin[origin] = wo.transform; return wo.transform; }
            }
            return null;
        }

        // Inside the pen box, in the farm's own frame; the box keeps clear of the barn, so a straight walk between two
        // points in it never crosses the barn or a fence (the sheep's agent walks in straight lines, no pathfinding).
        internal static Vector3 ClampToPen(Transform farm, Vector3 world)
        {
            var l = farm.InverseTransformPoint(world);
            l.x = Mathf.Clamp(l.x, Plugin.PenMinX.Value, Plugin.PenMaxX.Value);
            l.z = Mathf.Clamp(l.z, Plugin.PenMinZ.Value, Plugin.PenMaxZ.Value);
            l.y = 0f;
            var w = farm.TransformPoint(l); w.y = world.y;
            return w;
        }

        private static readonly AccessTools.FieldRef<WanderAroundOriginSimple, SimpleAgent> WanderAgent = AccessTools.FieldRefAccess<WanderAroundOriginSimple, SimpleAgent>("agent");
        private static readonly AccessTools.FieldRef<WanderAroundOriginSimple, float> WanderSetTime = AccessTools.FieldRefAccess<WanderAroundOriginSimple, float>("destinationSetTime");
        private static readonly AccessTools.FieldRef<WanderAroundOriginSimple, float> WanderPause = AccessTools.FieldRefAccess<WanderAroundOriginSimple, float>("pauseTime");

        [HarmonyPatch(typeof(WanderAroundOriginSimple), "SetDestination"), HarmonyPrefix]
        private static bool PenWander(WanderAroundOriginSimple __instance)
        {
            try
            {
                var go = Food.GameObjectOf(__instance);
                var wo = go != null ? go.GetComponent<WorldObject>() : null;
                if (wo == null || wo.data == null || wo.data.name != "Sheep") return true;
                var agent = WanderAgent(__instance);
                var farm = agent != null ? FarmFor(agent.originUsedForWander) : null;
                if (farm == null) return true;   // a wild herd: the game's own wander
                var centre = farm.InverseTransformPoint(agent.originUsedForWander) + new Vector3(Plugin.SheepCentreShiftX.Value, 0f, 0f);
                float r = Random.Range(Plugin.SheepWanderMin.Value, Plugin.SheepWanderMax.Value), a = Random.Range(0f, Mathf.PI * 2f);
                var target = ClampToPen(farm, farm.TransformPoint(centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r)));
                target.y = go.transform.position.y;
                agent.SetDestination(target);
                WanderSetTime(__instance) = Time.time;
                WanderPause(__instance) = Random.Range(__instance.minTimeBetweenDestinations.Value, __instance.maxTimeBetweenDestinations.Value);
                return false;
            }
            catch { return true; }
        }

        // --- the Tailor's look: the cafe without its furniture, plus a clothing line and a banner ---

        // The cafe's mesh is not readable at runtime, so the trimmed shop (umbrella and tables cut away) ships
        // inside the plugin as an OBJ, exported from the game files by mods/tools/export_tailor_obj.py.
        private static Mesh CutCafeMesh()
        {
            if (cutCafeMesh != null) return cutCafeMesh;
            if (cafePrefab != null)
            {
                var mf = cafePrefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.sharedMesh != null && m.sharedMesh.name == "teaShop_type1_var001");
                cafeMaterials = mf != null ? mf.GetComponent<MeshRenderer>()?.sharedMaterials : null;
            }
            cutCafeMesh = LoadObj("tailor_shop.obj", "Fab tailor shop", (t, tris, subCount) =>
            {
                // The Tailor's own colours (Jess, 2026-10-08): with tailor_albedo.png in the icons folder, the atlas part of
                // the mesh is pointed at that image instead of the shared 8192 sheet. The image is the Cafe's block of the
                // sheet, pixels 5125..6138 across and 1040..2042 down, so the uv maps from that box to 0..1.
                if (subCount < 2 || Icons.LoadTexture("tailor_albedo", false) == null) return;
                const float S = 8192f, x0 = 5125f, x1 = 6138f, y0 = 1040f, y1 = 2042f;
                float u0 = x0 / S, uw = (x1 - x0) / S, vb = 1f - y1 / S, vh = (y1 - y0) / S;
                var done = new HashSet<int>();
                foreach (var i in tris[1])
                    if (done.Add(i)) t[i] = new Vector2((t[i].x - u0) / uw, (t[i].y - vb) / vh);
                ownLook = true;
            });
            return cutCafeMesh;
        }

        // A mesh shipped inside the plugin as an OBJ-like file (see mods/tools/export_*_obj.py): positions, uvs, normals
        // and the extra streams the game's shaders read (colour, uv1, uv2, tangent), faces grouped by submesh ("g N").
        // uvEdit may rewrite uv0 per submesh before the mesh is built.
        internal static Mesh LoadObj(string file, string meshName, System.Action<List<Vector2>, List<int>[], int> uvEdit = null)
        {
            try
            {
                var asm = typeof(Tailor).Assembly;
                var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(file));
                if (resName == null) { Plugin.Log.LogError($"Tailor: {file} not embedded."); return null; }
                string text;
                using (var stream = asm.GetManifestResourceStream(resName))
                using (var reader = new System.IO.StreamReader(stream)) text = reader.ReadToEnd();

                var pos = new List<Vector3>(); var uv = new List<Vector2>(); var nrm = new List<Vector3>();
                // extra per-vertex streams the atlas shader reads (index-aligned with positions): colour, uv1, uv2, tangent
                var col = new List<Color>(); var uv1 = new List<Vector2>(); var uv2 = new List<Vector2>(); var tan = new List<Vector4>();
                var faces = new List<int[]>();
                int sub = 0, subCount = 1;   // "g N" lines mark the submeshes
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length < 2) continue;
                    var p = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                    switch (p[0])
                    {
                        case "g": if (p.Length > 1 && int.TryParse(p[1], out int g)) { sub = Mathf.Max(0, g); subCount = Mathf.Max(subCount, sub + 1); } break;
                        case "v": pos.Add(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv))); break;
                        case "vt": uv.Add(new Vector2(float.Parse(p[1], inv), float.Parse(p[2], inv))); break;
                        case "vn": nrm.Add(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv))); break;
                        case "vc": col.Add(new Color(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv), float.Parse(p[4], inv))); break;
                        case "vt1": uv1.Add(new Vector2(float.Parse(p[1], inv), float.Parse(p[2], inv))); break;
                        case "vt2": uv2.Add(new Vector2(float.Parse(p[1], inv), float.Parse(p[2], inv))); break;
                        case "vtan": tan.Add(new Vector4(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv), float.Parse(p[4], inv))); break;
                        case "f":
                            var tri = new int[10];
                            tri[9] = sub;
                            for (int i = 0; i < 3; i++)
                            {
                                var q = p[i + 1].Split('/');
                                tri[i * 3] = int.Parse(q[0]) - 1;
                                tri[i * 3 + 1] = q.Length > 1 && q[1].Length > 0 ? int.Parse(q[1]) - 1 : -1;
                                tri[i * 3 + 2] = q.Length > 2 && q[2].Length > 0 ? int.Parse(q[2]) - 1 : -1;
                            }
                            faces.Add(tri);
                            break;
                    }
                }
                // Unity wants one uv and normal per vertex: build vertices per unique (position, uv, normal) corner.
                var corners = new Dictionary<(int, int, int), int>();
                var v = new List<Vector3>(); var t = new List<Vector2>(); var n = new List<Vector3>();
                var c = new List<Color>(); var t1 = new List<Vector2>(); var t2 = new List<Vector2>(); var tg = new List<Vector4>();
                var tris = new List<int>[subCount];
                for (int s = 0; s < subCount; s++) tris[s] = new List<int>();
                foreach (var f in faces)
                    for (int i = 0; i < 3; i++)
                    {
                        var k = (f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
                        if (!corners.TryGetValue(k, out int idx))
                        {
                            idx = v.Count; corners[k] = idx;
                            v.Add(pos[k.Item1]);
                            t.Add(k.Item2 >= 0 && k.Item2 < uv.Count ? uv[k.Item2] : Vector2.zero);
                            n.Add(k.Item3 >= 0 && k.Item3 < nrm.Count ? nrm[k.Item3] : Vector3.up);
                            int pi = k.Item1;
                            if (col.Count == pos.Count) c.Add(col[pi]);
                            if (uv1.Count == pos.Count) t1.Add(uv1[pi]);
                            if (uv2.Count == pos.Count) t2.Add(uv2[pi]);
                            if (tan.Count == pos.Count) tg.Add(tan[pi]);
                        }
                        tris[f[9]].Add(idx);
                    }
                var mesh = new Mesh { name = meshName, indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.SetVertices(v); mesh.SetUVs(0, t); mesh.SetNormals(n);
                if (c.Count == v.Count) mesh.SetColors(c);
                if (t1.Count == v.Count) mesh.SetUVs(1, t1);
                if (t2.Count == v.Count) mesh.SetUVs(2, t2);
                if (tg.Count == v.Count) mesh.SetTangents(tg);
                if (uvEdit != null) { uvEdit(t, tris, subCount); mesh.SetUVs(0, t); }
                mesh.subMeshCount = subCount;   // one submesh per material, in the source mesh's order
                for (int s = 0; s < subCount; s++) mesh.SetTriangles(tris[s], s);
                mesh.RecalculateBounds();
                Plugin.Log.LogInfo($"Tailor: {file} loaded ({v.Count} vertices).");
                return mesh;
            }
            catch (System.Exception ex) { Plugin.Log.LogError($"Tailor: {file} failed: " + ex); return null; }
        }

        // The Tailor's plot (Jess, 2026-10-08: brush still growing inside it). The ground under a finished building is a
        // painted ground texture, and painted ground is what the grass and bushes give way to. The game's own draw for the
        // Tailor stub threw before painting; this runs after it whatever happened (a finalizer), and paints every cell
        // with the configured texture (STONE_TILES by default, the paving round the Palace; DIRT is the Cafe's).
        [HarmonyPatch(typeof(AlterGroundTexture), "DrawOnConstructed"), HarmonyFinalizer]
        private static System.Exception PaveTailor(AlterGroundTexture __instance, System.Exception __exception)
        {
            try
            {
                var wo = __instance.GetComponent<WorldObject>();
                if (wo == null || TailorData == null || !GroundFor(wo.data, out GroundTexture tex)) return __exception;
                if (__exception != null) Plugin.Log.LogWarning("Tailor: the game's ground draw failed (" + __exception.Message + "); painting the plot instead.");
                var grid = __instance.GetComponent<GridObject>();
                if (grid == null || grid.cells == null) return null;
                foreach (var cell in grid.cells)
                {
                    GroundTexureRenderer.Instance.DrawGroundTexture(cell.coord.x, cell.coord.z, tex, null, true, false, 15f, ShapeType.SQUARE, 0f, null, false);
                    ShaderManager.Instance.AddGroundEditor(cell.coord, __instance);
                }
                if (!pavedLogged) { pavedLogged = true; Plugin.Log.LogInfo($"Tailor: plot painted with {tex} ({grid.cells.Count} cells)."); }
                return null;   // the plot is painted either way; nothing left to fail
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: paving: " + ex.Message); return __exception; }
        }
        // Snow on a building is worked out per renderer: the game's pieces carry a SnowShaderBase that tells the shader how
        // high they stand, and their materials sit on the ShaderManager's seasonal lists. Pieces we build get the same
        // component as their source piece (Jess, 2026-10-08: overlays stayed bare in winter).
        internal static void CopySnow(GameObject part, Component source)
        {
            try
            {
                var sb = source != null ? source.GetComponent<SnowShaderBase>() : null;
                if (sb != null && part.GetComponent<SnowShaderBase>() == null)
                {
                    var copy = part.AddComponent<SnowShaderBase>();
                    Traverse.Create(copy).Field("removeNormals").SetValue(Traverse.Create(sb).Field("removeNormals").GetValue<bool>());
                }
                // a material on neither seasonal list never gets snow (the Chancery tower's cap, roofProtection_material);
                // put it on the snow list if its shader has snow at all
                var smgr = ShaderManager.Instance;
                if (smgr != null && smgr.materialsAffectedBySnow != null)
                    foreach (var m in part.GetComponent<Renderer>()?.sharedMaterials ?? new Material[0])
                        if (m != null && m.HasProperty("_Snow_Amount") && !smgr.materialsAffectedBySnow.Contains(m) && (smgr.atlas == null || !smgr.atlas.Contains(m)))
                            smgr.materialsAffectedBySnow.Add(m);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: snow copy: " + ex.Message); }
        }

        private static bool pavedLogged;

        // Which of our buildings get a painted plot, and with what: the Tailor (Jess, 2026-10-08) and the Confectionery
        // (Jess, 2026-10-15: "something under it to suppress the vegetation").
        internal static bool GroundFor(WorldObjectData data, out GroundTexture tex)
        {
            tex = GroundTexture.STONE_TILES;
            string name = data == null ? null : data == TailorData ? Plugin.TailorGround.Value : Confectionery.Data != null && data == Confectionery.Data ? Plugin.ConfGround.Value : null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (!System.Enum.TryParse(name, true, out tex)) tex = GroundTexture.STONE_TILES;
            return true;
        }

        // The game's draw is not reliably called for the Tailor (2026-10-08: no draw after a load at all), so every
        // finished Tailor's plot is painted after a load and once a day, which also catches new ones.
        private static readonly HashSet<WorldObject> Paved = new HashSet<WorldObject>();
        internal static void PaveAll(string when)
        {
            if (TailorData == null || GroundTexureRenderer.Instance == null) return;
            int n = 0;
            foreach (var wo in WorldObject.Instances.ToList())
            {
                if (wo == null || Paved.Contains(wo) || !GroundFor(wo.data, out GroundTexture tex)) continue;
                var c = wo.GetComponent<Constructable>(); var grid = wo.GetComponent<GridObject>(); var agt = wo.GetComponent<AlterGroundTexture>();
                if (c == null || !c.isConstructionComplete || grid == null || grid.cells == null) continue;
                try
                {
                    foreach (var cell in grid.cells)
                    {
                        GroundTexureRenderer.Instance.DrawGroundTexture(cell.coord.x, cell.coord.z, tex, null, true, false, 15f, ShapeType.SQUARE, 0f, null, false);
                        if (agt != null) ShaderManager.Instance.AddGroundEditor(cell.coord, agt);
                    }
                    Paved.Add(wo); n++;
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: paving: " + ex.Message); }
            }
            if (n > 0) Plugin.Log.LogInfo($"Tailor: {n} plot(s) painted ({when}).");
        }

        // Tailor's fabrics on the clothing lines (Jess's swatch sheet, 2026-10-08): the line's mesh is swapped for our copy
        // whose cloth panels are their own submesh with uvs on tailor_fabric.png (mods/tools/export_clothline_obj.py), and the
        // panels get a copy of the line's material with that sheet as colour and flat bump/shine maps. Poles and rope keep
        // the game's look and its wind sway. Missing sheet or mesh: the line stays as the game made it.
        private static readonly Dictionary<string, Mesh> fabricMeshes = new Dictionary<string, Mesh>();
        private static readonly Dictionary<Material, Material> fabricMats = new Dictionary<Material, Material>();
        private static readonly HashSet<string> fabricLogged = new HashSet<string>();
        private static void Fabric(Transform holder, string meshName)
        {
            try
            {
                var sheet = Icons.LoadTexture("tailor_fabric", false);
                if (sheet == null || holder == null || string.IsNullOrEmpty(meshName)) return;
                var mf = holder.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.sharedMesh != null && m.sharedMesh.name == meshName);
                var mr = mf != null ? mf.GetComponent<MeshRenderer>() : null;
                if (mr == null || mr.sharedMaterial == null) { if (fabricLogged.Add("no " + meshName)) Plugin.Log.LogInfo($"Tailor: fabric: no '{meshName}' on this line."); return; }
                if (!fabricMeshes.TryGetValue(meshName, out var mesh)) fabricMeshes[meshName] = mesh = LoadObj(meshName + ".obj", "Fab " + meshName);
                if (mesh == null || mesh.subMeshCount < 2) return;
                var baseMat = mr.sharedMaterial;
                if (!fabricMats.TryGetValue(baseMat, out var cloth))
                {
                    cloth = new Material(baseMat) { name = "Fab tailor fabric" };
                    if (cloth.HasProperty("Texture2D_F1E595C5")) cloth.SetTexture("Texture2D_F1E595C5", sheet);
                    if (cloth.HasProperty("Texture2D_32A6F549")) cloth.SetTexture("Texture2D_32A6F549", Flat(new Color32(128, 128, 255, 128), true));   // flat bumps
                    if (cloth.HasProperty("Texture2D_3A3EF09F")) cloth.SetTexture("Texture2D_3A3EF09F", Flat(new Color32(0, 0, 0, 0), true));
                    if (cloth.HasProperty("_Emission")) cloth.SetTexture("_Emission", Flat(new Color32(0, 0, 0, 0), false));
                    fabricMats[baseMat] = cloth;
                    Seasonal(cloth);
                }
                mf.sharedMesh = mesh;
                mr.sharedMaterials = new[] { baseMat, cloth };
                if (fabricLogged.Add(meshName)) Plugin.Log.LogInfo($"Tailor: fabric hung on {meshName}.");   // once a session per line mesh
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: fabric: " + ex.Message); }
        }
        // The game turns snow, rain and the time of day up and down on a fixed list of its own atlas materials; a copy we
        // make is not on it, so it never got snow (Jess, 2026-10-08: the Tailor stayed bare in winter). Put ours on it.
        private static readonly List<Material> seasonalPending = new List<Material>();
        internal static void Seasonal(Material m)
        {
            if (m == null) return;
            var sm = ShaderManager.Instance;
            if (sm == null || sm.atlas == null) { if (!seasonalPending.Contains(m)) seasonalPending.Add(m); return; }
            foreach (var p in seasonalPending) if (p != null && !sm.atlas.Contains(p)) sm.atlas.Add(p);
            seasonalPending.Clear();
            if (!sm.atlas.Contains(m)) sm.atlas.Add(m);
        }
        internal static void SeasonalPending() { if (seasonalPending.Count > 0) Seasonal(seasonalPending[0]); }

        private static readonly Dictionary<string, Texture2D> flats = new Dictionary<string, Texture2D>();
        private static Texture2D Flat(Color32 c, bool linear)
        {
            string k = c.ToString() + linear;
            if (flats.TryGetValue(k, out var t)) return t;
            t = new Texture2D(4, 4, TextureFormat.RGBA32, false, linear) { name = "Fab flat " + k };
            var px = new Color32[16]; for (int i = 0; i < 16; i++) px[i] = c;
            t.SetPixels32(px); t.Apply();
            return flats[k] = t;
        }

        // The Cafe's materials with the atlas one copied and given the Tailor's textures (colour from Jess's image; bumps,
        // roughness and glow cut from the same block of the game's own maps). The Cafe itself keeps the shared sheet.
        private static bool ownLook;
        private static Material[] ownMaterials;
        private static Material[] OwnMaterials()
        {
            if (ownMaterials != null) return ownMaterials;
            ownMaterials = (Material[])cafeMaterials.Clone();
            try
            {
                var m = new Material(cafeMaterials[1]) { name = "Fab tailor atlas" };
                void Set(string prop, string file, bool linear)
                {
                    var tex = Icons.LoadTexture(file, linear);
                    if (tex != null && m.HasProperty(prop)) m.SetTexture(prop, tex);
                    else Plugin.Log.LogInfo($"Tailor: own look: {prop} {(tex == null ? "has no " + file + ".png" : "is not on the material")}.");
                }
                Set("Texture2D_F1E595C5", "tailor_albedo", false);
                Set("Texture2D_32A6F549", "tailor_normal", true);
                Set("Texture2D_3A3EF09F", "tailor_roughness", true);
                Set("_Emission", "tailor_emission", false);
                ownMaterials[1] = m;
                Seasonal(m);
                Plugin.Log.LogInfo("Tailor: own look applied (atlas material copied with the Tailor's textures).");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: own look: " + ex.Message); ownMaterials = cafeMaterials; }
            return ownMaterials;
        }

        [HarmonyPatch(typeof(Constructable), "Awake"), HarmonyPostfix]
        private static void Dress(Constructable __instance)
        {
            var wo = __instance.GetComponent<WorldObject>();
            if (wo == null || TailorData == null || wo.data != TailorData) return;
            try
            {
                var root = __instance.transform;
                var mesh = CutCafeMesh();
                var main = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == "Mesh - Main");
                if (mesh != null && main != null)
                {
                    main.sharedMesh = mesh;
                    var cafeMain = cafePrefab != null ? cafePrefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.gameObject.name == "Mesh - Main") : null;
                    CopySnow(main.gameObject, cafeMain);
                    var mr = main.GetComponent<MeshRenderer>();
                    if (mr != null && cafeMaterials != null) mr.sharedMaterials = ownLook ? OwnMaterials() : cafeMaterials;
                }
                var deco = new GameObject("Fab tailor deco");
                deco.transform.SetParent(root, false);
                if (Plugin.TailorClothingLine.Value)
                {
                    AddPart(deco.transform, linePrefab, new Vector3(Plugin.LineX.Value, 0f, Plugin.LineZ.Value), Plugin.LineYaw.Value, "house_attachments_cloth_line_base");
                    Fabric(deco.transform.GetChild(deco.transform.childCount - 1), "house_attachments_cloth_line");
                }
                if (Plugin.TailorSecondLine.Value)
                {
                    AddPart(deco.transform, linePrefab, new Vector3(Plugin.Line2X.Value, 0f, Plugin.Line2Z.Value), Plugin.Line2Yaw.Value, "house_attachments_cloth_line_base");
                    // the game ships two more arrangements of washing (_1 and _3) that its own prefab never uses; hang a different one here
                    var holder = deco.transform.GetChild(deco.transform.childCount - 1);
                    var variant = Resources.FindObjectsOfTypeAll<Mesh>().FirstOrDefault(m => m != null && m.name == Plugin.Line2Variant.Value);
                    var mf2 = holder.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.sharedMesh != null && m.sharedMesh.name == "house_attachments_cloth_line");
                    if (variant != null && mf2 != null) mf2.sharedMesh = variant;
                    else if (fabricLogged.Add("second line")) Plugin.Log.LogInfo($"Tailor: second line keeps the usual washing ({(variant == null ? "variant mesh not loaded" : "line mesh not found")}).");
                    Fabric(holder, Plugin.Line2Variant.Value);
                }
                if (Plugin.TailorBanner.Value) AddPart(deco.transform, bannerPrefab, new Vector3(Plugin.BannerX.Value, 0f, Plugin.BannerZ.Value), 0f, null);
                if (Plugin.TailorMannequins.Value && puppetPrefab != null)
                {
                    // two cardboard standees from the puppet show, as mannequins showing off the wares
                    var standees = puppetPrefab.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh != null && m.sharedMesh.name.StartsWith("cardBoard")).Take(2).ToList();
                    for (int i = 0; i < standees.Count; i++)
                        AddSingle(deco.transform, puppetPrefab, standees[i], new Vector3(Plugin.MannequinX.Value * (i == 0 ? -1f : 1f) + Plugin.MannequinShiftX.Value, 0f, Plugin.MannequinZ.Value), 180f, FindSprite(i == 0 ? Plugin.MannequinSprite1.Value : Plugin.MannequinSprite2.Value));
                }
                deco.SetActive(__instance.isConstructionComplete);
                __instance.OnConstructionComplete += () => { if (deco != null) deco.SetActive(true); };
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor dressing failed: " + ex); }
        }

        [HarmonyPatch(typeof(Constructable), nameof(Constructable.OnLoad)), HarmonyPostfix]
        private static void AfterLoad(Constructable __instance)
        {
            var wo = __instance.GetComponent<WorldObject>();
            if (wo == null || TailorData == null || wo.data != TailorData) return;
            var deco = __instance.transform.Find("Fab tailor deco");
            if (deco != null && __instance.isConstructionComplete) deco.gameObject.SetActive(true);
        }

        // A dressed fabling for the mannequins: the game's noble and commoner pictures are standalone 256x256 textures.
        private static Sprite FindSprite(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var sp = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(x => x != null && x.name == name);
            if (sp == null) Plugin.Log.LogWarning("Tailor: no sprite named " + name + " for a mannequin.");
            return sp;
        }

        // One mesh of a prefab, stood on its own feet at a spot.
        private static void AddSingle(Transform parent, GameObject prefab, MeshFilter mf, Vector3 at, float yaw, Sprite picture = null)
        {
            var wb = mf.sharedMesh.bounds;
            var lo = prefab.transform.InverseTransformPoint(mf.transform.TransformPoint(wb.min));
            var hi = prefab.transform.InverseTransformPoint(mf.transform.TransformPoint(wb.max));
            var centre = (lo + hi) * 0.5f;
            var holder = new GameObject("Fab part " + mf.sharedMesh.name);
            holder.transform.SetParent(parent, false);
            holder.transform.localPosition = at;
            holder.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            var part = new GameObject(mf.sharedMesh.name);
            part.transform.SetParent(holder.transform, false);
            part.transform.localPosition = prefab.transform.InverseTransformPoint(mf.transform.position) - new Vector3(centre.x, Mathf.Min(lo.y, hi.y), centre.z);
            part.transform.localRotation = Quaternion.Inverse(prefab.transform.rotation) * mf.transform.rotation;
            part.transform.localScale = mf.transform.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var mr = part.AddComponent<MeshRenderer>();
            CopySnow(part, mf);
            mr.sharedMaterials = mf.GetComponent<MeshRenderer>().sharedMaterials;
            // The puppet show's standees get their picture from a CutoutSprite, which pushes the sprite's texture
            // into the renderer through a property block; do the same for our copy or it shows as a blank card.
            var cutout = mf.GetComponent<CutoutSprite>() ?? mf.GetComponentInParent<CutoutSprite>();
            var sprite = picture ?? (cutout != null ? Traverse.Create(cutout).Field("sprite").GetValue<Sprite>() : null);
            if (sprite != null && sprite.texture != null)
            {
                var block = new MaterialPropertyBlock();
                mr.GetPropertyBlock(block);
                block.SetTexture("_Base_Texture_A", sprite.texture);
                mr.SetPropertyBlock(block);
            }
        }

        private static void AddPart(Transform parent, GameObject prefab, Vector3 at, float yaw, string skipMesh)
        {
            if (prefab == null) return;
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true)
                .Where(mf => mf.sharedMesh != null && mf.GetComponent<MeshRenderer>() != null)
                .Where(mf => { var n = mf.sharedMesh.name.ToLowerInvariant(); return n != (skipMesh ?? "") && !n.Contains("woodpoles") && !n.Contains("confirmation") && !n.Contains("groundtexture"); })
                .ToList();
            if (filters.Count == 0) return;
            Bounds b = new Bounds(); bool first = true;
            foreach (var mf in filters)
            {
                var wb = mf.sharedMesh.bounds;
                foreach (var c in new[] { wb.min, wb.max })
                {
                    var p = prefab.transform.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
                }
            }
            var offset = new Vector3(b.center.x, b.min.y, b.center.z);
            var holder = new GameObject("Fab part " + prefab.name);
            holder.transform.SetParent(parent, false);
            holder.transform.localPosition = at;
            holder.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            foreach (var mf in filters)
            {
                var part = new GameObject(mf.sharedMesh.name);
                part.transform.SetParent(holder.transform, false);
                part.transform.localPosition = prefab.transform.InverseTransformPoint(mf.transform.position) - offset;
                part.transform.localRotation = Quaternion.Inverse(prefab.transform.rotation) * mf.transform.rotation;
                part.transform.localScale = mf.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = mf.GetComponent<MeshRenderer>().sharedMaterials;
                CopySnow(part, mf);
            }
        }
    }
}
