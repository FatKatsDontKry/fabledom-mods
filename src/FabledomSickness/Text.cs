using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine.Localization;

namespace FabledomSickness
{
    // A LocalizedString that returns the mod's own text (the Tailor's ModText pattern). Each mod keeps its own table;
    // a string another mod made is left to that mod's prefix.
    [HarmonyPatch]
    internal static class Text
    {
        private static readonly ConditionalWeakTable<LocalizedString, Func<string>> Texts = new ConditionalWeakTable<LocalizedString, Func<string>>();

        internal static LocalizedString Make(string text) => Make(() => text);

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
