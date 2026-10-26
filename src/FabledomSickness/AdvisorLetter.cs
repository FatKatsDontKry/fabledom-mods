using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FabledomSickness
{
    // The first time the town's hospitals fall behind (a staffed hospital exists and someone has waited
    // WarnNoBedAfterDays days for a bed), the royal advisors send a letter saying so, naming the milestone the
    // town has grown into. From then on the daily notice (NoBedWarning) does the nagging. (Jess, 2026-10-12.)
    //
    // The letter is the mod's own and the game must never see it in a save: its key is stripped from the letter
    // manager's saved lists, and the mod's own record ("fabSickness" in the save slot) says whether it has been
    // sent and whether it was answered, so an unanswered letter is sent again after a load.
    [HarmonyPatch]
    internal static class AdvisorLetter
    {
        internal const string Key = "fabSickBedsLetter";
        private const string SaveKey = "fabSickness";

        internal enum State { NotSent, Sent, Answered }
        internal static State Letter = State.NotSent;

        private static readonly Dictionary<PopulationMilestone, string> MilestoneWords = new Dictionary<PopulationMilestone, string>
        {
            { PopulationMilestone.ZERO, "settlement" }, { PopulationMilestone.HAMLET, "hamlet" }, { PopulationMilestone.SMALL_VILLAGE, "small village" },
            { PopulationMilestone.VILLAGE, "village" }, { PopulationMilestone.BIG_VILLAGE, "big village" }, { PopulationMilestone.SMALL_TOWN, "small town" },
            { PopulationMilestone.TOWN, "town" }, { PopulationMilestone.BIG_TOWN, "big town" }, { PopulationMilestone.SMALL_CITY, "small city" },
            { PopulationMilestone.CITY, "city" }, { PopulationMilestone.BIG_CITY, "big city" }, { PopulationMilestone.SMALL_KINGDOM, "small kingdom" },
            { PopulationMilestone.KINGDOM, "kingdom" },
        };

        private static string MilestoneWord()
        {
            try
            {
                var m = KingdomManager.Instance != null ? KingdomManager.Instance.GetCurrentPopulationMilestone() : PopulationMilestone.TOWN;
                return MilestoneWords.TryGetValue(m, out var w) ? w : "town";
            }
            catch { return "town"; }
        }

        // --- the letter's data: a plain one-option letter cloned from a basic one of the game's -------------------

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            try
            {
                if (__instance.worldEventSoData.Any(e => e != null && e.key == Key)) return;
                var template = __instance.worldEventSoData.FirstOrDefault(e => e != null && e.worldEventClass == WorldEventClass.BASIC
                    && !e.hasCustomActions && !e.belongsToEncounter && e.actionDatas != null && e.actionDatas.Count > 0 && e.actionDatas[0] != null);
                if (template == null) { Plugin.Log.LogWarning("[sickness] no template letter found; the advisors' letter is off."); return; }

                var clone = AccessTools.Method(typeof(object), "MemberwiseClone");
                var ok = (WorldEventActionData)clone.Invoke(template.actionDatas[0], null);
                ok.title = Text.Make("We will see to it.");
                ok.worldEventAction = WorldEventAction.NONE;
                ok.hasMessage = ok.hasSound = ok.hasProbability = ok.startsQuest = ok.startsMission = false;
                ok.hasNobilityEffect = ok.hasNobilityCost = ok.hasCoinCost = ok.hasResourceEffect = ok.customResourceEffect = false;
                ok.startsPlacement = ok.hasBuff = ok.hasReward = ok.addsImmigrants = ok.makesSick = ok.addNaturalResources = false;
                ok.alterFamilyAid = ok.addWorldObjectInRealm = ok.addSoldiers = ok.disableWorldEventSeries = ok.startsCombat = false;

                var d = UnityEngine.Object.Instantiate(template);
                d.name = Key; d.key = Key;
                d.belongsToEncounter = false; d.belongsToSeries = false; d.seriesKey = "";
                d.isDailyEvent = false; d.worldEventClass = WorldEventClass.BASIC;
                d.canExpire = false; d.showInstant = true; d.hasSetDate = false; d.canRepeat = true; d.daysBetweenRepeat = 0;   // opens on arrival (Jess, 2026-10-12)
                d.playConfetti = false; d.preventDuringTutorial = false;
                d.hasWorldObjectRequirement = d.hasMinPopulation = d.hasMaxPopulation = d.hasSeasonRequirements = false;
                d.requiresHero = d.hasMinFortification = d.hasMinHappiness = d.hasMaxHappiness = d.hasMinNobility = false;
                d.hasMinResources = d.hasMaxResources = d.hasMinStructure = d.hasMaxStructure = d.hasMinUnemployed = d.hasMaxUnemployed = false;
                d.requiresPeasantCapacity = d.requiresCommonerCapacity = d.requiresNobleCapacity = d.requiresSoldierCapacity = false;
                d.mustHaveAvailableTerritories = false; d.hasCustomActions = false; d.hideSignature = false; d.injectDescription = false;
                d.hasSound = false; d.stopsMusic = false;
                d.title = Text.Make("Beds running short");
                d.description = Text.Make(() =>
                    "Your Highness, a word from your physicians. Our " + MilestoneWord() + " has outgrown its hospitals. The sick are being turned away " +
                    "at the door, and the first of them have now waited " + Plugin.WarnNoBedAfterDays.Value + " days for a bed. A sickness left untreated " +
                    "for a month is a death, and there will be more of them as we grow.\n\nMore hospitals, or more doctors in the ones we have, " +
                    "would see every one of them treated in time. We will let you know whenever someone has been waiting too long.");
                d.signature = Text.Make("Your royal advisors");
                d.actionDatas = new List<WorldEventActionData> { ok };
                __instance.worldEventSoData.Add(d);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] advisors' letter setup: " + ex.Message); }
        }

        // --- sending and answering --------------------------------------------------------------------------------

        internal static bool Exists() => DataManager.Instance != null && DataManager.Instance.worldEventSoData.Any(e => e != null && e.key == Key);

        internal static bool Send()
        {
            try
            {
                if (!Exists() || WorldEventManager.Instance == null) return false;
                WorldEventManager.Instance.TriggerEvent(Key, resetDailyCounter: false);
                Letter = State.Sent;
                Plugin.Log.LogInfo("[sickness] the advisors' letter about hospital beds was sent.");
                return true;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] sending the advisors' letter: " + ex.Message); return false; }
        }

        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPostfix]
        private static void Answered(SaveableWorldEventData saveableWorldEvent)
        {
            if (saveableWorldEvent != null && saveableWorldEvent.key == Key && Letter == State.Sent)
            {
                Letter = State.Answered;
                Plugin.Log.LogInfo("[sickness] the advisors' letter was answered; daily notices from here on.");
            }
        }

        // --- the save: the mod's record in, the letter itself out ----------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPrefix]
        private static void SaveState()
        {
            try
            {
                if (SaveMaster.GetActiveSlot() < 0) return;
                // only fablings still alive and still sick need remembering
                var alive = new HashSet<string>();
                foreach (var w in Nielsen.Worker.Instances)
                    if (w != null && w.needs != null && w.needs.isSick && w.unit != null && w.unit.worldObject != null && w.unit.worldObject.guid != null) alive.Add(w.unit.worldObject.guid);
                NoBedWarning.Warned.RemoveWhere(g => !alive.Contains(g));
                SaveMaster.SetString(SaveKey, "letter=" + Letter + ";warned=" + string.Join(",", NoBedWarning.Warned));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] save: " + ex.Message); }
        }

        [HarmonyPatch(typeof(DataManager), "Load"), HarmonyPostfix]
        private static void LoadState(int slot)
        {
            Letter = State.NotSent;
            NoBedWarning.Warned.Clear();
            if (slot < 0) return;
            try
            {
                string s = SaveMaster.GetString(SaveKey) ?? "";
                if (s.Contains("letter=Answered")) Letter = State.Answered;
                else if (s.Contains("letter=Sent")) Letter = State.Sent;
                int i = s.IndexOf("warned=", StringComparison.Ordinal);
                if (i >= 0)
                    foreach (var g in s.Substring(i + 7).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) NoBedWarning.Warned.Add(g);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] load: " + ex.Message); }
        }

        // The letter's key is unknown to the game: keep it out of the letter manager's saved lists.
        [HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.OnSave)), HarmonyPostfix]
        private static void StripFromSave(ref string __result)
        {
            try
            {
                var o = JObject.Parse(__result);
                bool changed = false;
                foreach (var listName in new[] { "savedActiveWorldEvents", "savedHandeledWorldEvents" })
                {
                    if (!(o[listName] is JArray arr)) continue;
                    foreach (var item in arr.Where(t => (string)t["key"] == Key).ToList()) { item.Remove(); changed = true; }
                }
                if (changed) __result = o.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] cleaning the letter save: " + ex.Message); }
        }

        // After a load, a letter that was sent but not answered is on no list any more: send it again.
        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoad(DataManager __instance) => __instance.OnLoadComplete += () => Plugin.Instance.StartCoroutine(Resend());

        private static IEnumerator Resend()
        {
            for (int i = 0; i < 30; i++) yield return null;
            if (Letter == State.Sent) Send();
        }
    }
}
