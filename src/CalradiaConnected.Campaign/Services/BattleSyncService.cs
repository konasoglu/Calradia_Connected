using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Player;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using GameCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Synchronizes campaign map encounters, real Player-vs-Player (PvP) battles with a dual Ready gate,
    /// defeated party/prisoner handling, and Hero Execution / Permadeath consequences across the Server and all Clients.
    /// </summary>
    public sealed class BattleSyncService
    {
        private static BattleSyncService? _instance;
        public static BattleSyncService Instance => _instance ??= new BattleSyncService();

        private sealed class PvpBattleSessionState
        {
            public int AttackerPeerId;
            public int DefenderPeerId;
            public bool AttackerReady;
            public bool DefenderReady;
            public bool CombatStarted;
            public DateTime CreatedUtc = DateTime.UtcNow;
        }

        private readonly Dictionary<string, PvpBattleSessionState> _activePvpBattles = new();
        private readonly Dictionary<string, PvpBattleSessionState> _pendingPvpRequests = new();
        private readonly Dictionary<int, DateTime> _pvpDisengageCooldowns = new();
        private bool _subscribed;
        private bool _hasPendingPvpRequest;
        private int _pendingPvpTargetPeerId;
        private string _pendingPvpTargetPartyId = string.Empty;
        private DateTime _pvpRequestSentUtc = DateTime.MinValue;

        private bool _hasPendingPvpLaunch;
        private int _pendingLaunchAttackerPeerId;
        private int _pendingLaunchDefenderPeerId;
        private string _pendingLaunchAttackerPartyId = string.Empty;
        private string _pendingLaunchDefenderPartyId = string.Empty;
        private bool _pendingLaunchIAmAttacker;
        private bool _pendingLaunchIsNaval;
        private int _pendingLaunchWindSeed;
        private int _pendingLaunchFramesWaited;
        private DateTime _pendingLaunchQueuedUtc = DateTime.MinValue;
        private sealed class PendingPvpCaptureState
        {
            public int WinnerPeerId;
            public int LoserPeerId;
            public string WinnerPartyId = string.Empty;
            public string LoserPartyId = string.Empty;
            public DateTime QueuedUtc = DateTime.UtcNow;
            public int FramesWaited;
        }

        private DateTime _pvpCombatStartedUtc = DateTime.MinValue;
        private bool _pendingPostPvpEncounterClose;
        private DateTime _postPvpEncounterCloseUntilUtc = DateTime.MinValue;
        private bool _pvpResultReportedForSession;
        private PendingPvpCaptureState? _pendingPvpCapture;

        public void HandlePeerDisconnected(int peerId)
        {
            try
            {
                if (ActivePvpOpponentPeerId == peerId && TaleWorlds.MountAndBlade.Mission.Current != null)
                {
                    try { TaleWorlds.MountAndBlade.Mission.Current.PauseAITick = false; } catch { }
                    ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 3 });
                }
            }
            catch { }
            if (peerId <= 0) return;

            foreach (var key in _pendingPvpRequests.Where(kv => kv.Value.AttackerPeerId == peerId || kv.Value.DefenderPeerId == peerId).Select(kv => kv.Key).ToList())
            {
                _pendingPvpRequests.Remove(key);
            }
            foreach (var key in _activePvpBattles.Where(kv => kv.Value.AttackerPeerId == peerId || kv.Value.DefenderPeerId == peerId).Select(kv => kv.Key).ToList())
            {
                _activePvpBattles.Remove(key);
            }

            if (_hasPendingPvpRequest && _pendingPvpTargetPeerId == peerId)
            {
                _hasPendingPvpRequest = false;
                _pendingPvpTargetPeerId = 0;
                _pendingPvpTargetPartyId = string.Empty;
                try { InformationManager.HideInquiry(); } catch { }
            }

            if (ActivePvpOpponentPeerId == peerId)
            {
                ClearLocalPvpState();
                ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 3 });
            }
        }

        public bool IsPeerInDisengageCooldown(MobileParty? party)
        {
            if (party == null) return false;
            if (!NetworkObjectRegistry.TryExtractPeerId(party.StringId, out int peerId) || peerId <= 0) return false;
            if (_pvpDisengageCooldowns.TryGetValue(peerId, out DateTime untilUtc))
            {
                if (DateTime.UtcNow < untilUtc) return true;
                _pvpDisengageCooldowns.Remove(peerId);
            }
            return false;
        }

        private void RecordDisengageCooldown(int peerId, double seconds = 4.0)
        {
            if (peerId <= 0) return;
            _pvpDisengageCooldowns[peerId] = DateTime.UtcNow.AddSeconds(seconds);
        }

        private static void DisengageFromPartyOnMap(MobileParty? enemyParty)
        {
            var mainParty = MobileParty.MainParty;
            if (mainParty == null) return;

            try
            {
                if (GameStateManager.Current?.ActiveState is MapState mapState && mapState.AtMenu)
                {
                    mapState.ExitMenuMode();
                }
            }
            catch { }

            try
            {
                if (enemyParty != null)
                {
                    Vec2 away = mainParty.GetPosition2D - enemyParty.GetPosition2D;
                    if (away.LengthSquared < 0.01f) away = new Vec2(1f, 0.25f);
                    away.Normalize();
                    Vec2 newPos = enemyParty.GetPosition2D + away * 1.15f;
                    mainParty.Position = new CampaignVec2(newPos, isOnLand: !mainParty.IsCurrentlyAtSea && !enemyParty.IsCurrentlyAtSea);
                }
                mainParty.SetMoveModeHold();
            }
            catch { }
        }

        public void ClearPvpSessionForRematch()
        {
            ClearLocalPvpState();
        }

        public void NotifyPvpRetreatToMap(MobileParty? enemyParty)
        {
            if (enemyParty != null && NetworkObjectRegistry.TryExtractPeerId(enemyParty.StringId, out int peerId))
            {
                RecordDisengageCooldown(peerId, 4.0);
            }

            try
            {
                if (PlayerEncounter.Current != null)
                {
                    PlayerEncounter.Finish(true);
                }
            }
            catch { }

            DisengageFromPartyOnMap(enemyParty);
            ClearLocalPvpState();
            ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 3 });
        }

        public void HandleLocalPvpSurrender(MobileParty? enemyParty)
        {
            if (enemyParty == null || ModContext.IsServerRunning || !ModContext.IsClientConnected) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            NetworkObjectRegistry.TryExtractPeerId(enemyParty.StringId, out int enemyPeerId);
            RecordDisengageCooldown(enemyPeerId, 5.0);

            int ransom = 0;
            if (Hero.MainHero != null && Hero.MainHero.Gold > 0)
            {
                ransom = Math.Min(Hero.MainHero.Gold, Math.Min(5000, Math.Max(50, Hero.MainHero.Gold / 4)));
                Hero.MainHero.Gold -= ransom;
            }

            try
            {
                if (PlayerEncounter.Current != null)
                {
                    PlayerEncounter.Finish(true);
                }
            }
            catch { }

            DisengageFromPartyOnMap(enemyParty);
            ClearLocalPvpState();
            ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 3 });
            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "PvpSurrender" });

            if (enemyPeerId > 0)
            {
                ModContext.Client?.Send(new BattleSessionSyncPacket
                {
                    AttackerPeerId = myPeerId,
                    DefenderPeerId = enemyPeerId,
                    BattleStage = 15, // PvpSurrender
                    DefenderTroopsLost = ransom
                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            string enemyName = enemyParty.LeaderHero?.Name?.ToString() ?? enemyParty.Name?.ToString() ?? enemyParty.StringId;
            InformationManager.DisplayMessage(new InformationMessage(
                Loc.F("'{0}' tarafına teslim oldunuz! ({1} Altın savaş tazminatı ödendi.)",
                      "You surrendered to '{0}'! (Paid {1} Gold reparation.)",
                      enemyName, ransom),
                Colors.Yellow));
        }

        public bool IsSuppressingExecutionHook { get; set; }
        public bool IsLaunchingSynchronizedPvpMission { get; private set; }
        public int ActivePvpOpponentPeerId { get; private set; }
        public string ActivePvpOpponentName { get; private set; } = string.Empty;
        public int ActivePvpAttackerPeerId { get; private set; }
        public int ActivePvpDefenderPeerId { get; private set; }
        public bool LocalIsAttackerInPvp { get; private set; }
        public bool AttackerReady { get; private set; }
        public bool DefenderReady { get; private set; }
        public bool PvpCombatStarted { get; private set; }

        public bool HasActivePvpSession =>
            ActivePvpOpponentPeerId > 0 ||
            (_hasPendingPvpRequest && (DateTime.UtcNow - _pvpRequestSentUtc).TotalSeconds < 25);

        public bool IsInActivePvpWithParty(string? partyId)
        {
            if (string.IsNullOrEmpty(partyId)) return false;
            if (ActivePvpOpponentPeerId <= 0 && !_hasPendingPvpLaunch && !IsLaunchingSynchronizedPvpMission) return false;

            if (NetworkObjectRegistry.TryExtractPeerId(partyId, out int peerId) && peerId > 0)
            {
                return peerId == ActivePvpOpponentPeerId ||
                       peerId == _pendingLaunchAttackerPeerId ||
                       peerId == _pendingLaunchDefenderPeerId ||
                       peerId == ActivePvpAttackerPeerId ||
                       peerId == ActivePvpDefenderPeerId;
            }
            return false;
        }

        public bool IsPvpCombatReadyForEndChecks()
        {
            return PvpCombatStarted &&
                   _pvpCombatStartedUtc != DateTime.MinValue &&
                   (DateTime.UtcNow - _pvpCombatStartedUtc).TotalSeconds >= 4.0;
        }

        private static string GetPairKey(int peerA, int peerB)
        {
            int min = Math.Min(peerA, peerB);
            int max = Math.Max(peerA, peerB);
            return $"{min}_{max}";
        }

        public void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;

            ModContext.Events.Subscribe<PlayerEncounterInterceptedEvent>(OnPlayerEncounterStarted);
            ModContext.Events.Subscribe<PvpBattleStateChangedEvent>(OnLocalPvpEvent);
            ModContext.Events.Subscribe<PvpMissionEndedEvent>(OnPvpMissionEnded);
        }

        private void OnPvpMissionEnded(PvpMissionEndedEvent evt)
        {
            if (evt == null || ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            if (!evt.Resolved || _pvpResultReportedForSession) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            int oppPeerId = ActivePvpOpponentPeerId > 0 ? ActivePvpOpponentPeerId : evt.OpponentPeerId;
            if (myPeerId <= 0 || oppPeerId <= 0 || !PvpCombatStarted) return;

            int atkPeerId = ActivePvpAttackerPeerId > 0 ? ActivePvpAttackerPeerId : (LocalIsAttackerInPvp ? myPeerId : oppPeerId);
            int defPeerId = ActivePvpDefenderPeerId > 0 ? ActivePvpDefenderPeerId : (LocalIsAttackerInPvp ? oppPeerId : myPeerId);
            bool attackerWon = LocalIsAttackerInPvp ? evt.LocalWon : !evt.LocalWon;

            _pvpResultReportedForSession = true;
            _pendingPostPvpEncounterClose = true;
            _postPvpEncounterCloseUntilUtc = DateTime.UtcNow.AddSeconds(6.0);
            RecordDisengageCooldown(oppPeerId, 6.0);

            var packet = new BattleSessionSyncPacket
            {
                AttackerPeerId = atkPeerId,
                DefenderPeerId = defPeerId,
                AttackerPartyId = NetworkObjectRegistry.GetPlayerPartyId(atkPeerId),
                DefenderPartyId = NetworkObjectRegistry.GetPlayerPartyId(defPeerId),
                BattleStage = (byte)(attackerWon ? 2 : 3),
                DefenderTroopsLost = 0,
                DisbandDefender = false
            };

            try
            {
                TaleWorlds.Library.Debug.Print(
                    $"[Calradia-Connected PvP] Reporting mission end on TAB: Atk={atkPeerId}, Def={defPeerId}, Stage={packet.BattleStage}, LocalWon={evt.LocalWon}");
            }
            catch { }

            ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "PvpMissionEnded" });
        }

        private void OnLocalPvpEvent(PvpBattleStateChangedEvent evt)
        {
            // When Mission behavior or HUD publishes Stage=12 locally, transmit PvpPlayerReady to the server.
            if (evt.Stage == 12 && ActivePvpOpponentPeerId > 0)
            {
                SendPvpReadySignal();
            }
        }

        public void RequestPvpBattle(MobileParty? targetParty)
        {
            if (targetParty == null || ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            if (!CalradiaConnected.Common.Config.ServerRuntimeSettings.Current.PvpEnabled)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("Bu sunucuda oyuncular arası savaş kapalıdır.", "Player-vs-player combat is disabled on this server."),
                    Colors.Yellow));
                return;
            }
            if (!NetworkObjectRegistry.TryExtractPeerId(targetParty.StringId, out int targetPeerId) || targetPeerId <= 0) return;

            if (HasActivePvpSession || _hasPendingPvpLaunch) return;

            PlayerInteractionService.Instance.CloseLiveBarterScreenSilently();
            PlayerInteractionService.Instance.CloseActiveDialogLocalOnly();

            var mainParty = MobileParty.MainParty;
            if (mainParty != null)
            {
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

            _hasPendingPvpRequest = true;
            _pendingPvpTargetPeerId = targetPeerId;
            _pendingPvpTargetPartyId = targetParty.StringId;
            _pvpRequestSentUtc = DateTime.UtcNow;

            EnsureFactionsAtWar(Hero.MainHero?.MapFaction, targetParty.MapFaction);
            RosterSyncService.Instance.SyncLocalPlayerRosterToServer();

            int localPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string targetName = targetParty.LeaderHero?.Name?.ToString() ?? targetParty.Name?.ToString() ?? targetParty.StringId;

            PlayerInteractionService.Instance.SendInteractionPacket(new PlayerInteractionPacket
            {
                SenderPeerId = localPeerId,
                SenderName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", localPeerId),
                TargetPeerId = targetPeerId,
                TargetName = targetName,
                InteractionType = 9,
                ObjectId = "DECLARE_WAR"
            });

            // Immediate PvP attack — NO approval prompt sent to the defender!
            ModContext.Client?.Send(new BattleSessionSyncPacket
            {
                AttackerPeerId = localPeerId,
                DefenderPeerId = targetPeerId,
                AttackerPartyId = MobileParty.MainParty?.StringId ?? string.Empty,
                DefenderPartyId = targetParty.StringId,
                BattleStage = 10, // PvpImmediateAttack
                DefenderTroopsLost = 0,
                DisbandDefender = false
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);

            InformationManager.DisplayMessage(new InformationMessage(
                Loc.F("'{0}' birliğine saldırı başlatıldı! Ordular savaş alanına giriyor...",
                      "Attack launched against '{0}'! Armies are taking the field...", targetName),
                Colors.Red));
        }

        public void CancelPendingPvpRequest()
        {
            if (!_hasPendingPvpRequest) return;

            int targetPeerId = _pendingPvpTargetPeerId;
            string targetPartyId = _pendingPvpTargetPartyId;
            _hasPendingPvpRequest = false;
            _pendingPvpTargetPeerId = 0;
            _pendingPvpTargetPartyId = string.Empty;

            if (targetPeerId > 0)
            {
                RecordDisengageCooldown(targetPeerId, 5.0);
                ModContext.Client?.Send(new BattleSessionSyncPacket
                {
                    AttackerPeerId = ModContext.Client?.LocalPeerId ?? 1001,
                    DefenderPeerId = targetPeerId,
                    AttackerPartyId = MobileParty.MainParty?.StringId ?? string.Empty,
                    DefenderPartyId = targetPartyId,
                    BattleStage = 18 // PvpRequestCancelled
                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            InformationManager.DisplayMessage(new InformationMessage(
                Loc.T("[PvP] Savaş talebi iptal edildi.", "[PvP] Battle request cancelled."),
                Colors.Yellow));
        }

        public void SendPvpReadySignal()
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            if (ActivePvpOpponentPeerId <= 0 || ActivePvpAttackerPeerId <= 0 || ActivePvpDefenderPeerId <= 0) return;

            bool iAmReadyAlready = LocalIsAttackerInPvp ? AttackerReady : DefenderReady;
            if (iAmReadyAlready) return;

            if (LocalIsAttackerInPvp) AttackerReady = true;
            else DefenderReady = true;

            var packet = new BattleSessionSyncPacket
            {
                AttackerPeerId = ActivePvpAttackerPeerId,
                DefenderPeerId = ActivePvpDefenderPeerId,
                BattleStage = 12, // PvpPlayerReady
                AttackerReady = AttackerReady,
                DefenderReady = DefenderReady
            };

            ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);

            ModContext.Events.Publish(new PvpBattleStateChangedEvent
            {
                Stage = 14,
                AttackerPeerId = ActivePvpAttackerPeerId,
                DefenderPeerId = ActivePvpDefenderPeerId,
                OpponentPeerId = ActivePvpOpponentPeerId,
                OpponentName = ActivePvpOpponentName,
                AttackerReady = AttackerReady,
                DefenderReady = DefenderReady
            });
        }

        private void ClearLocalPvpState()
        {
            _hasPendingPvpRequest = false;
            _pendingPvpTargetPeerId = 0;
            _pendingPvpTargetPartyId = string.Empty;
            _hasPendingPvpLaunch = false;
            _pendingLaunchAttackerPeerId = 0;
            _pendingLaunchDefenderPeerId = 0;
            _pendingLaunchAttackerPartyId = string.Empty;
            _pendingLaunchDefenderPartyId = string.Empty;
            ActivePvpOpponentPeerId = 0;
            ActivePvpOpponentName = string.Empty;
            ActivePvpAttackerPeerId = 0;
            ActivePvpDefenderPeerId = 0;
            LocalIsAttackerInPvp = false;
            AttackerReady = false;
            DefenderReady = false;
            PvpCombatStarted = false;
            _pvpCombatStartedUtc = DateTime.MinValue;
            _pvpResultReportedForSession = false;
        }

        private void OnPlayerEncounterStarted(PlayerEncounterInterceptedEvent evt)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;

            var packet = new BattleSessionSyncPacket
            {
                AttackerPeerId = ModContext.Client?.LocalPeerId ?? 1001,
                AttackerPartyId = evt.AttackerPartyId ?? string.Empty,
                DefenderPartyId = evt.DefenderPartyId ?? string.Empty,
                BattleStage = 1, // BattleStarted
                DefenderTroopsLost = 0,
                DisbandDefender = false
            };

            ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void NotifyLocalMapEventEnded(MapEvent mapEvent)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || mapEvent == null) return;
            if (!mapEvent.IsPlayerMapEvent) return;

            try
            {
                var enemySide = mapEvent.GetMapEventSide(mapEvent.DefeatedSide == mapEvent.PlayerSide
                    ? mapEvent.WinningSide
                    : (mapEvent.PlayerSide == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker));

                var enemyPartyBase = enemySide?.LeaderParty;
                if (enemyPartyBase == null) return;

                string defenderId = enemyPartyBase.MobileParty?.StringId ?? enemyPartyBase.Id ?? string.Empty;
                if (string.IsNullOrEmpty(defenderId)) return;

                bool playerWon = (mapEvent.WinningSide == mapEvent.PlayerSide);
                int remainingEnemyTroops = enemyPartyBase.NumberOfHealthyMembers;
                bool isEnemyPlayer = enemyPartyBase.MobileParty != null && NetworkObjectRegistry.IsPlayerNetworkParty(enemyPartyBase.MobileParty);
                bool disbandDefender = playerWon && enemyPartyBase.MobileParty != null && !isEnemyPlayer &&
                                       (remainingEnemyTroops <= 0 || enemyPartyBase.MobileParty.IsBandit);

                int defenderPeerId = 0;
                if (isEnemyPlayer && NetworkObjectRegistry.TryExtractPeerId(defenderId, out int parsedPeer))
                {
                    defenderPeerId = parsedPeer;
                }

                if (isEnemyPlayer && defenderPeerId > 0)
                {
                    _pendingPostPvpEncounterClose = true;
                    _postPvpEncounterCloseUntilUtc = DateTime.UtcNow.AddSeconds(6.0);
                    RecordDisengageCooldown(defenderPeerId, 6.0);
                    DisengageFromPartyOnMap(enemyPartyBase.MobileParty);

                    if (_pvpResultReportedForSession)
                    {
                        ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "BattleEnded" });
                        ClearLocalPvpState();
                        return;
                    }

                    bool combatNeverStartedWin = !PvpCombatStarted && playerWon;
                    if (mapEvent.WinningSide == BattleSideEnum.None || combatNeverStartedWin)
                    {
                        ModContext.Client?.Send(new BattleSessionSyncPacket
                        {
                            AttackerPeerId = ModContext.Client?.LocalPeerId ?? 1001,
                            DefenderPeerId = defenderPeerId,
                            AttackerPartyId = MobileParty.MainParty?.StringId ?? string.Empty,
                            DefenderPartyId = defenderId,
                            BattleStage = 18
                        }, LiteNetLib.DeliveryMethod.ReliableOrdered);

                        ClearLocalPvpState();
                        ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 3 });
                        return;
                    }

                    int myPeer = ModContext.Client?.LocalPeerId ?? 1001;
                    int realAtkPeer = ActivePvpAttackerPeerId > 0 ? ActivePvpAttackerPeerId : (LocalIsAttackerInPvp ? myPeer : defenderPeerId);
                    int realDefPeer = ActivePvpDefenderPeerId > 0 ? ActivePvpDefenderPeerId : (LocalIsAttackerInPvp ? defenderPeerId : myPeer);
                    bool realAtkWon = LocalIsAttackerInPvp ? playerWon : !playerWon;
                    _pvpResultReportedForSession = true;

                    var pvpResultPacket = new BattleSessionSyncPacket
                    {
                        AttackerPeerId = realAtkPeer,
                        DefenderPeerId = realDefPeer,
                        AttackerPartyId = NetworkObjectRegistry.GetPlayerPartyId(realAtkPeer),
                        DefenderPartyId = NetworkObjectRegistry.GetPlayerPartyId(realDefPeer),
                        BattleStage = (byte)(realAtkWon ? 2 : 3),
                        DefenderTroopsLost = enemySide?.TroopCasualties ?? 0,
                        DisbandDefender = false
                    };

                    ModContext.Client?.Send(pvpResultPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "BattleEnded" });
                    ClearLocalPvpState();
                    ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = pvpResultPacket.BattleStage });
                    return;
                }
                else if (mapEvent.WinningSide == BattleSideEnum.None)
                {
                    ClearLocalPvpState();
                    ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 3 });
                    return;
                }

                var packet = new BattleSessionSyncPacket
                {
                    AttackerPeerId = ModContext.Client?.LocalPeerId ?? 1001,
                    DefenderPeerId = defenderPeerId,
                    AttackerPartyId = MobileParty.MainParty?.StringId ?? string.Empty,
                    DefenderPartyId = defenderId,
                    BattleStage = (byte)(playerWon ? 2 : 3),
                    DefenderTroopsLost = enemySide?.TroopCasualties ?? 0,
                    DisbandDefender = disbandDefender
                };

                ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "BattleEnded" });

                ClearLocalPvpState();
                ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = packet.BattleStage });
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] NotifyLocalMapEventEnded error: {ex.Message}");
            }
        }

        public void HandlePacket(BattleSessionSyncPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (GameCampaign.Current == null) return;

                    int myPeerId = ModContext.Client?.LocalPeerId ?? -1;

                    if (packet.BattleStage == 10 || packet.BattleStage == 17) // Immediate PvP Attack -> Server starts battle immediately without consent prompt
                    {
                        if (!ModContext.IsServerRunning) return;

                        if (!CalradiaConnected.Common.Config.ServerRuntimeSettings.Current.PvpEnabled)
                        {
                            ModContext.Server?.SendTo(packet.AttackerPeerId, new ChatMessagePacket
                            {
                                SenderPeerId = 0,
                                SenderName = "Sunucu",
                                Message = Loc.T("Bu sunucuda oyuncular arası savaş kapalıdır.", "Player-vs-player combat is disabled on this server."),
                                IsSystem = true,
                                Channel = 0
                            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        int senderPeerId = packet.BattleStage == 17 && packet.DefenderTroopsLost > 0 ? packet.DefenderTroopsLost : packet.DefenderPeerId;
                        if (packet.AttackerPeerId <= 0 || senderPeerId <= 0 || packet.AttackerPeerId == senderPeerId) return;

                        string pairKey = GetPairKey(packet.AttackerPeerId, senderPeerId);
                        if (_activePvpBattles.TryGetValue(pairKey, out var existingBattle) &&
                            (DateTime.UtcNow - existingBattle.CreatedUtc).TotalSeconds < 12)
                        {
                            return;
                        }
                        _pendingPvpRequests.Remove(pairKey);

                        var atkParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.AttackerPeerId)
                                       ?? NetworkObjectRegistry.Instance.ResolveParty(packet.AttackerPartyId);
                        var defParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(senderPeerId)
                                       ?? NetworkObjectRegistry.Instance.ResolveParty(packet.DefenderPartyId);

                        if (atkParty == null || defParty == null || !atkParty.IsActive || !defParty.IsActive) return;

                        RosterSyncService.Instance.ApplyCachedRosterIfExists(packet.AttackerPeerId, atkParty);
                        RosterSyncService.Instance.ApplyCachedRosterIfExists(senderPeerId, defParty);
                        EnsureCombatantReadyForPvp(atkParty);
                        EnsureCombatantReadyForPvp(defParty);
                        EnsureFactionsAtWar(atkParty.MapFaction, defParty.MapFaction);

                        // Sync authoritative troop rosters of BOTH armies before starting the battle mission
                        BroadcastLatestPlayerRoster(packet.AttackerPeerId, atkParty);
                        BroadcastLatestPlayerRoster(senderPeerId, defParty);

                        _activePvpBattles[pairKey] = new PvpBattleSessionState
                        {
                            AttackerPeerId = packet.AttackerPeerId,
                            DefenderPeerId = senderPeerId,
                            AttackerReady = false,
                            DefenderReady = false,
                            CombatStarted = false,
                            CreatedUtc = DateTime.UtcNow
                        };

                        bool isNaval = (atkParty.IsCurrentlyAtSea || defParty.IsCurrentlyAtSea) && NavalSyncService.IsNavalDlcActive;
                        int windSeed = (Math.Abs(atkParty.StringId.GetHashCode() ^ defParty.StringId.GetHashCode()) % 9000) + 1000;

                        ModContext.Server?.Broadcast(new BattleSessionSyncPacket
                        {
                            AttackerPeerId = packet.AttackerPeerId,
                            DefenderPeerId = senderPeerId,
                            AttackerPartyId = atkParty.StringId,
                            DefenderPartyId = defParty.StringId,
                            BattleStage = 11, // PvpBattleStart
                            AttackerReady = false,
                            DefenderReady = false,
                            IsNavalBattle = isNaval,
                            WindSeed = windSeed
                        }, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        return;
                    }

                    if (packet.BattleStage == 16 || packet.BattleStage == 18)
                    {
                        if (packet.BattleStage == 18 && ModContext.IsServerRunning && packet.AttackerPeerId > 0 && packet.DefenderPeerId > 0)
                        {
                            _activePvpBattles.Remove(GetPairKey(packet.AttackerPeerId, packet.DefenderPeerId));
                        }
                        return;
                    }

                    if (packet.BattleStage == 12) // Server receives PvpPlayerReady -> Sync status (14) -> When BOTH ready, Broadcasts PvpCombatBegin (13)
                    {
                        if (!ModContext.IsServerRunning) return;

                        int senderPeerId = packet.DefenderTroopsLost > 0 ? packet.DefenderTroopsLost : packet.AttackerPeerId;
                        PvpBattleSessionState? session = null;

                        if (packet.AttackerPeerId > 0 && packet.DefenderPeerId > 0)
                        {
                            string key = GetPairKey(packet.AttackerPeerId, packet.DefenderPeerId);
                            _activePvpBattles.TryGetValue(key, out session);
                        }

                        if (session == null)
                        {
                            session = _activePvpBattles.Values.FirstOrDefault(s =>
                                s.AttackerPeerId == senderPeerId || s.DefenderPeerId == senderPeerId);
                        }

                        if (session == null) return;

                        if (senderPeerId == session.AttackerPeerId)
                        {
                            session.AttackerReady = true;
                        }
                        else if (senderPeerId == session.DefenderPeerId)
                        {
                            session.DefenderReady = true;
                        }
                        else
                        {
                            return;
                        }

                        var statusSync = new BattleSessionSyncPacket
                        {
                            AttackerPeerId = session.AttackerPeerId,
                            DefenderPeerId = session.DefenderPeerId,
                            BattleStage = 14, // PvpReadyStatusSync
                            AttackerReady = session.AttackerReady,
                            DefenderReady = session.DefenderReady
                        };
                        ModContext.Server?.SendTo(session.AttackerPeerId, statusSync, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        ModContext.Server?.SendTo(session.DefenderPeerId, statusSync, LiteNetLib.DeliveryMethod.ReliableOrdered);

                        if (session.AttackerReady && session.DefenderReady && !session.CombatStarted)
                        {
                            session.CombatStarted = true;

                            var beginPacket = new BattleSessionSyncPacket
                            {
                                AttackerPeerId = session.AttackerPeerId,
                                DefenderPeerId = session.DefenderPeerId,
                                BattleStage = 13, // PvpCombatBegin
                                AttackerReady = true,
                                DefenderReady = true
                            };

                            InformationManager.DisplayMessage(new InformationMessage(
                                Loc.F("[PvP Hazır] Her iki oyuncu da hazır! ({0} vs {1}) Savaş başlıyor!",
                                      "[PvP Ready] Both players are READY! ({0} vs {1}) Combat begins!",
                                      session.AttackerPeerId, session.DefenderPeerId),
                                Colors.Green));

                            ModContext.Server?.SendTo(session.AttackerPeerId, beginPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            ModContext.Server?.SendTo(session.DefenderPeerId, beginPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        }
                        return;
                    }

                    if (packet.BattleStage == 15) // PvpSurrender
                    {
                        int loserPeerId = packet.AttackerPeerId;
                        int winnerPeerId = packet.DefenderPeerId;
                        int ransom = Math.Max(0, packet.DefenderTroopsLost);

                        if (ModContext.IsServerRunning)
                        {
                            _activePvpBattles.Remove(GetPairKey(loserPeerId, winnerPeerId));

                            var loserHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(loserPeerId);
                            var winnerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(winnerPeerId);
                            if (loserHero != null && ransom > 0)
                            {
                                loserHero.Gold = Math.Max(0, loserHero.Gold - ransom);
                            }
                            if (winnerHero != null && ransom > 0)
                            {
                                winnerHero.Gold += ransom;
                            }

                            ModContext.Server?.SendTo(winnerPeerId, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (myPeerId == winnerPeerId)
                        {
                            RecordDisengageCooldown(loserPeerId, 5.0);
                            if (Hero.MainHero != null && ransom > 0)
                            {
                                Hero.MainHero.Gold += ransom;
                            }
                            ClearLocalPvpState();
                            ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 2 });
                            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "PvpOpponentSurrendered" });

                            try
                            {
                                if (PlayerEncounter.Current != null)
                                {
                                    PlayerEncounter.Finish(true);
                                }
                            }
                            catch { }

                            var loserHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(loserPeerId);
                            string loserName = loserHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", loserPeerId);
                            InformationManager.DisplayMessage(new InformationMessage(
                                Loc.F("[PvP Zaferi] '{0}' size teslim oldu! (+{1} Altın savaş tazminatı)",
                                      "[PvP Victory] '{0}' surrendered to you! (+{1} Gold reparation)",
                                      loserName, ransom),
                                Colors.Green));
                        }
                        return;
                    }

                    if (packet.BattleStage == 14 || packet.BattleStage == 13) // PvpReadyStatusSync / PvpCombatBegin on clients
                    {
                        if (ModContext.IsServerRunning) return;
                        if (myPeerId != packet.AttackerPeerId && myPeerId != packet.DefenderPeerId) return;

                        AttackerReady = packet.AttackerReady;
                        DefenderReady = packet.DefenderReady;
                        if (packet.BattleStage == 13)
                        {
                            PvpCombatStarted = true;
                            _pvpCombatStartedUtc = DateTime.UtcNow;
                        }

                        ModContext.Events.Publish(new PvpBattleStateChangedEvent
                        {
                            Stage = packet.BattleStage,
                            AttackerPeerId = packet.AttackerPeerId,
                            DefenderPeerId = packet.DefenderPeerId,
                            OpponentPeerId = ActivePvpOpponentPeerId,
                            OpponentName = ActivePvpOpponentName,
                            AttackerReady = AttackerReady,
                            DefenderReady = DefenderReady
                        });
                        return;
                    }

                    if (packet.BattleStage == 11) // PvpBattleStart on clients & server notice
                    {
                        var atkParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.AttackerPeerId)
                                       ?? NetworkObjectRegistry.Instance.ResolveParty(packet.AttackerPartyId);
                        var defParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.DefenderPeerId)
                                       ?? NetworkObjectRegistry.Instance.ResolveParty(packet.DefenderPartyId);

                        if (atkParty == null || defParty == null) return;

                        EnsureFactionsAtWar(atkParty.MapFaction, defParty.MapFaction);
                        atkParty.SetMoveModeHold();
                        defParty.SetMoveModeHold();

                        string atkName = atkParty.LeaderHero?.Name?.ToString() ?? atkParty.Name?.ToString() ?? $"#{packet.AttackerPeerId}";
                        string defName = defParty.LeaderHero?.Name?.ToString() ?? defParty.Name?.ToString() ?? $"#{packet.DefenderPeerId}";

                        InformationManager.DisplayMessage(new InformationMessage(
                            Loc.F("[PvP Muharebe] '{0}' ve '{1}' canlı muharebeye tutuştu!",
                                  "[PvP Battle] '{0}' and '{1}' engaged in live combat!", atkName, defName),
                            Colors.Red));

                        if (!ModContext.IsServerRunning && (myPeerId == packet.AttackerPeerId || myPeerId == packet.DefenderPeerId))
                        {
                            _pendingPostPvpEncounterClose = false;
                            _hasPendingPvpRequest = false;
                            bool iAmAttacker = myPeerId == packet.AttackerPeerId;
                            ActivePvpAttackerPeerId = packet.AttackerPeerId;
                            ActivePvpDefenderPeerId = packet.DefenderPeerId;
                            LocalIsAttackerInPvp = iAmAttacker;
                            ActivePvpOpponentPeerId = iAmAttacker ? packet.DefenderPeerId : packet.AttackerPeerId;
                            ActivePvpOpponentName = iAmAttacker ? defName : atkName;
                            AttackerReady = false;
                            DefenderReady = false;
                            PvpCombatStarted = false;
                            _pvpCombatStartedUtc = DateTime.MinValue;
                            _pvpResultReportedForSession = false;
                            _pendingPvpCapture = null;

                            RosterSyncService.Instance.ApplyCachedRosterIfExists(packet.AttackerPeerId, atkParty);
                            RosterSyncService.Instance.ApplyCachedRosterIfExists(packet.DefenderPeerId, defParty);

                            ModContext.Events.Publish(new PvpBattleStateChangedEvent
                            {
                                Stage = 11,
                                AttackerPeerId = packet.AttackerPeerId,
                                DefenderPeerId = packet.DefenderPeerId,
                                OpponentPeerId = ActivePvpOpponentPeerId,
                                OpponentName = ActivePvpOpponentName,
                                AttackerReady = false,
                                DefenderReady = false
                            });

                            QueueSynchronizedPvpLaunch(atkParty, defParty, packet.AttackerPeerId, packet.DefenderPeerId, iAmAttacker, packet.IsNavalBattle, packet.WindSeed);
                        }
                        return;
                    }

                    bool isPvpResultPacket = (packet.BattleStage == 2 || packet.BattleStage == 3) &&
                                             packet.AttackerPeerId > 0 &&
                                             packet.DefenderPeerId > 0;

                    if (!ModContext.IsServerRunning && packet.AttackerPeerId == myPeerId && !isPvpResultPacket)
                    {
                        return;
                    }

                    var attackerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.AttackerPeerId);
                    var defenderHero = packet.DefenderPeerId > 0
                        ? NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.DefenderPeerId)
                        : null;
                    var attackerParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.AttackerPeerId)
                                        ?? NetworkObjectRegistry.Instance.ResolveParty(packet.AttackerPartyId);
                    var defenderParty = (packet.DefenderPeerId > 0
                                            ? NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.DefenderPeerId)
                                            : null)
                                        ?? NetworkObjectRegistry.Instance.ResolveParty(packet.DefenderPartyId);

                    string attackerName = attackerHero?.Name?.ToString()
                                          ?? attackerParty?.Name?.ToString()
                                          ?? Loc.F("Oyuncu #{0}", "Player #{0}", packet.AttackerPeerId);
                    string defenderName = defenderHero?.Name?.ToString()
                                          ?? defenderParty?.Name?.ToString()
                                          ?? packet.DefenderPartyId;

                    if (packet.BattleStage == 1) // BattleStarted
                    {
                        attackerParty?.SetMoveModeHold();
                        defenderParty?.SetMoveModeHold();

                        InformationManager.DisplayMessage(new InformationMessage(
                            Loc.F("[Muharebe] '{0}', '{1}' birliğiyle savaşa girdi!",
                                  "[Battle] '{0}' engaged '{1}' in battle!", attackerName, defenderName),
                            Colors.Yellow));
                    }
                    else if (packet.BattleStage == 2 || packet.BattleStage == 3) // Won / Lost
                    {
                        bool isPvpResult = isPvpResultPacket ||
                                           NetworkObjectRegistry.IsPlayerNetworkParty(attackerParty) ||
                                           NetworkObjectRegistry.IsPlayerNetworkParty(defenderParty);

                        if (ModContext.IsServerRunning && isPvpResult && packet.AttackerPeerId > 0 && packet.DefenderPeerId > 0)
                        {
                            string pairKey = GetPairKey(packet.AttackerPeerId, packet.DefenderPeerId);
                            if (!_activePvpBattles.TryGetValue(pairKey, out var activeSession))
                            {
                                // Ignore duplicate / already-resolved PvP end reports so both players never mutually capture each other
                                return;
                            }

                            _activePvpBattles.Remove(pairKey);
                            if (!activeSession.CombatStarted)
                            {
                                // Neither side properly completed Ready Combat — do not apply winner/loser captivity
                                return;
                            }
                        }

                        bool attackerWon = packet.BattleStage == 2;
                        var winningParty = attackerWon ? attackerParty : defenderParty;
                        var losingParty = attackerWon ? defenderParty : attackerParty;
                        int winnerPeerId = attackerWon ? packet.AttackerPeerId : packet.DefenderPeerId;
                        int loserPeerId = attackerWon ? packet.DefenderPeerId : packet.AttackerPeerId;

                        bool localIsPvpParticipant = !ModContext.IsServerRunning &&
                                                     isPvpResult &&
                                                     (myPeerId == packet.AttackerPeerId || myPeerId == packet.DefenderPeerId);

                        if (localIsPvpParticipant)
                        {
                            _pendingPostPvpEncounterClose = true;
                            _postPvpEncounterCloseUntilUtc = DateTime.UtcNow.AddSeconds(6.0);
                            int oppId = myPeerId == packet.AttackerPeerId ? packet.DefenderPeerId : packet.AttackerPeerId;
                            RecordDisengageCooldown(oppId, 6.0);

                            // Single-TAB return to campaign map: if this client is still inside the battle mission, end it now!
                            try
                            {
                                var activeMission = TaleWorlds.MountAndBlade.Mission.Current;
                                if (activeMission != null)
                                {
                                    activeMission.EndMission();
                                }
                            }
                            catch { }

                            if (winnerPeerId > 0 && loserPeerId > 0 && winnerPeerId != loserPeerId)
                            {
                                _pendingPvpCapture = new PendingPvpCaptureState
                                {
                                    WinnerPeerId = winnerPeerId,
                                    LoserPeerId = loserPeerId,
                                    WinnerPartyId = winningParty?.StringId ?? NetworkObjectRegistry.GetPlayerPartyId(winnerPeerId),
                                    LoserPartyId = losingParty?.StringId ?? NetworkObjectRegistry.GetPlayerPartyId(loserPeerId),
                                    QueuedUtc = DateTime.UtcNow,
                                    FramesWaited = 0
                                };
                            }

                            ClearLocalPvpState();
                            ModContext.Events.Publish(new PvpBattleStateChangedEvent
                            {
                                Stage = packet.BattleStage,
                                AttackerPeerId = packet.AttackerPeerId,
                                DefenderPeerId = packet.DefenderPeerId
                            });
                        }

                        if (losingParty != null && losingParty.IsActive && losingParty != winningParty)
                        {
                            if (packet.DisbandDefender && !NetworkObjectRegistry.IsPlayerNetworkParty(losingParty))
                            {
                                try { DestroyPartyAction.Apply(winningParty?.Party, losingParty); } catch { }
                            }
                            else if (!NetworkObjectRegistry.IsPlayerNetworkParty(losingParty) &&
                                     packet.DefenderTroopsLost > 0 &&
                                     losingParty.MemberRoster != null &&
                                     losingParty.MemberRoster.TotalHealthyCount > 1)
                            {
                                try
                                {
                                    losingParty.MemberRoster.RemoveNumberOfNonHeroTroopsRandomly(
                                        Math.Min(packet.DefenderTroopsLost, losingParty.MemberRoster.TotalRegulars));
                                }
                                catch { }
                            }

                            // Server and non-participant clients apply PvP captivity immediately; participant clients
                            // apply it cleanly on MapState via _pendingPvpCapture once Mission & PlayerEncounter finish!
                            if (!localIsPvpParticipant &&
                                NetworkObjectRegistry.IsPlayerNetworkParty(losingParty) &&
                                losingParty.LeaderHero != null &&
                                losingParty.LeaderHero.IsAlive &&
                                winningParty != null &&
                                winningParty.IsActive &&
                                winningParty.Party != null)
                            {
                                try
                                {
                                    if (!losingParty.LeaderHero.IsPrisoner)
                                    {
                                        TakePrisonerAction.Apply(winningParty.Party, losingParty.LeaderHero);
                                    }
                                    HideOrDestroyCapturedRemoteParty(losingParty, winningParty);
                                }
                                catch { }
                            }
                        }

                        string victorName = attackerWon ? attackerName : defenderName;
                        string defeatedName = attackerWon ? defenderName : attackerName;
                        InformationManager.DisplayMessage(new InformationMessage(
                            Loc.F("[Zafer] '{0}', '{1}' birliğini mağlup etti!",
                                  "[Victory] '{0}' defeated '{1}'!", victorName, defeatedName),
                            Colors.Green));
                    }

                    if (ModContext.IsServerRunning)
                    {
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] BattleSyncService error: {ex.Message}");
                }
            });
        }

        private static void BroadcastLatestPlayerRoster(int peerId, MobileParty party)
        {
            if (!ModContext.IsServerRunning || peerId <= 0 || party == null) return;
            var hero = party.LeaderHero ?? NetworkObjectRegistry.Instance.ResolvePlayerHero(peerId);
            RosterSyncService.NormalizePartyHeroRoster(party, hero);
            // Always prefer authoritative client-reported cached roster so server puppet never overwrites real headcount
            var rosterPkt = RosterSyncService.Instance.GetCachedRoster(peerId)
                            ?? RosterSyncService.Instance.BuildRosterPacket(peerId, hero, party);
            ModContext.Server?.Broadcast(rosterPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private static void EnsureFactionsAtWar(IFaction? a, IFaction? b)
        {
            if (a == null || b == null || ReferenceEquals(a, b)) return;
            try
            {
                if (!FactionManager.IsAtWarAgainstFaction(a, b))
                {
                    DeclareWarAction.ApplyByPlayerHostility(a, b);
                }
            }
            catch { }
        }

        private static void EnsureCombatantReadyForPvp(MobileParty? party)
        {
            if (party == null) return;

            if (party.CurrentSettlement != null)
            {
                try { LeaveSettlementAction.ApplyForParty(party); } catch { }
            }

            if (party.MapEvent != null && party.Party != null)
            {
                try { party.Party.MapEventSide = null; } catch { }
            }

            try { party.SetMoveModeHold(); } catch { }

            var hero = party.LeaderHero;
            int extractedPeerId = 0;
            bool hasPeerId = NetworkObjectRegistry.TryExtractPeerId(party.StringId, out extractedPeerId) && extractedPeerId > 0;
            if (hero == null && hasPeerId)
            {
                hero = NetworkObjectRegistry.Instance.ResolvePlayerHero(extractedPeerId);
            }

            if (hasPeerId && !party.IsMainParty)
            {
                RosterSyncService.Instance.ApplyCachedRosterIfExists(extractedPeerId, party);
                try { party.IsInspected = true; } catch { }
            }

            if (hero != null && hero.IsAlive)
            {
                if (hero.IsPrisoner)
                {
                    try
                    {
                        if (hero == Hero.MainHero) PlayerCaptivity.EndCaptivity();
                        else EndCaptivityAction.ApplyByEscape(hero);
                    }
                    catch { }
                }

                if (hero.HeroState != Hero.CharacterStates.Active)
                {
                    try { hero.ChangeState(Hero.CharacterStates.Active); } catch { }
                }

                if (party.LeaderHero != hero)
                {
                    try { party.ChangePartyLeader(hero); } catch { }
                }

                if (party.MemberRoster != null)
                {
                    if (hero.IsAlive && hero.HeroState != Hero.CharacterStates.Active)
                    {
                        try { hero.ChangeState(Hero.CharacterStates.Active); } catch { }
                    }

                    // Clamp every hero count to 1 so MemberRoster.TotalHealthyCount matches FlattenedTroopRoster 1:1
                    RosterSyncService.NormalizePartyHeroRoster(party, hero);

                    if (party.MemberRoster.TotalHealthyCount <= 0 || hero.IsWounded || hero.HitPoints < 50)
                    {
                        int minHealthyHp = Math.Max(100, hero.MaxHitPoints);
                        if (hero.HitPoints < minHealthyHp) hero.HitPoints = minHealthyHp;
                    }

                    int heroIdx = party.MemberRoster.FindIndexOfTroop(hero.CharacterObject);
                    if (heroIdx < 0)
                    {
                        party.MemberRoster.AddToCounts(hero.CharacterObject, 1, insertAtFront: true);
                    }
                    else
                    {
                        int wounded = party.MemberRoster.GetElementWoundedNumber(heroIdx);
                        if (wounded > 0)
                        {
                            party.MemberRoster.AddToCounts(hero.CharacterObject, 0, false, -wounded);
                        }
                    }

                    try { if (!party.IsActive) party.IsActive = true; } catch { }
                    try { party.Party?.SetVisualAsDirty(); } catch { }
                }
            }
        }

        private static int ComputeDeterministicSeed(string? key)
        {
            unchecked
            {
                uint hash = 2166136261u;
                if (!string.IsNullOrEmpty(key))
                {
                    foreach (char c in key!)
                    {
                        hash ^= c;
                        hash *= 16777619u;
                    }
                }
                return (int)(hash % 9000u) + 1000;
            }
        }

        private void TearDownOpenUiAndMissionsForPvp()
        {
            try { InformationManager.HideInquiry(); } catch { }
            PlayerInteractionService.Instance.CloseLiveBarterScreenSilently();
            PlayerInteractionService.Instance.CloseActiveDialogLocalOnly();

            try
            {
                var cm = GameCampaign.Current?.ConversationManager;
                if (cm != null && cm.IsConversationInProgress) cm.EndConversation();
            }
            catch { }

            try
            {
                var mission = TaleWorlds.MountAndBlade.Mission.Current;
                if (mission != null)
                {
                    mission.EndMission();
                }
            }
            catch { }

            try
            {
                for (int i = 0; i < 6; i++)
                {
                    var activeState = GameStateManager.Current?.ActiveState;
                    if (activeState == null || activeState is MapState) break;
                    GameStateManager.Current!.PopState(0);
                }
            }
            catch { }

            if (GameStateManager.Current?.ActiveState is MapState mapState && mapState.AtMenu)
            {
                try { mapState.ExitMenuMode(); } catch { }
            }

            if (PlayerEncounter.Current != null)
            {
                try { PlayerEncounter.Finish(true); } catch { }
            }
        }

        private void QueueSynchronizedPvpLaunch(MobileParty atkParty, MobileParty defParty, int attackerPeerId, int defenderPeerId, bool iAmAttacker, bool isNavalBattle, int windSeed)
        {
            _hasPendingPvpLaunch = true;
            _pendingLaunchAttackerPeerId = attackerPeerId;
            _pendingLaunchDefenderPeerId = defenderPeerId;
            _pendingLaunchAttackerPartyId = atkParty.StringId ?? string.Empty;
            _pendingLaunchDefenderPartyId = defParty.StringId ?? string.Empty;
            _pendingLaunchIAmAttacker = iAmAttacker;
            _pendingLaunchIsNaval = isNavalBattle;
            _pendingLaunchWindSeed = windSeed;
            _pendingLaunchFramesWaited = 0;
            _pendingLaunchQueuedUtc = DateTime.UtcNow;

            EnsureCombatantReadyForPvp(atkParty);
            EnsureCombatantReadyForPvp(defParty);
            TearDownOpenUiAndMissionsForPvp();
        }

        private static void HideOrDestroyCapturedRemoteParty(MobileParty? losingParty, MobileParty? winningParty)
        {
            if (losingParty == null || losingParty.IsMainParty) return;
            try
            {
                losingParty.SetMoveModeHold();
                losingParty.Ai?.SetDoNotMakeNewDecisions(true);
                losingParty.IsVisible = false;
                losingParty.IsInspected = false;
                losingParty.Party?.SetVisualAsDirty();

                if (losingParty.IsActive &&
                    TaleWorlds.MountAndBlade.Mission.Current == null &&
                    PlayerEncounter.Current == null &&
                    (losingParty.LeaderHero == null || losingParty.LeaderHero.IsPrisoner || !losingParty.LeaderHero.IsAlive) &&
                    (losingParty.MemberRoster == null || losingParty.MemberRoster.TotalManCount <= 0))
                {
                    DestroyPartyAction.Apply(winningParty?.Party, losingParty);
                }
            }
            catch { }
        }

        private void ApplyPendingPvpCapture(PendingPvpCaptureState cap)
        {
            try
            {
                int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
                var winningParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(cap.WinnerPeerId)
                                   ?? NetworkObjectRegistry.Instance.ResolveParty(cap.WinnerPartyId);
                var losingParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(cap.LoserPeerId)
                                  ?? NetworkObjectRegistry.Instance.ResolveParty(cap.LoserPartyId);
                var losingHero = losingParty?.LeaderHero
                                 ?? NetworkObjectRegistry.Instance.ResolvePlayerHero(cap.LoserPeerId);

                if (winningParty == null || !winningParty.IsActive || winningParty.Party == null ||
                    losingHero == null || !losingHero.IsAlive)
                {
                    return;
                }

                try
                {
                    if (winningParty.Party.MapEventSide != null) winningParty.Party.MapEventSide = null;
                    if (losingParty?.Party?.MapEventSide != null) losingParty.Party.MapEventSide = null;
                }
                catch { }

                if (!losingHero.IsPrisoner)
                {
                    TakePrisonerAction.Apply(winningParty.Party, losingHero);
                }

                // Hide/destroy the defeated remote player's empty map party immediately so no invisible banner trails the captor!
                HideOrDestroyCapturedRemoteParty(losingParty, winningParty);

                string winnerName = winningParty.LeaderHero?.Name?.ToString() ?? winningParty.Name?.ToString() ?? $"#{cap.WinnerPeerId}";
                string loserName = losingHero.Name?.ToString() ?? $"#{cap.LoserPeerId}";

                if (cap.WinnerPeerId == myPeerId)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.F("'{0}' esir alındı ve tutsak listenize eklendi!",
                              "'{0}' has been taken prisoner and added to your party!", loserName),
                        Colors.Green));
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "PvpPrisonerCaptured" });
                }
                else if (cap.LoserPeerId == myPeerId)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.F("'{0}' tarafından esir alındınız!",
                              "You were taken prisoner by '{0}'!", winnerName),
                        Colors.Red));
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "PvpCapturedAsPrisoner" });
                }

                TaleWorlds.Library.Debug.Print(
                    $"[Calradia-Connected PvP] ApplyPendingPvpCapture: Winner={cap.WinnerPeerId}({winnerName}), Loser={cap.LoserPeerId}({loserName}), IsPrisonerNow={losingHero.IsPrisoner}");
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ApplyPendingPvpCapture error: {ex.Message}");
            }
        }

        public void TickPendingPvpLaunch()
        {
            if (!ModContext.IsServerRunning && ModContext.IsClientConnected &&
                (_pendingPostPvpEncounterClose || _pendingPvpCapture != null) && !_hasPendingPvpLaunch &&
                TaleWorlds.MountAndBlade.Mission.Current == null &&
                GameStateManager.Current?.ActiveState is MapState postMapState)
            {
                bool closedSomething = false;
                if (PlayerEncounter.Current != null)
                {
                    var encMobile = PlayerEncounter.EncounteredParty?.MobileParty;
                    if (encMobile == null || NetworkObjectRegistry.IsPlayerNetworkParty(encMobile))
                    {
                        try { PlayerEncounter.Finish(true); } catch { }
                        if (encMobile != null && _pendingPvpCapture == null)
                        {
                            DisengageFromPartyOnMap(encMobile);
                        }
                        closedSomething = true;
                    }
                }

                if (postMapState.AtMenu)
                {
                    string menuId = postMapState.MenuContext?.GameMenu?.StringId ?? string.Empty;
                    if (string.Equals(menuId, "encounter", StringComparison.Ordinal) ||
                        string.Equals(menuId, "encounter_meeting", StringComparison.Ordinal))
                    {
                        try { postMapState.ExitMenuMode(); } catch { }
                        closedSomething = true;
                    }
                }

                if (_pendingPostPvpEncounterClose && !closedSomething && DateTime.UtcNow > _postPvpEncounterCloseUntilUtc)
                {
                    _pendingPostPvpEncounterClose = false;
                }

                if (_pendingPvpCapture != null)
                {
                    if ((DateTime.UtcNow - _pendingPvpCapture.QueuedUtc).TotalSeconds > 12.0)
                    {
                        _pendingPvpCapture = null;
                    }
                    else if (!closedSomething && PlayerEncounter.Current == null)
                    {
                        _pendingPvpCapture.FramesWaited++;
                        if (_pendingPvpCapture.FramesWaited >= 2)
                        {
                            ApplyPendingPvpCapture(_pendingPvpCapture);
                            _pendingPvpCapture = null;
                        }
                    }
                }
            }

            if (!_hasPendingPvpLaunch || ModContext.IsServerRunning || !ModContext.IsClientConnected)
            {
                return;
            }

            bool timedOut = (DateTime.UtcNow - _pendingLaunchQueuedUtc).TotalSeconds > 2.5;
            var cm = GameCampaign.Current?.ConversationManager;
            var activeMission = TaleWorlds.MountAndBlade.Mission.Current;
            var activeState = GameStateManager.Current?.ActiveState;

            if (!timedOut)
            {
                if (activeMission != null || (cm != null && cm.IsConversationInProgress) || activeState is not MapState ms || ms.AtMenu)
                {
                    TearDownOpenUiAndMissionsForPvp();
                    return;
                }

                _pendingLaunchFramesWaited++;
                if (_pendingLaunchFramesWaited < 2)
                {
                    return;
                }
            }

            _hasPendingPvpLaunch = false;
            var atkParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(_pendingLaunchAttackerPeerId)
                           ?? NetworkObjectRegistry.Instance.ResolveParty(_pendingLaunchAttackerPartyId);
            var defParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(_pendingLaunchDefenderPeerId)
                           ?? NetworkObjectRegistry.Instance.ResolveParty(_pendingLaunchDefenderPartyId);
            if (atkParty == null || defParty == null || !atkParty.IsActive || !defParty.IsActive)
            {
                ClearLocalPvpState();
                return;
            }

            LaunchSynchronizedPvpMission(atkParty, defParty, _pendingLaunchIAmAttacker, _pendingLaunchIsNaval, _pendingLaunchWindSeed);
        }

        private void LaunchSynchronizedPvpMission(MobileParty atkParty, MobileParty defParty, bool iAmAttacker, bool isNavalBattle = false, int windSeed = 0)
        {
            IsLaunchingSynchronizedPvpMission = true;
            try
            {
                TearDownOpenUiAndMissionsForPvp();

                // Ensure latest cached rosters are applied before creating MapEvent, then pull BOTH parties out of settlement/wounded state
                if (NetworkObjectRegistry.TryExtractPeerId(atkParty.StringId, out int atkPeerId) && atkPeerId > 0)
                {
                    RosterSyncService.Instance.ApplyCachedRosterIfExists(atkPeerId, atkParty);
                }
                if (NetworkObjectRegistry.TryExtractPeerId(defParty.StringId, out int defPeerId) && defPeerId > 0)
                {
                    RosterSyncService.Instance.ApplyCachedRosterIfExists(defPeerId, defParty);
                }
                EnsureCombatantReadyForPvp(atkParty);
                EnsureCombatantReadyForPvp(defParty);
                EnsureFactionsAtWar(atkParty.MapFaction, defParty.MapFaction);

                atkParty.SetMoveModeHold();
                defParty.SetMoveModeHold();

                if (MobileParty.MainParty != null)
                {
                    var pos = MobileParty.MainParty.GetPosition2D;
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

                bool launchNaval = isNavalBattle &&
                                   NavalSyncService.IsNavalDlcActive &&
                                   ((atkParty.Ships != null && atkParty.Ships.Count > 0) ||
                                    (defParty.Ships != null && defParty.Ships.Count > 0));

                if (launchNaval)
                {
                    try
                    {
                        atkParty.IsCurrentlyAtSea = true;
                        defParty.IsCurrentlyAtSea = true;
                    }
                    catch { }
                }

                // Flush any deferred rosters and ensure remote party has latest synced troops before PlayerEncounter starts
                RosterSyncService.Instance.FlushPendingRosters();
                // Re-run PvP combatant readiness AFTER FlushPendingRosters so no cached/queued roster flush can drop or wound either commander.
                EnsureCombatantReadyForPvp(atkParty);
                EnsureCombatantReadyForPvp(defParty);

                // Anchor defender right beside attacker's position on BOTH clients so both generate the exact same mapPatch & battle scene
                if ((atkParty.GetPosition2D - defParty.GetPosition2D).Length > 1.2f)
                {
                    defParty.Position = new CampaignVec2(atkParty.GetPosition2D + new Vec2(0.35f, 0f), isOnLand: !launchNaval && !atkParty.IsCurrentlyAtSea);
                }

                if (PlayerEncounter.Current != null)
                {
                    try { PlayerEncounter.Finish(true); } catch { }
                }

                try
                {
                    if (atkParty.Party.MapEventSide != null) atkParty.Party.MapEventSide = null;
                    if (defParty.Party.MapEventSide != null) defParty.Party.MapEventSide = null;
                }
                catch { }

                PlayerEncounter.RestartPlayerEncounter(defParty.Party, atkParty.Party, forcePlayerOutFromSettlement: true, isPlayerEncounterRestartedForRaid: false);
                PlayerEncounter.StartBattle();

                try
                {
                    var mapEvent = MapEvent.PlayerMapEvent ?? PlayerEncounter.Battle;
                    if (mapEvent != null)
                    {
                        if (atkParty.Party.MapEventSide != mapEvent.AttackerSide)
                        {
                            atkParty.Party.MapEventSide = mapEvent.AttackerSide;
                        }
                        if (defParty.Party.MapEventSide != mapEvent.DefenderSide)
                        {
                            defParty.Party.MapEventSide = mapEvent.DefenderSide;
                        }
                    }

                    int atkHealthy = atkParty.MemberRoster?.TotalHealthyCount ?? 0;
                    int defHealthy = defParty.MemberRoster?.TotalHealthyCount ?? 0;
                    int involvedDef = mapEvent?.GetNumberOfInvolvedMen(BattleSideEnum.Defender) ?? 0;
                    int involvedAtk = mapEvent?.GetNumberOfInvolvedMen(BattleSideEnum.Attacker) ?? 0;
                    TaleWorlds.Library.Debug.Print(
                        $"[Calradia-Connected PvP] LaunchMission: Atk={atkParty.StringId}(Healthy={atkHealthy},Involved={involvedAtk},Side={(atkParty.Party.MapEventSide?.MissionSide.ToString() ?? "null")}), Def={defParty.StringId}(Healthy={defHealthy},Involved={involvedDef},Side={(defParty.Party.MapEventSide?.MissionSide.ToString() ?? "null")}), LocalIsAtk={LocalIsAttackerInPvp}");

                    if (mapEvent != null && (involvedDef <= 0 || involvedAtk <= 0))
                    {
                        EnsureCombatantReadyForPvp(atkParty);
                        EnsureCombatantReadyForPvp(defParty);
                        involvedDef = mapEvent.GetNumberOfInvolvedMen(BattleSideEnum.Defender);
                        involvedAtk = mapEvent.GetNumberOfInvolvedMen(BattleSideEnum.Attacker);
                        TaleWorlds.Library.Debug.Print(
                            $"[Calradia-Connected PvP] LaunchMission Re-Ensured: InvolvedDef={involvedDef}, InvolvedAtk={involvedAtk}");
                    }
                }
                catch { }

                var campaign = GameCampaign.Current;
                var mapPatch = campaign.MapSceneWrapper.GetMapPatchAtPosition(atkParty.Position);
                string battleScene = campaign.Models.SceneModel.GetBattleSceneForMapPatch(mapPatch, launchNaval);

                var rec = new MissionInitializerRecord(battleScene)
                {
                    TerrainType = (int)campaign.MapSceneWrapper.GetFaceTerrainType(atkParty.CurrentNavigationFace),
                    DamageToFriendsMultiplier = campaign.Models.DifficultyModel.GetPlayerTroopsReceivedDamageMultiplier(),
                    DamageFromPlayerToFriendsMultiplier = campaign.Models.DifficultyModel.GetPlayerTroopsReceivedDamageMultiplier(),
                    NeedsRandomTerrain = false,
                    PlayingInCampaignMode = true,
                    RandomTerrainSeed = windSeed > 0 ? windSeed : ComputeDeterministicSeed(atkParty.StringId),
                    AtmosphereOnCampaign = campaign.Models.MapWeatherModel.GetAtmosphereModel(atkParty.Position),
                    SceneHasMapPatch = true,
                    DecalAtlasGroup = 2,
                    PatchCoordinates = mapPatch.normalizedCoordinates
                };

                Vec2 dir = defParty.GetPosition2D - atkParty.GetPosition2D;
                rec.PatchEncounterDir = dir.LengthSquared > 0.0001f ? dir.Normalized() : Vec2.Forward;

                if (launchNaval && TryOpenNavalBattleMission(rec))
                {
                    PlayerEncounter.StartAttackMission();
                    MapEvent.PlayerMapEvent?.BeginWait();
                    return;
                }

                CampaignMission.OpenBattleMission(rec);
                PlayerEncounter.StartAttackMission();
                MapEvent.PlayerMapEvent?.BeginWait();
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] LaunchSynchronizedPvpMission error: {ex}");
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.F("[PvP] Muharebe başlatılamadı: {0}", "[PvP] Failed to start battle: {0}", ex.Message),
                    Colors.Red));
            }
            finally
            {
                IsLaunchingSynchronizedPvpMission = false;
            }
        }

        private static bool TryOpenNavalBattleMission(MissionInitializerRecord rec)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.Equals(asm.GetName().Name, "NavalDLC", StringComparison.OrdinalIgnoreCase)) continue;
                    var navalMissionsType = asm.GetType("NavalDLC.Missions.NavalMissions");
                    var openMethod = navalMissionsType?.GetMethod(
                        "OpenNavalBattleMission",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(MissionInitializerRecord) },
                        null);
                    if (openMethod != null)
                    {
                        openMethod.Invoke(null, new object[] { rec });
                        return true;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] TryOpenNavalBattleMission fallback to land: {ex.Message}");
            }
            return false;
        }

        public void HandleHeroExecutionPacket(HeroExecutionPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (GameCampaign.Current == null) return;

                    if (packet.ReasonStage == 3) // RespawnResetRequest on Server
                    {
                        if (!ModContext.IsServerRunning) return;
                        PlayerHeroManager.Instance.PrepareRespawnAfterDeath(packet.VictimPeerId);

                        var ack = new HeroExecutionPacket
                        {
                            VictimPeerId = packet.VictimPeerId,
                            ReasonStage = 4
                        };
                        ModContext.Server?.SendTo(packet.VictimPeerId, ack, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        return;
                    }

                    if (packet.ReasonStage == 4) // RespawnResetApproved on Client
                    {
                        if (!ModContext.IsServerRunning)
                        {
                            PlayerHeroManager.Instance.PrepareRespawnAfterDeath(packet.VictimPeerId);
                        }
                        return;
                    }

                    // ReasonStage 1 (Executed) or 2 (KilledInBattle)
                    int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
                    var victim = NetworkObjectRegistry.Instance.ResolveHero(packet.VictimHeroId)
                                 ?? NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.VictimPeerId);
                    var killer = packet.KillerPeerId > 0
                        ? NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.KillerPeerId)
                        : null;

                    bool isLocalExecutedPlayer = !ModContext.IsServerRunning &&
                                                 (packet.VictimPeerId == myPeerId || victim == Hero.MainHero);

                    if (isLocalExecutedPlayer)
                    {
                        // Do NOT call KillCharacterAction.ApplyByExecution(Hero.MainHero) on the victim's own client:
                        // Bannerlord's native SP execution destroys Clan.PlayerClan & fires GameOverState which crashes
                        // Disconnect / CharacterCreationState! Instead end captivity/encounter/menu cleanly and show our modal.
                        try
                        {
                            if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);
                        }
                        catch { }
                        try
                        {
                            if (Hero.MainHero != null && Hero.MainHero.IsPrisoner) PlayerCaptivity.EndCaptivity();
                        }
                        catch { }
                        try
                        {
                            if (GameStateManager.Current?.ActiveState is MapState ms && ms.AtMenu) ms.ExitMenuMode();
                        }
                        catch { }
                        try
                        {
                            MobileParty.MainParty?.SetMoveModeHold();
                        }
                        catch { }
                    }
                    else if (victim != null && victim.IsAlive)
                    {
                        IsSuppressingExecutionHook = true;
                        try
                        {
                            var victimParty = victim.PartyBelongedTo ?? NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.VictimPeerId);
                            if (PlayerEncounter.Current != null && killer == Hero.MainHero)
                            {
                                try { PlayerEncounter.Finish(true); } catch { }
                            }
                            KillCharacterAction.ApplyByExecution(victim, killer, false, true);
                            HideOrDestroyCapturedRemoteParty(victimParty, killer?.PartyBelongedTo);
                        }
                        finally
                        {
                            IsSuppressingExecutionHook = false;
                        }
                    }

                    string victimName = !string.IsNullOrEmpty(packet.VictimHeroName)
                        ? packet.VictimHeroName
                        : (victim?.Name?.ToString() ?? $"#{packet.VictimPeerId}");
                    string killerName = !string.IsNullOrEmpty(packet.KillerName)
                        ? packet.KillerName
                        : (killer?.Name?.ToString() ?? Loc.T("Rakip", "Executioner"));

                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.F("[İDAM] '{0}', '{1}' tarafından idam edildi ve dünyadan silindi!",
                              "[EXECUTION] '{0}' was executed by '{1}' and permanently slain!", victimName, killerName),
                        Colors.Red));

                    if (ModContext.IsServerRunning)
                    {
                        // Broadcast to all connected clients (without infinite re-echo)
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        return;
                    }

                    if (isLocalExecutedPlayer)
                    {
                        ModContext.Events.Publish(new HeroExecutionReceivedEvent { Packet = packet });
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] HandleHeroExecutionPacket error: {ex}");
                }
            });
        }
    }
}
