using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FabledomLetters
{
    // Edits the letter data once, when DataManager wakes (before any save loads):
    // takes the family and sister letters out of the series system, adjusts their conditions,
    // adds the new options, and registers the mod's own letter, buff and notification.
    [HarmonyPatch]
    internal static class DataSetup
    {
        private static readonly System.Reflection.MethodInfo Clone = AccessTools.Method(typeof(object), "MemberwiseClone");
        private static readonly AccessTools.FieldRef<WorldEventActionData, int> CoinCost = AccessTools.FieldRefAccess<WorldEventActionData, int>("coinCost");

        private static WorldEventActionData Copy(WorldEventActionData a) => (WorldEventActionData)Clone.Invoke(a, null);

        [HarmonyPatch(typeof(DataManager), "Awake"), HarmonyPostfix]
        private static void AfterDataManagerAwake(DataManager __instance)
        {
            try { Setup(__instance); }
            catch (System.Exception ex) { Plugin.Log.LogError("Letter data setup failed: " + ex); }
        }

        private static void Setup(DataManager dm)
        {
            if (dm.worldEventSoData.Any(e => e.key == Keys.Demand)) return;   // already done

            WorldEventData Get(string key)
            {
                var d = dm.worldEventSoData.FirstOrDefault(e => e.key == key);
                if (d == null) Plugin.Log.LogError("Letter not found in game data: " + key);
                return d;
            }

            foreach (var key in Keys.All.Where(k => !Keys.Custom.Contains(k)))
            {
                var d = Get(key);
                if (d == null) continue;
                d.belongsToSeries = false;   // the mod decides availability instead of series locks
                d.canRepeat = false;
                Plugin.Log.LogInfo($"[letters] {key}: class={d.worldEventClass}, daily={d.isDailyEvent}, options={d.actionDatas.Count}, " +
                                   $"minPop={(d.hasMinPopulation ? d.minPopulationRequired.ToString() : "-")}, maxPop={(d.hasMaxPopulation ? d.maxPopulationLimit.ToString() : "-")}");
            }

            var gift = Get(Keys.Gift);
            var invest = Get(Keys.Invest);
            var move = Get(Keys.Move);
            var card = Get(Keys.Card);
            var cure = Get(Keys.Cure);
            var q1 = Get(Keys.Q1);
            var q4 = Get(Keys.Q4);

            // Family move joins the random draw (nothing sends it in the base game).
            if (move != null) move.isDailyEvent = true;

            // Credit card: hold 150 coin or less (was 20).
            if (card != null && card.hasMaxResources && card.maxResources != null)
                card.maxResources.amount = Plugin.CardMaxCoin.Value;

            // No upper milestone for the cure and the first sister quest; no coin window for territory.
            if (cure != null) cure.hasMaxPopulation = false;
            if (q1 != null) q1.hasMaxPopulation = false;
            if (q4 != null) { q4.hasMinResources = false; q4.hasMaxResources = false; }

            // Exchange buff (-happiness while the noble youth is away).
            var buffTemplate = dm.buffSoData.FirstOrDefault(b => b.key == "happinessDebuff") ?? dm.buffSoData.FirstOrDefault(b => b.isNegative);
            BuffData exchange = null;
            if (buffTemplate != null)
            {
                exchange = Object.Instantiate(buffTemplate);
                exchange.name = Keys.ExchangeBuff;
                exchange.key = Keys.ExchangeBuff;
                exchange.daysActive = Plugin.ExchangeDays.Value;
                exchange.activeForever = false;
                exchange.isHappiness = true;
                exchange.isNegative = true;
                exchange.hasSound = false;
                exchange.values = new List<string> { Plugin.ExchangeHappiness.Value.ToString() };
                exchange.title = ModText.Make("Noble exchange");
                exchange.description = ModText.Make("Your most promising noble youth is abroad with your parents. Happiness -{0}. Days remaining: {1}.");
                dm.buffSoData.Add(exchange);
            }
            else Plugin.Log.LogError("No buff template found; Invest in family's exchange option will have no happiness effect.");

            // Big sister's gift: add a Decline option (cloned from Invest in family's decline).
            var declineTemplate = invest?.actionDatas.FirstOrDefault(a => a.worldEventAction == WorldEventAction.DECLINE);
            if (gift != null && declineTemplate != null && gift.actionDatas.Count == 1)
            {
                var decline = Copy(declineTemplate);
                decline.title = ModText.Make("Keep it, dear sister. We'll manage.");
                decline.hasMessage = false;
                gift.actionDatas.Add(decline);
            }

            // Invest in family: second accept option, inserted before Decline.
            if (invest != null && invest.actionDatas.Count == 2)
            {
                var b = Copy(invest.actionDatas[0]);
                b.title = ModText.Make("Take the coin, and our most promising noble youth for a season abroad.");
                b.hasMessage = true;
                b.description = ModText.Make("Pay {0}. Your parents host a noble youth for " + Plugin.ExchangeDays.Value +
                                             " days (happiness -" + Plugin.ExchangeHappiness.Value + "). Family aid is unchanged.");
                b.hasNobilityCost = false;
                b.hasNobilityEffect = false;
                b.hasResourceEffect = false;
                b.hasProbability = false;
                b.alterFamilyAid = false;
                b.hasCoinCost = true;
                b.scaleCoinCost = false;
                CoinCost(b) = Plugin.InvestBCoin.Value;
                b.hasBuff = exchange != null;
                b.buffToActivate = exchange;
                invest.actionDatas.Insert(1, b);
            }

            // The parents' demand letter (after declining the cure), built from the credit card letter.
            if (card != null)
            {
                var demand = Object.Instantiate(card);
                demand.name = Keys.Demand;
                demand.key = Keys.Demand;
                demand.isDailyEvent = true;   // drawn like any letter; the mod's rules decide when
                demand.belongsToSeries = false;
                demand.canRepeat = false;
                demand.hasSetDate = false;
                demand.hasMinPopulation = demand.hasMaxPopulation = false;
                demand.hasMinNobility = demand.hasMinResources = demand.hasMaxResources = false;
                demand.hasMinHappiness = demand.hasMaxHappiness = false;
                demand.hasMinStructure = demand.hasMaxStructure = false;
                demand.hasWorldObjectRequirement = false;
                demand.preventDuringTutorial = false;
                demand.injectDescription = false;
                demand.canExpire = true;
                demand.expiresAfterDays = 10;
                demand.title = ModText.Make("A family matter");
                demand.description = ModText.Make(
                    "You turned your back on your sister when she needed you most. We have not forgotten.\n\n" +
                    "The family expects you to make it right. Either show some humility before the court, " +
                    "or send more of your treasury home each payday.");

                var template = card.actionDatas[0];
                var pay = Copy(template);
                pay.title = ModText.Make("Humble myself before the court.");
                pay.worldEventAction = WorldEventAction.NONE;
                pay.hasMessage = true;
                pay.description = ModText.Make("Lose {0}.");
                pay.hasResourceEffect = false;
                pay.hasCoinCost = false;
                pay.hasNobilityEffect = false;
                pay.hasNobilityCost = true;
                pay.nobilityCost = Plugin.DemandNobility.Value;
                pay.alterFamilyAid = false;
                pay.hasBuff = false;
                pay.startsQuest = false;

                var aid = Copy(template);
                aid.title = ModText.Make("Send more money home.");
                aid.worldEventAction = WorldEventAction.NONE;
                aid.hasMessage = true;
                aid.description = ModText.Make("Family aid +" + Plugin.DemandFamilyAid.Value + ", permanently. Family aid lowers the coin you gain each payment day.");
                aid.hasResourceEffect = false;
                aid.hasCoinCost = false;
                aid.hasNobilityEffect = false;
                aid.hasNobilityCost = false;
                aid.alterFamilyAid = true;
                aid.alterFamilyAidAmount = Plugin.DemandFamilyAid.Value;
                aid.hasBuff = false;
                aid.startsQuest = false;

                demand.actionDatas = new List<WorldEventActionData> { pay, aid };
                dm.worldEventSoData.Add(demand);
            }

            // The sister's thank-you letter (after helping find her cure), built from Big sister's gift.
            if (gift != null)
            {
                var thanks = Object.Instantiate(gift);
                thanks.name = Keys.Thanks;
                thanks.key = Keys.Thanks;
                thanks.isDailyEvent = true;
                thanks.belongsToSeries = false;
                thanks.canRepeat = false;
                thanks.hasSetDate = false;
                thanks.hasMinPopulation = thanks.hasMaxPopulation = false;
                thanks.hasMinNobility = thanks.hasMinResources = thanks.hasMaxResources = false;
                thanks.hasMinHappiness = thanks.hasMaxHappiness = false;
                thanks.hasMinStructure = thanks.hasMaxStructure = false;
                thanks.hasWorldObjectRequirement = false;
                thanks.preventDuringTutorial = false;
                thanks.injectDescription = false;
                thanks.title = ModText.Make("With all my thanks");
                thanks.description = ModText.Make(
                    "Dearest sibling,\n\nI am well again, and every season I remember that it was you who found my cure. " +
                    "The tulips are from my own garden. The rest, I hope, will help your kingdom grow.\n\nWith love, always.");

                var accept = Copy(gift.actionDatas[0]);
                accept.title = ModText.Make("Thank you, dear sister!");
                accept.worldEventAction = WorldEventAction.NONE;
                accept.hasMessage = true;
                accept.description = ModText.Make(() => "Receive " + Gifts.ThanksDescription() + ".");
                accept.hasResourceEffect = false;   // the mod delivers the gift itself
                accept.hasCoinCost = false;
                accept.hasNobilityCost = false;
                accept.hasNobilityEffect = false;
                accept.alterFamilyAid = false;
                accept.hasBuff = false;
                accept.startsQuest = false;
                accept.hasReward = false;
                thanks.actionDatas = new List<WorldEventActionData> { accept };
                dm.worldEventSoData.Add(thanks);
            }
            LetterPacing.SetupBeggars(dm);

            // Which quest each sister letter starts.
            foreach (var key in Keys.SisterQuests)
            {
                var q = Get(key)?.actionDatas.FirstOrDefault(a => a.startsQuest && a.quest != null)?.quest;
                if (q != null) Keys.QuestOf[key] = q.key;
            }

            Plugin.Log.LogInfo("[letters] Letter data set up. Sister quests: " +
                               string.Join(", ", Keys.QuestOf.Select(kv => kv.Key + "->" + kv.Value)));
        }
    }
}
