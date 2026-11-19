using System;
using System.Reflection;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Engine;
using CalradiaConnected.Missions;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Integrates PvP Ready confirmation directly into native Bannerlord's Order of Battle deployment screen:
    /// renames the Begin Mission button to "HAZIRIM" / "READY" and immediately dispatches Ready on click/finish.
    /// </summary>
    public static class PvpOrderOfBattlePatch
    {
        private static PropertyInfo? _beginMissionTextProp;

        public static void Apply(Harmony harmony)
        {
            try
            {
                var oobVmType = AccessTools.TypeByName("TaleWorlds.MountAndBlade.ViewModelCollection.OrderOfBattle.OrderOfBattleVM");
                if (oobVmType != null)
                {
                    _beginMissionTextProp = oobVmType.GetProperty("BeginMissionText", BindingFlags.Public | BindingFlags.Instance);

                    var refreshMethod = AccessTools.Method(oobVmType, "RefreshValues");
                    if (refreshMethod != null)
                    {
                        harmony.Patch(refreshMethod, postfix: new HarmonyMethod(typeof(PvpOrderOfBattlePatch), nameof(RefreshValuesPostfix)));
                    }

                    var executeBeginMethod = AccessTools.Method(oobVmType, "ExecuteBeginMission");
                    if (executeBeginMethod != null)
                    {
                        harmony.Patch(executeBeginMethod, postfix: new HarmonyMethod(typeof(PvpOrderOfBattlePatch), nameof(ExecuteBeginMissionPostfix)));
                    }
                }

                var finishDeployMethod = AccessTools.Method(typeof(DeploymentHandler), "FinishDeployment");
                if (finishDeployMethod != null)
                {
                    harmony.Patch(finishDeployMethod, postfix: new HarmonyMethod(typeof(PvpOrderOfBattlePatch), nameof(FinishDeploymentPostfix)));
                }

                var battleEndCheckMethod = AccessTools.Method(typeof(BattleEndLogic), "CheckIsEnemySideRetreatingOrOneSideDepleted");
                if (battleEndCheckMethod != null)
                {
                    harmony.Patch(battleEndCheckMethod, prefix: new HarmonyMethod(typeof(PvpOrderOfBattlePatch), nameof(CheckIsEnemySideRetreatingOrOneSideDepletedPrefix)));
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] PvpOrderOfBattlePatch hatası: {ex}");
            }
        }

        private static bool IsActivePvpSession()
        {
            return !CalradiaConnectedSubModule.IsServerProcess &&
                   ModContext.IsClientConnected &&
                   BattleSyncService.Instance.ActivePvpOpponentPeerId > 0;
        }

        /// <summary>
        /// Blocks Bannerlord's native side-depletion / retreat victory ("Savaş Kazandın") during a synchronized PvP mission
        /// while both players are still loading, deploying, pressing Ready, or spawning initial agents.
        /// </summary>
        public static bool CheckIsEnemySideRetreatingOrOneSideDepletedPrefix()
        {
            try
            {
                if (!IsActivePvpSession()) return true;
                if (!BattleSyncService.Instance.IsPvpCombatReadyForEndChecks()) return false;

                var mission = TaleWorlds.MountAndBlade.Mission.Current;
                if (mission != _trackedEndCheckMission)
                {
                    _trackedEndCheckMission = mission;
                    _enemyTeamEverHadActiveAgent = false;
                }

                if (mission?.PlayerEnemyTeam?.ActiveAgents?.Count > 0)
                {
                    _enemyTeamEverHadActiveAgent = true;
                }

                // Never allow premature instant-victory on a zero-enemy battlefield before at least one enemy agent has spawned.
                return _enemyTeamEverHadActiveAgent;
            }
            catch
            {
                return true;
            }
        }

        public static void RefreshValuesPostfix(object __instance)
        {
            try
            {
                if (__instance == null || _beginMissionTextProp == null || !IsActivePvpSession()) return;
                _beginMissionTextProp.SetValue(__instance, Loc.T("HAZIRIM", "READY"));
            }
            catch { }
        }

        public static void ExecuteBeginMissionPostfix()
        {
            try
            {
                if (!IsActivePvpSession()) return;
                CoopMissionSyncBehavior.ActiveInstance?.MarkLocalReadyFromUi();
                BattleSyncService.Instance.SendPvpReadySignal();
                UI.PvpReadyOverlayManager.Instance.SyncFromService();
            }
            catch { }
        }

        private static TaleWorlds.MountAndBlade.Mission? _trackedEndCheckMission;
        private static bool _enemyTeamEverHadActiveAgent;

        public static void FinishDeploymentPostfix(DeploymentHandler __instance)
        {
            try
            {
                if (!IsActivePvpSession()) return;
                // Do not auto-send Ready on Frame-0/1 automatic FinishDeployment (which fires immediately for armies < 20 troops);
                // only treat FinishDeployment as an explicit Ready action when Order of Battle deployment was actually active.
                float missionTime = __instance?.Mission?.CurrentTime ?? TaleWorlds.MountAndBlade.Mission.Current?.CurrentTime ?? 0f;
                if (missionTime <= 0.5f) return;

                bool hasOobDeployment = TaleWorlds.MountAndBlade.MissionGameModels.Current?.BattleInitializationModel?.CanPlayerSideDeployWithOrderOfBattle() ?? false;
                if (!hasOobDeployment) return;

                CoopMissionSyncBehavior.ActiveInstance?.MarkLocalReadyFromUi();
                BattleSyncService.Instance.SendPvpReadySignal();
                UI.PvpReadyOverlayManager.Instance.SyncFromService();
            }
            catch { }
        }
    }
}
