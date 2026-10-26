using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FabledomFixes
{
    // KingdomManager.DailySickness:
    //     float risk = kingdomConfig.baseRiskOfSickness;
    //     if (globalSickness buff) risk *= 2f;
    //     if (roll <= kingdomConfig.baseRiskOfSickness)   // <- ignores the doubled local
    //
    // The transpiler swaps the second read of kingdomConfig.baseRiskOfSickness
    // for a load of the local, so the debuff takes effect.
    [HarmonyPatch(typeof(KingdomManager), "DailySickness")]
    internal static class GlobalSicknessFix
    {
        private static readonly FieldInfo RiskField =
            AccessTools.Field(typeof(KingdomConfig), nameof(KingdomConfig.baseRiskOfSickness));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            int first = code.FindIndex(i => i.LoadsField(RiskField));
            int second = first < 0 ? -1 : code.FindIndex(first + 1, i => i.LoadsField(RiskField));

            // Expect: ldarg.0; ldfld kingdomConfig; ldfld baseRiskOfSickness; stloc <risk>
            if (second < 2 || first + 1 >= code.Count || !code[first + 1].IsStloc())
            {
                Plugin.Log.LogWarning("DailySickness IL changed; Global sickness fix not applied.");
                return code;
            }

            CodeInstruction loadLocal = StlocToLdloc(code[first + 1]);

            // Replace "ldarg.0; ldfld kingdomConfig; ldfld baseRiskOfSickness" with "ldloc <risk>",
            // keeping any branch labels on the first instruction.
            int start = second - 2;
            loadLocal.labels.AddRange(code[start].labels);
            loadLocal.blocks.AddRange(code[start].blocks);
            code.RemoveRange(start, 3);
            code.Insert(start, loadLocal);

            Plugin.Log.LogInfo("DailySickness patched: Global sickness debuff now doubles the risk.");
            return code;
        }

        private static CodeInstruction StlocToLdloc(CodeInstruction st)
        {
            if (st.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (st.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (st.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (st.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            return new CodeInstruction(OpCodes.Ldloc, st.operand);
        }
    }
}
