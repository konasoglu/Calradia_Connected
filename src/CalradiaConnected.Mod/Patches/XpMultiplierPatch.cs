using System;
using CalradiaConnected.Common.Config;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Applies the Server Manager's SkillXpMultiplier to player skill XP gains in real time.
    /// </summary>
    [HarmonyPatch(typeof(HeroDeveloper), nameof(HeroDeveloper.AddSkillXp))]
    public static class SkillXpMultiplierPatch
    {
        public static void Prefix(HeroDeveloper __instance, SkillObject skill, ref float rawXp)
        {
            try
            {
                if (__instance == null || rawXp <= 0f) return;
                if (__instance.Hero != Hero.MainHero) return;

                float mult = ServerRuntimeSettings.Current.SkillXpMultiplier;
                if (mult > 1.001f || (mult > 0.05f && mult < 0.999f))
                {
                    rawXp *= mult;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Applies the Server Manager's CharacterXpMultiplier to player character level XP gains in real time.
    /// </summary>
    [HarmonyPatch(typeof(HeroDeveloper), "GainRawXp")]
    public static class CharacterXpMultiplierPatch
    {
        public static void Prefix(HeroDeveloper __instance, ref float rawXp)
        {
            try
            {
                if (__instance == null || rawXp <= 0f) return;
                if (__instance.Hero != Hero.MainHero) return;

                float mult = ServerRuntimeSettings.Current.CharacterXpMultiplier;
                if (mult > 1.001f || (mult > 0.05f && mult < 0.999f))
                {
                    rawXp *= mult;
                }
            }
            catch { }
        }
    }
}
