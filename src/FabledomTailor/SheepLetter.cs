using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Lowscope.Saving;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // The sheep letter (Jess, 2026-10-06): with the rivalry game absent, stealing cannot be the only source of extra
    // herds, so a copy of the game's "More piggies" letter offers a Sheep herd instead. Conditions: at least 5
    // nobility and a Sheep farm. It re-arms 275 days after it is answered (110 while someone goes without) and then waits for the letter draw, like
    // the pigs (330 for them). A guaranteed offer fires the first time a garment happiness penalty appears. Price as
    // the pigs': coin times the milestone multiplier, or nobility. The pig letter is found by what it does (its
    // action adds a Pig herd), not by its key, and the herd placed is the game's own world-event drop.
    [HarmonyPatch]
    internal static class SheepLetter
    {
        internal const string Key = "fabSheepHerd";
        internal const string Notice = "fabSheepHerdNotice";   // the top-bar notice with a locate button when the flock arrives
        private static WorldEventActionData Copy(WorldEventActionData a) => ModText.Clone(a);

        // On: the letter exists. Auto: only when the Rivals plugin is not loaded (stealing is the route then).
        internal static bool Enabled()
        {
            string mode = (Plugin.SheepLetterMode.Value ?? "auto").Trim().ToLowerInvariant();
            if (mode == "on") return true;
            if (mode == "off") return false;
            try { return !BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("fab.fabledomrivals"); } catch { return true; }
        }

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            try
            {
                if (__instance.worldEventSoData.Any(e => e != null && e.key == Key)) return;
                if (!Enabled()) { Plugin.Log.LogInfo("Tailor: sheep letter off (the Rivals plugin is loaded, or the config says off)."); return; }
                var pig = __instance.worldEventSoData.FirstOrDefault(e => e != null && e.actionDatas != null
                    && e.actionDatas.Any(a => a != null && a.addWorldObjectInRealm && a.worldObjectToAddInRealm != null && a.worldObjectToAddInRealm.name.ToLowerInvariant().Contains("pig")));
                var herd = Resources.FindObjectsOfTypeAll<WorldObjectData>().FirstOrDefault(w => w != null && w.name == "Sheep herd");
                if (pig == null || herd == null || Tailor.FarmData == null) { Plugin.Log.LogWarning($"Tailor: sheep letter: pig letter {(pig != null ? pig.key : "not found")}, herd {(herd != null ? "ok" : "missing")}, farm {(Tailor.FarmData != null ? "ok" : "missing")}."); return; }

                var letter = Object.Instantiate(pig);
                letter.name = Key; letter.key = Key;
                letter.belongsToSeries = false; letter.belongsToEncounter = false;
                letter.isDailyEvent = true;
                letter.canRepeat = true; letter.daysBetweenRepeat = Plugin.SheepLetterRepeatDays.Value;
                letter.hasSetDate = false;
                letter.hasMinNobility = true; letter.minNobility = Plugin.SheepLetterNobility.Value;
                letter.hasMinStructure = true; letter.minStructure = Tailor.FarmData; letter.minStructureAmount = 1;
                letter.hasMaxStructure = false;
                letter.hasWorldObjectRequirement = false;
                letter.hasMinResources = letter.hasMaxResources = false;
                letter.preventDuringTutorial = false;
                letter.injectDescription = false;
                // plainer and friendlier (Jess, 2026-10-10): the first draft read stiff
                letter.title = ModText.Make("Sheep going spare");
                letter.description = ModText.Make("I've got more sheep than grass up here, and word is you've got more grass than sheep. Your nobles like their wool, so you know what a flock is worth to you. Say yes and I'll drive them down and leave them wherever there's room on your land. Look for the notice to find where they settled.");
                letter.signature = ModText.Make("A shepherd from over the hills");

                var actions = new List<WorldEventActionData>();
                foreach (var a in pig.actionDatas)
                {
                    if (a == null) continue;
                    var c = Copy(a);
                    if (c.addWorldObjectInRealm) c.worldObjectToAddInRealm = herd;
                    actions.Add(c);
                }
                // every option gets its own words: the pig letter's say "They are so cute!", "No sorry, I'm allergic." and
                // a tooltip about one more pigsty (Jess, 2026-10-10)
                foreach (var c in actions)
                {
                    if (c.addWorldObjectInRealm)
                    {
                        c.title = ModText.Make(c.hasNobilityCost ? "Send them down. We'll pay in standing." : "Send them down. Here's your coin.");
                        c.description = ModText.Make("A flock of sheep is driven onto your land, wherever there is room. One more Sheep farm can take it in.");
                    }
                    else
                    {
                        c.title = ModText.Make("No thanks, our pastures are full.");
                        c.description = ModText.Make("");
                    }
                }
                letter.actionDatas = actions;

                // the notice that says where the flock went, styled like the troll camp one (which has a locate button)
                if (!__instance.notificationSoData.Any(n => n != null && n.key == Notice))
                {
                    var troll = __instance.notificationSoData.FirstOrDefault(n => n != null && n.key == "trollCamp") ?? __instance.notificationSoData.FirstOrDefault();
                    var n = ScriptableObject.CreateInstance<NotificationData>();
                    n.name = Notice; n.key = Notice;
                    n.title = ModText.Make("A flock arrives");
                    n.description = ModText.Make("{0}");
                    n.importance = NotificationImportance.HIGH;
                    n.hasActionButton = true;
                    if (troll != null) { n.showTime = troll.showTime; n.bgColor = troll.bgColor; n.icon = troll.icon; n.actionButtonIcon = troll.actionButtonIcon; n.hasSound = false; }   // no sound: the Wwise event type is not referenced here
                    __instance.notificationSoData.Add(n);
                }
                __instance.worldEventSoData.Add(letter);
                Plugin.Log.LogInfo($"Tailor: sheep letter added, modelled on '{pig.key}' ({actions.Count} options; nobility {letter.minNobility}, a Sheep farm, re-arms after {letter.daysBetweenRepeat} days).");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor: sheep letter setup failed: " + ex); }
        }

        // The flock the game drops lands on a random free cell with no word of where (Jess, 2026-10-10: "I paid nobility
        // for the herd and I can't find it"): note the herds before the answer and point a notice at the new one after.
        private static List<AnimalHerd> herdsBefore;
        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPrefix]
        private static void HerdsBefore(SaveableWorldEventData saveableWorldEvent)
        {
            herdsBefore = saveableWorldEvent != null && saveableWorldEvent.key == Key && AnimalHerd.Instances != null ? AnimalHerd.Instances.ToList() : null;
        }

        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPostfix]
        private static void WhereTheFlockWent(SaveableWorldEventData saveableWorldEvent)
        {
            try
            {
                if (herdsBefore == null || saveableWorldEvent == null || saveableWorldEvent.key != Key || AnimalHerd.Instances == null) return;
                var herd = AnimalHerd.Instances.FirstOrDefault(h => h != null && !herdsBefore.Contains(h));
                herdsBefore = null;
                if (herd == null) return;
                var wo = herd.GetComponent<WorldObject>();
                string guid = wo != null && wo.guid != null ? wo.guid : "";
                UIManager.Instance.notificationContainer.ShowNotification(Notice, new[] { "A flock of sheep has arrived." }, guid);
                Plugin.Log.LogInfo($"Tailor: the flock from the letter is at {herd.transform.position} (guid {(guid.Length >= 8 ? guid.Substring(0, 8) : guid)}); notice shown.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: flock notice: " + ex.Message); }
        }

        // The notice carries a key the game does not know: keep it out of the saved history so the save loads without the mod.
        private static List<SaveableNotificationData> strippedNotices;
        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPrefix]
        private static void BeforeSave(DataManager __instance)
        {
            if (SaveMaster.GetActiveSlot() < 0 || __instance.gameData == null) return;
            strippedNotices = __instance.gameData.notificationHistory.Where(n => n != null && n.key == Notice).ToList();
            __instance.gameData.notificationHistory.RemoveAll(n => n != null && n.key == Notice);
        }

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPostfix]
        private static void AfterSave(DataManager __instance)
        {
            if (strippedNotices != null && __instance.gameData != null) __instance.gameData.notificationHistory.AddRange(strippedNotices);
            strippedNotices = null;
        }

        // Answered with a garment penalty on the books (Jess, 2026-10-07): the letter re-arms sooner, in RepeatDaysInNeed
        // days; answered in comfort it waits the full RepeatDays. The repeat interval lives on the letter's data, so it
        // is set at the moment of answering and governs the wait that follows.
        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPrefix]
        private static void RepeatByNeed(SaveableWorldEventData saveableWorldEvent, List<WorldEventActionData> actionDatas, int actionIndex)
        {
            try
            {
                if (saveableWorldEvent == null || saveableWorldEvent.key != Key || DataManager.Instance == null) return;
                var letter = DataManager.Instance.worldEventSoData.FirstOrDefault(e => e != null && e.key == Key);
                if (letter == null) return;
                bool inNeed = Garments.AnyoneInNeed();
                letter.daysBetweenRepeat = inNeed ? Plugin.SheepLetterRepeatDaysInNeed.Value : Plugin.SheepLetterRepeatDays.Value;
                bool herd = actionIndex >= 0 && actionIndex < actionDatas.Count && actionDatas[actionIndex] != null && actionDatas[actionIndex].addWorldObjectInRealm;
                Plugin.Log.LogInfo($"Tailor: sheep letter answered ({(herd ? "a herd bought" : "declined or postponed")}, {(inNeed ? "with" : "without")} a garment penalty): next in {letter.daysBetweenRepeat} days.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: sheep letter repeat: " + ex.Message); }
        }

        // Garments are a need, not a treat (Jess, 2026-10-07): re-arming only puts the pig letter back in the draw,
        // which can take a season. So once the sheep letter is re-armed, if anyone is going without a garment, it is
        // sent outright that day; in comfort it waits for the draw like any letter.
        private static int lastForcedDay = -1;
        internal static void Daily()
        {
            try
            {
                if (!Enabled() || DataManager.Instance == null || WorldEventManager.Instance == null) return;
                var letter = DataManager.Instance.worldEventSoData.FirstOrDefault(e => e != null && e.key == Key);
                if (letter == null) return;
                int today = DateTimeManager.Instance.GetTotalDaysTranspired();
                if (today == lastForcedDay) return;
                bool inNeed = Garments.AnyoneInNeed();
                if (!inNeed) return;
                if (KingdomManager.Instance.nobility < letter.minNobility) return;
                if (!WorldObject.Instances.Any(w => w != null && w.data == Tailor.FarmData)) return;
                var tr = Traverse.Create(WorldEventManager.Instance);
                var active = tr.Field("activeWorldEvents").GetValue<List<SaveableWorldEventData>>();
                var handled = tr.Field("handeledWorldEvents").GetValue<List<SaveableWorldEventData>>();
                if (active != null && active.Any(e => e != null && e.key == Key)) return;   // already on the table
                var last = handled?.FirstOrDefault(e => e != null && e.key == Key);
                if (last != null && today - last.dayHandled <= letter.daysBetweenRepeat - 1) return;   // not re-armed yet
                lastForcedDay = today;
                Garments.S.SheepLetterOffered = true;
                WorldEventManager.Instance.TriggerEvent(Key, resetDailyCounter: false);
                Plugin.Log.LogInfo($"Tailor: the sheep letter is re-armed and someone is without a garment: sent today (day {today}).");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: sheep letter daily: " + ex.Message); }
        }

        // The guaranteed offer: the first time a garment penalty appears, the letter comes regardless of the draw.
        internal static void OnFirstGarmentPenalty()
        {
            try
            {
                if (Garments.S.SheepLetterOffered || !Enabled()) return;
                if (DataManager.Instance == null || !DataManager.Instance.worldEventSoData.Any(e => e != null && e.key == Key)) return;
                Garments.S.SheepLetterOffered = true;
                WorldEventManager.Instance.TriggerEvent(Key, resetDailyCounter: false);
                Plugin.Log.LogInfo("Tailor: the first garment penalty brought the sheep letter.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: sheep letter trigger: " + ex.Message); }
        }
    }
}
