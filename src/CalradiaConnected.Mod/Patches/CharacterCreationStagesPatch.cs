using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterCreationContent;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Prevents Difficulty (CharacterCreationOptionsStage) and Review (CharacterCreationReviewStage)
    /// from ever being added to the Character Creation flow in Calradia Connected.
    /// The character creation flow ends strictly on Clan Naming (CharacterCreationClanNamingStage).
    /// </summary>
    public static class CharacterCreationStagesPatch
    {
        [HarmonyPatch(typeof(CharacterCreationManager), "AddStage")]
        public static class AddStagePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(CharacterCreationStageBase stage)
            {
                try
                {
                    // Block Review and Difficulty (Options) stages completely
                    if (stage is CharacterCreationOptionsStage || stage is CharacterCreationReviewStage)
                    {
                        return false; // Do not add to stages list
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] AddStage patch error: {ex}");
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(CharacterCreationState), "OnActivate")]
        public static class CharacterCreationStateActivatePatch
        {
            [HarmonyPrefix]
            public static void Prefix(CharacterCreationState __instance)
            {
                try
                {
                    if (__instance.CharacterCreationManager != null)
                    {
                        // Extra safety net: prune any Review or Options stages that might have slipped through
                        while (__instance.CharacterCreationManager.RemoveStage<CharacterCreationOptionsStage>()) { }
                        while (__instance.CharacterCreationManager.RemoveStage<CharacterCreationReviewStage>()) { }
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] CharacterCreationState OnActivate prune error: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(CharacterCreationCampaignBehavior), "InitializeCharacterCreationStages")]
        public static class InitializeCharacterCreationStagesPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(CharacterCreationManager characterCreationManager)
            {
                try
                {
                    characterCreationManager.AddStage(new CharacterCreationCultureStage());
                    characterCreationManager.AddStage(new CharacterCreationFaceGeneratorStage());
                    characterCreationManager.AddStage(new CharacterCreationNarrativeStage());
                    characterCreationManager.AddStage(new CharacterCreationBannerEditorStage());
                    characterCreationManager.AddStage(new CharacterCreationClanNamingStage());
                    return false;
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] InitializeCharacterCreationStages error: {ex}");
                    return true;
                }
            }
        }
    }
}
