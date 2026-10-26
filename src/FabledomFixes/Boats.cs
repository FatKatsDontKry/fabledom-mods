using System;
using BepInEx.Configuration;
using Nielsen;
using Pathfinding;
using UnityEngine;

namespace FabledomFixes
{
    // Two late-game boat faults, both in how the pathfinding library drives a boat (Nielsen.Vehicle), found with
    // the FabledomBoatWatch diagnostic on a 1,712-fabling water save (2026-10-13/14; write-up: fabledom-mods.md, Mod 8).
    //
    // 1. Stuck boats. The library's "crowded destination" behaviour (on by default, threshold 0.1 on the boat prefab)
    //    tells a boat whose destination has other boats near it to stop short, drops its avoidance priority to a
    //    tenth and latches the stop once its progress reads zero. Nothing in the game clears the latch, and the
    //    game's own stuck handling never fires because the boat is not at the end of its path. Two sibling boats
    //    parked at the dock are enough; two boats at one hut were seen deadlocking each other for 14 minutes while
    //    their fishermen starved. Fix: turn the behaviour off on every boat.
    //
    // 2. Wiggling boats. A boat steers toward a point 1 unit ahead on its path, moves 7.5 units a second, and its
    //    avoidance target updates once a frame. On a slow frame (0.2 game seconds at x2) it moves 1.5 units, past
    //    the point, then swings 40-86 degrees back toward it next frame, and overshoots the other way. A control
    //    loop with a frame's delay and too much gain; it only appears once frames get long, i.e. with population,
    //    and never on walkers (0.2-0.4 units a frame). Fix: steer 5 units ahead, beyond any frame's movement.
    //
    // Both are applied by a sweep over the live boats every half second (a boat's agent exists only after it is
    // spawned, and both settings are plain fields the game never writes back), so this needs no Harmony patch.
    internal static class Boats
    {
        internal static ConfigEntry<bool> NoCrowdedStop;
        internal static ConfigEntry<float> LookAhead;

        private static float nextSweep;
        private static int crowdedOff, lookAheadSet;

        internal static void Bind(ConfigFile config)
        {
            NoCrowdedStop = config.Bind("Boats", "NoCrowdedStop", true,
                "Turn off the pathfinding library's 'crowded destination' behaviour on boats. With it on (the game's default) a boat heading for " +
                "a dock or fishing spot that has other boats near it stops short, drops its avoidance priority to a tenth and never resumes " +
                "until the other boats leave; two boats at one hut can deadlock each other while their fishermen starve.");
            LookAhead = config.Bind("Boats", "LookAheadDistance", 5f,
                "How far ahead along its path a boat steers (the game uses 1; 0 = leave alone). A boat moves 7.5 units a second, so on a slow " +
                "frame it moves past a 1-unit look-ahead point and swings back toward it every frame: the late-game wiggle. 5 is beyond any frame's movement.");
        }

        internal static void Sweep()
        {
            if (Time.realtimeSinceStartup < nextSweep) return;
            nextSweep = Time.realtimeSinceStartup + 0.5f;
            if (!NoCrowdedStop.Value && LookAhead.Value <= 0f) return;
            foreach (var v in Vehicle.Instances)
            {
                try
                {
                    if (v == null || v.ai == null) continue;
                    var a = v.ai.agent;
                    if (a == null) continue;
                    if (NoCrowdedStop.Value && a.rvoDensityBehavior.enabled)
                    {
                        a.rvoDensityBehavior.enabled = false;
                        crowdedOff++;
                    }
                    if (LookAhead.Value > 0f && Mathf.Abs(a.pickNextWaypointDist - LookAhead.Value) > 0.01f)
                    {
                        a.pickNextWaypointDist = LookAhead.Value;
                        lookAheadSet++;
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[boats] " + ex.Message); }
            }
        }

        internal static string Status() => $"boats: crowded stop off on {crowdedOff}, look-ahead set on {lookAheadSet}";
    }
}
