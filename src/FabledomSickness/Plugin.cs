using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomSickness
{
    // The game's daily sickness roll (KingdomManager.DailySickness) ignores population: once a day, a 23% chance
    // that exactly one fabling falls ill, whether the town has 100 fablings or 2,200. So one hospital is always
    // enough, and at Kingdom size there are usually no sick fablings at all.
    //
    // This replaces the roll with one that scales: expected cases a day = population / FablingsPerCasePerDay
    // (default 400, which matches the game's own rate at about 90 fablings, 1 a day at 400, 5-6 a day at 2,200).
    // The whole number of cases always happens; the fraction is a chance of one more. The Global sickness debuff
    // multiplies the expectation (the game meant double; 1.6 by default, Jess's call). The game's own gates stay: not in sandbox, not before the
    // sickness milestone, and not until its offset days after that. Hospital beds are the game's own cap: a
    // sick fabling with no bed wanders until it recovers or dies, so a big town needs more hospitals.
    //
    // Everything else about sickness is untouched: who can get sick, recovery, death, immunity, notifications.
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "fab.fabledomsickness";
        public const string Name = "Fabledom Sickness";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> FablingsPerCasePerDay;
        internal static ConfigEntry<int> MaxCasesPerDay, LogWaitingAfterDays;
        internal static ConfigEntry<bool> LogWaiting;
        internal static ConfigEntry<float> DebuffMultiplier;
        internal static ConfigEntry<bool> LogDaily;
        internal static ConfigEntry<bool> WarnNoBed;
        internal static ConfigEntry<int> WarnNoBedAfterDays;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Enabled = Config.Bind("Sickness", "Enabled", true,
                "Scale daily sickness with population. Off = the game's roll (at most one case a day at any size).");
            FablingsPerCasePerDay = Config.Bind("Sickness", "FablingsPerCasePerDay", 520,
                "Expected new cases a day = population / this. 520 (Jess, 2026-10-18): a little under the game's 0.23 a day at 90 fablings, the same at 120, then climbing: about 0.8 a day at 400, 1.9 at 1,000, 4.2 at 2,200. (400 was the game's rate at 90 and 1 a day at 400.)");
            MaxCasesPerDay = Config.Bind("Sickness", "MaxCasesPerDay", 12,
                "Hard ceiling on new cases in one day, whatever the population.");
            DebuffMultiplier = Config.Bind("Sickness", "GlobalSicknessMultiplier", 1.6f,
                "What the Global sickness debuff multiplies the expectation by. The game says double; Jess (2026-10-12) finds 2 too harsh, 1.6 it is. The in-game text is left as it is.");
            LogWaiting = Config.Bind("Sickness", "LogWaiting", true,
                "One log line a day per sick fabling without a bed (name, job, home, position, nearest open hospital), after LogWaitingAfterDays days sick. For finding out why someone never reaches a bed (Jess, 2026-10-18).");
            LogWaitingAfterDays = Config.Bind("Sickness", "LogWaitingAfterDays", 2, "Days sick without a bed before the line above is written.");
            LogDaily = Config.Bind("Sickness", "LogDaily", true,
                "Write one log line per day with the expectation and the cases rolled.");

            WarnNoBed = Config.Bind("Sickness", "WarnNoBed", true,
                "Once the town has a staffed hospital, show a notice (with locate) when someone has been sick without a bed for WarnNoBedAfterDays days. One a day at most.");
            WarnNoBedAfterDays = Config.Bind("Sickness", "WarnNoBedAfterDays", 10, "Days sick without a bed before the notice.");
            if (FablingsPerCasePerDay.Value < 1) FablingsPerCasePerDay.Value = 1;
            if (MaxCasesPerDay.Value < 0) MaxCasesPerDay.Value = 0;
            if (DebuffMultiplier.Value < 1f) DebuffMultiplier.Value = 1f;

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(DailyRoll));
            harmony.PatchAll(typeof(NoBedWarning));
            harmony.PatchAll(typeof(AdvisorLetter));
            harmony.PatchAll(typeof(Text));
            Log.LogInfo($"{Name} {Version} loaded: {(Enabled.Value ? $"ON, one case a day per {FablingsPerCasePerDay.Value} fablings, at most {MaxCasesPerDay.Value}" : "OFF")}.");
        }
    }

    [HarmonyPatch]
    internal static class DailyRoll
    {
        // Sick now, beds staffed and total, and how many beds the current rate needs (cases a day x days to recover).
        private static string Beds(float expectedPerDay)
        {
            int sick = 0, inHospital = 0; float recover = 0f, die = 0f;
            foreach (var w in Worker.Instances)
            {
                if (w == null || w.needs == null) continue;
                if (recover <= 0f && w.needs.config != null) { recover = w.needs.config.daysToRecover; die = w.needs.config.daysSickToDie; }
                if (w.needs.isSick) { sick++; if (w.needs.hospital != null) inHospital++; }
            }
            int hospitals = 0, staffedBeds = 0, totalBeds = 0;
            if (Hospital.Instances != null)
                foreach (var h in Hospital.Instances)
                {
                    if (h == null) continue;
                    hospitals++;
                    try { staffedBeds += h.GetCurrentCapacity(); totalBeds += h.GetTotalCapacity(); } catch { }
                }
            float needed = expectedPerDay * recover;
            int hospitalsNeeded = Mathf.CeilToInt(needed / 10f);
            return $"sick {sick} ({inHospital} in a bed, {sick - inHospital} without); hospitals {hospitals}, beds {staffedBeds} staffed of {totalBeds}; " +
                   $"recovery {recover:0} days, death untreated after {die:0}; at this rate ~{needed:0} beds are needed, {hospitalsNeeded} fully staffed hospital{(hospitalsNeeded == 1 ? "" : "s")}.";
        }

        private static readonly System.Random Rng = new System.Random();
        private static int dayCounter;   // the mod's own count of daily rolls, for the one-notice-a-day rule

        // Replaces the game's roll when enabled. The Fixes mod's transpiler on the same method (which corrects the
        // debuff) is simply not reached then; the doubling is done here.
        [HarmonyPatch(typeof(KingdomManager), "DailySickness"), HarmonyPrefix]
        private static bool BeforeDailySickness(KingdomManager __instance)
        {
            if (!Plugin.Enabled.Value) return true;
            try
            {
                var cfg = __instance.kingdomConfig;
                if (GameManager.Instance.IsSandboxMode(isCreativeMode: true) || __instance.GetCurrentPopulationMilestone() < cfg.sicknessFeatureMilestone) return false;
                __instance.daysSinceHospitalAvailable++;
                if (__instance.daysSinceHospitalAvailable < cfg.sicknessFeatureOffset) return false;

                int population = Worker.Instances != null ? Worker.Instances.Count : 0;
                float expected = (float)population / Plugin.FablingsPerCasePerDay.Value;
                bool debuff = DataManager.Instance != null && DataManager.Instance.IsBuffActivated("globalSickness");
                if (debuff) expected *= Plugin.DebuffMultiplier.Value;

                int cases = Mathf.FloorToInt(expected);
                if (Rng.NextDouble() < expected - cases) cases++;
                cases = Mathf.Min(cases, Plugin.MaxCasesPerDay.Value);

                int made = 0;
                if (cases > 0)
                {
                    var eligible = new List<Worker>();
                    foreach (var w in Worker.Instances)
                        if (w != null && w.needs != null && w.needs.CanGetSick()) eligible.Add(w);
                    // Fisher-Yates, then take the first `cases`
                    for (int i = eligible.Count - 1; i > 0; i--)
                    {
                        int j = Rng.Next(i + 1);
                        var t = eligible[i]; eligible[i] = eligible[j]; eligible[j] = t;
                    }
                    for (int i = 0; i < eligible.Count && made < cases; i++)
                    {
                        try { eligible[i].needs.MakeSick(); made++; }
                        catch (Exception ex) { Plugin.Log.LogWarning("[sickness] making a fabling sick: " + ex.Message); }
                    }
                }
                if (Plugin.LogDaily.Value)
                {
                    Plugin.Log.LogInfo($"[sickness] population {population}, expected {expected:0.00} a day{(debuff ? $" (Global sickness x{Plugin.DebuffMultiplier.Value:0.##})" : "")}: {made} new case{(made == 1 ? "" : "s")} today.");
                    try { Plugin.Log.LogInfo("[sickness] " + Beds(expected)); }
                    catch (Exception ex) { Plugin.Log.LogWarning("[sickness] bed count: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[sickness] daily roll failed, no cases today: " + ex);
            }
            try { NoBedWarning.Check(++dayCounter); }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] no-bed check: " + ex.Message); }
            try { PreventionLetter.Check(); }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] prevention letter: " + ex.Message); }
            try { SickWatch.Daily(); }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] sick watch: " + ex.Message); }
            return false;
        }
    }
}
