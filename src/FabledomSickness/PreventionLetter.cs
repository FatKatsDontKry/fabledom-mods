using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nielsen;
using UnityEngine;

namespace FabledomSickness
{
    // The Hospital letter (Jess, 2026-10-18; fabledom-mods.md, "Sickness scaling with population"). With scaling on,
    // the game's own "Building a hospital" letter (the advisors' Spring 1 quest, Town to Big town, never in tutorial
    // games) is switched off, and "An ounce of prevention" comes instead the first time a fabling falls ill while no
    // Hospital stands or is being built, in every game. Tutorial off: it carries its own quest, build a Hospital in
    // 90 days for 10 nobility, the terms of every royal advisors' quest. Tutorial on: the letter alone, since the
    // tutorial has its own Hospital step, so there are never two. Why: the tutorial's step has no deadline, and players
    // used to flat sickness delay the Hospital; with scaling, that hurts by 120.
    //
    // The letters and the quest are the mod's own: they are cut from the game's save (the quest kept here with its
    // progress and put back after a load), so a save made with the mod loads without it.
    [HarmonyPatch]
    internal static class PreventionLetter
    {
        internal const string KeyQuestLetter = "fabOuncePrevention", KeyPlainLetter = "fabOuncePreventionPlain", QuestKey = "fabHospitalQuest";
        private const string GameLetter = "buildingAnHospital";
        private const string SaveKey = "fabSicknessPrevention";

        internal class State
        {
            public bool Sent, Pending;        // sent once; still waiting for an answer (re-sent after a load)
            public string QuestJson;          // the quest's saved record, cut from the game's save
        }
        internal static State S = new State();
        private static bool ready;

        // --- data -------------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            try
            {
                if (!Plugin.Enabled.Value || __instance.worldEventSoData.Any(e => e != null && e.key == KeyQuestLetter)) return;
                // the game's letter stands down while scaling is on
                var game = __instance.worldEventSoData.FirstOrDefault(e => e != null && e.key == GameLetter);
                if (game != null) { game.hasSetDate = false; game.isDailyEvent = false; }

                var template = __instance.worldEventSoData.FirstOrDefault(e => e != null && e.key == "freshBread" && e.actionDatas != null && e.actionDatas.Count >= 2)
                    ?? __instance.worldEventSoData.FirstOrDefault(e => e != null && e.actionDatas != null && e.actionDatas.Any(a => a != null && a.startsQuest) && e.actionDatas.Any(a => a != null && a.worldEventAction == WorldEventAction.DECLINE));
                var hospital = Resources.FindObjectsOfTypeAll<WorldObjectData>().FirstOrDefault(w => w != null && w.name == "Hospital");
                if (template == null || hospital == null) { Plugin.Log.LogWarning($"[sickness] prevention letter: template {(template != null ? "ok" : "missing")}, Hospital data {(hospital != null ? "ok" : "missing")}; the letter is off."); return; }
                var clone = AccessTools.Method(typeof(object), "MemberwiseClone");
                var questAction = template.actionDatas.First(a => a != null && a.startsQuest);
                var plainAction = template.actionDatas.First(a => a != null && a.worldEventAction == WorldEventAction.DECLINE);

                var q = ScriptableObject.CreateInstance<QuestData>();
                q.name = QuestKey; q.key = QuestKey;
                q.title = Text.Make("An ounce of prevention");
                q.isNobilityReward = true; q.reward = 10;
                q.hasDeadline = true; q.deadline = 90; q.deadlineMissedEffect = -10;
                q.focusRingKeys = new List<string>();
                q.tasks = new List<QuestTask> { new QuestTask { isConstruction = true, constructionData = hospital, requiredProgress = 1, description = Text.Make("Build a Hospital"), help = Text.Make(""), additionalValues = new List<string>() } };
                __instance.questSoData.Add(q);

                WorldEventActionData Action(WorldEventActionData from, WorldEventAction kind, string title, QuestData quest, string tooltip)
                {
                    var a = (WorldEventActionData)clone.Invoke(from, null);
                    a.worldEventAction = kind;
                    a.title = Text.Make(title);
                    a.startsQuest = quest != null; a.quest = quest;
                    a.hasMessage = quest != null; a.description = Text.Make(tooltip ?? "");
                    a.hasSound = a.hasProbability = a.startsMission = false;
                    a.hasNobilityEffect = a.hasNobilityCost = a.hasCoinCost = a.hasResourceEffect = a.customResourceEffect = false;
                    a.startsPlacement = a.hasBuff = a.hasReward = a.addsImmigrants = a.makesSick = a.addNaturalResources = false;
                    a.alterFamilyAid = a.addWorldObjectInRealm = a.addSoldiers = a.disableWorldEventSeries = a.startsCombat = false;
                    return a;
                }
                WorldEventData Letter(string key, params WorldEventActionData[] actions)
                {
                    var d = UnityEngine.Object.Instantiate(template);
                    d.name = key; d.key = key;
                    d.belongsToEncounter = false; d.belongsToSeries = false; d.seriesKey = "";
                    d.isDailyEvent = false; d.worldEventClass = actions.Any(a => a.startsQuest) ? WorldEventClass.QUEST : WorldEventClass.BASIC;
                    d.canExpire = false; d.showInstant = false; d.hasSetDate = false; d.canRepeat = false; d.daysBetweenRepeat = 0;
                    d.playConfetti = false; d.preventDuringTutorial = false;
                    d.hasWorldObjectRequirement = d.hasMinPopulation = d.hasMaxPopulation = d.hasSeasonRequirements = false;
                    d.requiresHero = d.hasMinFortification = d.hasMinHappiness = d.hasMaxHappiness = d.hasMinNobility = false;
                    d.hasMinResources = d.hasMaxResources = d.hasMinStructure = d.hasMaxStructure = d.hasMinUnemployed = d.hasMaxUnemployed = false;
                    d.requiresPeasantCapacity = d.requiresCommonerCapacity = d.requiresNobleCapacity = d.requiresSoldierCapacity = false;
                    d.mustHaveAvailableTerritories = false; d.hasCustomActions = false; d.hideSignature = false; d.injectDescription = false;
                    d.hasSound = false; d.stopsMusic = false;
                    d.title = Text.Make("An ounce of prevention");
                    d.description = Text.Make("Your health advisor reports the first cough in town. In a kingdom this size, sickness travels with every new neighbour: the more fablings we have, the more of them fall ill. A Hospital now would spare us a great deal of grief later.\n\n(His son has offered a potion. We have politely declined.)");
                    d.signature = Text.Make("The royal advisors");
                    d.actionDatas = actions.ToList();
                    __instance.worldEventSoData.Add(d);
                    return d;
                }
                Letter(KeyQuestLetter, Action(questAction, WorldEventAction.NONE, "We will build one.", q, "Build a Hospital within 90 days for 10 nobility."), Action(plainAction, WorldEventAction.DECLINE, "Not now.", null, null));
                Letter(KeyPlainLetter, Action(plainAction, WorldEventAction.NONE, "We will see to it.", null, null));
                ready = true;
                Plugin.Log.LogInfo($"[sickness] prevention letter ready; the game's '{GameLetter}' letter is {(game != null ? "switched off" : "not in the data")}.");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] prevention letter setup: " + ex.Message); }
        }

        // --- sending ------------------------------------------------------------------------------------------------

        // Once a day, from the daily roll: the first sick fabling while no Hospital stands or is being built.
        internal static void Check()
        {
            try
            {
                if (!ready || S.Sent || WorldEventManager.Instance == null || Worker.Instances == null) return;
                bool anySick = Worker.Instances.Any(w => w != null && w.needs != null && w.needs.isSick && w.unit != null && w.unit.health != null && !w.unit.health.isDead);
                if (!anySick) return;
                if (Hospital.Instances != null && Hospital.Instances.Any(h => h != null)) return;   // placed or built: the letter has nothing to add
                bool tutorial = DataManager.Instance != null && DataManager.Instance.gameData != null && DataManager.Instance.gameData.isUsingTutorial;
                string key = tutorial ? KeyPlainLetter : KeyQuestLetter;
                S.Sent = true; S.Pending = true;
                WorldEventManager.Instance.TriggerEvent(key, resetDailyCounter: false);
                Plugin.Log.LogInfo($"[sickness] the first sickness with no Hospital: 'An ounce of prevention' sent ({(tutorial ? "tutorial game, letter only" : "with its quest")}).");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] prevention letter: " + ex.Message); }
        }

        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPostfix]
        private static void Answered(SaveableWorldEventData saveableWorldEvent)
        {
            if (saveableWorldEvent != null && (saveableWorldEvent.key == KeyQuestLetter || saveableWorldEvent.key == KeyPlainLetter) && S.Pending)
            {
                S.Pending = false;
                Plugin.Log.LogInfo("[sickness] 'An ounce of prevention' answered.");
            }
        }

        // --- the save -----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Save"), HarmonyPrefix]
        private static void SaveState()
        {
            try { if (SaveMaster.GetActiveSlot() >= 0) SaveMaster.SetString(SaveKey, JsonConvert.SerializeObject(S)); }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] prevention save: " + ex.Message); }
        }

        [HarmonyPatch(typeof(DataManager), "Load"), HarmonyPostfix]
        private static void LoadState(int slot)
        {
            S = new State();
            if (slot < 0) return;
            try
            {
                string json = SaveMaster.GetString(SaveKey);
                if (!string.IsNullOrEmpty(json)) S = JsonConvert.DeserializeObject<State>(json) ?? new State();
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] prevention load: " + ex.Message); S = new State(); }
        }

        [HarmonyPatch(typeof(WorldEventManager), "Start"), HarmonyPrefix]
        private static void NewGame(WorldEventManager __instance)
        {
            if (!Traverse.Create(__instance).Field("isLoaded").GetValue<bool>()) S = new State();
        }

        [HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.OnSave)), HarmonyPostfix]
        private static void StripLetters(ref string __result)
        {
            if (!ready) return;
            try
            {
                var o = JObject.Parse(__result);
                bool changed = false;
                foreach (var listName in new[] { "savedActiveWorldEvents", "savedHandeledWorldEvents" })
                {
                    if (!(o[listName] is JArray arr)) continue;
                    foreach (var item in arr.Where(t => (string)t["key"] == KeyQuestLetter || (string)t["key"] == KeyPlainLetter).ToList()) { item.Remove(); changed = true; }
                }
                if (changed) __result = o.ToString(Formatting.None);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] cleaning the letter save: " + ex.Message); }
        }

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.OnSave)), HarmonyPostfix]
        private static void StripQuest(ref string __result)
        {
            if (!ready) return;
            try
            {
                var o = JObject.Parse(__result);
                if (!(o["savedQuests"] is JArray arr)) return;
                var mine = arr.FirstOrDefault(t => (string)t["key"] == QuestKey);
                if (mine == null) return;
                S.QuestJson = mine.ToString(Formatting.None);
                mine.Remove();
                __result = o.ToString(Formatting.None);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] cleaning the quest save: " + ex.Message); }
        }

        // After a load: the quest back with its progress, and an unanswered letter sent again.
        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void HookLoad(DataManager __instance) => __instance.OnLoadComplete += () => Plugin.Instance.StartCoroutine(AfterLoad());

        private static IEnumerator AfterLoad()
        {
            for (int i = 0; i < 30; i++) yield return null;
            if (!ready) yield break;
            try
            {
                if (!string.IsNullOrEmpty(S.QuestJson) && QuestManager.Instance != null)
                {
                    var quests = Traverse.Create(QuestManager.Instance).Field("quests").GetValue<List<SaveableQuestData>>();
                    if (quests != null && !quests.Any(x => x != null && x.key == QuestKey))
                    {
                        var q = JsonUtility.FromJson<SaveableQuestData>(S.QuestJson);
                        if (q != null)
                        {
                            quests.Add(q);
                            if (q.isVisible && UIManager.Instance != null && UIManager.Instance.questContainer != null) UIManager.Instance.questContainer.AddQuestSlot(q);
                            Plugin.Log.LogInfo("[sickness] the Hospital quest is back in the panel.");
                        }
                    }
                }
                if (S.Pending && WorldEventManager.Instance != null)
                {
                    var active = Traverse.Create(WorldEventManager.Instance).Field("activeWorldEvents").GetValue<List<SaveableWorldEventData>>();
                    bool waiting = active != null && active.Any(e => e != null && (e.key == KeyQuestLetter || e.key == KeyPlainLetter));
                    if (!waiting)
                    {
                        bool tutorial = DataManager.Instance != null && DataManager.Instance.gameData != null && DataManager.Instance.gameData.isUsingTutorial;
                        WorldEventManager.Instance.TriggerEvent(tutorial ? KeyPlainLetter : KeyQuestLetter, resetDailyCounter: false);
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] prevention after load: " + ex.Message); }
        }
    }
}
