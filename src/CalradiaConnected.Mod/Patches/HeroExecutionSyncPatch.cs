using System;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using HarmonyLib;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Synchronizes player hero executions and fatal deaths across the Server and all Clients
    /// so an executed player cannot keep playing their dead hero.
    /// </summary>
    [HarmonyPatch(typeof(KillCharacterAction), "ApplyInternal")]
    public static class HeroExecutionSyncPatch
    {
        [HarmonyPostfix]
        public static void Postfix(
            Hero victim,
            Hero killer,
            KillCharacterAction.KillCharacterActionDetail actionDetail,
            bool showNotification,
            bool isForced)
        {
            try
            {
                if (victim == null || BattleSyncService.Instance.IsSuppressingExecutionHook) return;
                if (!CalradiaConnectedSubModule.IsServerProcess && !ModContext.IsClientConnected) return;

                if (string.IsNullOrEmpty(victim.StringId) ||
                    !victim.StringId.StartsWith(NetworkObjectRegistry.HeroPrefix, StringComparison.Ordinal))
                {
                    return;
                }

                if (!NetworkObjectRegistry.TryExtractPeerId(victim.StringId, out int victimPeerId) || victimPeerId <= 0)
                {
                    return;
                }

                int killerPeerId = 0;
                if (killer != null && NetworkObjectRegistry.TryExtractPeerId(killer.StringId, out int parsedKiller))
                {
                    killerPeerId = parsedKiller;
                }

                bool isExecution = actionDetail == KillCharacterAction.KillCharacterActionDetail.Executed ||
                                   actionDetail == KillCharacterAction.KillCharacterActionDetail.ExecutionAfterMapEvent;

                var packet = new HeroExecutionPacket
                {
                    VictimPeerId = victimPeerId,
                    KillerPeerId = killerPeerId,
                    KillerName = killer?.Name?.ToString() ?? string.Empty,
                    VictimHeroId = victim.StringId,
                    VictimHeroName = victim.Name?.ToString() ?? string.Empty,
                    ReasonStage = (byte)(isExecution ? 1 : 2)
                };

                if (ModContext.IsServerRunning)
                {
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                }
                else if (ModContext.IsClientConnected)
                {
                    ModContext.Client?.Send(packet, DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Suppresses Bannerlord's native singleplayer GameOverState on connected Co-Op clients so executed/fallen
    /// players cleanly use the multiplayer permadeath modal (Create New Character / Disconnect) without crashing.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.Campaign), "OnGameOver")]
    public static class SuppressSpGameOverInCoopPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (ModContext.IsClientConnected && !CalradiaConnectedSubModule.IsServerProcess)
            {
                return false;
            }
            return true;
        }
    }
}
