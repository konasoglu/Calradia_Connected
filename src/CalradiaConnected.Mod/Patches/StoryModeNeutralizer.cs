using System;
using HarmonyLib;

namespace CalradiaConnected.Mod.Patches
{
    public static class StoryModeNeutralizer
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var suppressPrefix = new HarmonyMethod(typeof(StoryModeNeutralizer), nameof(SuppressMethod));

                var tutType = AccessTools.TypeByName("StoryMode.GameComponents.CampaignBehaviors.TutorialPhaseCampaignBehavior");
                if (tutType != null)
                {
                    var mOver = AccessTools.Method(tutType, "OnCharacterCreationIsOver");
                    if (mOver != null)
                    {
                        harmony.Patch(mOver, prefix: suppressPrefix);
                    }
                }

                var trainType = AccessTools.TypeByName("StoryMode.GameComponents.CampaignBehaviors.TrainingFieldCampaignBehavior");
                if (trainType != null)
                {
                    var mOver = AccessTools.Method(trainType, "OnCharacterCreationIsOver");
                    if (mOver != null)
                    {
                        harmony.Patch(mOver, prefix: suppressPrefix);
                    }
                }

                var smCcType = AccessTools.TypeByName("StoryMode.GameComponents.CampaignBehaviors.StoryModeCharacterCreationCampaignBehavior");
                if (smCcType != null)
                {
                    SuppressMethods(harmony, suppressPrefix, smCcType,
                        "OnCharacterCreationIsOver",
                        "InitializeCharacterCreationStages");
                }

                // War Sails (NavalDLC) registers intro/tutorial popups unconditionally even in Sandbox;
                // suppress them via reflection so no hard NavalDLC reference is needed.
                SuppressTypeMethods(harmony, suppressPrefix,
                    "NavalDLC.CampaignBehaviors.NavalInitializationCampaignBehavior",
                    "OnCharacterCreationIsOver",
                    "ShowIntroductionPopUp",
                    "OnStealthTutorialActivated");

                SuppressTypeMethods(harmony, suppressPrefix,
                    "NavalDLC.CampaignBehaviors.NavalDLCTutorialBoxCampaignBehavior",
                    "OnSessionLaunched",
                    "OnQuestStarted",
                    "OnTutorialListRequested");

                SuppressTypeMethods(harmony, suppressPrefix,
                    "NavalDLC.Storyline.CampaignBehaviors.NavalStorylineCampaignBehavior",
                    "ShowSisterPopUp",
                    "Tick");
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] StoryModeNeutralizer hatası: {ex}");
            }
        }

        private static void SuppressTypeMethods(Harmony harmony, HarmonyMethod prefix, string typeName, params string[] methodNames)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type != null)
            {
                SuppressMethods(harmony, prefix, type, methodNames);
            }
        }

        private static void SuppressMethods(Harmony harmony, HarmonyMethod prefix, Type type, params string[] methodNames)
        {
            foreach (string methodName in methodNames)
            {
                var method = AccessTools.Method(type, methodName);
                if (method != null)
                {
                    harmony.Patch(method, prefix: prefix);
                }
            }
        }

        public static bool SuppressMethod()
        {
            // Suppress singleplayer story mode tutorial and quest hooks in multiplayer sandbox
            return false;
        }
    }
}
