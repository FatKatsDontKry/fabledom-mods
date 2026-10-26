using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine.Localization;

namespace FabledomTailor
{
    // Text the mod adds. The game reads every text through LocalizedString.GetLocalizedString();
    // for strings the mod created we return our own (English only).
    [HarmonyPatch]
    internal static class ModText
    {
        private static readonly ConditionalWeakTable<LocalizedString, Func<string>> Texts = new ConditionalWeakTable<LocalizedString, Func<string>>();

        internal static LocalizedString Make(string text) => Make(() => text);

        internal static LocalizedString Make(Func<string> text)
        {
            var ls = new LocalizedString();
            Texts.Add(ls, text);
            return ls;
        }

        // Serves the mod's own text for LocalizedString objects the mod made; the game's own pass through.
        [HarmonyPatch(typeof(LocalizedString), nameof(LocalizedString.GetLocalizedString), new Type[0]), HarmonyPrefix]
        private static bool GetText(LocalizedString __instance, ref string __result)
        {
            if (__instance == null || !Texts.TryGetValue(__instance, out var text)) return true;
            __result = text();
            return false;
        }
    }
}
