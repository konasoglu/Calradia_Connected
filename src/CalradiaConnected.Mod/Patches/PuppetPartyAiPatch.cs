using System;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Sync;
using CalradiaConnected.Engine;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Ensures connected player parties (cc_party_*) remain spotted and visible on either the Server map
    /// or remote Clients in Bannerlord v1.4.8+, while keeping the server's dummy host party
    /// (cc_server_party) and garrisoned remote parties hidden.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.IsVisible), MethodType.Getter)]
    public static class PlayerPartyVisibilityPersistentPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MobileParty __instance, ref bool __result)
        {
            try
            {
                if (__instance == null) return;

                if (__instance.StringId == "cc_server_party" ||
                    !__instance.IsActive ||
                    WorldSyncManager.IsRecentlyDisconnectedParty(__instance.StringId))
                {
                    __result = false;
                    return;
                }

                if (NetworkObjectRegistry.IsPlayerNetworkParty(__instance))
                {
                    bool isCapturedOrEmpty = !__instance.IsMainParty &&
                                             (__instance.LeaderHero == null ||
                                              !__instance.LeaderHero.IsAlive ||
                                              __instance.LeaderHero.IsPrisoner ||
                                              (__instance.MemberRoster != null && __instance.MemberRoster.TotalManCount <= 0));
                    if (isCapturedOrEmpty)
                    {
                        __result = false;
                    }
                    else if (__instance.CurrentSettlement == null)
                    {
                        if (!__instance.IsInspected)
                        {
                            __instance.IsInspected = true;
                        }
                        __result = true;
                    }
                    else if (!__instance.IsMainParty)
                    {
                        __result = false;
                    }
                }
                else if (!__instance.IsMainParty &&
                         (__instance.CurrentSettlement != null || WorldSyncManager.IsAiPartyHiddenByServer(__instance.StringId)))
                {
                    __result = false;
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Intercepts MobileParty.IsVisible setter so active remote player parties stay visible outside settlements
    /// and any party inside a settlement (or the cc_server_party dummy) stays invisible on the map edge.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.IsVisible), MethodType.Setter)]
    public static class PlayerPartyVisibilityStatePatch
    {
        [HarmonyPrefix]
        public static void Prefix(MobileParty __instance, ref bool value)
        {
            try
            {
                if (__instance == null) return;

                if (__instance.StringId == "cc_server_party" ||
                    !__instance.IsActive ||
                    WorldSyncManager.IsRecentlyDisconnectedParty(__instance.StringId))
                {
                    value = false;
                    return;
                }

                if (!__instance.IsMainParty &&
                    (__instance.CurrentSettlement != null ||
                     (!NetworkObjectRegistry.IsPlayerNetworkParty(__instance) && WorldSyncManager.IsAiPartyHiddenByServer(__instance.StringId))))
                {
                    value = false;
                    return;
                }

                if (NetworkObjectRegistry.IsPlayerNetworkParty(__instance) && __instance.CurrentSettlement == null)
                {
                    bool isCapturedOrEmpty = !__instance.IsMainParty &&
                                             (__instance.LeaderHero == null ||
                                              !__instance.LeaderHero.IsAlive ||
                                              __instance.LeaderHero.IsPrisoner ||
                                              (__instance.MemberRoster != null && __instance.MemberRoster.TotalManCount <= 0));
                    if (isCapturedOrEmpty)
                    {
                        value = false;
                        return;
                    }
                    value = true;
                    __instance.IsInspected = true;
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Blocks autonomous short-term AI initiative (chasing bandits / fleeing) for player parties on the Server
    /// and for ALL parties on connected Clients, while allowing MobilePartyAi.Tick -> UpdateBehavior -> SetNavigationModePoint
    /// to execute exactly like MainParty so parties walk smoothly at 60 FPS along the navmesh!
    /// </summary>
    [HarmonyPatch(typeof(DefaultMobilePartyAIModel), "ShouldPartyCheckInitiativeBehavior")]
    public static class PuppetPartyInitiativeBlockPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty mobileParty, ref bool __result)
        {
            try
            {
                if (mobileParty == null) return true;

                // Client mode: No party on the client initiates autonomous AI chasing/fleeing
                if (!CalradiaConnectedSubModule.IsServerProcess && ModContext.IsClientConnected)
                {
                    __result = false;
                    return false;
                }

                // Server mode: Player network parties and server host party never initiate autonomous AI chasing/fleeing
                if (CalradiaConnectedSubModule.IsServerProcess)
                {
                    if (mobileParty.IsMainParty ||
                        mobileParty.StringId == "cc_server_party" ||
                        NetworkObjectRegistry.IsPlayerNetworkParty(mobileParty))
                    {
                        __result = false;
                        return false;
                    }
                }
            }
            catch (Exception) { }

            return true;
        }
    }

    /// <summary>
    /// Blocks autonomous hourly AI decision-making (AiPartyThinkBehavior) for player parties on the Server
    /// and for ALL parties on connected Clients (where the Server is authoritative for AI goals).
    /// </summary>
    [HarmonyPatch(typeof(AiPartyThinkBehavior), "PartyHourlyAiTick")]
    public static class PuppetPartyHourlyAiBlockPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty mobileParty)
        {
            try
            {
                if (mobileParty == null) return true;

                // Client mode: Server is authoritative for hourly AI goals
                if (!CalradiaConnectedSubModule.IsServerProcess && ModContext.IsClientConnected)
                {
                    return false;
                }

                // Server mode: Player network parties and server host party are controlled by players, not AI
                if (CalradiaConnectedSubModule.IsServerProcess)
                {
                    if (mobileParty.IsMainParty ||
                        mobileParty.StringId == "cc_server_party" ||
                        NetworkObjectRegistry.IsPlayerNetworkParty(mobileParty))
                    {
                        return false;
                    }
                }
            }
            catch (Exception) { }

            return true;
        }
    }

    /// <summary>
    /// Ensures only the local MainParty triggers local encounters on a connected Client, and blocks
    /// the hidden server dummy party (cc_server_party) from ever triggering encounters on the Server.
    /// </summary>
    [HarmonyPatch(typeof(EncounterManager), "HandleEncounterForMobileParty")]
    public static class PuppetPartyEncounterGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty mobileParty)
        {
            try
            {
                if (mobileParty == null) return true;

                if (CalradiaConnectedSubModule.IsServerProcess)
                {
                    if (mobileParty.IsMainParty ||
                        mobileParty.StringId == "cc_server_party" ||
                        NetworkObjectRegistry.IsPlayerNetworkParty(mobileParty))
                    {
                        return false;
                    }
                }
                else if (ModContext.IsClientConnected)
                {
                    // On a connected Client, only the local player's MainParty triggers local MapState encounters;
                    // remote parties and AI lords enter/leave settlements authoritatively via Server sync packets.
                    if (!mobileParty.IsMainParty)
                    {
                        return false;
                    }
                }
            }
            catch (Exception) { }

            return true;
        }
    }

    [HarmonyPatch(typeof(MobilePartyAi), "Tick")]
    public static class PuppetPartyAiTickPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty? ____mobileParty)
        {
            var ___mobileParty = ____mobileParty;
            try
            {
                if (___mobileParty == null) return true;

                // Only block the hidden server host dummy party (cc_server_party).
                // Allow MobilePartyAi.Tick to run on player parties and local AOI puppet parties so that
                // SetMoveGoToPoint / SetMoveGoToSettlement / SetMoveEngageParty transitions PartyMoveMode
                // via UpdateBehavior() -> SetNavigationModePoint() and animates smoothly within spotting range!
                if (___mobileParty.StringId == "cc_server_party" ||
                    (CalradiaConnectedSubModule.IsServerProcess && ___mobileParty.IsMainParty))
                {
                    return false;
                }

                if (PuppetPartyDistanceCulling.ShouldSkipDistantClientAi(___mobileParty))
                {
                    return false;
                }
            }
            catch (Exception) { }

            return true;
        }
    }

    [HarmonyPatch(typeof(MobileParty), "DoAIMove")]
    public static class PuppetPartyDoAIMovePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty __instance)
        {
            try
            {
                if (__instance == null) return true;

                if (__instance.StringId == "cc_server_party" ||
                    (CalradiaConnectedSubModule.IsServerProcess && __instance.IsMainParty))
                {
                    return false;
                }

                if (PuppetPartyDistanceCulling.ShouldSkipDistantClientAi(__instance))
                {
                    return false;
                }
            }
            catch (Exception) { }

            return true;
        }
    }

    internal static class PuppetPartyDistanceCulling
    {
        private const float ClientAiSkipRadiusSq = 65.0f * 65.0f;

        /// <summary>
        /// On connected clients, invisible AI parties outside spotting/AOI range are governed
        /// authoritatively by the server and skipped locally to eliminate navmesh/AI CPU load.
        /// </summary>
        public static bool ShouldSkipDistantClientAi(MobileParty party)
        {
            if (CalradiaConnectedSubModule.IsServerProcess || !ModContext.IsClientConnected)
            {
                return false;
            }

            if (party.IsMainParty ||
                party.CurrentSettlement != null ||
                party.IsVisible ||
                NetworkObjectRegistry.IsPlayerNetworkParty(party))
            {
                return false;
            }

            var mainParty = MobileParty.MainParty;
            if (mainParty == null)
            {
                return true;
            }

            var mainPos = mainParty.GetPosition2D;
            var partyPos = party.GetPosition2D;
            if (!mainPos.IsValid || !partyPos.IsValid)
            {
                return true;
            }

            return (partyPos - mainPos).LengthSquared > ClientAiSkipRadiusSq;
        }
    }

    /// <summary>
    /// Synchronizes remote player party movement speeds (including cargo overburden penalties, wounded slowdowns,
    /// and herd penalties) so an overburdened player walks at the exact same speed on the Server and all other Clients!
    /// </summary>
    [HarmonyPatch(typeof(DefaultPartySpeedCalculatingModel), "CalculateFinalSpeed")]
    public static class SyncedPlayerPartySpeedPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MobileParty mobileParty, ref ExplainedNumber __result)
        {
            try
            {
                if (mobileParty == null || mobileParty.IsMainParty) return;
                if (!NetworkObjectRegistry.IsPlayerNetworkParty(mobileParty)) return;

                if (NetworkObjectRegistry.Instance.TryGetSyncedPartySpeed(mobileParty.StringId, out float syncedSpeed) &&
                    syncedSpeed > 0.05f)
                {
                    __result = new ExplainedNumber(syncedSpeed, false, null);
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Prevents native HeroSpawnCampaignBehavior from spawning extra random minor faction members
    /// inside the custom "Developer" clan (cc_clan_developer), so "konasoglu" remains its sole member.
    /// </summary>
    [HarmonyPatch(typeof(HeroSpawnCampaignBehavior), "SpawnMinorFactionHeroes")]
    public static class DeveloperClanSingleHeroGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Clan clan)
        {
            if (clan?.StringId == "cc_clan_developer")
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Prevents native desertion ticks from removing troops on remote player puppet parties
    /// (owning clients are strictly authoritative for their own party roster).
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.DesertionCampaignBehavior), "DailyTickParty")]
    public static class PuppetPartyDesertionGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty mobileParty)
        {
            if (mobileParty != null && !mobileParty.IsMainParty &&
                (ModContext.IsServerRunning || ModContext.IsClientConnected) &&
                NetworkObjectRegistry.IsPlayerNetworkParty(mobileParty))
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Prevents native food consumption / starvation desertion on remote player puppet parties
    /// so puppet headcounts never drift out of sync with the owning player.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.FoodConsumptionBehavior), "PartyConsumeFood")]
    public static class PuppetPartyFoodGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty mobileParty)
        {
            if (mobileParty != null && !mobileParty.IsMainParty &&
                (ModContext.IsServerRunning || ModContext.IsClientConnected) &&
                NetworkObjectRegistry.IsPlayerNetworkParty(mobileParty))
            {
                try { mobileParty.IsInspected = true; } catch { }
                return false;
            }
            return true;
        }
    }

    internal static class PuppetPartyGuardHelper
    {
        public static bool IsRemotePuppetParty(MobileParty? mobileParty)
        {
            if (mobileParty == null || mobileParty.IsMainParty) return false;
            if (!ModContext.IsServerRunning && !ModContext.IsClientConnected) return false;
            if (!NetworkObjectRegistry.IsPlayerNetworkParty(mobileParty)) return false;
            try { mobileParty.IsInspected = true; } catch { }
            return true;
        }
    }

    /// <summary>
    /// Prevents daily auto-upgrade ticks on remote player puppet parties so owning clients stay sole authority.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.PartyUpgraderCampaignBehavior), "DailyTickParty")]
    public static class PuppetPartyUpgraderDailyGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty party)
        {
            return !PuppetPartyGuardHelper.IsRemotePuppetParty(party);
        }
    }

    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.PartyUpgraderCampaignBehavior), "UpgradeReadyTroops")]
    public static class PuppetPartyUpgradeReadyGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(TaleWorlds.CampaignSystem.Party.PartyBase party)
        {
            return !PuppetPartyGuardHelper.IsRemotePuppetParty(party?.MobileParty);
        }
    }

    /// <summary>
    /// Prevents local healing and starvation attrition (-25% regulars/day when IsStarving) on remote player puppet parties.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultPartyHealingModel), "GetDailyHealingForRegulars")]
    public static class PuppetPartyRegularHealingGuardPatch
    {
        [HarmonyPostfix]
        public static void Postfix(object[] __args, ref TaleWorlds.CampaignSystem.ExplainedNumber __result)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                MobileParty? mp = __args[0] as MobileParty
                    ?? (__args[0] as TaleWorlds.CampaignSystem.Party.PartyBase)?.MobileParty;
                if (PuppetPartyGuardHelper.IsRemotePuppetParty(mp))
                {
                    __result = new TaleWorlds.CampaignSystem.ExplainedNumber(0f, false, null);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultPartyHealingModel), "GetDailyHealingHpForHeroes")]
    public static class PuppetPartyHeroHealingGuardPatch
    {
        [HarmonyPostfix]
        public static void Postfix(object[] __args, ref TaleWorlds.CampaignSystem.ExplainedNumber __result)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                MobileParty? mp = __args[0] as MobileParty
                    ?? (__args[0] as TaleWorlds.CampaignSystem.Party.PartyBase)?.MobileParty;
                if (PuppetPartyGuardHelper.IsRemotePuppetParty(mp))
                {
                    __result = new TaleWorlds.CampaignSystem.ExplainedNumber(0f, false, null);
                }
            }
            catch { }
        }
    }
}
