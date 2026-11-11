using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BehaviorDesigner.Runtime.Tasks;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // The shearer's own routine, like the miller's and the tailor's, walks into the building and hides there while
    // production runs. Jess wants them seen: in the pen working the sheep with the crop farmer's scythe swing, or out
    // front in winter. So for a Sheep farm worker "go inside" means "go to the farm's work spot", the inside check is
    // satisfied while they stand there, and "go outside" (before a grain run) clears it.
    [HarmonyPatch]
    internal static class Shearer
    {
        private static readonly HashSet<Unit> AtSpot = new HashSet<Unit>();

        private static bool IsShearer(Worker w) => w != null && w.workPlace != null && w.workPlace.worldObject != null && Tailor.FarmData != null && w.workPlace.worldObject.data == Tailor.FarmData;

        private static Transform Spot(Worker w)
        {
            var wps = w.workPlace.GetComponent<UnitWaypoints>();
            var parent = Tailor.WaypointParent(wps);
            return parent != null ? parent.transform.Find("Fab work spot 0") : null;
        }

        [HarmonyPatch(typeof(GoInside), nameof(GoInside.OnUpdate)), HarmonyPrefix]
        private static bool ToTheSpotInstead(GoInside __instance, ref TaskStatus __result)
        {
            try
            {
                var go = Food.GameObjectOf(__instance);
                var worker = go != null ? go.GetComponent<Worker>() : null;
                if (!IsShearer(worker)) return true;
                var unit = go.GetComponent<Unit>();
                if (unit != null && AtSpot.Contains(unit)) { __result = TaskStatus.Success; return false; }   // already there, or on the way
                if (!SendToSpot(unit, worker)) return true;
                __result = TaskStatus.Success;
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: shearer spot: " + ex.Message); return true; }
        }

        // A patrol every few seconds: a Sheep farm worker who is not at the spot, has empty hands and is on the
        // farm's idle or production branch (not out fetching grain) is sent to the spot, however they got into the
        // barn.
        internal static IEnumerator Patrol()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(5f);
                try { AtSpot.RemoveWhere(u => u == null); } catch { }   // a shearer gone (dead, destroyed) leaves the spot's record
                try
                {
                    if (Worker.Instances == null) continue;
                    var placed = new HashSet<WorldObject>();
                    foreach (var w in Worker.Instances)
                    {
                        if (!IsShearer(w)) continue;
                        // out front when the wool store fills, back in the pen once there is room (checked here, not daily)
                        if (placed.Add(w.workPlace.worldObject)) Tailor.PlaceFarmSpots(w.workPlace.gameObject, Winter.StandsInFront(w.workPlace.worldObject));
                        PenWalk.Ensure(w.workPlace.worldObject);
                        var unit = w.GetComponent<Unit>();
                        if (unit == null || AtSpot.Contains(unit)) continue;
                        string tasks = ActiveTasks(w.gameObject);
                        bool idle = tasks.Contains("Idle") || tasks.Contains("Wait") || tasks.Contains("IsInside") || tasks.Contains("GoInside") || tasks.Contains("SetProductionStatus");
                        bool busy = w.isTransportingResource || (w.storage != null && w.storage.GetResourcesStoredTotal() > 0) || tasks.Contains("PickupResource") || tasks.Contains("UnitSeek");
                        if (idle && !busy) SendToSpot(unit, w);
                    }
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: shearer patrol: " + ex.Message); }
            }
        }

        // The names of the worker's running behaviour-tree tasks, which the patrol reads to tell idle from busy.
        private static string ActiveTasks(GameObject go)
        {
            try
            {
                var bt = go != null ? go.GetComponent<BehaviorDesigner.Runtime.Behavior>() : null;
                var tasks = bt != null ? bt.GetActiveTasks() : null;
                if (tasks == null || tasks.Count == 0) return "(none)";
                return string.Join(" > ", tasks.Select(t => t.GetType().Name));
            }
            catch (System.Exception ex) { return "(unknown: " + ex.Message + ")"; }
        }

        private static readonly HashSet<Worker> NoSpotLogged = new HashSet<Worker>();
        private static bool sentLogged, nudgeLogged;
        private static bool SendToSpot(Unit unit, Worker worker)
        {
            var spot = Spot(worker);
            if (unit == null || unit.ai == null || spot == null)
            {
                if (NoSpotLogged.Add(worker)) Plugin.Log.LogWarning($"Tailor: cannot send {unit?.GetLocalizedBirthName()} to the Sheep farm's work spot: unit={(unit != null)}, ai={(unit != null && unit.ai != null)}, spot={(spot != null)}.");
                return false;
            }
            if (unit.isInside) unit.GoOutside();
            unit.ai.SetDestination(spot.position, spot.gameObject);
            AtSpot.Add(unit);
            Plugin.Instance.StartCoroutine(WorkTheSheep(unit, worker, spot));
            // said once a session; it happens after every grain run
            if (!sentLogged) { sentLogged = true; Plugin.Log.LogInfo($"Tailor: {unit.GetLocalizedBirthName()} sent to the Sheep farm's work spot."); }
            return true;
        }

        // Standing at the spot counts as being inside, so the routine does not send them back to the door.
        [HarmonyPatch(typeof(IsInside), nameof(IsInside.OnUpdate)), HarmonyPostfix]
        private static void SpotCounts(IsInside __instance, ref TaskStatus __result)
        {
            try
            {
                var go = Food.GameObjectOf(__instance);
                var unit = go != null ? go.GetComponent<Unit>() : null;
                if (unit == null) return;
                if (__result == TaskStatus.Success)
                {
                    // Already inside the barn (a loaded save puts the shearer there, and "go inside" is never run
                    // again): step out to the work spot instead.
                    if (unit.isInside && !AtSpot.Contains(unit)) { var w = go.GetComponent<Worker>(); if (IsShearer(w)) SendToSpot(unit, w); }
                    return;
                }
                if (AtSpot.Contains(unit)) __result = TaskStatus.Success;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: shearer inside check: " + ex.Message); }
        }

        [HarmonyPatch(typeof(Unit), nameof(Unit.GoOutside)), HarmonyPostfix]
        private static void LeftTheSpot(Unit __instance) => AtSpot.Remove(__instance);

        // Something hides a shearer who is at the spot (seen 2026-10-07: a floating sickle in front of the barn).
        // Refuse it, with a warning once per worker.
        private static readonly HashSet<Unit> HiddenLogged = new HashSet<Unit>();
        [HarmonyPatch(typeof(Unit), nameof(Unit.GoInside)), HarmonyPrefix]
        private static bool NotWhileAtTheSpot(Unit __instance)
        {
            try
            {
                if (!AtSpot.Contains(__instance)) return true;
                var w = __instance.GetComponent<Worker>();
                if (!IsShearer(w)) return true;
                if (HiddenLogged.Add(__instance)) Plugin.Log.LogWarning($"Tailor: {__instance.GetLocalizedBirthName()} was sent inside while at the work spot; refused.");
                return false;
            }
            catch { return true; }
        }

        // Once there: the scythe swing every few seconds while the flock is being sheared, with one of the farm's sheep
        // brought over to stand beside the shearer and swapped for another every few days; just standing in winter.
        private static IEnumerator WorkTheSheep(Unit unit, Worker worker, Transform spot)
        {
            float t = 0f;
            while (unit != null && AtSpot.Contains(unit) && t < 20f && !unit.ai.ReachedDestination()) { t += Time.deltaTime; yield return null; }
            float next = 0f, nextShoo = 0f, nextFace = 0f, nextNudge = 0f;
            int faceSign = 0;   // out front: +1 faces south (away from the farm), -1 north; rolled afresh each stint (Jess, 2026-10-10)
            HerdAnimal held = null; int heldSince = -1;
            Vector3 sentTo = spot.position;
            while (unit != null && AtSpot.Contains(unit) && IsShearer(worker))
            {
                if (Vector3.Distance(spot.position, sentTo) > 0.5f) { sentTo = spot.position; unit.ai.SetDestination(spot.position, spot.gameObject); }
                // stopped well short of the spot (a path cut off by a navigation rescan, see PenWalk): carve and send again
                if (Time.time >= nextNudge && unit.ai != null && !unit.ai.IsMoving() && Vector3.Distance(unit.transform.position, spot.position) > 2.5f)
                {
                    nextNudge = Time.time + 5f;
                    PenWalk.Ensure(worker.workPlace.worldObject);
                    unit.ai.SetDestination(spot.position, spot.gameObject);
                    if (!nudgeLogged) { nudgeLogged = true; Plugin.Log.LogInfo($"Tailor: {unit.GetLocalizedBirthName()} stood {Vector3.Distance(unit.transform.position, spot.position):0.#} units from the work spot and was sent again (said once a session)."); }
                }
                bool shearing = !Winter.StandsInFront(worker.workPlace.worldObject) && !string.IsNullOrEmpty(Plugin.ShearAnimation.Value);
                int today = DateTimeManager.Instance != null ? DateTimeManager.Instance.GetTotalDaysTranspired() : 0;
                if (shearing)
                {
                    faceSign = 0;
                    if (held == null || today - heldSince >= Plugin.ShearSheepDays.Value)
                    {
                        Release(held);
                        held = PickSheep(worker, held);
                        heldSince = today;
                        if (held != null) Hold(held, spot, unit, worker.workPlace.transform.forward);
                    }
                    if (Time.time >= nextShoo) { nextShoo = Time.time + 0.25f; Shoo(worker, held, unit); }
                    if (Time.time >= next)
                    {
                        // face the sheep where it actually stands, every swing (Jess, 2026-10-08): the one turn when it was
                        // picked was skipped while the shearer was still walking, and the sheep was not in place yet
                        try
                        {
                            if (held != null && unit.ai != null && !unit.ai.IsMoving())
                            {
                                var to = held.transform.position - unit.transform.position; to.y = 0f;
                                if (to.sqrMagnitude > 0.05f) unit.ai.ForceDirectionRotate(Quaternion.LookRotation(to.normalized).eulerAngles, 0.25f);
                            }
                        }
                        catch { }
                        try { unit.animatorController.SetTrigger(Plugin.ShearAnimation.Value, "sickle"); } catch { }
                        next = Time.time + Plugin.ShearSwingSeconds.Value;
                    }
                }
                else
                {
                    if (held != null) { Release(held); held = null; }
                    // waiting out front (winter, recovery, a full wool store): face straight south or north, chosen at
                    // random when the stint begins (Jess, 2026-10-08: the pig keeper's angle looked odd here; 2026-10-10: vary it)
                    if (faceSign == 0) faceSign = UnityEngine.Random.value < 0.5f ? 1 : -1;
                    if (Time.time >= nextFace)
                    {
                        nextFace = Time.time + 2f;
                        try
                        {
                            var face = -worker.workPlace.transform.forward * faceSign; face.y = 0f;
                            if (unit.ai != null && !unit.ai.IsMoving() && face.sqrMagnitude > 0.01f && Vector3.Angle(unit.transform.forward, face) > 10f)
                                unit.ai.ForceDirectionRotate(Quaternion.LookRotation(face.normalized).eulerAngles, 0.4f);
                        }
                        catch { }
                    }
                }
                yield return null;
            }
            Release(held);
        }

        // The rest of the flock walks round the shearer, not through them: any sheep that strays within reach is sent a few
        // steps further off, away from the shearer (Jess, 2026-10-08).
        private static void Shoo(Worker worker, HerdAnimal held, Unit shearer)
        {
            float reach = Plugin.SheepKeepAway.Value;
            if (reach <= 0f) return;
            try
            {
                var farm = worker.workPlace.GetComponent<AnimalHerdFarm>();
                var herd = Tailor.HerdOf(farm);
                if (herd == null || herd.animals == null) return;
                var here = shearer.transform.position;
                foreach (var a in herd.animals)
                {
                    if (a == null || a == held) continue;
                    var away = a.transform.position - here; away.y = 0f;
                    if (away.sqrMagnitude > reach * reach) continue;
                    if (away.sqrMagnitude < 0.01f) away = -shearer.transform.forward;
                    var agent = a.GetComponent<SimpleAgent>();
                    var farmT = worker.workPlace.transform;
                    if (agent != null) agent.SetDestination(Tailor.ClampToPen(farmT, here + away.normalized * (reach + 2f)));   // never into the barn or a fence
                }
            }
            catch { }
        }

        private static HerdAnimal PickSheep(Worker worker, HerdAnimal not)
        {
            try
            {
                var farm = worker.workPlace.GetComponent<AnimalHerdFarm>();
                var herd = Tailor.HerdOf(farm);
                if (herd == null || herd.animals == null) return null;
                var choices = herd.animals.FindAll(a => a != null && a != not);
                if (choices.Count == 0) choices = herd.animals.FindAll(a => a != null);
                return choices.Count > 0 ? choices[Random.Range(0, choices.Count)] : null;
            }
            catch { return null; }
        }

        // The chosen sheep stops wandering and walks to the shearer's side.
        private static void Hold(HerdAnimal sheep, Transform spot, Unit shearer, Vector3 back)
        {
            try
            {
                var btc = sheep.GetComponent<BehaviorTreeController>();
                if (btc != null && btc.current != null) btc.current.DisableBehavior();
                var agent = sheep.GetComponent<SimpleAgent>();
                // from the work spot toward the back fence (Jess, 2026-10-08: it stood on top of the shearer), and the
                // shearer turns to face it
                var facing = back;
                facing.y = 0f; if (facing.sqrMagnitude < 0.01f) facing = Vector3.forward; facing.Normalize();
                var farmT = shearer != null && shearer.GetComponent<Worker>()?.workPlace != null ? shearer.GetComponent<Worker>().workPlace.transform : null;
                var place = spot.position + facing * Plugin.SheepGap.Value;
                if (farmT != null) place = Tailor.ClampToPen(farmT, place);
                if (agent != null) agent.SetDestination(place);
                try { if (shearer != null && shearer.ai != null) shearer.ai.ForceDirectionRotate(Quaternion.LookRotation(facing).eulerAngles, 0.3f); } catch { }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: holding a sheep: " + ex.Message); }
        }

        // Back to the flock.
        private static void Release(HerdAnimal sheep)
        {
            if (sheep == null) return;
            try
            {
                var btc = sheep.GetComponent<BehaviorTreeController>();
                if (btc != null && btc.current != null) { btc.current.EnableBehavior(); btc.Restart(); }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: releasing a sheep: " + ex.Message); }
        }
    }
}
