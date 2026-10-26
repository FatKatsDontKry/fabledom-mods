using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Nielsen;
using UnityEngine;

namespace FabledomSickness
{
    // The game warns about the first sickness and about each death, but never that the hospitals are full. With
    // population-scaled sickness that is the state that kills people. So, once the town has at least one staffed
    // hospital and someone has been sick without a bed for WarnNoBedAfterDays days: the first time, the royal
    // advisors' letter (AdvisorLetter); every day after that, a top-bar notice with the locate button pointing at
    // the longest-waiting fabling. One notice a day at most. (Jess, 2026-10-12.)
    // The notice's key is the mod's own, so it is kept out of the saved notification history.
    [HarmonyPatch]
    internal static class NoBedWarning
    {
        internal const string Notice = "fabSickNoBed";
        private static int lastWarnedDay = -1;
        internal static readonly HashSet<string> Warned = new HashSet<string>();   // guids already pointed at: once per fabling (Jess, 2026-10-12)

        // The notice's look is copied from the game's troll-camp notice, which has a locate button.
        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            try
            {
                if (__instance.notificationSoData.Any(n => n != null && n.key == Notice)) return;
                var model = __instance.notificationSoData.FirstOrDefault(n => n != null && n.key == "trollCamp") ?? __instance.notificationSoData.FirstOrDefault();
                var n = ScriptableObject.CreateInstance<NotificationData>();
                n.name = Notice; n.key = Notice;
                n.title = Text.Make("Beds running short");
                n.description = Text.Make("{0}");
                n.importance = NotificationImportance.HIGH;
                n.hasActionButton = true;
                if (model != null) { n.showTime = model.showTime; n.bgColor = model.bgColor; n.icon = model.icon; n.actionButtonIcon = model.actionButtonIcon; n.hasSound = false; }
                __instance.notificationSoData.Add(n);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] notice setup: " + ex.Message); }
        }

        // Called once a day from the daily roll.
        internal static void Check(int day)
        {
            if (!Plugin.WarnNoBed.Value || day == lastWarnedDay) return;
            bool staffedHospital = Hospital.Instances != null && Hospital.Instances.Any(h => h != null && h.GetCurrentCapacity() > 0);
            if (!staffedHospital) return;   // nothing the player can do about it yet is the game's own warning's job
            // Jess (2026-10-18): the notice came while beds stood empty, for fablings still walking in after the hospital
            // was staffed. A long wait with a free bed is not a bed problem: only speak when every hospital is full.
            bool anyFreeBed = Hospital.Instances.Any(h => h != null && h.HasRoomForMorePatients() && (h.closeable == null || !h.closeable.IsClosed()));
            if (anyFreeBed) return;

            int limit = Plugin.WarnNoBedAfterDays.Value;
            var waiting = new List<Worker>();
            var sickNow = new HashSet<string>();
            foreach (var w in Worker.Instances)
            {
                if (w == null || w.needs == null || !w.needs.isSick) continue;
                if (w.unit == null || w.unit.health == null || w.unit.health.isDead) continue;
                if (w.unit.worldObject != null && w.unit.worldObject.guid != null) sickNow.Add(w.unit.worldObject.guid);
                if (w.needs.hospital == null && w.needs.daysSick >= limit) waiting.Add(w);   // daysSick counts days sick without treatment
            }
            // A fabling leaves the flagged list when it is no longer sick: cured, or dead. A later illness is a new wait.
            Warned.RemoveWhere(g => !sickNow.Contains(g));
            if (waiting.Count == 0) return;

            // First time: the advisors write. The notices start the day after.
            if (AdvisorLetter.Letter == AdvisorLetter.State.NotSent)
            {
                lastWarnedDay = day;
                if (AdvisorLetter.Send()) return;
                // no letter could be made: fall through to the notice
            }

            // One notice a day, and each fabling is pointed at once: the longest waiter not yet flagged.
            string Guid(Worker w) => w.unit.worldObject != null && w.unit.worldObject.guid != null ? w.unit.worldObject.guid : "";
            var worst = waiting.Where(w => !Warned.Contains(Guid(w))).OrderByDescending(w => w.needs.daysSick).FirstOrDefault();
            if (worst == null) return;   // everyone waiting has had their notice
            lastWarnedDay = day;
            string guid = Guid(worst);
            if (guid.Length > 0) Warned.Add(guid);
            string text = "You may not have enough hospital beds for your sick fablings!";
            try { UIManager.Instance.notificationContainer.ShowNotification(Notice, new[] { text }, guid); }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] notice: " + ex.Message); }
            string name;
            try { name = worst.unit.GetLocalizedBirthName(); } catch { name = worst.name; }
            Plugin.Log.LogInfo($"[sickness] notice shown: {waiting.Count} waiting {limit}+ days, {Warned.Count} flagged so far; locate points at {name} ({worst.needs.daysSick:0} days).");
        }

        // Keep the mod's notices out of the saved history so the save loads without the mod.
        private static List<SaveableNotificationData> stripped;

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPrefix]
        private static void BeforeSave(DataManager __instance)
        {
            if (SaveMaster.GetActiveSlot() < 0 || __instance.gameData == null || __instance.gameData.notificationHistory == null) return;
            stripped = __instance.gameData.notificationHistory.Where(n => n != null && n.key == Notice).ToList();
            __instance.gameData.notificationHistory.RemoveAll(n => n != null && n.key == Notice);
        }

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPostfix]
        private static void AfterSave(DataManager __instance)
        {
            if (stripped != null && __instance.gameData != null) __instance.gameData.notificationHistory.AddRange(stripped);
            stripped = null;
        }
    }
}
