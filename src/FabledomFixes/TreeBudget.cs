using System;
using System.Collections.Generic;
using BehaviorDesigner.Runtime;
using HarmonyLib;
using Nielsen;
using UnityEngine;

using FabledomFixes;

namespace FabledomTreeBudget   // type names kept: the Tailor finds FabledomTreeBudget.Dormant by name
{
    // BehaviorManager.Tick() (no arguments) runs Tick(tree) on every tree in its list. This replaces that loop
    // with a round-robin slice of at most TreesPerTick trees, carrying a cursor between ticks so every tree gets
    // its turn in order. Trees added or removed during a tick are handled: the list is re-checked after each
    // tree, and the cursor only moves on if the tree it just ran is still in that slot.
    //
    // Priority Low so the profiler's timing prefix (normal priority) has already run before this one returns false.
    [HarmonyPatch]
    internal static class TreeBudget
    {
        // The manager keeps its live trees in a private list; the per-tree Tick is private too.
        private static readonly AccessTools.FieldRef<BehaviorManager, List<BehaviorManager.BehaviorTree>> Trees =
            AccessTools.FieldRefAccess<BehaviorManager, List<BehaviorManager.BehaviorTree>>("behaviorTrees");
        private static readonly Action<BehaviorManager, BehaviorManager.BehaviorTree> TickOne =
            AccessTools.MethodDelegate<Action<BehaviorManager, BehaviorManager.BehaviorTree>>(
                AccessTools.Method(typeof(BehaviorManager), "Tick", new[] { typeof(BehaviorManager.BehaviorTree) }));

        private static int cursor;
        private static float lastTickGameTime = -1f, lastTickDt = 0.2f;

        // Counters for the periodic log line.
        private static int ticks, treesTicked, slicedTicks;
        private static float lastStats = -1f;
        private static long budgetSum;   // for the average budget in the log line

        [HarmonyPatch(typeof(BehaviorManager), nameof(BehaviorManager.Tick), new Type[0]), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool BeforeTick(BehaviorManager __instance)
        {
            ticks++;
            // Game time since the previous tick: how much of the decision interval one tick uses up.
            float now = Time.time;
            float dt = lastTickGameTime < 0f ? 0.2f : now - lastTickGameTime;
            lastTickGameTime = now;
            if (dt > 0.01f && dt < 2f) lastTickDt = dt;   // ignore the first tick, pauses and load hitches

            var list = Trees(__instance);
            if (list == null) return true;
            int count = list.Count;
            bool enabled = Plugin.TreeBudgetEnabled.Value;
            int budget = Budget(count);
            budgetSum += budget;
            treesTicked += enabled ? Math.Min(count, budget) : count;

            // Off, or a town small enough that every tree fits in the budget: let the game do its own loop.
            if (!enabled || count <= budget) return true;

            slicedTicks++;
            if (cursor >= count) cursor = 0;
            for (int n = 0; n < budget && list.Count > 0; n++)
            {
                if (cursor >= list.Count) cursor = 0;
                var tree = list[cursor];
                TickOne(__instance, tree);
                // If the tree left the list during its tick (died, restarted, paused), the next tree now sits at
                // the cursor, so only advance when the slot still holds the one we ran.
                if (cursor < list.Count && ReferenceEquals(list[cursor], tree)) cursor++;
            }
            return false;
        }

        // Trees per tick: the configured floor, raised so that every tree gets a turn within DecisionIntervalGameSeconds.
        // Ticks are a fixed slice of game time, so the share of the list to run each tick is (tick length / interval).
        private static int Budget(int treeCount)
        {
            int budget = Plugin.TreesPerTick.Value;
            float interval = Plugin.DecisionIntervalGameSeconds.Value;
            if (interval > 0f) budget = Math.Max(budget, Mathf.CeilToInt(treeCount * lastTickDt / interval));
            return Math.Max(1, budget);
        }

        internal static void LogStats(float periodSeconds)
        {
            float now = Time.realtimeSinceStartup;
            float elapsed = lastStats < 0f ? periodSeconds : now - lastStats;
            lastStats = now;

            var bm = BehaviorManager.instance;
            int trees = bm != null && Trees(bm) != null ? Trees(bm).Count : 0;
            if (trees == 0 && ticks == 0) { ticks = treesTicked = slicedTicks = 0; return; }   // menu or loading

            float ticksPerSecond = elapsed > 0f ? ticks / elapsed : 0f;
            string budget;
            if (!Plugin.TreeBudgetEnabled.Value) budget = "budget OFF";
            else if (slicedTicks == 0) budget = $"budget {Budget(trees)} not reached";
            else
            {
                int perTick = (int)(budgetSum / Math.Max(1, ticks));
                float every = perTick > 0 ? (float)trees / perTick : 0f;
                float seconds = ticksPerSecond > 0f ? every / ticksPerSecond : 0f;
                budget = $"budget {perTick}/tick -> each tree every {every:0.0} ticks (~{seconds:0.00} s real, ~{every * lastTickDt:0.00} s game)";
            }

            int pop = Worker.Instances != null ? Worker.Instances.Count : 0;
            int unemployed = 0;
            try { unemployed = Worker.GetUnemployedCount(); } catch { }
            string share = pop > 0 ? $"{100 * unemployed / pop}%" : "-";

            Plugin.Log.LogInfo($"[treebudget] trees {trees}, {ticksPerSecond:0.0} ticks/s, {budget}; " +
                               $"fablings {pop}, unemployed {unemployed} ({share}); evaluations/s {treesTicked / Math.Max(elapsed, 0.01f):0}");
            ticks = treesTicked = slicedTicks = 0;
            budgetSum = 0;
        }
    }
}
