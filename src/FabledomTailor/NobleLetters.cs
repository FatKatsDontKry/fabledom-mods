using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // Three noble letters for the new goods (fabledom-mod-tailor.md, "Noble letters for garments and pastries", Jess,
    // 2026-10-22), in the game's own shape: copies of the game's "A noble wanting food" (goods now, +5 nobility, family
    // aid +20, a noble moves in) and "A noble requesting to gather stuff" (10 nobility to accept, a quest with no deadline
    // that cannot fail, the noble as its reward). They join the game's NobleInNeed series, so the game's daily draw sends
    // them under the same rules: two free noble beds, the series cooldown, once per 1,000 days each. Everything exists
    // only while the mod is loaded, so the letters' records and the quests are cut from the game's save, kept in the
    // mod's own state, and put back after a load.
    [HarmonyPatch]
    internal static class NobleLetters
    {
        internal const string Fashion = "fabNobleFashion", Wardrobe = "fabNobleWardrobe", Pastries = "fabNoblePastries";
        private const string WardrobeQuest = "fabNobleWardrobeQuest", PastriesQuest = "fabNoblePastriesQuest";
        internal static readonly string[] LetterKeys = { Fashion, Wardrobe, Pastries };
        private static readonly string[] QuestKeys = { WardrobeQuest, PastriesQuest };
        private static WorldEventActionData Copy(WorldEventActionData a) => ModText.Clone(a);
        private static bool ready;

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Setup(DataManager __instance)
        {
            var dm = __instance;
            if (!Plugin.NobleLettersOn.Value || dm.worldEventSoData.Any(e => e != null && e.key == Fashion)) return;
            try
            {
                var wanting = dm.worldEventSoData.FirstOrDefault(e => e != null && e.key == "aNobleWantingFood" && e.actionDatas != null && e.actionDatas.Count >= 3);
                var requesting = dm.worldEventSoData.FirstOrDefault(e => e != null && e.key == "aNobleQuestD" && e.actionDatas != null && e.actionDatas.Count >= 2);
                if (wanting == null || requesting == null) { Plugin.Log.LogWarning("Tailor: noble letters: the game's noble letters were not found; none added."); return; }
                if (Tailor.TailorData == null || Confectionery.Data == null) { Plugin.Log.LogWarning("Tailor: noble letters: the buildings are not set up; none added."); return; }
                ResourceData Res(string k) => dm.resourceSoData.FirstOrDefault(r => r != null && r.key == k);
                ResourceData garment = Res("garment"), pastry = Res("pastry"), pork = Res("pork"), egg = Res("egg");
                if (garment == null || pastry == null || pork == null || egg == null) { Plugin.Log.LogWarning("Tailor: noble letters: a resource is missing; none added."); return; }

                QuestData Quest(string key, string title, params QuestTask[] tasks)
                {
                    var q = ScriptableObject.CreateInstance<QuestData>();
                    q.name = key; q.key = key;
                    q.title = ModText.Make(title);
                    q.isImmigrantReward = true; q.immigrants = new KeyValue<FablingClass, int>(FablingClass.NOBLE, 1);
                    q.isNobilityReward = false; q.reward = 0;
                    q.hasDeadline = false; q.deadline = 0; q.deadlineMissedEffect = 0;   // noble quests cannot fail (Jess)
                    q.focusRingKeys = new List<string>();
                    q.tasks = tasks.ToList();
                    dm.questSoData.Add(q);
                    return q;
                }
                QuestTask Produce(ResourceData what, int amount, string text) => new QuestTask { isProduce = true, resourceData = what, requiredProgress = amount, highlightRequiredProgress = true, description = ModText.Make(text), help = ModText.Make(""), additionalValues = new List<string>() };

                var wardrobeQ = Quest(WardrobeQuest, "A noble requesting a wardrobe",
                    Produce(garment, Plugin.WardrobeGarments.Value, "Make {0} garments"),
                    Produce(pork, Plugin.WardrobePork.Value, "Produce {0} pork (skins for the gloves)"),
                    Produce(egg, Plugin.WardrobeEggs.Value, "Produce {0} eggs (feathers)"));
                var pastriesQ = Quest(PastriesQuest, "A noble requesting pastries",
                    Produce(pastry, Plugin.PastriesQuestAmount.Value, "Make {0} pastries"));

                WorldEventData Letter(WorldEventData template, string key, string title, string body, string signature, WorldObjectData gate, params WorldEventActionData[] actions)
                {
                    var l = Object.Instantiate(template);   // keeps the noble-letter shape: NOBLE class, daily, the NobleInNeed series, 1,000-day repeat, two free beds
                    l.name = key; l.key = key;
                    l.title = ModText.Make(title);
                    l.description = ModText.Make(body);
                    l.signature = ModText.Make(signature); l.hideSignature = false;
                    l.injectDescription = false;
                    l.hasMinStructure = true; l.minStructure = gate; l.minStructureAmount = 1;
                    l.hasMaxStructure = false;
                    l.preventDuringTutorial = false;
                    l.actionDatas = actions.ToList();
                    dm.worldEventSoData.Add(l);
                    return l;
                }
                WorldEventActionData Goods(string text, string tooltip, params (ResourceData res, int amount)[] cost)
                {
                    var a = Copy(wanting.actionDatas[0]);   // +5 nobility, family aid +20, addsImmigrants NOBLE 1 come with it
                    a.title = ModText.Make(text); a.hasMessage = true; a.description = ModText.Make(tooltip);
                    a.hasResourceEffect = true; a.customResourceEffect = false; a.scaleResourceEffect = false;
                    var list = cost.Select(c => new WorldEventActionDataResource { resourceAmountData = new ResourceAmountData(c.res, c.amount), isCost = true, hasTradeData = false }).ToList();
                    Traverse.Create(a).Field("resources").SetValue(list);
                    return a;
                }
                WorldEventActionData StartQuest(QuestData q, string text, string tooltip)
                {
                    var a = Copy(requesting.actionDatas[0]);   // startsQuest, 10 nobility to accept come with it
                    a.startsQuest = true; a.quest = q;
                    a.title = ModText.Make(text); a.hasMessage = true; a.description = ModText.Make(tooltip);
                    a.addsImmigrants = false;
                    return a;
                }
                WorldEventActionData Plain(WorldEventData template, int index) => Copy(template.actionDatas[index]);   // the game's own Decline / Postpone

                Letter(wanting, Fashion, "A noble of fashion",
                    "Lady Ottoline has heard your tailor is the talk of the road. A hundred and fifty garments to open her wardrobe, fifty pastries for the journey, and rooms in the palace; in return she promises to be seen in nothing else.",
                    "Lady Ottoline", Tailor.TailorData,
                    Goods("Open her wardrobe.", $"{Plugin.FashionGarments.Value} garments and {Plugin.FashionPastries.Value} pastries; she moves into the palace.", (garment, Plugin.FashionGarments.Value), (pastry, Plugin.FashionPastries.Value)),
                    Plain(wanting, 1), Plain(wanting, 2));
                Letter(requesting, Wardrobe, "A noble requesting a wardrobe",
                    "A countess will come, she writes, when your tailor can dress a court: three hundred garments, a hundred pork for the glove skins, two hundred eggs for the feathers, and she will bring her own trunks.",
                    "The Countess of Ashby", Tailor.TailorData,
                    StartQuest(wardrobeQ, "Dress the court.", $"10 nobility. Make {Plugin.WardrobeGarments.Value} garments, {Plugin.WardrobePork.Value} pork and {Plugin.WardrobeEggs.Value} eggs; the countess arrives when it is done."),
                    Plain(requesting, 1));
                Letter(requesting, Pastries, "A noble requesting pastries",
                    "Five hundred pastries, the letter says, would prove the confectioner is no passing fancy. The signature is sticky.",
                    "(a sticky signature)", Confectionery.Data,
                    StartQuest(pastriesQ, "Prove it.", $"10 nobility. Make {Plugin.PastriesQuestAmount.Value} pastries; the noble arrives when it is done."),
                    Plain(requesting, 1));
                ready = true;
                Plugin.Log.LogInfo($"Tailor: noble letters added ({LetterKeys.Length} letters, {QuestKeys.Length} quests; modelled on '{wanting.key}' and '{requesting.key}').");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("Tailor: noble letters setup failed: " + ex); }
        }

        // The data holds one structure gate; A noble of fashion needs a Confectionery as well as a Tailor. The game has two
        // overloads of this check; the daily draw calls the private two-argument one and the public one wraps it, so the
        // patch names that signature (by name alone Harmony refused it as ambiguous and the whole plugin failed, 2026-10-22).
        [HarmonyPatch(typeof(WorldEventManager), "IsEventUnavailable", new[] { typeof(List<SaveableWorldEventData>), typeof(WorldEventData) }), HarmonyPostfix]
        private static void SecondGate(WorldEventData eventToCheck, ref bool __result)
        {
            if (__result || !ready || eventToCheck == null || eventToCheck.key != Fashion || Confectionery.Data == null) return;
            try
            {
                if (!Placeable.Instances.Any(p => p != null && p.worldObject != null && p.worldObject.data != null && p.worldObject.data.id == Confectionery.Data.id)) __result = true;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: noble letters gate: " + ex.Message); }
        }

        // --- save hygiene ----------------------------------------------------------------------------
        // The letters' records (one waiting for an answer, and the answered ones that hold the 1,000-day repeat timer)
        // and the quests are cut from the game's save and kept in the mod's state; after a load the answered records and
        // the quests go back, and a letter that was waiting is sent again.

        [HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.OnSave)), HarmonyPostfix]
        private static void StripLetters(ref string __result)
        {
            if (!ready) return;
            try
            {
                var o = JObject.Parse(__result);
                var S = Garments.S;
                S.NobleHandled.Clear(); S.NoblePending.Clear();
                int n = 0;
                if (o["savedActiveWorldEvents"] is JArray act)
                    for (int i = act.Count - 1; i >= 0; i--)
                        if (LetterKeys.Contains((string)act[i]["key"])) { S.NoblePending.Add((string)act[i]["key"]); act.RemoveAt(i); n++; }
                if (o["savedHandeledWorldEvents"] is JArray han)
                    for (int i = han.Count - 1; i >= 0; i--)
                        if (LetterKeys.Contains((string)han[i]["key"])) { S.NobleHandled.Add(han[i].ToString(Newtonsoft.Json.Formatting.None)); han.RemoveAt(i); n++; }
                if (n > 0) __result = o.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: noble letters out of the save: " + ex.Message); }
        }

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.OnSave)), HarmonyPostfix]
        private static void StripQuests(ref string __result)
        {
            if (!ready) return;
            try
            {
                var o = JObject.Parse(__result);
                if (!(o["savedQuests"] is JArray arr)) return;
                Garments.S.NobleQuests.Clear();
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    string key = (string)arr[i]["key"];
                    if (!QuestKeys.Contains(key)) continue;
                    Garments.S.NobleQuests[key] = arr[i].ToString(Newtonsoft.Json.Formatting.None);
                    arr.RemoveAt(i);
                }
                if (Garments.S.NobleQuests.Count > 0) __result = o.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: noble quests out of the save: " + ex.Message); }
        }

        internal static void AfterLoad()
        {
            if (!ready || QuestManager.Instance == null || WorldEventManager.Instance == null) return;
            try
            {
                var S = Garments.S;
                var quests = Traverse.Create(QuestManager.Instance).Field("quests").GetValue<List<SaveableQuestData>>();
                int restored = 0;
                foreach (var kv in S.NobleQuests.ToList())
                {
                    if (quests == null || quests.Any(q => q != null && q.key == kv.Key)) continue;
                    var q = JsonUtility.FromJson<SaveableQuestData>(kv.Value);
                    if (q == null) continue;
                    quests.Add(q);
                    if (q.isVisible && UIManager.Instance != null && UIManager.Instance.questContainer != null) UIManager.Instance.questContainer.AddQuestSlot(q);
                    restored++;
                }
                var handled = Traverse.Create(WorldEventManager.Instance).Field("handeledWorldEvents").GetValue<List<SaveableWorldEventData>>();
                int records = 0;
                if (handled != null)
                    foreach (var json in S.NobleHandled.ToList())
                    {
                        var r = JsonUtility.FromJson<SaveableWorldEventData>(json);
                        if (r == null || handled.Any(h => h != null && h.key == r.key)) continue;
                        handled.Add(r); records++;
                    }
                var active = Traverse.Create(WorldEventManager.Instance).Field("activeWorldEvents").GetValue<List<SaveableWorldEventData>>();
                int resent = 0;
                foreach (var key in S.NoblePending.ToList())
                    if (active == null || !active.Any(e => e != null && e.key == key)) { WorldEventManager.Instance.TriggerEvent(key, resetDailyCounter: false); resent++; }
                if (restored > 0 || records > 0 || resent > 0) Plugin.Log.LogInfo($"Tailor: after load: {restored} noble quest(s) restored, {records} answered noble letter(s) remembered, {resent} re-sent.");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: noble letters after load: " + ex.Message); }
        }
    }
}
