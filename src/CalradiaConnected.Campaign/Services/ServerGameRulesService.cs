#nullable enable
using System;
using CalradiaConnected.Campaign.Progression;
using CalradiaConnected.Campaign.Time;
using CalradiaConnected.Common.Config;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Applies and synchronizes Server Manager Game Rules, World Settings, Difficulty,
    /// PvP/Arena toggles, and Admin status across Server and all Connected Clients.
    /// </summary>
    public sealed class ServerGameRulesService
    {
        public static ServerGameRulesService Instance { get; } = new ServerGameRulesService();

        private bool _hasSnapshottedBannerlordConfig;
        private int _origBattleSize;
        private int _origNumberOfCorpses;
        private int _origDamageDifficulty;
        private int _origAutoSaveInterval;

        private ServerGameRulesService() { }

        private void SnapshotOriginalBannerlordConfigIfNeeded()
        {
            if (_hasSnapshottedBannerlordConfig) return;
            try
            {
                _origBattleSize = BannerlordConfig.BattleSize;
                _origNumberOfCorpses = BannerlordConfig.NumberOfCorpses;
                _origDamageDifficulty = BannerlordConfig.PlayerReceivedDamageDifficulty;
                _origAutoSaveInterval = BannerlordConfig.AutoSaveInterval;
                _hasSnapshottedBannerlordConfig = true;
            }
            catch { }
        }

        /// <summary>
        /// Restores the player's original vanilla BannerlordConfig values when leaving multiplayer
        /// or exiting the game so server rules never overwrite the user's singleplayer BannerlordConfig.txt.
        /// </summary>
        public void RestoreOriginalBannerlordConfig()
        {
            if (!_hasSnapshottedBannerlordConfig) return;
            try
            {
                BannerlordConfig.BattleSize = _origBattleSize;
                BannerlordConfig.NumberOfCorpses = _origNumberOfCorpses;
                BannerlordConfig.PlayerReceivedDamageDifficulty = _origDamageDifficulty;
                BannerlordConfig.AutoSaveInterval = _origAutoSaveInterval;
            }
            catch { }
            finally
            {
                _hasSnapshottedBannerlordConfig = false;
            }
        }

        public void LoadAndApplyOnServer()
        {
            ServerRuntimeSettings.Current.LoadFromDisk();
            CampaignTimeSyncBehavior.PersistentWorldTime = ServerRuntimeSettings.Current.PersistentWorldTime;
            ApplyActiveRulesToCampaign();
        }

        public void ApplyActiveRulesToCampaign()
        {
            var s = ServerRuntimeSettings.Current;
            try
            {
                SnapshotOriginalBannerlordConfigIfNeeded();
                // 1. Bannerlord engine/config live settings (restored on disconnect/exit)
                BannerlordConfig.BattleSize = ServerRuntimeSettings.MapBattleSizeToIndex(s.BattleSize);
                BannerlordConfig.NumberOfCorpses = ServerRuntimeSettings.MapCorpseCountToIndex(s.NumberOfCorpses);
                BannerlordConfig.PlayerReceivedDamageDifficulty = Math.Max(0, Math.Min(2, s.DamageDifficulty));
                BannerlordConfig.AutoSaveInterval = Math.Max(0, s.AutoSaveIntervalMinutes);
            }
            catch { }

            try
            {
                if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

                // 2. CampaignOptions difficulty settings (0 = VeryEasy, 1 = Easy, 2 = Realistic)
                var dmgDiff = MapDifficulty(s.DamageDifficulty);
                var aiDiff = MapDifficulty(s.CombatAiDifficulty);

                CampaignOptions.PlayerTroopsReceivedDamage = dmgDiff;
                CampaignOptions.CombatAIDifficulty = aiDiff;

                // 3. Hero Death in Battle (0 = Disabled, 1 = Companions Only, 2 = All Heroes)
                switch (s.HeroDeathInBattle)
                {
                    case 0:
                        CampaignOptions.IsLifeDeathCycleDisabled = true;
                        CampaignOptions.BattleDeath = CampaignOptions.Difficulty.VeryEasy;
                        CampaignOptions.ClanMemberDeathChance = CampaignOptions.Difficulty.VeryEasy;
                        break;
                    case 1:
                        CampaignOptions.IsLifeDeathCycleDisabled = false;
                        CampaignOptions.BattleDeath = CampaignOptions.Difficulty.Easy;
                        CampaignOptions.ClanMemberDeathChance = CampaignOptions.Difficulty.Easy;
                        break;
                    default:
                        CampaignOptions.IsLifeDeathCycleDisabled = false;
                        CampaignOptions.BattleDeath = CampaignOptions.Difficulty.Realistic;
                        CampaignOptions.ClanMemberDeathChance = CampaignOptions.Difficulty.Realistic;
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] ApplyActiveRulesToCampaign error: {ex.Message}");
            }
        }

        public void BroadcastCurrentConfigToAllClients(bool forceReapplyStarter = false)
        {
            if (!ModContext.IsServerRunning || ModContext.Server == null) return;

            var s = ServerRuntimeSettings.Current;
            foreach (var session in ModContext.Server.Sessions.Values)
            {
                try
                {
                    bool isAdmin = global::Common.Users.UserDatabase.Instance.IsAdmin(session.PlayerName);
                    var pkt = s.ToSyncPacket(isAdmin);
                    pkt.ForceReapplyStarter = forceReapplyStarter;
                    ModContext.Server.SendTo(session.PeerId, pkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                catch { }
            }
        }

        public void SendConfigToPeer(int peerId, string username, bool forceReapplyStarter = false)
        {
            if (!ModContext.IsServerRunning || ModContext.Server == null || peerId <= 0) return;
            try
            {
                bool isAdmin = !string.IsNullOrWhiteSpace(username) && global::Common.Users.UserDatabase.Instance.IsAdmin(username);
                var pkt = ServerRuntimeSettings.Current.ToSyncPacket(isAdmin);
                pkt.ForceReapplyStarter = forceReapplyStarter;
                ModContext.Server.SendTo(peerId, pkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            catch { }
        }

        public void HandleServerConfigSyncPacket(ServerConfigSyncPacket packet)
        {
            if (packet == null) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    bool wasAdmin = ServerRuntimeSettings.Current.IsLocalPlayerAdmin;
                    bool hadSyncBefore = ServerRuntimeSettings.Current.HasReceivedServerSync;

                    ServerRuntimeSettings.Current.ApplyFromSyncPacket(packet);
                    ApplyActiveRulesToCampaign();
                    PlayerStarterProgressionBehavior.Instance?.SyncFromRuntimeSettings();

                    if (packet.ForceReapplyStarter)
                    {
                        PlayerStarterProgressionBehavior.Instance?.ResetLocalProgressionAndReapply();
                    }

                    if (hadSyncBefore && wasAdmin != packet.IsLocalPlayerAdmin)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            packet.IsLocalPlayerAdmin
                                ? Loc.T("[Sunucu Yönetimi] Yönetici yetkisi verildi! F9 sohbetinden /help ile komutları görebilirsiniz.",
                                        "[Server Admin] Admin privileges granted! Type /help in F9 chat for admin commands.")
                                : Loc.T("[Sunucu Yönetimi] Yönetici yetkiniz kaldırıldı.",
                                        "[Server Admin] Your admin privileges have been revoked."),
                            packet.IsLocalPlayerAdmin ? Colors.Green : Colors.Yellow));
                    }
                    else if (hadSyncBefore && TaleWorlds.CampaignSystem.Campaign.Current?.GameStarted == true)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            Loc.F("[Sunucu Ayarları] Sunucu kuralları güncellendi (Savaş Boyutu: {0}, XP: {1:0.#}x / {2:0.#}x, PvP: {3}).",
                                  "[Server Settings] Server rules updated (Battle Size: {0}, XP: {1:0.#}x / {2:0.#}x, PvP: {3}).",
                                  packet.BattleSize,
                                  packet.CharacterXpMultiplier,
                                  packet.SkillXpMultiplier,
                                  packet.PvpEnabled ? Loc.T("Açık", "On") : Loc.T("Kapalı", "Off")),
                            Colors.Cyan));
                    }
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] HandleServerConfigSyncPacket error: {ex.Message}");
                }
            });
        }

        public static CampaignOptions.Difficulty MapDifficulty(int level)
        {
            return level switch
            {
                0 => CampaignOptions.Difficulty.VeryEasy,
                1 => CampaignOptions.Difficulty.Easy,
                _ => CampaignOptions.Difficulty.Realistic
            };
        }
    }
}
