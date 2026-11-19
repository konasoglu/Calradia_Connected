using System;
using System.Linq;
using CalradiaConnected.Campaign.Player;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// In Calradia Connected the client joins an ALREADY RUNNING server world: character creation is
    /// pushed on top of the loaded MapState. The native FinalizeCharacterCreationState calls
    /// CleanAndPushState(new MapState) which destroys the loaded map scene and crashes the engine.
    /// Here we simply pop back to the existing MapState instead and publish the completion event.
    /// </summary>
    [HarmonyPatch(typeof(CharacterCreationState), "FinalizeCharacterCreationState")]
    public static class CharacterCreationFinalizePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CharacterCreationState __instance)
        {
            try
            {
                if (CalradiaConnectedSubModule.IsServerProcess) return true;

                var gsm = Game.Current?.GameStateManager;
                if (gsm == null) return true;

                // Structural check: only intercept when a loaded MapState lies beneath character creation.
                bool mapStateBelow = gsm.GameStates.Any(s => s is MapState);
                if (!mapStateBelow) return true;

                TaleWorlds.Library.Debug.Print("[Calradia-Connected] Karakter oluşturma tamamlandı, mevcut haritaya dönülüyor.");

                gsm.UnregisterActiveStateDisableRequest(__instance);

                if (ModContext.IsClientConnected)
                {
                    int peerId = ModContext.Client?.LocalPeerId ?? 1001;
                    string username = UI.ClientLoginManager.Instance.CurrentUsername;
                    PlayerHeroManager.Instance.EnsureEventSubscriptions();
                    ModContext.Events.Publish(new LocalCharacterCreationCompletedEvent
                    {
                        PeerId = peerId,
                        Username = username
                    });
                }

                // Pop CharacterCreationState -> the existing MapState/MapScreen is re-activated intact.
                gsm.PopState();

                PartyBase.MainParty?.SetVisualAsDirty();

                if (gsm.ActiveState is MapState mapState && mapState.Handler != null)
                {
                    try
                    {
                        mapState.Handler.ResetCamera(resetDistance: true, teleportToMainParty: true);
                        mapState.Handler.TeleportCameraToMainParty();
                    }
                    catch (Exception ex)
                    {
                        TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] FinalizeCharacterCreationState camera reset warning: {ex.Message}");
                    }
                }

                try
                {
                    CampaignEventDispatcher.Instance?.OnCharacterCreationIsOver();
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] FinalizeCharacterCreationState OnCharacterCreationIsOver warning: {ex}");
                }

                if (ModContext.IsClientConnected)
                {
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "CharacterCreationIsOver" });
                }

                return false; // skip native destructive CleanAndPushState(MapState)
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] FinalizeCharacterCreationState patch error: {ex}");
                TaleWorlds.Library.Debug.Print($"[Calradia-Connected] FinalizeCharacterCreationState patch error: {ex}");
            }
            return true;
        }
    }
}
