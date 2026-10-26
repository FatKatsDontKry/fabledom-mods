using System;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace FabledomPerformance
{
    // Every walker with gravity casts a ray to the ground on every physics step (AIBase.RaycastPosition) and snaps to
    // the hit. With ~1,400 awake walkers and 5 physics steps a frame that is ~7,000 raycasts a frame, most of the
    // 36-46 ms "walker movement" cost. Turning gravity off (the old GroundCheck = Off) removed the raycast but also
    // the only thing that ever updates a walker's height, so on a slope it walked into the hill.
    //
    // This keeps the ray but casts it every GroundCheckEverySteps physics steps per walker, staggered so the casts
    // spread evenly across steps; on the steps between, the walker stands on the last ground height it found.
    //
    // The game's own ray only reaches as far below the feet as gravity has pulled the walker since the last step, so
    // a walker going downhill is found by gravity building up step by step. Holding the height between casts stops
    // that, and the first version never came back down a hill (Jess, 2026-10-12). So the cast made here probes a
    // few units below the feet as well, and a descending walker is put on the ground every cast.
    [HarmonyPatch]
    internal static class GroundCheckCadence
    {
        internal static ConfigEntry<int> EverySteps;
        internal static ConfigEntry<float> ProbeBelow;

        // Between casts the height follows the slope of the last two ground hits (Jess, 2026-10-12: holding the height
        // between casts looked jerky on hills).
        private class State { public int Step; public bool HasHit; public Vector3 Hit, PrevHit; public bool HasPrev; public float Slope; }
        private static readonly ConditionalWeakTable<AIBase, State> States = new ConditionalWeakTable<AIBase, State>();
        private static readonly AccessTools.FieldRef<AIBase, float> VerticalVelocity = AccessTools.FieldRefAccess<AIBase, float>("verticalVelocity");
        private static readonly AccessTools.FieldRef<AIBase, float> LastDeltaTime = AccessTools.FieldRefAccess<AIBase, float>("lastDeltaTime");
        private static readonly AccessTools.FieldRef<AIBase, RaycastHit> LastHit = AccessTools.FieldRefAccess<AIBase, RaycastHit>("lastRaycastHit");
        private static readonly RaycastHit[] Hits = new RaycastHit[8];
        private static int serial;

        internal static void Bind(ConfigFile config)
        {
            EverySteps = config.Bind("Walkers", "GroundCheckEverySteps", 1,
                "Cast the ground ray every N physics steps per walker instead of every step (1 = the game's behaviour). Between casts a walker " +
                "stands on the last ground height it found. 4 removes three quarters of the raycasts; walkers on slopes get a new height every 0.08 s.");
            ProbeBelow = config.Bind("Walkers", "GroundProbeBelow", 3f,
                "How far below the feet the cast looks for ground (units), so a walker going downhill is found. The game's own ray only reaches as far as gravity pulled since the last step.");
        }

        [HarmonyPatch(typeof(AIBase), "RaycastPosition"), HarmonyPrefix]
        private static bool Before(AIBase __instance, Vector3 position, float lastElevation, ref Vector3 __result)
        {
            int every = EverySteps.Value;
            if (every <= 1 || WalkerGroundCheck.IsOff) return true;
            if (!States.TryGetValue(__instance, out var s))
            {
                s = new State { Step = serial++ % every };   // stagger: not every walker casts on the same step
                States.Add(__instance, s);
            }
            s.Step++;
            if (s.Step < every && s.HasHit)
            {
                // Between casts: the last ground height, carried along the slope of the last two hits; "grounded",
                // so gravity does not build up.
                float dx = position.x - s.Hit.x, dz = position.z - s.Hit.z;
                float along = Mathf.Sqrt(dx * dx + dz * dz);
                __result = new Vector3(position.x, s.Hit.y + s.Slope * along, position.z);
                VerticalVelocity(__instance) = 0f;
                return false;
            }
            s.Step = 0;

            // The cast: from above the head down to ProbeBelow under the feet (plus any drop since the last step).
            var tr = __instance.transform;
            float half = tr.localScale.y * __instance.height * 0.5f;
            float up = half + 0.25f;
            float length = up + Mathf.Max(0f, ProbeBelow.Value) + Mathf.Max(0f, lastElevation - position.y);
            int n = Physics.RaycastNonAlloc(position + Vector3.up * up, Vector3.down, Hits, length, __instance.groundMask, QueryTriggerInteraction.Ignore);
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.transform == null || h.transform == tr || h.transform.IsChildOf(tr)) continue;   // not our own body
                if (best < 0 || h.distance < Hits[best].distance) best = i;
            }
            if (best >= 0)
            {
                var hit = Hits[best];
                LastHit(__instance) = hit;
                VerticalVelocity(__instance) *= Math.Max(0f, 1f - 5f * LastDeltaTime(__instance));
                if (s.HasHit) { s.PrevHit = s.Hit; s.HasPrev = true; }
                s.Hit = hit.point; s.HasHit = true;
                // slope along the ground between the last two hits: rise over run, signed by the direction of travel
                s.Slope = 0f;
                if (s.HasPrev)
                {
                    float rx = s.Hit.x - s.PrevHit.x, rz = s.Hit.z - s.PrevHit.z;
                    float run = Mathf.Sqrt(rx * rx + rz * rz);
                    if (run > 0.02f) s.Slope = Mathf.Clamp((s.Hit.y - s.PrevHit.y) / run, -2f, 2f);
                }
                __result = hit.point;
            }
            else
            {
                s.HasHit = false;           // no ground within reach: let the game's own gravity act until there is
                __result = position;
            }
            return false;
        }
    }
}
