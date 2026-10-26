using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GPUInstancer;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace FabledomPerformance
{
    // Stops the game wasting time on log spam and on a repeated failed lookup. No gameplay changes.
    [BepInPlugin("fab.fabledomperformance", "Fabledom Performance", "0.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> FixGpuiLookup, QuietPathLog, MuteSpam, NoWarningStackTraces, FastLookups;
        internal static string[] Muted = new string[0];
        internal static long MutedCount;

        private void Awake()
        {
            Log = Logger;
            FixGpuiLookup = Config.Bind("Performance", "FixInfluenceAreaLookup", true,
                "Showing a building's influence area makes GPU Instancer search for a render manager that doesn't exist, " +
                "every time, and log a warning (tens of thousands per session). Remember the miss instead.");
            QuietPathLog = Config.Bind("Performance", "QuietPathfindingLog", true,
                "Stop the pathfinder writing a log entry for every cancelled or failed path (paths still behave the same).");
            MuteSpam = Config.Bind("Performance", "MuteKnownSpam", true,
                "Drop known-harmless warnings before Unity writes them.");
            var mutedList = Config.Bind("Performance", "MutedMessages",
                "Can not find GPUI Prefab Manager|No ground detected below point|The Grid Graph is not scanned|No anchors found for the current mesh|Mesh is not readable|[InfluencedByArea]",
                "Messages starting with any of these (separated by |) are dropped.");
            NoWarningStackTraces = Config.Bind("Performance", "NoWarningStackTraces", true,
                "Don't capture a call stack for every warning and info message (errors keep theirs).");

            FastLookups = Config.Bind("Performance", "FastLookups", true,
                "Answer the storage and resource questions the fetch searches ask (how much of X is here, is X retrievable, which resource is key X) " +
                "with plain loops and a lookup table instead of the game's allocating searches. Same answers; those searches were ~17 ms a frame at 2,200 fablings.");

            WalkerGroundCheck.Bind(Config);
            GroundCheckCadence.Bind(Config);

            Muted = mutedList.Value.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

            if (NoWarningStackTraces.Value)
            {
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            }

            Log.LogInfo($"Garbage collector: incremental = {UnityEngine.Scripting.GarbageCollector.isIncremental}, mode = {UnityEngine.Scripting.GarbageCollector.GCMode}" +
                        (UnityEngine.Scripting.GarbageCollector.isIncremental ? "" : " (add gc-max-time-slice=3 to Fabledom_Data\\boot.config to turn on incremental)"));

            var h = new Harmony("fab.fabledomperformance");
            if (MuteSpam.Value) h.PatchAll(typeof(MuteLogs));
            if (FixGpuiLookup.Value) h.PatchAll(typeof(GpuiLookup));
            if (QuietPathLog.Value) h.PatchAll(typeof(PathLogging));
            h.PatchAll(typeof(WalkerGroundCheck));
            if (FastLookups.Value) h.PatchAll(typeof(FastLookups));
            h.PatchAll(typeof(GroundCheckCadence));
            Log.LogInfo($"Fabledom Performance loaded (influence lookup fix: {FixGpuiLookup.Value}, quiet paths: {QuietPathLog.Value}, fast lookups: {FastLookups.Value}, " +
                        $"muted messages: {(MuteSpam.Value ? Muted.Length : 0)}, warning stack traces off: {NoWarningStackTraces.Value}, " +
                        $"walker ground check: {WalkerGroundCheck.Mode.Value} (auto above {WalkerGroundCheck.AutoAbove.Value}), ground ray every {GroundCheckCadence.EverySteps.Value} step(s)).");
        }

        private void Update()
        {
            try { WalkerGroundCheck.Tick(); }
            catch (System.Exception ex) { Log.LogError("Walker ground check failed: " + ex.Message); }
        }

        private void OnApplicationQuit()
        {
            if (MutedCount > 0) Log.LogInfo($"Muted {MutedCount} log messages this session.");
        }
    }

    // Drop known-harmless messages before Unity formats and writes them.
    [HarmonyPatch]
    internal static class MuteLogs
    {
        private static bool IsMuted(object message)
        {
            if (!(message is string s)) return false;
            foreach (var m in Plugin.Muted)
                if (s.StartsWith(m, System.StringComparison.Ordinal)) { Plugin.MutedCount++; return true; }
            return false;
        }

        [HarmonyPatch(typeof(Debug), nameof(Debug.LogWarning), new[] { typeof(object) }), HarmonyPrefix]
        private static bool Warning(object message) => !IsMuted(message);

        [HarmonyPatch(typeof(Debug), nameof(Debug.LogWarning), new[] { typeof(object), typeof(Object) }), HarmonyPrefix]
        private static bool WarningCtx(object message) => !IsMuted(message);

        [HarmonyPatch(typeof(Debug), nameof(Debug.Log), new[] { typeof(object) }), HarmonyPrefix]
        private static bool Info(object message) => !IsMuted(message);
    }

    // GPUInstancerPrefabRuntimeHandler.GetPrefabManager searches every active manager on each
    // enable/disable, and never caches a miss. Remember misses until the set of managers changes.
    [HarmonyPatch]
    internal static class GpuiLookup
    {
        private static readonly HashSet<GPUInstancerPrefabPrototype> Missing = new HashSet<GPUInstancerPrefabPrototype>();
        private static int managerCount = -1;

        private static bool ManagersChanged()
        {
            int n = GPUInstancerManager.activeManagerList?.Count ?? -1;
            if (n == managerCount) return false;
            managerCount = n;
            Missing.Clear();
            return true;
        }

        [HarmonyPatch(typeof(GPUInstancerPrefabRuntimeHandler), "GetPrefabManager"), HarmonyPrefix]
        private static bool Before(GPUInstancerPrefabRuntimeHandler __instance, ref GPUInstancerPrefabManager __result)
        {
            ManagersChanged();
            var proto = __instance.gpuiPrefab?.prefabPrototype;
            if (proto == null || !Missing.Contains(proto)) return true;
            __result = null;
            return false;
        }

        [HarmonyPatch(typeof(GPUInstancerPrefabRuntimeHandler), "GetPrefabManager"), HarmonyPostfix]
        private static void After(GPUInstancerPrefabRuntimeHandler __instance, GPUInstancerPrefabManager __result)
        {
            var proto = __instance.gpuiPrefab?.prefabPrototype;
            if (__result == null && proto != null && GPUInstancerManager.activeManagerList != null) Missing.Add(proto);
        }
    }

    [HarmonyPatch]
    internal static class PathLogging
    {
        [HarmonyPatch(typeof(AstarPath), "Awake"), HarmonyPostfix]
        private static void AfterAstarAwake(AstarPath __instance) => __instance.logPathResults = PathLog.None;
    }
}
