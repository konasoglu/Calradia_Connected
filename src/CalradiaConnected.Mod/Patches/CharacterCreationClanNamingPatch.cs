using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace CalradiaConnected.Mod.Patches
{
    [HarmonyPatch(typeof(CharacterCreationClanNamingStageVM))]
    public static class CharacterCreationClanNamingPatch
    {
        public static string LastChosenClanName { get; set; } = string.Empty;

        [HarmonyPatch(MethodType.Constructor, new Type[] {
            typeof(BasicCharacterObject),
            typeof(CharacterCreationManager),
            typeof(Action),
            typeof(TextObject),
            typeof(Action),
            typeof(TextObject)
        })]
        [HarmonyPostfix]
        public static void ConstructorPostfix(CharacterCreationClanNamingStageVM __instance)
        {
            try
            {
                __instance.NextStageText = new TextObject("{=cc_connect_button}Bağlan").ToString();
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ClanNamingStageVM ctor patch error: {ex}");
            }
        }

        [HarmonyPatch("RefreshValues")]
        [HarmonyPostfix]
        public static void RefreshValuesPostfix(CharacterCreationClanNamingStageVM __instance)
        {
            try
            {
                __instance.NextStageText = new TextObject("{=cc_connect_button}Bağlan").ToString();
            }
            catch { }
        }

        [HarmonyPatch("OnNextStage")]
        [HarmonyPrefix]
        public static void OnNextStagePrefix(CharacterCreationClanNamingStageVM __instance)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(__instance.ClanName))
                {
                    LastChosenClanName = __instance.ClanName.Trim();
                    if (Clan.PlayerClan != null)
                    {
                        var clanNameObj = new TextObject(LastChosenClanName);
                        Clan.PlayerClan.ChangeClanName(clanNameObj, clanNameObj);
                    }
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ClanNamingStageVM OnNextStagePrefix error: {ex}");
            }
        }
    }
}
