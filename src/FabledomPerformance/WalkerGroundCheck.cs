using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Nielsen;
using Pathfinding;
using UnityEngine;

namespace FabledomPerformance
{
    internal enum GroundCheckMode { On, Off, Auto }

    // Every walker (AIPath) casts a ray to the ground on every physics step to keep its feet on the
    // terrain. With ~2,000 fablings that is ~10,000 raycasts a frame. With the check off, walkers
    // follow the height of their path instead: a little clipping on slopes, much less work.
    // "gravity == zero" is how the pathfinding library itself turns the check off.
    internal static class WalkerGroundCheck
    {
        internal static ConfigEntry<GroundCheckMode> Mode;
        internal static ConfigEntry<int> AutoAbove;

        private class Saved { public Vector3 Gravity; }
        private static readonly ConditionalWeakTable<AIBase, Saved> Original = new ConditionalWeakTable<AIBase, Saved>();
        private static bool checkOff;
        private static float nextCheck;

        internal static void Bind(ConfigFile config)
        {
            Mode = config.Bind("Walkers", "GroundCheck", GroundCheckMode.Auto,
                "On = the game's normal ground raycast every physics step. Off = walkers follow their path's height " +
                "(cheaper, may clip a little on slopes). Auto = Off only while the population is above AutoAbovePopulation.");
            AutoAbove = config.Bind("Walkers", "AutoAbovePopulation", 1000,
                "In Auto mode, switch the ground check off above this many fablings (and back on below it).");
        }

        // Called every frame by the plugin; re-evaluates every few seconds.
        internal static void Tick()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 5f;

            int pop = Worker.Instances?.Count ?? 0;
            bool wantOff = Mode.Value == GroundCheckMode.Off || (Mode.Value == GroundCheckMode.Auto && pop > AutoAbove.Value);
            if (wantOff == checkOff) return;
            checkOff = wantOff;

            int n = 0;
            foreach (var a in Object.FindObjectsOfType<AIPath>()) { Apply(a); n++; }
            Plugin.Log.LogInfo($"Walker ground check {(checkOff ? "OFF" : "ON")} ({n} walkers, {pop} fablings, mode {Mode.Value}).");
        }

        internal static bool IsOff => checkOff;

        private static void Apply(AIBase a)
        {
            if (a == null) return;
            if (checkOff)
            {
                if (!Original.TryGetValue(a, out _)) Original.Add(a, new Saved { Gravity = a.gravity });
                a.gravity = Vector3.zero;
            }
            else if (Original.TryGetValue(a, out var saved))
            {
                a.gravity = saved.Gravity;
                Original.Remove(a);
            }
        }

        // Walkers created later (new fablings, soldiers, trolls) get the current setting too.
        [HarmonyPatch(typeof(AIBase), "OnEnable"), HarmonyPostfix]
        private static void AfterEnable(AIBase __instance)
        {
            if (checkOff && __instance is AIPath) Apply(__instance);
        }
    }
}
