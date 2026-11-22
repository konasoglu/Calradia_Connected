using System;
using System.Collections.Generic;
using System.Linq;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Core;
using TaleWorlds.Library;
using GameCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Phase 4: Synchronizes Co-op Town Tournaments (shared bracket, live player-vs-player arena
    /// rounds/finals, winner & prize sync) and Co-op Bandit Hideout Raids (nearby/army ally lobby,
    /// synchronized OpenHideoutBattleMission with Friendly Fire OFF, and shared clear rewards).
    /// </summary>
    public sealed class CoopActivityService
    {
        private static CoopActivityService? _instance;
        public static CoopActivityService Instance => _instance ??= new CoopActivityService();

        public const float HIDEOUT_INVITE_RADIUS = 18.0f;

        public sealed class TournamentLobby
        {
            public string ActivityId = string.Empty;
            public string SettlementId = string.Empty;
            public string SettlementName = string.Empty;
            public int HostPeerId;
            public string HostName = string.Empty;
            public int BracketSeed;
            public bool Started;
            public byte RoundNumber = 1;
            public readonly List<int> ParticipantPeerIds = new List<int>();
            public readonly HashSet<int> EliminatedPeerIds = new HashSet<int>();
            public DateTime CreatedUtc = DateTime.UtcNow;
        }

        public sealed class HideoutLobby
        {
            public string ActivityId = string.Empty;
            public string SettlementId = string.Empty;
            public string SettlementName = string.Empty;
            public int HostPeerId;
            public string HostName = string.Empty;
            public bool Launched;
            public readonly List<int> ParticipantPeerIds = new List<int>();
            public DateTime CreatedUtc = DateTime.UtcNow;
        }

        private readonly Dictionary<string, TournamentLobby> _tournamentsBySettlement = new Dictionary<string, TournamentLobby>(StringComparer.Ordinal);
        private readonly Dictionary<string, HideoutLobby> _hideoutsBySettlement = new Dictionary<string, HideoutLobby>(StringComparer.Ordinal);
        private string _activeLocalTournamentId = string.Empty;
        private string _activeLocalHideoutId = string.Empty;

        private static bool IsCampaignReady()
        {
            var c = GameCampaign.Current;
            return c != null && c.GameStarted && c.SaveHandler?.IsSaving != true;
        }

        public TournamentLobby? GetTournamentLobbyForSettlement(Settlement? settlement)
        {
            if (settlement == null) return null;
            return _tournamentsBySettlement.TryGetValue(settlement.StringId, out var lobby) && !lobby.Started ? lobby : null;
        }

        public HideoutLobby? GetHideoutLobbyForSettlement(Settlement? settlement)
        {
            if (settlement == null) return null;
            return _hideoutsBySettlement.TryGetValue(settlement.StringId, out var lobby) && !lobby.Launched ? lobby : null;
        }

        #region Co-op Tournament Client Actions

        public void OpenOrManageCoopTournament(Settlement? settlement)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;
            if (settlement == null || !settlement.IsTown) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (myPeerId <= 0) return;

            var existing = GetTournamentLobbyForSettlement(settlement);
            if (existing == null)
            {
                string actId = $"tour_{settlement.StringId}_{myPeerId}_{DateTime.UtcNow.Ticks & 0xFFFF}";
                string myName = Hero.MainHero?.Name?.ToString() ?? $"Lord #{myPeerId}";

                ModContext.Client?.Send(new CoopActivitySyncPacket
                {
                    ActionType = 1, // TournamentLobbyOpen
                    ActivityId = actId,
                    SettlementId = settlement.StringId,
                    SettlementName = settlement.Name?.ToString() ?? settlement.StringId,
                    HostPeerId = myPeerId,
                    ActorPeerId = myPeerId,
                    HostName = myName,
                    ActorName = myName,
                    ParticipantPeerIds = new List<int> { myPeerId }
                }, DeliveryMethod.ReliableOrdered);
                return;
            }

            if (existing.HostPeerId == myPeerId)
            {
                // Host can start the tournament now or cancel
                InformationManager.ShowInquiry(new InquiryData(
                    titleText: Loc.T("Ortak Turnuva", "Co-op Tournament"),
                    text: Loc.F("{0} şehrindeki ortak turnuvada şu an {1} oyuncu hazır. Başlatmak ister misin?",
                                "{1} player(s) are ready in the Co-op Tournament at {0}. Start now?",
                                existing.SettlementName, existing.ParticipantPeerIds.Count),
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: Loc.T("Turnuvayı Başlat", "Start Co-op Tournament Now"),
                    negativeText: Loc.T("Vazgeç", "Cancel"),
                    affirmativeAction: () =>
                    {
                        ModContext.Client?.Send(new CoopActivitySyncPacket
                        {
                            ActionType = 3, // TournamentStart
                            ActivityId = existing.ActivityId,
                            SettlementId = existing.SettlementId,
                            SettlementName = existing.SettlementName,
                            HostPeerId = myPeerId,
                            ActorPeerId = myPeerId
                        }, DeliveryMethod.ReliableOrdered);
                    },
                    negativeAction: null));
            }
            else if (!existing.ParticipantPeerIds.Contains(myPeerId))
            {
                ModContext.Client?.Send(new CoopActivitySyncPacket
                {
                    ActionType = 2, // TournamentJoin
                    ActivityId = existing.ActivityId,
                    SettlementId = existing.SettlementId,
                    SettlementName = existing.SettlementName,
                    HostPeerId = existing.HostPeerId,
                    ActorPeerId = myPeerId,
                    ActorName = Hero.MainHero?.Name?.ToString() ?? $"Lord #{myPeerId}"
                }, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Calradia-Connected] {Loc.F("Ortak turnuva lobisindesin ({0} oyuncu). Kurucu başlattığında arena açılacak.", "You are in the Co-op Tournament lobby ({0} players). Arena opens when host starts.", existing.ParticipantPeerIds.Count)}",
                    Colors.Cyan));
            }
        }

        #endregion

        #region Co-op Hideout Raid Client Actions

        public void OpenOrManageCoopHideoutRaid(Settlement? hideoutSettlement)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;
            if (hideoutSettlement == null || !hideoutSettlement.IsHideout) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (myPeerId <= 0) return;

            var existing = GetHideoutLobbyForSettlement(hideoutSettlement);
            if (existing == null)
            {
                string actId = $"hide_{hideoutSettlement.StringId}_{myPeerId}_{DateTime.UtcNow.Ticks & 0xFFFF}";
                string myName = Hero.MainHero?.Name?.ToString() ?? $"Lord #{myPeerId}";

                ModContext.Client?.Send(new CoopActivitySyncPacket
                {
                    ActionType = 10, // HideoutLobbyOpen
                    ActivityId = actId,
                    SettlementId = hideoutSettlement.StringId,
                    SettlementName = hideoutSettlement.Name?.ToString() ?? hideoutSettlement.StringId,
                    HostPeerId = myPeerId,
                    ActorPeerId = myPeerId,
                    HostName = myName,
                    ActorName = myName,
                    ParticipantPeerIds = new List<int> { myPeerId }
                }, DeliveryMethod.ReliableOrdered);
                return;
            }

            if (existing.HostPeerId == myPeerId)
            {
                InformationManager.ShowInquiry(new InquiryData(
                    titleText: Loc.T("Ortak Sığınak Baskını", "Co-op Hideout Raid"),
                    text: Loc.F("{0} sığınağına baskın için {1} müttefik hazır (Dost Ateşi: KAPALI). Baskını başlat?",
                                "{1} ally/allies ready to raid {0} (Friendly Fire: OFF). Launch raid now?",
                                existing.SettlementName, existing.ParticipantPeerIds.Count),
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: Loc.T("Baskını Şimdi Başlat", "Launch Hideout Raid Now"),
                    negativeText: Loc.T("Vazgeç", "Cancel"),
                    affirmativeAction: () =>
                    {
                        ModContext.Client?.Send(new CoopActivitySyncPacket
                        {
                            ActionType = 12, // HideoutLaunch
                            ActivityId = existing.ActivityId,
                            SettlementId = existing.SettlementId,
                            SettlementName = existing.SettlementName,
                            HostPeerId = myPeerId,
                            ActorPeerId = myPeerId
                        }, DeliveryMethod.ReliableOrdered);
                    },
                    negativeAction: null));
            }
            else if (!existing.ParticipantPeerIds.Contains(myPeerId))
            {
                JoinHideoutLobby(existing);
            }
        }

        private void JoinHideoutLobby(HideoutLobby lobby)
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (myPeerId <= 0) return;

            ModContext.Client?.Send(new CoopActivitySyncPacket
            {
                ActionType = 11, // HideoutJoin
                ActivityId = lobby.ActivityId,
                SettlementId = lobby.SettlementId,
                SettlementName = lobby.SettlementName,
                HostPeerId = lobby.HostPeerId,
                ActorPeerId = myPeerId,
                ActorName = Hero.MainHero?.Name?.ToString() ?? $"Lord #{myPeerId}"
            }, DeliveryMethod.ReliableOrdered);
        }

        public void OnLocalHideoutBattleCompleted(
            BattleSideEnum winnerSide,
            HideoutEventComponent? hideoutEventComponent,
            HideoutEventComponent.HideoutBattleEndState endState)
        {
            try
            {
                if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
                if (string.IsNullOrEmpty(_activeLocalHideoutId)) return;

                string actId = _activeLocalHideoutId;
                _activeLocalHideoutId = string.Empty;

                // Clear mission Co-op state
                ModContext.Events.Publish(new CoopBattleSessionUpdatedEvent
                {
                    IsActive = false,
                    SessionId = string.Empty,
                    IsHost = false,
                    HostPeerId = 0,
                    FriendlyFireEnabled = false,
                    AlliedPeerIds = Array.Empty<int>()
                });

                var lobby = _hideoutsBySettlement.Values.FirstOrDefault(h => string.Equals(h.ActivityId, actId, StringComparison.Ordinal));
                int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
                if (lobby != null && lobby.HostPeerId == myPeerId)
                {
                    bool cleared = winnerSide == BattleSideEnum.Attacker || endState == HideoutEventComponent.HideoutBattleEndState.Victory;
                    ModContext.Client?.Send(new CoopActivitySyncPacket
                    {
                        ActionType = 13, // HideoutCompleted
                        ActivityId = lobby.ActivityId,
                        SettlementId = lobby.SettlementId,
                        SettlementName = lobby.SettlementName,
                        HostPeerId = myPeerId,
                        ActorPeerId = myPeerId,
                        ActorWon = cleared,
                        RewardGold = cleared ? 500 : 0,
                        RewardRenown = cleared ? 10 : 0
                    }, DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] CoopActivityService.OnLocalHideoutBattleCompleted hatası: {ex.Message}");
            }
        }

        public void HandlePeerDisconnected(int peerId)
        {
            if (peerId <= 0) return;
            ModContext.Dispatcher.Enqueue(() =>
            {
                foreach (var kvp in _tournamentsBySettlement.ToList())
                {
                    kvp.Value.ParticipantPeerIds.Remove(peerId);
                    if (kvp.Value.HostPeerId == peerId || kvp.Value.ParticipantPeerIds.Count == 0)
                    {
                        _tournamentsBySettlement.Remove(kvp.Key);
                    }
                }
                foreach (var kvp in _hideoutsBySettlement.ToList())
                {
                    kvp.Value.ParticipantPeerIds.Remove(peerId);
                    if (kvp.Value.HostPeerId == peerId || kvp.Value.ParticipantPeerIds.Count == 0)
                    {
                        _hideoutsBySettlement.Remove(kvp.Key);
                    }
                }
            });
        }

        #endregion

        #region Inbound Packet Handling

        public void HandlePacket(CoopActivitySyncPacket packet)
        {
            if (packet == null) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (!IsCampaignReady()) return;
                    if (ModContext.IsServerRunning)
                    {
                        HandlePacketOnServer(packet);
                    }
                    else
                    {
                        HandlePacketOnClient(packet);
                    }
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] CoopActivityService.HandlePacket({packet.ActionType}) hatası: {ex.Message}");
                }
            });
        }

        private void HandlePacketOnServer(CoopActivitySyncPacket packet)
        {
            switch (packet.ActionType)
            {
                case 1: // TournamentLobbyOpen
                {
                    if (string.IsNullOrEmpty(packet.SettlementId) || packet.HostPeerId <= 0) return;
                    var lobby = new TournamentLobby
                    {
                        ActivityId = packet.ActivityId,
                        SettlementId = packet.SettlementId,
                        SettlementName = packet.SettlementName,
                        HostPeerId = packet.HostPeerId,
                        HostName = packet.HostName,
                        BracketSeed = (Environment.TickCount ^ packet.HostPeerId) & 0x7FFFFFFF
                    };
                    lobby.ParticipantPeerIds.Add(packet.HostPeerId);
                    _tournamentsBySettlement[lobby.SettlementId] = lobby;

                    packet.BracketSeed = lobby.BracketSeed;
                    packet.ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds);
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 2: // TournamentJoin
                {
                    if (!_tournamentsBySettlement.TryGetValue(packet.SettlementId, out var lobby) || lobby.Started) return;
                    if (packet.ActorPeerId > 0 && !lobby.ParticipantPeerIds.Contains(packet.ActorPeerId))
                    {
                        lobby.ParticipantPeerIds.Add(packet.ActorPeerId);
                    }
                    packet.ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds);
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 3: // TournamentStart
                {
                    if (!_tournamentsBySettlement.TryGetValue(packet.SettlementId, out var lobby) || lobby.Started) return;
                    if (lobby.HostPeerId != packet.ActorPeerId) return;
                    lobby.Started = true;
                    lobby.RoundNumber = 1;

                    // Broadcast TournamentStart with deterministic seed
                    packet.BracketSeed = lobby.BracketSeed;
                    packet.ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds);
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);

                    // If 2+ real players joined, pair the first two for a live arena tournament match!
                    if (lobby.ParticipantPeerIds.Count >= 2)
                    {
                        ScheduleNextTournamentMatchOnServer(lobby);
                    }
                    else
                    {
                        // Single player in lobby -> award victory after solo arena run or immediate finish
                        FinalizeTournamentOnServer(lobby, lobby.HostPeerId);
                    }
                    break;
                }
                case 5: // TournamentMatchResult
                {
                    var lobby = _tournamentsBySettlement.Values.FirstOrDefault(t => string.Equals(t.ActivityId, packet.ActivityId, StringComparison.Ordinal));
                    if (lobby == null) return;

                    int winnerPeerId = packet.ActorWon ? packet.ActorPeerId : packet.OpponentPeerId;
                    int loserPeerId = packet.ActorWon ? packet.OpponentPeerId : packet.ActorPeerId;
                    if (loserPeerId > 0) lobby.EliminatedPeerIds.Add(loserPeerId);

                    var remaining = lobby.ParticipantPeerIds.Where(p => !lobby.EliminatedPeerIds.Contains(p)).ToList();
                    if (remaining.Count <= 1)
                    {
                        int champion = remaining.Count == 1 ? remaining[0] : winnerPeerId;
                        FinalizeTournamentOnServer(lobby, champion);
                    }
                    else
                    {
                        lobby.RoundNumber++;
                        ScheduleNextTournamentMatchOnServer(lobby);
                    }
                    break;
                }
                case 10: // HideoutLobbyOpen
                {
                    if (string.IsNullOrEmpty(packet.SettlementId) || packet.HostPeerId <= 0) return;
                    var lobby = new HideoutLobby
                    {
                        ActivityId = packet.ActivityId,
                        SettlementId = packet.SettlementId,
                        SettlementName = packet.SettlementName,
                        HostPeerId = packet.HostPeerId,
                        HostName = packet.HostName
                    };
                    lobby.ParticipantPeerIds.Add(packet.HostPeerId);
                    _hideoutsBySettlement[lobby.SettlementId] = lobby;

                    packet.ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds);
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 11: // HideoutJoin
                {
                    if (!_hideoutsBySettlement.TryGetValue(packet.SettlementId, out var lobby) || lobby.Launched) return;
                    if (packet.ActorPeerId > 0 && !lobby.ParticipantPeerIds.Contains(packet.ActorPeerId))
                    {
                        lobby.ParticipantPeerIds.Add(packet.ActorPeerId);
                    }
                    packet.ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds);
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 12: // HideoutLaunch
                {
                    if (!_hideoutsBySettlement.TryGetValue(packet.SettlementId, out var lobby) || lobby.Launched) return;
                    if (lobby.HostPeerId != packet.ActorPeerId) return;
                    lobby.Launched = true;

                    packet.ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds);
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 13: // HideoutCompleted
                {
                    var lobby = _hideoutsBySettlement.Values.FirstOrDefault(h => string.Equals(h.ActivityId, packet.ActivityId, StringComparison.Ordinal));
                    if (lobby != null)
                    {
                        packet.ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds);
                        _hideoutsBySettlement.Remove(lobby.SettlementId);
                    }
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 20: // CancelActivity
                {
                    _tournamentsBySettlement.Remove(packet.SettlementId);
                    _hideoutsBySettlement.Remove(packet.SettlementId);
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
            }
        }

        private void ScheduleNextTournamentMatchOnServer(TournamentLobby lobby)
        {
            var remaining = lobby.ParticipantPeerIds.Where(p => !lobby.EliminatedPeerIds.Contains(p)).ToList();
            if (remaining.Count < 2) return;

            int peerA = remaining[0];
            int peerB = remaining[1];
            string nameA = NetworkObjectRegistry.Instance.ResolvePlayerHero(peerA)?.Name?.ToString() ?? $"Lord #{peerA}";
            string nameB = NetworkObjectRegistry.Instance.ResolvePlayerHero(peerB)?.Name?.ToString() ?? $"Lord #{peerB}";

            ModContext.Server?.Broadcast(new CoopActivitySyncPacket
            {
                ActionType = 4, // TournamentPvpMatchStart
                ActivityId = lobby.ActivityId,
                SettlementId = lobby.SettlementId,
                SettlementName = lobby.SettlementName,
                HostPeerId = peerA,
                ActorPeerId = peerA,
                OpponentPeerId = peerB,
                HostName = nameA,
                ActorName = nameB,
                BracketSeed = lobby.BracketSeed,
                RoundNumber = lobby.RoundNumber,
                ParticipantPeerIds = new List<int>(remaining)
            }, DeliveryMethod.ReliableOrdered);
        }

        private void FinalizeTournamentOnServer(TournamentLobby lobby, int championPeerId)
        {
            _tournamentsBySettlement.Remove(lobby.SettlementId);

            var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(lobby.SettlementId);
            var championHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(championPeerId);
            string championName = championHero?.Name?.ToString() ?? $"Lord #{championPeerId}";

            string prizeId = string.Empty;
            string prizeName = string.Empty;
            try
            {
                var tGame = settlement?.Town != null
                    ? GameCampaign.Current?.TournamentManager?.GetTournamentGame(settlement.Town)
                    : null;
                if (tGame?.Prize != null)
                {
                    prizeId = tGame.Prize.StringId ?? string.Empty;
                    prizeName = tGame.Prize.Name?.ToString() ?? string.Empty;
                }
                if (tGame != null && championHero != null)
                {
                    GameCampaign.Current?.TournamentManager?.GivePrizeToWinner(tGame, championHero, isPlayerParticipated: false);
                }
            }
            catch { }

            ModContext.Server?.Broadcast(new CoopActivitySyncPacket
            {
                ActionType = 6, // TournamentFinished
                ActivityId = lobby.ActivityId,
                SettlementId = lobby.SettlementId,
                SettlementName = lobby.SettlementName,
                HostPeerId = lobby.HostPeerId,
                ActorPeerId = championPeerId,
                ActorName = championName,
                PrizeItemId = prizeId,
                PrizeName = prizeName,
                RewardGold = 750,
                RewardRenown = 15,
                ParticipantPeerIds = new List<int>(lobby.ParticipantPeerIds)
            }, DeliveryMethod.ReliableOrdered);
        }

        private void HandlePacketOnClient(CoopActivitySyncPacket packet)
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;

            switch (packet.ActionType)
            {
                case 1: // TournamentLobbyOpen
                {
                    var lobby = new TournamentLobby
                    {
                        ActivityId = packet.ActivityId,
                        SettlementId = packet.SettlementId,
                        SettlementName = packet.SettlementName,
                        HostPeerId = packet.HostPeerId,
                        HostName = packet.HostName,
                        BracketSeed = packet.BracketSeed
                    };
                    if (packet.ParticipantPeerIds != null) lobby.ParticipantPeerIds.AddRange(packet.ParticipantPeerIds);
                    _tournamentsBySettlement[lobby.SettlementId] = lobby;

                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Calradia-Connected] {Loc.F("{0}, {1} şehrinde ortak turnuva açtı! Şehir menüsünden katılın.", "{0} opened a Co-op Tournament in {1}! Join from the town menu.", lobby.HostName, lobby.SettlementName)}",
                        Colors.Yellow));
                    break;
                }
                case 2: // TournamentJoin
                {
                    if (_tournamentsBySettlement.TryGetValue(packet.SettlementId, out var lobby))
                    {
                        lobby.ParticipantPeerIds.Clear();
                        if (packet.ParticipantPeerIds != null) lobby.ParticipantPeerIds.AddRange(packet.ParticipantPeerIds);
                        if (lobby.ParticipantPeerIds.Contains(myPeerId))
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                $"[Calradia-Connected] {Loc.F("{0} ortak turnuvaya katıldı! ({1} oyuncu)", "{0} joined the Co-op Tournament! ({1} players)", packet.ActorName, lobby.ParticipantPeerIds.Count)}",
                                Colors.Green));
                        }
                    }
                    break;
                }
                case 4: // TournamentPvpMatchStart
                {
                    if (packet.ActorPeerId != myPeerId && packet.OpponentPeerId != myPeerId) return;

                    _activeLocalTournamentId = packet.ActivityId;
                    int oppPeerId = packet.ActorPeerId == myPeerId ? packet.OpponentPeerId : packet.ActorPeerId;
                    string oppName = packet.ActorPeerId == myPeerId ? packet.ActorName : packet.HostName;

                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Calradia-Connected] {Loc.F("Ortak Turnuva Finali: {0} vs {1}!", "Co-op Tournament Finale: {0} vs {1}!", Hero.MainHero?.Name?.ToString() ?? "Sen", oppName)}",
                        Colors.Magenta));

                    LaunchLiveTournamentArenaMatch(packet, oppPeerId);
                    break;
                }
                case 6: // TournamentFinished
                {
                    _tournamentsBySettlement.Remove(packet.SettlementId);
                    if (string.Equals(_activeLocalTournamentId, packet.ActivityId, StringComparison.Ordinal))
                    {
                        _activeLocalTournamentId = string.Empty;
                    }

                    if (packet.ActorPeerId == myPeerId && Hero.MainHero != null)
                    {
                        if (packet.RewardGold > 0) Hero.MainHero.Gold += packet.RewardGold;
                        if (packet.RewardRenown > 0) Clan.PlayerClan?.AddRenown(packet.RewardRenown);
                        if (!string.IsNullOrEmpty(packet.PrizeItemId))
                        {
                            try
                            {
                                var prizeItem = GameCampaign.Current?.ObjectManager?.GetObject<ItemObject>(packet.PrizeItemId);
                                if (prizeItem != null)
                                {
                                    MobileParty.MainParty?.ItemRoster?.AddToCounts(prizeItem, 1);
                                }
                            }
                            catch { }
                        }
                    }

                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Calradia-Connected] {Loc.F("{0}, {1} şehrindeki ortak turnuvayı kazandı! (+{2} Altın, +{3} Nam)", "{0} won the Co-op Tournament in {1}! (+{2} Gold, +{3} Renown)", packet.ActorName, packet.SettlementName, packet.RewardGold, packet.RewardRenown)}",
                        Colors.Yellow));
                    break;
                }
                case 10: // HideoutLobbyOpen
                {
                    var lobby = new HideoutLobby
                    {
                        ActivityId = packet.ActivityId,
                        SettlementId = packet.SettlementId,
                        SettlementName = packet.SettlementName,
                        HostPeerId = packet.HostPeerId,
                        HostName = packet.HostName
                    };
                    if (packet.ParticipantPeerIds != null) lobby.ParticipantPeerIds.AddRange(packet.ParticipantPeerIds);
                    _hideoutsBySettlement[lobby.SettlementId] = lobby;

                    if (packet.HostPeerId == myPeerId)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[Calradia-Connected] {Loc.T("Ortak sığınak baskını lobisi açıldı! Yakındaki dostlara davet gönderildi.", "Co-op Hideout Raid lobby opened! Nearby allies have been invited.")}",
                            Colors.Cyan));
                        return;
                    }

                    // Prompt nearby or same-army allies to join the hideout raid
                    if (TaleWorlds.MountAndBlade.Mission.Current != null) return;
                    var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(lobby.SettlementId);
                    bool sameArmy = PlayerArmyService.Instance.ArePeersInSameArmy(myPeerId, lobby.HostPeerId);
                    float dist = (settlement != null && MobileParty.MainParty != null)
                        ? (MobileParty.MainParty.GetPosition2D - settlement.GetPosition2D).Length
                        : 999f;

                    if (sameArmy || dist <= HIDEOUT_INVITE_RADIUS)
                    {
                        InformationManager.ShowInquiry(new InquiryData(
                            titleText: Loc.T("Ortak Sığınak Baskını", "Co-op Hideout Raid"),
                            text: Loc.F("{0}, {1} sığınağına ortak baskın düzenliyor! Katılmak ister misin?",
                                        "{0} is organizing a Co-op Hideout Raid at {1}! Join them?",
                                        lobby.HostName, lobby.SettlementName),
                            isAffirmativeOptionShown: true,
                            isNegativeOptionShown: true,
                            affirmativeText: Loc.T("Baskına Katıl", "Join Raid"),
                            negativeText: Loc.T("Reddet", "Decline"),
                            affirmativeAction: () => JoinHideoutLobby(lobby),
                            negativeAction: null));
                    }
                    break;
                }
                case 11: // HideoutJoin
                {
                    if (_hideoutsBySettlement.TryGetValue(packet.SettlementId, out var lobby))
                    {
                        lobby.ParticipantPeerIds.Clear();
                        if (packet.ParticipantPeerIds != null) lobby.ParticipantPeerIds.AddRange(packet.ParticipantPeerIds);
                        if (lobby.ParticipantPeerIds.Contains(myPeerId))
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                $"[Calradia-Connected] {Loc.F("{0} ortak sığınak baskınına katıldı! ({1} müttefik)", "{0} joined the Co-op Hideout Raid! ({1} allies)", packet.ActorName, lobby.ParticipantPeerIds.Count)}",
                                Colors.Green));
                        }
                    }
                    break;
                }
                case 12: // HideoutLaunch
                {
                    if (!_hideoutsBySettlement.TryGetValue(packet.SettlementId, out var lobby))
                    {
                        lobby = new HideoutLobby
                        {
                            ActivityId = packet.ActivityId,
                            SettlementId = packet.SettlementId,
                            SettlementName = packet.SettlementName,
                            HostPeerId = packet.HostPeerId,
                            HostName = packet.HostName,
                            Launched = true
                        };
                    }
                    lobby.Launched = true;
                    lobby.ParticipantPeerIds.Clear();
                    if (packet.ParticipantPeerIds != null) lobby.ParticipantPeerIds.AddRange(packet.ParticipantPeerIds);

                    if (!lobby.ParticipantPeerIds.Contains(myPeerId)) return;

                    _activeLocalHideoutId = lobby.ActivityId;

                    // Configure Co-op session with Friendly Fire STRICTLY OFF before launching mission
                    ModContext.Events.Publish(new CoopBattleSessionUpdatedEvent
                    {
                        IsActive = true,
                        SessionId = lobby.ActivityId,
                        IsHost = lobby.HostPeerId == myPeerId,
                        HostPeerId = lobby.HostPeerId,
                        FriendlyFireEnabled = false,
                        AlliedPeerIds = lobby.ParticipantPeerIds.ToArray()
                    });

                    LaunchCoopHideoutMission(lobby);
                    break;
                }
                case 13: // HideoutCompleted
                {
                    _hideoutsBySettlement.Remove(packet.SettlementId);
                    if (string.Equals(_activeLocalHideoutId, packet.ActivityId, StringComparison.Ordinal))
                    {
                        _activeLocalHideoutId = string.Empty;
                    }

                    bool iParticipated = packet.ParticipantPeerIds != null && packet.ParticipantPeerIds.Contains(myPeerId);
                    if (iParticipated && packet.ActorWon && Hero.MainHero != null)
                    {
                        if (packet.RewardGold > 0) Hero.MainHero.Gold += packet.RewardGold;
                        if (packet.RewardRenown > 0) Clan.PlayerClan?.AddRenown(packet.RewardRenown);

                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[Calradia-Connected] {Loc.F("Ortak sığınak temizlendi! Paylaşılan ödül: +{0} Altın, +{1} Nam.", "Co-op Hideout Cleared! Shared reward: +{0} Gold, +{1} Renown.", packet.RewardGold, packet.RewardRenown)}",
                            Colors.Green));
                    }
                    break;
                }
                case 20: // CancelActivity
                {
                    _tournamentsBySettlement.Remove(packet.SettlementId);
                    _hideoutsBySettlement.Remove(packet.SettlementId);
                    break;
                }
            }
        }

        #endregion

        #region Mission Launch Helpers

        private void LaunchLiveTournamentArenaMatch(CoopActivitySyncPacket packet, int opponentPeerId)
        {
            try
            {
                var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId)
                                 ?? Settlement.CurrentSettlement
                                 ?? Settlement.All?.FirstOrDefault(s => s != null && s.IsTown);
                if (settlement == null) return;

                var opponentHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(opponentPeerId);
                CharacterObject? opponentChar = opponentHero?.CharacterObject;
                if (opponentChar == null) return;

                Location? arenaLoc = settlement.LocationComplex?.GetLocationWithId("arena");
                string arenaScene = arenaLoc?.GetSceneName(1) ?? "arena_empire_a";
                int myPeerId = ModContext.Client?.LocalPeerId ?? 0;

                // Ensure friendly fire is NOT blocking enemy arena opponent (arena match is 1v1 PvP)
                ModContext.Events.Publish(new CoopBattleSessionUpdatedEvent
                {
                    IsActive = false,
                    SessionId = string.Empty,
                    IsHost = false,
                    HostPeerId = 0,
                    FriendlyFireEnabled = true,
                    AlliedPeerIds = Array.Empty<int>()
                });

                CampaignMission.OpenArenaDuelMission(
                    scene: arenaScene,
                    location: arenaLoc,
                    talkToChar: opponentChar,
                    requireCivilianEquipment: false,
                    spawnBothSidesWithHorse: false,
                    onDuelEnd: winnerChar =>
                    {
                        bool iWon = winnerChar == CharacterObject.PlayerCharacter;
                        ModContext.Client?.Send(new CoopActivitySyncPacket
                        {
                            ActionType = 5, // TournamentMatchResult
                            ActivityId = packet.ActivityId,
                            SettlementId = packet.SettlementId,
                            SettlementName = packet.SettlementName,
                            HostPeerId = packet.HostPeerId,
                            ActorPeerId = myPeerId,
                            OpponentPeerId = opponentPeerId,
                            ActorWon = iWon,
                            RoundNumber = packet.RoundNumber
                        }, DeliveryMethod.ReliableOrdered);
                    },
                    customAgentHealth: 100f);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] LaunchLiveTournamentArenaMatch hatası: {ex.Message}");
            }
        }

        private void LaunchCoopHideoutMission(HideoutLobby lobby)
        {
            try
            {
                var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(lobby.SettlementId);
                if (settlement == null || !settlement.IsHideout) return;

                var mainParty = MobileParty.MainParty;
                if (mainParty != null)
                {
                    if (mainParty.CurrentSettlement != null && mainParty.CurrentSettlement != settlement)
                    {
                        try { LeaveSettlementAction.ApplyForParty(mainParty); } catch { }
                    }
                    try
                    {
                        mainParty.Position = settlement.GatePosition.IsValid()
                            ? settlement.GatePosition
                            : new CampaignVec2(settlement.GetPosition2D, isOnLand: true);
                        mainParty.SetMoveModeHold();
                    }
                    catch { }
                }

                if (PlayerEncounter.Current == null && settlement.Party != null)
                {
                    PlayerEncounter.RestartPlayerEncounter(settlement.Party, PartyBase.MainParty, forcePlayerOutFromSettlement: true);
                }

                var selectedTroops = new FlattenedTroopRoster(12);
                if (CharacterObject.PlayerCharacter != null)
                {
                    selectedTroops.Add(CharacterObject.PlayerCharacter, isWounded: false, xp: 0);
                }
                if (mainParty?.MemberRoster != null)
                {
                    int added = 0;
                    foreach (var elem in mainParty.MemberRoster.GetTroopRoster())
                    {
                        if (elem.Character == null || elem.Character.IsPlayerCharacter) continue;
                        int healthy = elem.Number - elem.WoundedNumber;
                        for (int i = 0; i < healthy && added < 9; i++)
                        {
                            selectedTroops.Add(elem.Character, isWounded: false, xp: 0);
                            added++;
                        }
                        if (added >= 9) break;
                    }
                }

                string sceneName = settlement.LocationComplex?.GetLocationWithId("hideout_center")?.GetSceneName(1)
                                   ?? "hideout_forest_001";

                PlayerEncounter.StartBattle();
                CampaignMission.OpenHideoutBattleMission(sceneName, selectedTroops, isTutorial: false);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] LaunchCoopHideoutMission hatası: {ex.Message}");
            }
        }

        #endregion
    }
}
