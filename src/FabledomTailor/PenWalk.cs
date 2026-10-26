using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using Pathfinding;
using UnityEngine;

namespace FabledomTailor
{
    // A Sheep farm's whole 3x3 footprint is an obstacle to the pathfinder, pen included, so a shearer sent into the pen
    // stops at the fence (Jess, 2026-10-07: "can they not go into the pen?"). This carves the pen floor walkable once
    // the farm stands, plus a gap in the front fence beside the barn to get in by, and does it again after every
    // navigation rescan (a load). The barn's own cell and the fence line stay solid. Sheep do not use the grid.
    [HarmonyPatch]
    internal static class PenWalk
    {
        private static readonly HashSet<int> Done = new HashSet<int>();
        private static bool logged;

        // Local-space boxes (the farm is 30x30 units, the barn in the front-left cell, the entry on the front edge).
        private static readonly (Vector3 min, Vector3 max)[] Boxes =
        {
            (new Vector3(-13.5f, -1f, -3.5f), new Vector3(13.5f, 3f, 13.5f)),   // the back two rows of the pen
            (new Vector3(-3.5f, -1f, -13.5f), new Vector3(13.5f, 3f, -3.5f)),   // the front row, right of the barn
            (new Vector3(-3.5f, -1f, -17f), new Vector3(-0.5f, 3f, -12f)),      // the gap through the front fence beside the barn
        };

        // A rescan (a load, anything built) makes the pens solid again. Carve them back at once rather than at the next patrol:
        // in the gap a shearer re-pathing to a spot inside a solid block was sent to the nearest reachable node, outside the back
        // fence, and stood there (Jess, 2026-10-24: "sometimes the shearer goes to the back of the field").
        [HarmonyPatch(typeof(NavigationManager), nameof(NavigationManager.InitGridSurfaces)), HarmonyPostfix]
        private static void AfterRescan()
        {
            Done.Clear();
            try
            {
                if (Tailor.FarmData == null || WorldObject.Instances == null) return;
                foreach (var wo in WorldObject.Instances.ToList()) if (wo != null && wo.data == Tailor.FarmData) Ensure(wo);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: pens after rescan: " + ex.Message); }
        }

        internal static void Ensure(WorldObject farm)
        {
            try
            {
                if (farm == null || !Plugin.PenWalkable.Value || !NavigationManager.AiSurfaceInitialized || AstarPath.active == null) return;
                if (Done.Contains(farm.GetInstanceID())) return;
                var c = farm.GetComponent<Constructable>();
                if (c != null && !c.isConstructionComplete) return;
                Done.Add(farm.GetInstanceID());
                var t = farm.transform;
                foreach (var box in Boxes)
                {
                    // world bounds that cover the rotated local box
                    var b = new Bounds(t.TransformPoint((box.min + box.max) * 0.5f), Vector3.zero);
                    foreach (var corner in new[] { box.min, box.max, new Vector3(box.min.x, 0f, box.max.z), new Vector3(box.max.x, 0f, box.min.z) })
                        b.Encapsulate(t.TransformPoint(corner));
                    b.Expand(new Vector3(0f, 4f, 0f));
                    var guo = new GraphUpdateObject(b) { modifyWalkability = true, setWalkability = true, updatePhysics = false };
                    AstarPath.active.UpdateGraphs(guo);
                }
                if (!logged) { logged = true; Plugin.Log.LogInfo("Tailor: Sheep farm pens made walkable."); }   // once a session; it reruns per farm on every rescan
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: pen walkable: " + ex.Message); }
        }
    }
}
