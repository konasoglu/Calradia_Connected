using System;
using CalradiaConnected.Campaign.Time;
using CalradiaConnected.Engine;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Locks Campaign.TimeControlMode on BOTH the Server game window and all connected Clients
    /// strictly to CampaignTimeSyncBehavior.AuthoritativeSpeedMode, which can ONLY be changed
    /// from the external CalradiaConnected.ServerManager via IPC ("SET_TIME 0/1/2").
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.Campaign), "TimeControlMode", MethodType.Setter)]
    public static class NonStopTimeControlModePatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref CampaignTimeControlMode value)
        {
            try
            {
                if (TaleWorlds.CampaignSystem.Campaign.Current == null ||
                    GameStateManager.Current?.ActiveState == null ||
                    GameStateManager.Current.ActiveState is InitialState)
                {
                    return;
                }

                if (CalradiaConnectedSubModule.IsServerProcess || ModContext.IsClientConnected)
                {
                    value = CampaignTimeSyncBehavior.AuthoritativeSpeedMode;
                }
            }
            catch (Exception)
            {
                // Suppress any errors during transitions
            }
        }
    }

    /// <summary>
    /// Blocks in-game SetTimeSpeed calls (UI buttons, Space, 1, 2, 3, map click auto-resume) on BOTH
    /// the Server game window and all connected Clients so time speed is controlled exclusively by Server Manager.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.Campaign), "SetTimeSpeed")]
    public static class NonStopSetTimeSpeedPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref int speed)
        {
            try
            {
                if (TaleWorlds.CampaignSystem.Campaign.Current == null ||
                    GameStateManager.Current?.ActiveState == null ||
                    GameStateManager.Current.ActiveState is InitialState)
                {
                    return true;
                }

                if (CalradiaConnectedSubModule.IsServerProcess || ModContext.IsClientConnected)
                {
                    TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode = CampaignTimeSyncBehavior.AuthoritativeSpeedMode;
                    return false;
                }
            }
            catch (Exception)
            {
                // Suppress any errors during transitions
            }

            return true;
        }
    }

    /// <summary>
    /// Ensures pressing ESC (opening the Escape Menu, which puts MapState into OnIdleTick) NEVER pauses
    /// the campaign simulation or clock on connected clients or on the server host window.
    /// </summary>
    [HarmonyPatch(typeof(MapState), "OnIdleTick")]
    public static class ClientEscNeverPausePatch
    {
        private static readonly System.Reflection.MethodInfo? RealTickMethod =
            AccessTools.Method(typeof(TaleWorlds.CampaignSystem.Campaign), "RealTick", new[] { typeof(float) });

        private static readonly System.Reflection.MethodInfo? TickMethod =
            AccessTools.Method(typeof(TaleWorlds.CampaignSystem.Campaign), "Tick", Type.EmptyTypes);

        [HarmonyPostfix]
        public static void Postfix(MapState __instance, float dt)
        {
            try
            {
                if (__instance == null || !__instance.IsActive) return;
                if (!CalradiaConnectedSubModule.IsServerProcess && !ModContext.IsClientConnected) return;

                var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                if (campaign == null || !campaign.GameStarted || campaign.SaveHandler?.IsSaving == true) return;
                if (__instance.AtMenu) return;

                // Keep authoritative server speed locked and continue ticking campaign simulation while ESC menu is open
                campaign.TimeControlMode = CampaignTimeSyncBehavior.AuthoritativeSpeedMode;
                RealTickMethod?.Invoke(campaign, new object[] { dt });
                TickMethod?.Invoke(campaign, null);
            }
            catch (Exception)
            {
                // Never break UI frame loop during state transitions
            }
        }
    }
}
