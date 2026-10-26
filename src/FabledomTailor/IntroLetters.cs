using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // Letters from the royal advisors that introduce the package as the kingdom grows (fabledom-mod-tailor.md,
    // "Introduction letters", Jess, 2026-10-17): a warning at Town that commoners will want variety, a quest at Big town
    // to build a Sheep farm, a reminder 15 days after Big town when the variety rule starts, a quest at Small city to
    // build a Tailor and make 20 garments, and two notices when the first and the 11th noble arrive. Each is sent once,
    // by code at its moment (not the random draw), one a day at most. The quests follow the game's own advisor quests:
    // 90 days, 10 nobility, -10 if missed. Everything is created at runtime, so the letters and quests are stripped
    // from the game's save and put back after a load, and a save made with the mod loads without it.
    [HarmonyPatch]
    internal static class IntroLetters
    {
        private const string TasteExotic = "fabTasteExotic", ShepherdsSeason = "fabShepherdsSeason", SupperServed = "fabSupperServed",
            TailorCourt = "fabTailorCourt", DressedPalace = "fabDressedPalace", LatestFashion = "fabLatestFashion";
        private const string ShepherdQuest = "fabShepherdQuest", TailorQuest = "fabTailorQuest";
        private static readonly string[] LetterKeys = { TasteExotic, ShepherdsSeason, SupperServed, TailorCourt, DressedPalace, LatestFashion };
        private static readonly string[] QuestKeys = { ShepherdQuest, TailorQuest };
        private static readonly System.Reflection.MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static WorldEventActionData Copy(WorldEventActionData a) => (WorldEventActionData)Clone.Invoke(a, null);
        private static bool ready;

        // --- setup ---------------------------------------------------------------------------------

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            var dm = __instance;   // Harmony passes the instance only under this name (2026-10-17: "dm" dropped the whole plugin)
            if (!Plugin.IntroLettersOn.Value || dm.worldEventSoData.Any(e => e != null && e.key == TasteExotic)) return;
            try
            {
                if (Tailor.FarmData == null || Tailor.TailorData == null) { Plugin.Log.LogWarning("Tailor: introduction letters: the buildings are not set up; no letters."); return; }
                var template = dm.worldEventSoData.FirstOrDefault(e => e != null && e.key == "freshBread" && e.actionDatas != null && e.actionDatas.Count >= 2)
                    ?? dm.worldEventSoData.FirstOrDefault(e => e != null && e.actionDatas != null && e.actionDatas.Any(a => a != null && a.startsQuest) && e.actionDatas.Any(a => a != null && a.worldEventAction == WorldEventAction.DECLINE));
                if (template == null) { Plugin.Log.LogWarning("Tailor: introduction letters: no quest letter to model on."); return; }
                var questAction = template.actionDatas.First(a => a != null && a.startsQuest);
                var plainAction = template.actionDatas.First(a => a != null && a.worldEventAction == WorldEventAction.DECLINE);
                var garment = dm.resourceSoData.FirstOrDefault(r => r != null && r.key == "garment");

                QuestData Quest(string key, string title, params QuestTask[] tasks)
                {
                    var q = ScriptableObject.CreateInstance<QuestData>();
                    q.name = key; q.key = key;
                    q.title = ModText.Make(title);
                    q.isNobilityReward = true; q.reward = 10;
                    q.hasDeadline = true; q.deadline = 90; q.deadlineMissedEffect = -10;
                    q.focusRingKeys = new List<string>();
                    q.tasks = tasks.ToList();
                    dm.questSoData.Add(q);
                    return q;
                }
                QuestTask Build(WorldObjectData what, string text) => new QuestTask { isConstruction = true, constructionData = what, requiredProgress = 1, description = ModText.Make(text), help = ModText.Make(""), additionalValues = new List<string>() };
                QuestTask Produce(ResourceData what, int amount, string text) => new QuestTask { isProduce = true, resourceData = what, requiredProgress = amount, highlightRequiredProgress = true, description = ModText.Make(text), help = ModText.Make(""), additionalValues = new List<string>() };

                var shepherd = Quest(ShepherdQuest, "The shepherd's season", Build(Tailor.FarmData, "Build a Sheep farm"));
                var tailorQ = garment != null
                    ? Quest(TailorQuest, "A tailor for the court", Build(Tailor.TailorData, "Build a Tailor"), Produce(garment, 20, "Make {0} garments"))
                    : Quest(TailorQuest, "A tailor for the court", Build(Tailor.TailorData, "Build a Tailor"));

                WorldEventData Letter(string key, WorldEventClass cls, string title, string body, string signature, params WorldEventActionData[] actions)
                {
                    var l = Object.Instantiate(template);
                    l.name = key; l.key = key;
                    l.worldEventClass = cls;
                    l.isDailyEvent = false; l.canRepeat = false; l.daysBetweenRepeat = 0;
                    l.belongsToSeries = false; l.belongsToEncounter = false;
                    l.canExpire = false; l.hasSetDate = false; l.showInstant = false;
                    l.hasMinPopulation = l.hasMaxPopulation = false;
                    l.hasMinStructure = l.hasMaxStructure = false;
                    l.hasWorldObjectRequirement = false;
                    l.hasMinResources = l.hasMaxResources = false;
                    l.hasMinNobility = false;
                    l.preventDuringTutorial = false;
                    l.injectDescription = false;
                    l.title = ModText.Make(title);
                    l.description = ModText.Make(body);
                    l.signature = ModText.Make(signature);
                    l.hideSignature = false;
                    l.actionDatas = actions.ToList();
                    dm.worldEventSoData.Add(l);
                    return l;
                }
                WorldEventActionData Ok(string text)
                {
                    var a = Copy(plainAction);
                    a.worldEventAction = WorldEventAction.NONE;
                    a.title = ModText.Make(text); a.hasMessage = false; a.description = ModText.Make("");
                    a.startsQuest = false; a.quest = null;
                    return a;
                }
                WorldEventActionData StartQuest(QuestData q, string text, string tooltip)
                {
                    var a = Copy(questAction);
                    a.worldEventAction = WorldEventAction.NONE;
                    a.startsQuest = true; a.quest = q;
                    a.title = ModText.Make(text); a.hasMessage = true; a.description = ModText.Make(tooltip);
                    return a;
                }
                WorldEventActionData Decline(string text)
                {
                    var a = Copy(plainAction);
                    a.worldEventAction = WorldEventAction.DECLINE;
                    a.title = ModText.Make(text); a.hasMessage = false; a.description = ModText.Make("");
                    a.startsQuest = false; a.quest = null;
                    return a;
                }
                const string advisors = "The royal advisors";

                Letter(TasteExotic, WorldEventClass.BASIC, "A taste for the exotic",
                    "As our town grows, so do our commoners' appetites. Travellers bring tales of delicious foods in far-away lands, and soon nobody will be content with the same supper twice. Once we are a Big town, commoners will want three different dishes across their last four meals, or they will grow restless. Best to keep the shelves varied: fish, eggs, bread, pork, and whatever wonders the bakers dream up next.",
                    advisors, Ok("We shall set a finer table."));
                Letter(ShepherdsSeason, WorldEventClass.QUEST, "The shepherd's season",
                    "Word from the other realms: the finest courts are dressing in wool. There are wild sheep on our lands, and a Sheep farm would bring a flock home. Your advisors would like to look fashionable before the neighbours notice we don't.",
                    advisors, StartQuest(shepherd, "Bring a flock home.", "Build a Sheep farm within 90 days for 10 nobility."), Decline("Sheep can wait."));
                Letter(SupperServed, WorldEventClass.BASIC, "Supper is served",
                    "The day has come: our commoners now expect three different dishes across their last four meals, and the grumbling has begun where the shelves are bare. Fish, eggs, bread and pork keep a table honest; the heads of each condominium will fetch what a house is missing, if the stores hold it.",
                    advisors, Ok("Noted. Tell the cooks."));
                Letter(TailorCourt, WorldEventClass.QUEST, "A tailor for the court",
                    "We have wool. We have nobody who knows what to do with it. A Tailor turns three wool into one garment, and our future nobles will expect a wardrobe. Also, a Confectionery has opened its doors to the idea of pastry. Your advisors have tried one. Several.",
                    advisors, StartQuest(tailorQ, "Open a Tailor.", "Build a Tailor and make 20 garments within 90 days for 10 nobility."), Decline("Wool is warm enough as it is."));
                var dressed = Letter(DressedPalace, WorldEventClass.BASIC, "Dressed for the palace", "", advisors, Ok("The quarters will be kept."));
                // the interval is by difficulty (Garments.DaysByDifficulty), known only once a game is loaded: read when shown
                dressed.description = ModText.Make(() => $"Your first noble has arrived, and has already asked what everyone is wearing. Nobles want a new garment every {Garments.DaysByDifficulty().noble} days and a table with real variety. Keep the living quarters stocked, or hear about it.");
                Letter(LatestFashion, WorldEventClass.BASIC, "The latest fashion",
                    "The court's fashions are spreading. Your commoners have started admiring the nobles' garments and want some of their own. Expect requests from the town houses.",
                    advisors, Ok("Let them dress up."));
                ready = true;
                Plugin.Log.LogInfo($"Tailor: introduction letters added ({LetterKeys.Length} letters, {QuestKeys.Length} quests; modelled on '{template.key}').");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor: introduction letters setup failed: " + ex); }
        }

        // --- sending -------------------------------------------------------------------------------

        private static bool Sent(string key) => Garments.S.IntroSent.Contains(key);
        private static bool Waiting(string key)
        {
            var active = Traverse.Create(WorldEventManager.Instance).Field("activeWorldEvents").GetValue<List<SaveableWorldEventData>>();
            return active != null && active.Any(e => e != null && e.key == key);
        }
        private static void Send(string key, string why)
        {
            Garments.S.IntroSent.Add(key);
            Garments.S.IntroPending.Add(key);
            WorldEventManager.Instance.TriggerEvent(key, resetDailyCounter: false);
            Plugin.Log.LogInfo($"Tailor: letter '{key}' sent ({why}).");
        }

        // Once a day, from the garment step: at most one letter, in the order of the table.
        internal static void Daily()
        {
            if (!ready || !Plugin.IntroLettersOn.Value || KingdomManager.Instance == null || WorldEventManager.Instance == null || DateTimeManager.Instance == null) return;
            var S = Garments.S;
            var ms = KingdomManager.Instance.GetCurrentPopulationMilestone();
            int today = DateTimeManager.Instance.GetTotalDaysTranspired();
            int nobles = Worker.GetCount(FablingClass.NOBLE);
            // A save that is already past these moments when the mod arrives gets no stale letters (once, on the first day).
            if (!S.IntroCaughtUp)
            {
                S.IntroCaughtUp = true;
                if (ms >= PopulationMilestone.BIG_TOWN) { S.IntroSent.Add(TasteExotic); S.IntroSent.Add(SupperServed); S.IntroSent.Add(ShepherdsSeason); S.BigTownDay = today; }
                if (ms >= PopulationMilestone.SMALL_CITY) S.IntroSent.Add(TailorCourt);
                if (nobles >= 1) S.IntroSent.Add(DressedPalace);
                if (nobles >= 11) S.IntroSent.Add(LatestFashion);
                if (S.IntroSent.Count > 0) Plugin.Log.LogInfo($"Tailor: introduction letters: {S.IntroSent.Count} already past on this save; not sent.");
            }
            if (ms >= PopulationMilestone.BIG_TOWN && S.BigTownDay < 0) S.BigTownDay = today;
            if (ms >= PopulationMilestone.TOWN && !Sent(TasteExotic)) { Send(TasteExotic, "Town"); return; }
            if (ms >= PopulationMilestone.BIG_TOWN && !Sent(ShepherdsSeason)) { Send(ShepherdsSeason, "Big town"); return; }
            if (S.BigTownDay >= 0 && today - S.BigTownDay >= Plugin.IntroReminderDays.Value && !Sent(SupperServed)) { Send(SupperServed, $"{Plugin.IntroReminderDays.Value} days after Big town"); return; }
            if (ms >= PopulationMilestone.SMALL_CITY && !Sent(TailorCourt)) { Send(TailorCourt, "Small city"); return; }
            if (nobles >= 1 && !Sent(DressedPalace)) { Send(DressedPalace, "the first noble"); return; }
            if (nobles >= 11 && !Sent(LatestFashion)) { Send(LatestFashion, "the 11th noble"); return; }
        }

        [HarmonyPatch(typeof(WorldEventManager), "ActionTaken"), HarmonyPostfix]
        private static void Answered(SaveableWorldEventData saveableWorldEvent)
        {
            if (saveableWorldEvent != null && Garments.S.IntroPending.Remove(saveableWorldEvent.key)) Plugin.Log.LogInfo($"Tailor: letter '{saveableWorldEvent.key}' answered.");
        }

        // --- save hygiene ----------------------------------------------------------------------------
        // The letters and quests exist only while the mod is loaded, so they are cut from the game's save: the letters
        // from the letter manager's lists, the quests from the quest manager's list (kept in our own state with their
        // progress, and put back after a load).

        [HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.OnSave)), HarmonyPostfix]
        private static void StripLetters(ref string __result)
        {
            if (!ready) return;
            try
            {
                var o = JObject.Parse(__result);
                int n = 0;
                foreach (var listName in new[] { "savedActiveWorldEvents", "savedHandeledWorldEvents" })
                {
                    if (!(o[listName] is JArray arr)) continue;
                    for (int i = arr.Count - 1; i >= 0; i--)
                        if (LetterKeys.Contains((string)arr[i]["key"])) { arr.RemoveAt(i); n++; }
                }
                if (n > 0) __result = o.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: letters out of the save: " + ex.Message); }
        }

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.OnSave)), HarmonyPostfix]
        private static void StripQuests(ref string __result)
        {
            if (!ready) return;
            try
            {
                var o = JObject.Parse(__result);
                if (!(o["savedQuests"] is JArray arr)) return;
                Garments.S.IntroQuests.Clear();
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    string key = (string)arr[i]["key"];
                    if (!QuestKeys.Contains(key)) continue;
                    Garments.S.IntroQuests[key] = arr[i].ToString(Newtonsoft.Json.Formatting.None);
                    arr.RemoveAt(i);
                }
                if (Garments.S.IntroQuests.Count > 0) __result = o.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: quests out of the save: " + ex.Message); }
        }

        // After a load (from the garment step's settled coroutine): put our quests back with their progress, and
        // re-send letters that were still waiting for an answer.
        internal static void AfterLoad()
        {
            if (!ready || QuestManager.Instance == null || WorldEventManager.Instance == null) return;
            try
            {
                var quests = Traverse.Create(QuestManager.Instance).Field("quests").GetValue<List<SaveableQuestData>>();
                int restored = 0;
                foreach (var kv in Garments.S.IntroQuests.ToList())
                {
                    if (quests == null || quests.Any(q => q != null && q.key == kv.Key)) continue;
                    var q = JsonUtility.FromJson<SaveableQuestData>(kv.Value);
                    if (q == null) continue;
                    quests.Add(q);
                    if (q.isVisible && UIManager.Instance != null && UIManager.Instance.questContainer != null) UIManager.Instance.questContainer.AddQuestSlot(q);
                    restored++;
                }
                int resent = 0;
                foreach (var key in Garments.S.IntroPending.ToList())
                    if (!Waiting(key)) { WorldEventManager.Instance.TriggerEvent(key, resetDailyCounter: false); resent++; }
                if (restored > 0 || resent > 0) Plugin.Log.LogInfo($"Tailor: after load: {restored} introduction quest(s) restored, {resent} letter(s) re-sent.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: introduction letters after load: " + ex.Message); }
        }
    }
}
