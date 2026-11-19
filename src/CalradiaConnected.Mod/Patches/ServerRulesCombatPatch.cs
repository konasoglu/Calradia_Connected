using System;
using System.Linq;
using CalradiaConnected.Common.Config;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Missions.Arena;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Enforces Server Manager combat rules live in missions:
    /// 1. ArenaDuelManager 1-HP knockout safeguard when ArenaPvpDuelsEnabled is true.
    /// 2. Suppresses player-vs-player damage when PvpEnabled is false (outside allowed duels).
    /// 3. Continuous Arena Practice respawn when ArenaContinuousPracticeRespawn is true.
    /// </summary>
    [HarmonyPatch(typeof(Agent), nameof(Agent.RegisterBlow))]
    public static class ServerRulesCombatPatch
    {
        private static DateTime _lastArenaRespawnUtc = DateTime.MinValue;

        public static void Prefix(Agent __instance, ref Blow blow)
        {
            try
            {
                if (__instance == null) return;

                var settings = ServerRuntimeSettings.Current;

                // 1. Arena Duel 1-HP knockout interception
                if (settings.ArenaPvpDuelsEnabled && ArenaDuelManager.Instance.IsDuelActive)
                {
                    ArenaDuelManager.Instance.InterceptDuelBlow(__instance, ref blow);
                    return;
                }

                // 2. PvP disabled guard: block player-to-player damage when PvpEnabled == false
                int ownerId = blow.OwnerId;
                if (!settings.PvpEnabled && blow.InflictedDamage > 0 && ownerId >= 0)
                {
                    var mission = Mission.Current;
                    var attacker = mission?.Agents?.FirstOrDefault(a => a != null && a.Index == ownerId);
                    if (attacker != null && attacker != __instance && IsHumanPlayerAgent(__instance) && IsHumanPlayerAgent(attacker))
                    {
                        blow.InflictedDamage = 0;
                        return;
                    }
                }

                // 3. Continuous Arena Practice Respawn
                if (settings.ArenaContinuousPracticeRespawn &&
                    __instance == Agent.Main &&
                    blow.InflictedDamage >= __instance.Health &&
                    IsArenaPracticeMission(Mission.Current))
                {
                    blow.InflictedDamage = 0;
                    __instance.Health = __instance.HealthLimit;

                    if ((DateTime.UtcNow - _lastArenaRespawnUtc).TotalSeconds >= 0.8)
                    {
                        _lastArenaRespawnUtc = DateTime.UtcNow;
                        try
                        {
                            // Reposition to an arena spawn point so player gets clean continuous practice
                            var mission = Mission.Current;
                            var spawnEntity = mission?.Scene?.FindEntityWithTag("sp_arena")
                                           ?? mission?.Scene?.FindEntityWithTag("sp_player_near_arena_master");
                            if (spawnEntity != null)
                            {
                                var origin = spawnEntity.GetGlobalFrame().origin;
                                if (origin.IsValid && origin.LengthSquared > 1f)
                                {
                                    __instance.TeleportToPosition(origin);
                                }
                            }
                        }
                        catch { }

                        InformationManager.DisplayMessage(new InformationMessage(
                            Loc.T("[Arena İdman] Yeniden doğdunuz! Çıkmak için TAB tuşuna basın.",
                                  "[Arena Practice] Respawned! Press TAB to leave."),
                            Colors.Cyan));
                    }
                }
            }
            catch
            {
                // Never break native combat pipeline
            }
        }

        private static bool IsHumanPlayerAgent(Agent agent)
        {
            if (agent == null || !agent.IsHuman) return false;
            if (agent.IsPlayerControlled) return true;
            string? id = agent.Character?.StringId;
            return !string.IsNullOrEmpty(id) && id!.StartsWith("cc_hero_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsArenaPracticeMission(Mission? mission)
        {
            if (mission == null) return false;
            try
            {
                if (mission.MissionBehaviors != null)
                {
                    foreach (var b in mission.MissionBehaviors)
                    {
                        if (b != null && string.Equals(b.GetType().Name, "ArenaPracticeFightMissionController", StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
