using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomFixes
{
    // Fablings that fell through the world (found 2026-10-05: five of them at the map origin, kilometres down).
    // A unit under the map never gets a path, so every walk "arrives" at once and it hauls goods by remote control.
    // The falls happen in play, through teleports and agent moves that land at the origin (the pathfinder's answer
    // when it finds nothing) or far below the ground. Those are refused here, and a sweep puts any unit that still
    // ends up lost back on the ground: at its home's door, else the Embassy's, else the map's centre. The sweep runs
    // every few seconds and in a series of looks after a load.
    internal static class FallGuard
    {
        private static bool AtOrigin(Vector3 v) => Mathf.Abs(v.x) < 0.5f && Mathf.Abs(v.z) < 0.5f;
        private static bool Bad(Vector3 v) => AtOrigin(v) || v.y < -20f || float.IsNaN(v.x);

        // The game's "teleport to the closest walkable spot" step (run when a unit leaves a mount or a vehicle) trusts
        // the pathfinder's answer; when the pathfinder finds nothing it answers (0,0,0), and the unit is dropped there to
        // fall for good. An answer at the origin, far from the unit, or off the grid is replaced by the unit's own last
        // wander origin (else the Embassy, else the map's centre).
        [HarmonyPatch(typeof(TeleportToClosestWalkable), nameof(TeleportToClosestWalkable.OnUpdate)), HarmonyPrefix]
        private static bool GuardTeleport(TeleportToClosestWalkable __instance, ref BehaviorDesigner.Runtime.Tasks.TaskStatus __result)
        {
            try
            {
                var go = Traverse.Create(__instance).Field("gameObject").GetValue<GameObject>();
                var ai = go != null ? go.GetComponent<AstarAgent>() : null;
                if (go == null || ai == null) return true;
                var here = go.transform.position;
                var nearest = NavigationManager.GetNearestWalkablePosition(here);
                bool bad = AtOrigin(nearest) || Vector3.Distance(nearest, here) > 60f || !GridManager.IsWorldPosWithinGrid(nearest);
                if (!bad) return true;
                var safe = ai.originUsedForWander;
                if (AtOrigin(safe)) safe = Embassy.Current != null ? Embassy.Current.transform.position : GridManager.GetWorldCenter();
                Plugin.Log.LogWarning($"[falls] Walkable teleport REFUSED for {Who(go)} at {here} (pathfinder answered {nearest}); sent to {safe}.");
                if (ai.isPathfindingDisabled) ai.EnableAi();
                ai.TeleportWithRaycastHeight(safe);
                ai.Stop();
                __result = BehaviorDesigner.Runtime.Tasks.TaskStatus.Success;
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Teleport guard: " + ex.Message); return true; }
        }

        // Every teleport the game makes goes through here; one to the origin or below the ground is refused.
        [HarmonyPatch(typeof(AstarAgent), nameof(AstarAgent.Teleport)), HarmonyPrefix]
        private static bool GuardAnyTeleport(AstarAgent __instance, Vector3 destination)
        {
            try
            {
                if (!Bad(destination) && !float.IsNaN(destination.y) && !float.IsNaN(destination.z)) return true;
                Plugin.Log.LogWarning($"[falls] Teleport to {destination} REFUSED for {Who(__instance.gameObject)} at {__instance.transform.position}; tasks: {ActiveTasks(__instance.gameObject)}");
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Teleport guard (any): " + ex.Message); return true; }
        }

        private static Vector3 TaskTarget(GameObject target, bool useEntry)
        {
            var pos = target.transform.position;
            if (useEntry)
            {
                var wps = target.GetComponent<UnitWaypoints>();
                if (wps != null && wps.GetEntry() != null) pos = wps.GetEntry().transform.position;
            }
            return pos;
        }

        private static void LogRefusal(GameObject go, string step, GameObject target, Vector3 pos)
        {
            Plugin.Log.LogWarning($"[falls] {step} to {target?.name} at {pos} REFUSED for {Who(go)} at {go?.transform.position}; tasks: {ActiveTasks(go)}");
        }

        // The tree's own "teleport to target" step writes the position directly when pathfinding is off.
        [HarmonyPatch(typeof(Teleport), nameof(Teleport.OnUpdate)), HarmonyPrefix]
        private static bool GuardTeleportTask(Teleport __instance, ref BehaviorDesigner.Runtime.Tasks.TaskStatus __result)
        {
            try
            {
                var target = __instance.target != null ? __instance.target.Value : null;
                if (target == null) return true;
                var pos = TaskTarget(target, __instance.useEntry);
                if (!Bad(pos)) return true;
                LogRefusal(Traverse.Create(__instance).Field("gameObject").GetValue<GameObject>(), "Teleport step", target, pos);
                __result = BehaviorDesigner.Runtime.Tasks.TaskStatus.Success;
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Teleport step guard: " + ex.Message); return true; }
        }

        [HarmonyPatch(typeof(TeleportOnEnd), nameof(TeleportOnEnd.Decorate)), HarmonyPrefix]
        private static bool GuardTeleportOnEnd(TeleportOnEnd __instance, BehaviorDesigner.Runtime.Tasks.TaskStatus status, ref BehaviorDesigner.Runtime.Tasks.TaskStatus __result)
        {
            try
            {
                var target = __instance.target != null ? __instance.target.Value : null;
                if (target == null) return true;
                var pos = target.transform.position;
                if (!Bad(pos)) return true;
                LogRefusal(Traverse.Create(__instance).Field("gameObject").GetValue<GameObject>(), "Teleport-on-end step", target, pos);
                __result = status;
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Teleport-on-end guard: " + ex.Message); return true; }
        }

        // Below the game's own code: the pathfinding agent itself. Every position it writes goes through
        // FinalizeMovement, and its own Teleport bypasses the game's wrapper. For fablings, a write to the origin or a
        // jump of more than 50 units in one step is refused (logged once per unit), as is an agent teleport to the
        // origin or below the ground (2026-10-07: two more fell right after a load with no game-side teleport involved).
        private static readonly HashSet<int> JumpLogged = new HashSet<int>();

        [HarmonyPatch(typeof(Pathfinding.AIBase), nameof(Pathfinding.AIBase.FinalizeMovement)), HarmonyPrefix]
        private static bool GuardAgentMove(Pathfinding.AIBase __instance, Vector3 nextPosition)
        {
            try
            {
                var here = __instance.transform.position;
                bool bad = AtOrigin(nextPosition) || Vector3.Distance(here, nextPosition) > 50f || float.IsNaN(nextPosition.x);
                if (!bad) return true;
                var unit = __instance.GetComponent<Unit>();
                if (unit == null) return true;   // animals and others: leave the game to it
                if (JumpLogged.Add(__instance.GetInstanceID()))
                    Plugin.Log.LogWarning($"[falls] Agent move to {nextPosition} REFUSED for {Who(__instance.gameObject)} at {here}; tasks: {ActiveTasks(__instance.gameObject)}");
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Agent move guard: " + ex.Message); return true; }
        }

        [HarmonyPatch(typeof(Pathfinding.AIBase), nameof(Pathfinding.AIBase.Teleport)), HarmonyPrefix]
        private static bool GuardAgentTeleport(Pathfinding.AIBase __instance, Vector3 newPosition)
        {
            try
            {
                if (!Bad(newPosition)) return true;
                if (__instance.GetComponent<Unit>() == null) return true;
                Plugin.Log.LogWarning($"[falls] Agent teleport to {newPosition} REFUSED for {Who(__instance.gameObject)} at {__instance.transform.position}; tasks: {ActiveTasks(__instance.gameObject)}");
                return false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Agent teleport guard: " + ex.Message); return true; }
        }

        // "Name (profession)" for the log.
        private static string Who(GameObject go)
        {
            var unit = go != null ? go.GetComponent<Unit>() : null;
            var worker = go != null ? go.GetComponent<Worker>() : null;
            return $"{unit?.GetLocalizedBirthName()} ({worker?.GetProfessionKey() ?? "unit"})";
        }

        // The names of the unit's running behaviour-tree tasks, for the log.
        private static string ActiveTasks(GameObject go)
        {
            try
            {
                var bt = go != null ? go.GetComponent<BehaviorDesigner.Runtime.Behavior>() : null;
                var tasks = bt != null ? bt.GetActiveTasks() : null;
                if (tasks == null || tasks.Count == 0) return "(none)";
                return string.Join(" > ", tasks.Select(t => t.GetType().Name));
            }
            catch { return "(unknown)"; }
        }

        // A sweep every few seconds, so a fall is caught soon after it happens.
        internal static IEnumerator Patrol()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(5f);
                try { if (Unit.Instances != null && Unit.Instances.Count > 0) Rescue("patrol"); }
                catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Patrol: " + ex.Message); }
            }
        }

        internal static int Rescue(string when)
        {
            int n = 0;
            try
            {
                foreach (var unit in Unit.Instances.ToList())
                {
                    if (unit == null || unit.transform == null) continue;
                    var p = unit.transform.position;
                    bool lost = p.y < -20f || AtOrigin(p) || !GridManager.IsWorldPosWithinGrid(p);
                    if (!lost) continue;
                    Vector3 target = Vector3.zero; string where = "";
                    var worker = unit.GetComponent<Worker>();
                    try
                    {
                        if (worker != null && worker.resident != null && worker.resident.HasHousing() && worker.resident.housing.unitWaypoints != null)
                        {
                            var entry = worker.resident.housing.unitWaypoints.GetFirstWaypointOfType(UnitWaypoint.Type.ENTRY);
                            if (entry != null) { target = entry.transform.position; where = "home"; }
                        }
                    }
                    catch { }
                    if (where == "" && Embassy.Current != null && Embassy.Current.unitWaypoints != null)
                    {
                        var entry = Embassy.Current.unitWaypoints.GetEntry();
                        if (entry != null) { target = entry.transform.position; where = "the Embassy"; }
                    }
                    if (where == "") { target = GridManager.GetWorldCenter(); where = "the map's centre"; }
                    var ai = unit.GetComponent<AstarAgent>();
                    if (ai == null) continue;
                    ai.TeleportWithRaycastHeight(target + new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f)));
                    n++;
                    Plugin.Log.LogWarning($"[falls] {Who(unit.gameObject)} was at {p} ({when}); put back at {where}.");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[falls] Sweep: " + ex.Message); }
            return n;
        }

        // After a load: look at 1, 3, 10, 30 and 60 seconds, since falls have begun after the first look.
        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoad(DataManager __instance) => __instance.OnLoadComplete += () => Plugin.Instance.StartCoroutine(AfterLoad());

        private static IEnumerator AfterLoad()
        {
            Rescue("after load");
            float last = 0f;
            foreach (var t in new[] { 1f, 3f, 10f, 30f, 60f })
            {
                yield return new WaitForSecondsRealtime(t - last); last = t;
                Rescue($"after load, {t:0} s");
            }
        }
    }
}
