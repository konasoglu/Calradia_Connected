using System;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace CalradiaConnected.Mod.Patches
{
    internal static class PlayerEncounterInterceptHelper
    {
        private static DateTime _lastP2PTriggerUtc = DateTime.MinValue;
        private static string _lastP2PTargetId = string.Empty;

        public static void HaltLocalPartyAndSyncStop()
        {
            var mainParty = MobileParty.MainParty;
            if (mainParty == null) return;

            mainParty.SetMoveModeHold();
            var pos = mainParty.GetPosition2D;
            ModContext.Events.Publish(new LocalPartyMoveRequestedEvent
            {
                MoveType = 0,
                TargetSettlementId = string.Empty,
                TargetPartyId = string.Empty,
                CurrentX = pos.X,
                CurrentY = pos.Y,
                TargetX = pos.X,
                TargetY = pos.Y,
                Speed = 0f
            });
        }

        public static bool TryHandlePlayerToPlayerParty(MobileParty? encounteredMobile)
        {
            if (encounteredMobile == null || !NetworkObjectRegistry.IsPlayerNetworkParty(encounteredMobile))
            {
                return false;
            }

            if (BattleSyncService.Instance.IsLaunchingSynchronizedPvpMission)
            {
                return false;
            }

            if (BattleSyncService.Instance.IsPeerInDisengageCooldown(encounteredMobile))
            {
                return true;
            }

            // Always tear down any native encounter state first so Bannerlord never opens singleplayer meeting/battle menus.
            try
            {
                if (PlayerEncounter.Current != null)
                {
                    PlayerEncounter.Finish(true);
                }
            }
            catch { }

            HaltLocalPartyAndSyncStop();

            // Prevent re-triggering every map tick while parties remain adjacent or already in dialog/pvp.
            if (PlayerInteractionService.Instance.IsInDialog || BattleSyncService.Instance.HasActivePvpSession)
            {
                return true;
            }

            string targetId = encounteredMobile.StringId ?? string.Empty;
            var now = DateTime.UtcNow;
            if (_lastP2PTargetId == targetId && (now - _lastP2PTriggerUtc).TotalMilliseconds < 1500)
            {
                return true;
            }
            _lastP2PTargetId = targetId;
            _lastP2PTriggerUtc = now;

            bool isWar = encounteredMobile.MapFaction != null &&
                         Hero.MainHero?.MapFaction != null &&
                         FactionManager.IsAtWarAgainstFaction(Hero.MainHero.MapFaction, encounteredMobile.MapFaction);

            if (isWar)
            {
                BattleSyncService.Instance.RequestPvpBattle(encounteredMobile);
            }
            else
            {
                PlayerInteractionService.Instance.OpenInteractionMenu(encounteredMobile);
            }

            return true;
        }
    }

    /// <summary>
    /// Intercepts Bannerlord's primary party-to-party encounter entry point BEFORE PlayerEncounter.Start()
    /// initializes EncounteredParty, preventing native singleplayer encounter_meeting or AI-vs-AI PvP battles.
    /// </summary>
    [HarmonyPatch(typeof(EncounterManager), "StartPartyEncounter")]
    public static class EncounterManagerStartPartyEncounterPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PartyBase attackerParty, PartyBase defenderParty)
        {
            try
            {
                if (BattleSyncService.Instance.IsLaunchingSynchronizedPvpMission)
                {
                    return true;
                }

                if (CalradiaConnectedSubModule.IsServerProcess)
                {
                    bool attackerIsPlayer = attackerParty?.MobileParty != null && NetworkObjectRegistry.IsPlayerNetworkParty(attackerParty.MobileParty);
                    bool defenderIsPlayer = defenderParty?.MobileParty != null && NetworkObjectRegistry.IsPlayerNetworkParty(defenderParty.MobileParty);
                    if (attackerIsPlayer && defenderIsPlayer)
                    {
                        attackerParty?.MobileParty?.SetMoveModeHold();
                        defenderParty?.MobileParty?.SetMoveModeHold();
                        return false;
                    }
                    return true;
                }

                if (!ModContext.IsClientConnected)
                {
                    return true;
                }

                // Never allow native P2P encounters between two remote player parties on a client either.
                var atkMobile = attackerParty?.MobileParty;
                var defMobile = defenderParty?.MobileParty;

                if (attackerParty == PartyBase.MainParty || defenderParty == PartyBase.MainParty)
                {
                    var otherParty = attackerParty == PartyBase.MainParty ? defMobile : atkMobile;
                    if (PlayerEncounterInterceptHelper.TryHandlePlayerToPlayerParty(otherParty))
                    {
                        return false;
                    }
                }
                else if (atkMobile != null && defMobile != null &&
                         NetworkObjectRegistry.IsPlayerNetworkParty(atkMobile) &&
                         NetworkObjectRegistry.IsPlayerNetworkParty(defMobile))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                // Fallback gracefully
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerEncounter), "Start")]
    public static class SafeEncounterPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            try
            {
                if (BattleSyncService.Instance.IsLaunchingSynchronizedPvpMission)
                {
                    return true;
                }

                if (!CalradiaConnectedSubModule.IsServerProcess && ModContext.IsClientConnected)
                {
                    // 1. Settlement Encounters
                    if (PlayerEncounter.EncounterSettlement != null)
                    {
                        return true;
                    }

                    // 2. Mobile Party Encounters
                    if (PlayerEncounter.EncounteredParty != null)
                    {
                        var encounteredParty = PlayerEncounter.EncounteredParty;
                        var encounteredMobile = encounteredParty.MobileParty;

                        // 2A. Player-to-Player Interaction or Hostile PvP Battle on the Campaign Map!
                        if (PlayerEncounterInterceptHelper.TryHandlePlayerToPlayerParty(encounteredMobile))
                        {
                            return false;
                        }

                        // 2B. FAZ 3: Hostile / Bandit Party Encounter -> Sync with Server & Allow Combat
                        bool isHostile = encounteredParty.MapFaction != null &&
                                         Hero.MainHero?.MapFaction != null &&
                                         FactionManager.IsAtWarAgainstFaction(Hero.MainHero.MapFaction, encounteredParty.MapFaction);

                        bool isBandit = (encounteredMobile != null && encounteredMobile.IsBandit) ||
                                        (encounteredParty.Culture != null && encounteredParty.Culture.IsBandit);

                        if (isHostile || isBandit)
                        {
                            ModContext.Events.Publish(new PlayerEncounterInterceptedEvent
                            {
                                AttackerPartyId = MobileParty.MainParty?.StringId ?? string.Empty,
                                DefenderPartyId = encounteredMobile?.StringId ?? encounteredParty.Id ?? string.Empty
                            });
                        }

                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Fallback gracefully
            }

            return true;
        }
    }

    internal static class PvpEncounterMenuHelper
    {
        public static MobileParty? GetEncounteredRealPlayerParty()
        {
            if (CalradiaConnectedSubModule.IsServerProcess || !ModContext.IsClientConnected)
            {
                return null;
            }

            var mobile = PlayerEncounter.EncounteredParty?.MobileParty;
            if (mobile != null && NetworkObjectRegistry.IsPlayerNetworkParty(mobile))
            {
                return mobile;
            }
            return null;
        }
    }

    /// <summary>
    /// Hides "Send Troops" (Asker Gönder) when encountering real players so auto-calc is restricted to AI NPCs.
    /// </summary>
    [HarmonyPatch(typeof(EncounterGameMenuBehavior), "game_menu_encounter_order_attack_on_condition")]
    public static class DisableSendTroopsInPvpPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MenuCallbackArgs args, ref bool __result)
        {
            if (PvpEncounterMenuHelper.GetEncounteredRealPlayerParty() != null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Replaces native singleplayer Surrender (which destroys MainParty and causes captivity crash in co-op)
    /// with safe PvP surrender & reparation payout when fighting a connected player.
    /// </summary>
    [HarmonyPatch(typeof(EncounterGameMenuBehavior), "game_menu_encounter_surrender_on_consequence")]
    public static class SafePvpSurrenderPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MenuCallbackArgs args)
        {
            var enemyPlayerParty = PvpEncounterMenuHelper.GetEncounteredRealPlayerParty();
            if (enemyPlayerParty != null)
            {
                BattleSyncService.Instance.HandleLocalPvpSurrender(enemyPlayerParty);
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Routes clicking Attack from a post-retreat encounter menu against a real player back through synchronized PvP launch.
    /// </summary>
    [HarmonyPatch(typeof(EncounterGameMenuBehavior), "game_menu_encounter_attack_on_consequence")]
    public static class SafePvpMenuAttackPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MenuCallbackArgs args)
        {
            var enemyPlayerParty = PvpEncounterMenuHelper.GetEncounteredRealPlayerParty();
            if (enemyPlayerParty != null)
            {
                try
                {
                    if (PlayerEncounter.Current != null)
                    {
                        PlayerEncounter.Finish(true);
                    }
                }
                catch { }

                if (Hero.MainHero == null || !Hero.MainHero.IsAlive || Hero.MainHero.IsPrisoner ||
                    MobileParty.MainParty == null || !MobileParty.MainParty.IsActive ||
                    MobileParty.MainParty.MemberRoster == null || MobileParty.MainParty.MemberRoster.TotalHealthyCount <= 0)
                {
                    return false;
                }

                BattleSyncService.Instance.ClearPvpSessionForRematch();
                BattleSyncService.Instance.RequestPvpBattle(enemyPlayerParty);
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Always permits Leave/Retreat cleanly from a PvP encounter menu so retreating players are never softlocked or forced to crash.
    /// </summary>
    [HarmonyPatch(typeof(EncounterGameMenuBehavior), "game_menu_encounter_leave_on_condition")]
    public static class AlwaysAllowPvpLeavePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MenuCallbackArgs args, ref bool __result)
        {
            if (PvpEncounterMenuHelper.GetEncounteredRealPlayerParty() != null)
            {
                args.optionLeaveType = GameMenuOption.LeaveType.Leave;
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(EncounterGameMenuBehavior), "game_menu_encounter_leave_on_consequence")]
    public static class SafePvpLeavePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MenuCallbackArgs args)
        {
            var enemyPlayerParty = PvpEncounterMenuHelper.GetEncounteredRealPlayerParty();
            if (enemyPlayerParty != null)
            {
                BattleSyncService.Instance.NotifyPvpRetreatToMap(enemyPlayerParty);
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Intercepts Bannerlord's native Trade Done action when trading with another player's party,
    /// converting local mutations into a mutual server-authorized offer instead of mutating unilaterally.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.Inventory.InventoryLogic), nameof(TaleWorlds.CampaignSystem.Inventory.InventoryLogic.DoneLogic))]
    public static class NativePlayerTradeDonePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(TaleWorlds.CampaignSystem.Inventory.InventoryLogic __instance, ref bool __result)
        {
            if (PlayerInteractionService.Instance.TryHandleNativeTradeDone(__instance, out bool doneResult))
            {
                __result = doneResult;
                return false;
            }
            return true;
        }
    }

    internal static class PvpEncounterLifecycleGuard
    {
        public static bool IsPvpOrRemotePlayerEncounter()
        {
            if (!ModContext.IsClientConnected && !ModContext.IsServerRunning) return false;
            if (BattleSyncService.Instance.ActivePvpOpponentPeerId > 0) return true;
            if (PvpEncounterMenuHelper.GetEncounteredRealPlayerParty() != null) return true;

            var encParty = PlayerEncounter.EncounteredParty?.MobileParty;
            if (encParty != null && NetworkObjectRegistry.IsPlayerNetworkParty(encParty)) return true;

            var mapEvent = TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent;
            if (mapEvent != null)
            {
                foreach (var party in mapEvent.InvolvedParties)
                {
                    if (party?.MobileParty != null && !party.MobileParty.IsMainParty &&
                        NetworkObjectRegistry.IsPlayerNetworkParty(party.MobileParty))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static void FinishPvpEncounterSafely()
        {
            try
            {
                if (PlayerEncounter.Current != null)
                {
                    PlayerEncounter.Finish(true);
                }
            }
            catch { }

            try
            {
                if (TaleWorlds.Core.GameStateManager.Current?.ActiveState is MapState ms && ms.AtMenu)
                {
                    string menuId = ms.MenuContext?.GameMenu?.StringId ?? string.Empty;
                    if (string.Equals(menuId, "encounter", StringComparison.Ordinal) ||
                        string.Equals(menuId, "encounter_meeting", StringComparison.Ordinal))
                    {
                        ms.ExitMenuMode();
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Prevents native SP post-battle hero capture dialogs and captivity transfers from crashing or looping
    /// after a synchronized PvP battle or mid-battle opponent disconnect.
    /// </summary>
    [HarmonyPatch(typeof(PlayerEncounter), "DoCaptureHeroes")]
    public static class SafePvpDoCaptureHeroesPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (PvpEncounterLifecycleGuard.IsPvpOrRemotePlayerEncounter())
            {
                PvpEncounterLifecycleGuard.FinishPvpEncounterSafely();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerEncounter), "DoFreeOrCapturePrisonerHeroes")]
    public static class SafePvpDoFreeOrCapturePrisonersPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (PvpEncounterLifecycleGuard.IsPvpOrRemotePlayerEncounter())
            {
                PvpEncounterLifecycleGuard.FinishPvpEncounterSafely();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerEncounter), "DoPlayerVictory")]
    public static class SafePvpDoPlayerVictoryPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (PvpEncounterLifecycleGuard.IsPvpOrRemotePlayerEncounter())
            {
                PvpEncounterLifecycleGuard.FinishPvpEncounterSafely();
                return false;
            }
            return true;
        }
    }
}
