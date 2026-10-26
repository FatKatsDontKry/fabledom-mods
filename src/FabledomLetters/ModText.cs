using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine.Localization;

namespace FabledomLetters
{
    // Text for options and letters the mod adds. The game reads every text through
    // LocalizedString.GetLocalizedString(); for strings the mod created, return our own text.
    // (English only: there are no translations for added content.)
    [HarmonyPatch]
    internal static class ModText
    {
        private static readonly ConditionalWeakTable<LocalizedString, Func<string>> Texts = new ConditionalWeakTable<LocalizedString, Func<string>>();

        internal static LocalizedString Make(string text) => Make(() => text);

        // Text worked out each time it is shown (e.g. a gift amount that depends on the current milestone).
        internal static LocalizedString Make(Func<string> text)
        {
            var ls = new LocalizedString();
            Texts.Add(ls, text);
            return ls;
        }

        [HarmonyPatch(typeof(LocalizedString), nameof(LocalizedString.GetLocalizedString), new Type[0]), HarmonyPrefix]
        private static bool GetText(LocalizedString __instance, ref string __result)
        {
            if (__instance == null || !Texts.TryGetValue(__instance, out var text)) return true;
            __result = text();
            return false;
        }
    }
}
