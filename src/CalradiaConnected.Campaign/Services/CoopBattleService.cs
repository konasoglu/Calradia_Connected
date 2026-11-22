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
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using GameCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Phase 3: Coordinates Co-op shared battles against AI lords, bandits, and sieges.
    /// Nearby allies and Player Army members receive an instant "Join Battle" prompt,
    /// join the same live combat mission beside the host with Friendly Fire STRICTLY OFF,
    /// and share post-battle gold/renown rewards.
    /// </summary>
    public sealed class CoopBattleService
    {
        private static CoopBattleService? _instance;
        public static CoopBattleService Instance => _instance ??= new CoopBattleService();

        public const float COOP_JOIN_RADIUS = 18.0f;

        public sealed class CoopBattleSession
        {
            public string SessionId = string.Empty;
            public int HostPeerId;
            public string HostName = string.Empty;
            public byte BattleKind; // 0 = Field, 1 = Siege, 2 = Hideout
            public string TargetPartyId = string.Empty;
            public string TargetPartyName = string.Empty;
            public string SettlementId = string.Empty;
            public string SceneName = string.Empty;
            public float MapPosX;
            public float MapPosY;
            public bool FriendlyFireEnabled = false; // Always OFF per user requirement
            public readonly HashSet<int> AlliedPeerIds = new HashSet<int>();
            public DateTime CreatedUtc = DateTime.UtcNow;
        }

        private readonly Dictionary<string, CoopBattleSession> _activeSessions = new Dictionary<string, CoopBattleSession>(StringComparer.Ordinal);
        private string _localSessionId = string.Empty;
        private bool _localIsJoiningCoop;

        public string ActiveLocalSessionId => _localSessionId;
        public bool IsInActiveCoopBattle => !string.IsNullOrEmpty(_localSessionId);

        private static bool IsCampaignReady()
        {
            var c = GameCampaign.Current;
            return c != null && c.GameStarted && c.SaveHandler?.IsSaving != true;
        }

        public List<CoopBattleSession> GetJoinableSessionsForLocalPlayer()
        {
            var result = new List<CoopBattleSession>();
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return result;
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            var mainParty = MobileParty.MainParty;
            if (myPeerId <= 0 || mainParty == null) return result;

            Vec2 myPos = mainParty.GetPosition2D;
            foreach (var s in _activeSessions.Values)
            {
                if (s == null || s.HostPeerId == myPeerId || s.AlliedPeerIds.Contains(myPeerId)) continue;
                if ((DateTime.UtcNow - s.CreatedUtc).TotalMinutes > 20) continue;

                bool sameArmy = PlayerArmyService.Instance.ArePeersInSameArmy(myPeerId, s.HostPeerId);
                bool sameSettlement = !string.IsNullOrEmpty(s.SettlementId) &&
                                      string.Equals(mainParty.CurrentSettlement?.StringId, s.SettlementId, StringComparison.Ordinal);
                float dist = (myPos - new Vec2(s.MapPosX, s.MapPosY)).Length;

                if (sameArmy || sameSettlement || dist <= COOP_JOIN_RADIUS)
                {
                    result.Add(s);
                }
            }
            return result;
        }

        #region Campaign Event Hooks

        public void OnLocalMapEventStarted(MapEvent? mapEvent, PartyBase? attackerParty, PartyBase? defenderParty)
        {
            try
            {
                if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;
                if (mapEvent == null || !mapEvent.IsPlayerMapEvent) return;
                if (_localIsJoiningCoop) return;

                // Ignore PvP battles (handled by BattleSyncService)
                if (BattleSyncService.Instance.ActivePvpOpponentPeerId > 0) return;
                if (attackerParty?.MobileParty != null && NetworkObjectRegistry.IsPlayerNetworkParty(attackerParty.MobileParty) &&
                    defenderParty?.MobileParty != null && NetworkObjectRegistry.IsPlayerNetworkParty(defenderParty.MobileParty))
                {
                    return;
                }

                // Ignore if already hosting/participating in a Co-op battle
                if (!string.IsNullOrEmpty(_localSessionId)) return;

                int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
                if (myPeerId <= 0) return;

                byte battleKind = 0;
                if (mapEvent.IsSiegeAssault || mapEvent.IsSiegeOutside || mapEvent.IsSallyOut) battleKind = 1;
                else if (mapEvent.IsHideoutBattle) battleKind = 2;

                PartyBase? enemySideLeader = mapEvent.PlayerSide == BattleSideEnum.Attacker
                    ? mapEvent.DefenderSide?.LeaderParty
                    : mapEvent.AttackerSide?.LeaderParty;

                string targetPartyId = enemySideLeader?.MobileParty?.StringId ?? enemySideLeader?.Id ?? string.Empty;
                string targetPartyName = enemySideLeader?.Name?.ToString()
                                         ?? mapEvent.MapEventSettlement?.Name?.ToString()
                                         ?? Loc.T("Düşman Kuvveti", "Enemy Force");
                string settlementId = mapEvent.MapEventSettlement?.StringId ?? string.Empty;

                Vec2 pos = MobileParty.MainParty?.GetPosition2D ?? Vec2.Zero;
                string sessionId = $"coop_{myPeerId}_{DateTime.UtcNow.Ticks & 0xFFFFFF}";
                string myName = Hero.MainHero?.Name?.ToString() ?? $"Lord #{myPeerId}";

                var session = new CoopBattleSession
                {
                    SessionId = sessionId,
                    HostPeerId = myPeerId,
                    HostName = myName,
                    BattleKind = battleKind,
                    TargetPartyId = targetPartyId,
                    TargetPartyName = targetPartyName,
                    SettlementId = settlementId,
                    MapPosX = pos.x,
                    MapPosY = pos.y,
                    FriendlyFireEnabled = false
                };
                session.AlliedPeerIds.Add(myPeerId);

                _activeSessions[sessionId] = session;
                _localSessionId = sessionId;

                PublishMissionCoopState(session, isHost: true);

                ModContext.Client?.Send(new CoopBattleSyncPacket
                {
                    ActionType = 1, // BattleCreated
                    SessionId = sessionId,
                    HostPeerId = myPeerId,
                    ActorPeerId = myPeerId,
                    HostName = myName,
                    BattleKind = battleKind,
                    TargetPartyId = targetPartyId,
                    TargetPartyName = targetPartyName,
                    SettlementId = settlementId,
                    MapPosX = pos.x,
                    MapPosY = pos.y,
                    FriendlyFireEnabled = false,
                    AlliedPeerIds = new List<int> { myPeerId }
                }, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] CoopBattleService.OnLocalMapEventStarted hatası: {ex.Message}");
            }
        }

        public void OnLocalMapEventEnded(MapEvent? mapEvent)
        {
            try
            {
                if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
                if (string.IsNullOrEmpty(_localSessionId)) return;
                if (mapEvent != null && !mapEvent.IsPlayerMapEvent) return;

                string sid = _localSessionId;
                int myPeerId = ModContext.Client?.LocalPeerId ?? 0;

                if (_activeSessions.TryGetValue(sid, out var session))
                {
                    if (session.HostPeerId == myPeerId)
                    {
                        bool hostWon = mapEvent != null && mapEvent.WinningSide == mapEvent.PlayerSide;
                        int enemyCount = Math.Max(1, mapEvent?.GetNumberOfInvolvedMen(mapEvent.PlayerSide == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker) ?? 10);
                        int sharedGold = hostWon ? Math.Min(3500, 120 + enemyCount * 18) : 0;
                        int sharedRenown = hostWon ? Math.Min(30, 3 + enemyCount / 6) : 0;

                        ModContext.Client?.Send(new CoopBattleSyncPacket
                        {
                            ActionType = 5, // BattleEnded
                            SessionId = sid,
                            HostPeerId = myPeerId,
                            ActorPeerId = myPeerId,
                            HostSideWon = hostWon,
                            SharedGoldLoot = sharedGold,
                            SharedRenown = sharedRenown,
                            TargetPartyId = session.TargetPartyId,
                            SettlementId = session.SettlementId
                        }, DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        ModContext.Client?.Send(new CoopBattleSyncPacket
                        {
                            ActionType = 4, // LeaveBattle
                            SessionId = sid,
                            HostPeerId = session.HostPeerId,
                            ActorPeerId = myPeerId
                        }, DeliveryMethod.ReliableOrdered);
                    }
                    _activeSessions.Remove(sid);
                }

                ClearLocalCoopState();
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] CoopBattleService.OnLocalMapEventEnded hatası: {ex.Message}");
            }
        }

        public void RequestJoinCoopBattle(string sessionId)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;
            if (string.IsNullOrEmpty(sessionId) || !_activeSessions.TryGetValue(sessionId, out var session)) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (myPeerId <= 0 || session.HostPeerId == myPeerId) return;

            ModContext.Client?.Send(new CoopBattleSyncPacket
            {
                ActionType = 2, // JoinBattleRequest
                SessionId = sessionId,
                HostPeerId = session.HostPeerId,
                ActorPeerId = myPeerId,
                ActorName = Hero.MainHero?.Name?.ToString() ?? $"Lord #{myPeerId}",
                Side = 0, // Ally side
                FriendlyFireEnabled = false
            }, DeliveryMethod.ReliableOrdered);
        }

        public void OpenNearbyCoopBattlesMenu()
        {
            var joinable = GetJoinableSessionsForLocalPlayer();
            if (joinable.Count == 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Calradia-Connected] {Loc.T("Yakında katılınabilecek aktif ortak savaş yok.", "No nearby active Co-op battles to join.")}",
                    Colors.Yellow));
                return;
            }

            if (joinable.Count == 1)
            {
                ShowJoinBattleInquiry(joinable[0]);
                return;
            }

            var elements = new List<InquiryElement>();
            foreach (var s in joinable)
            {
                string label = $"{s.HostName} vs {s.TargetPartyName} ({s.AlliedPeerIds.Count})";
                elements.Add(new InquiryElement(s.SessionId, label, null, true,
                    Loc.T("Müttefik olarak savaşa katıl (Dost Ateşi: KAPALI)", "Join as an ally (Friendly Fire: OFF)")));
            }

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                titleText: Loc.T("Yakındaki Ortak Savaşlar", "Nearby Co-op Battles"),
                descriptionText: Loc.T("Katılmak istediğiniz savaşı seçin (Dost Ateşi: KAPALI).", "Select a battle to join (Friendly Fire: OFF)."),
                inquiryElements: elements,
                isExitShown: true,
                minSelectableOptionCount: 1,
                maxSelectableOptionCount: 1,
                affirmativeText: Loc.T("Savaşa Katıl", "Join Battle"),
                negativeText: Loc.T("Vazgeç", "Cancel"),
                affirmativeAction: selected =>
                {
                    if (selected != null && selected.Count > 0 && selected[0].Identifier is string sid)
                    {
                        RequestJoinCoopBattle(sid);
                    }
                },
                negativeAction: null));
        }

        public void HandlePeerDisconnected(int peerId)
        {
            if (peerId <= 0) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                foreach (var kvp in _activeSessions.ToList())
                {
                    var s = kvp.Value;
                    if (s.HostPeerId == peerId)
                    {
                        _activeSessions.Remove(kvp.Key);
                        if (string.Equals(_localSessionId, kvp.Key, StringComparison.Ordinal))
                        {
                            ClearLocalCoopState();
                        }
                        if (ModContext.IsServerRunning)
                        {
                            ModContext.Server?.Broadcast(new CoopBattleSyncPacket
                            {
                                ActionType = 5,
                                SessionId = s.SessionId,
                                HostPeerId = s.HostPeerId,
                                HostSideWon = false
                            }, DeliveryMethod.ReliableOrdered);
                        }
                    }
                    else if (s.AlliedPeerIds.Remove(peerId))
                    {
                        if (ModContext.IsServerRunning)
                        {
                            ModContext.Server?.Broadcast(new CoopBattleSyncPacket
                            {
                                ActionType = 4,
                                SessionId = s.SessionId,
                                HostPeerId = s.HostPeerId,
                                ActorPeerId = peerId,
                                AlliedPeerIds = s.AlliedPeerIds.ToList()
                            }, DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
            });
        }

        #endregion

        #region Inbound Packet Handling

        public void HandlePacket(CoopBattleSyncPacket packet)
        {
            if (packet == null) return;

            // High-frequency enemy sync & damage reports (ActionType 6 & 7) are relayed directly in SubModule
            if (packet.ActionType == 6 || packet.ActionType == 7) return;

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
                    Debug.PrintError($"[Calradia-Connected] CoopBattleService.HandlePacket({packet.ActionType}) hatası: {ex.Message}");
                }
            });
        }

        private void HandlePacketOnServer(CoopBattleSyncPacket packet)
        {
            switch (packet.ActionType)
            {
                case 1: // BattleCreated
                {
                    if (string.IsNullOrEmpty(packet.SessionId) || packet.HostPeerId <= 0) return;

                    var session = new CoopBattleSession
                    {
                        SessionId = packet.SessionId,
                        HostPeerId = packet.HostPeerId,
                        HostName = packet.HostName,
                        BattleKind = packet.BattleKind,
                        TargetPartyId = packet.TargetPartyId,
                        TargetPartyName = packet.TargetPartyName,
                        SettlementId = packet.SettlementId,
                        SceneName = packet.SceneName,
                        MapPosX = packet.MapPosX,
                        MapPosY = packet.MapPosY,
                        FriendlyFireEnabled = false // Enforced OFF
                    };
                    session.AlliedPeerIds.Add(packet.HostPeerId);
                    _activeSessions[session.SessionId] = session;

                    packet.FriendlyFireEnabled = false;
                    packet.AlliedPeerIds = session.AlliedPeerIds.ToList();
                    ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 2: // JoinBattleRequest
                {
                    if (!_activeSessions.TryGetValue(packet.SessionId, out var session)) return;
                    if (packet.ActorPeerId <= 0) return;

                    session.AlliedPeerIds.Add(packet.ActorPeerId);

                    var approved = new CoopBattleSyncPacket
                    {
                        ActionType = 3, // JoinBattleApproved
                        SessionId = session.SessionId,
                        HostPeerId = session.HostPeerId,
                        ActorPeerId = packet.ActorPeerId,
                        HostName = session.HostName,
                        ActorName = packet.ActorName,
                        BattleKind = session.BattleKind,
                        Side = 0,
                        TargetPartyId = session.TargetPartyId,
                        TargetPartyName = session.TargetPartyName,
                        SettlementId = session.SettlementId,
                        SceneName = session.SceneName,
                        MapPosX = session.MapPosX,
                        MapPosY = session.MapPosY,
                        FriendlyFireEnabled = false,
                        AlliedPeerIds = session.AlliedPeerIds.ToList()
                    };
                    ModContext.Server?.Broadcast(approved, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 4: // LeaveBattle
                {
                    if (_activeSessions.TryGetValue(packet.SessionId, out var session))
                    {
                        session.AlliedPeerIds.Remove(packet.ActorPeerId);
                        packet.AlliedPeerIds = session.AlliedPeerIds.ToList();
                        ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    }
                    break;
                }
                case 5: // BattleEnded
                {
                    if (_activeSessions.TryGetValue(packet.SessionId, out var session))
                    {
                        packet.AlliedPeerIds = session.AlliedPeerIds.ToList();
                        _activeSessions.Remove(packet.SessionId);
                        ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    }
                    break;
                }
            }
        }

        private void HandlePacketOnClient(CoopBattleSyncPacket packet)
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;

            switch (packet.ActionType)
            {
                case 1: // BattleCreated
                {
                    if (string.IsNullOrEmpty(packet.SessionId) || packet.HostPeerId <= 0) return;

                    var session = new CoopBattleSession
                    {
                        SessionId = packet.SessionId,
                        HostPeerId = packet.HostPeerId,
                        HostName = packet.HostName,
                        BattleKind = packet.BattleKind,
                        TargetPartyId = packet.TargetPartyId,
                        TargetPartyName = packet.TargetPartyName,
                        SettlementId = packet.SettlementId,
                        SceneName = packet.SceneName,
                        MapPosX = packet.MapPosX,
                        MapPosY = packet.MapPosY,
                        FriendlyFireEnabled = false
                    };
                    if (packet.AlliedPeerIds != null)
                    {
                        foreach (int p in packet.AlliedPeerIds) session.AlliedPeerIds.Add(p);
                    }
                    session.AlliedPeerIds.Add(packet.HostPeerId);
                    _activeSessions[session.SessionId] = session;

                    if (packet.HostPeerId == myPeerId) return;

                    // Don't interrupt players who are already in a mission or battle
                    if (TaleWorlds.MountAndBlade.Mission.Current != null || MobileParty.MainParty?.MapEvent != null) return;

                    bool sameArmy = PlayerArmyService.Instance.ArePeersInSameArmy(myPeerId, session.HostPeerId);
                    bool sameSettlement = !string.IsNullOrEmpty(session.SettlementId) &&
                                          string.Equals(MobileParty.MainParty?.CurrentSettlement?.StringId, session.SettlementId, StringComparison.Ordinal);
                    float dist = MobileParty.MainParty != null
                        ? (MobileParty.MainParty.GetPosition2D - new Vec2(session.MapPosX, session.MapPosY)).Length
                        : 999f;

                    if (sameArmy || sameSettlement || dist <= COOP_JOIN_RADIUS)
                    {
                        ShowJoinBattleInquiry(session);
                    }
                    break;
                }
                case 3: // JoinBattleApproved
                {
                    if (!_activeSessions.TryGetValue(packet.SessionId, out var session))
                    {
                        session = new CoopBattleSession
                        {
                            SessionId = packet.SessionId,
                            HostPeerId = packet.HostPeerId,
                            HostName = packet.HostName,
                            BattleKind = packet.BattleKind,
                            TargetPartyId = packet.TargetPartyId,
                            TargetPartyName = packet.TargetPartyName,
                            SettlementId = packet.SettlementId,
                            SceneName = packet.SceneName,
                            MapPosX = packet.MapPosX,
                            MapPosY = packet.MapPosY,
                            FriendlyFireEnabled = false
                        };
                        _activeSessions[packet.SessionId] = session;
                    }

                    session.AlliedPeerIds.Clear();
                    session.AlliedPeerIds.Add(session.HostPeerId);
                    if (packet.AlliedPeerIds != null)
                    {
                        foreach (int p in packet.AlliedPeerIds) session.AlliedPeerIds.Add(p);
                    }

                    if (session.HostPeerId == myPeerId)
                    {
                        PublishMissionCoopState(session, isHost: true);
                        string allyName = !string.IsNullOrWhiteSpace(packet.ActorName) ? packet.ActorName : $"Lord #{packet.ActorPeerId}";
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[Calradia-Connected] {Loc.F("{0} savaşına müttefik olarak katıldı! (Dost Ateşi: KAPALI)", "{0} joined your battle as an ally! (Friendly Fire: OFF)", allyName)}",
                            Colors.Green));
                    }
                    else if (packet.ActorPeerId == myPeerId)
                    {
                        _localSessionId = session.SessionId;
                        PublishMissionCoopState(session, isHost: false);
                        LaunchJoinedCoopBattleMission(session);
                    }
                    else if (string.Equals(_localSessionId, session.SessionId, StringComparison.Ordinal))
                    {
                        PublishMissionCoopState(session, isHost: false);
                    }
                    break;
                }
                case 4: // LeaveBattle
                {
                    if (_activeSessions.TryGetValue(packet.SessionId, out var session))
                    {
                        session.AlliedPeerIds.Remove(packet.ActorPeerId);
                        if (string.Equals(_localSessionId, session.SessionId, StringComparison.Ordinal))
                        {
                            PublishMissionCoopState(session, isHost: session.HostPeerId == myPeerId);
                        }
                    }
                    break;
                }
                case 5: // BattleEnded
                {
                    _activeSessions.Remove(packet.SessionId);
                    if (!string.Equals(_localSessionId, packet.SessionId, StringComparison.Ordinal)) return;

                    bool iWasAlly = packet.HostPeerId != myPeerId &&
                                    packet.AlliedPeerIds != null &&
                                    packet.AlliedPeerIds.Contains(myPeerId);

                    if (iWasAlly)
                    {
                        if (packet.HostSideWon && Hero.MainHero != null)
                        {
                            if (packet.SharedGoldLoot > 0) Hero.MainHero.Gold += packet.SharedGoldLoot;
                            if (packet.SharedRenown > 0) Clan.PlayerClan?.AddRenown(packet.SharedRenown);

                            InformationManager.DisplayMessage(new InformationMessage(
                                $"[Calradia-Connected] {Loc.F("Ortak Savaş Zaferi! Paylaşılan ödül: +{0} Altın, +{1} Nam.", "Co-op Battle Victory! Shared reward: +{0} Gold, +{1} Renown.", packet.SharedGoldLoot, packet.SharedRenown)}",
                                Colors.Green));
                        }
                        else
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                $"[Calradia-Connected] {Loc.T("Ortak savaş sona erdi.", "Co-op Battle Ended.")}",
                                Colors.Yellow));
                        }
                    }

                    ClearLocalCoopState();
                    break;
                }
            }
        }

        #endregion

        #region Join Prompt & Mission Launch

        private void ShowJoinBattleInquiry(CoopBattleSession session)
        {
            string hostName = !string.IsNullOrWhiteSpace(session.HostName) ? session.HostName : $"Lord #{session.HostPeerId}";
            string enemyName = !string.IsNullOrWhiteSpace(session.TargetPartyName) ? session.TargetPartyName : Loc.T("Düşman", "Enemy");

            InformationManager.ShowInquiry(new InquiryData(
                titleText: Loc.T("Ortak Savaş", "Co-op Battle"),
                text: Loc.F("{0}, {1} ile savaşa girdi! Müttefik olarak katılmak ister misin? (Dost Ateşi: KAPALI)",
                            "{0} engaged {1} in battle! Would you like to join as an ally? (Friendly Fire: OFF)",
                            hostName, enemyName),
                isAffirmativeOptionShown: true,
                isNegativeOptionShown: true,
                affirmativeText: Loc.T("Savaşa Katıl", "Join Battle"),
                negativeText: Loc.T("Haritada Kal", "Stay on Map"),
                affirmativeAction: () => RequestJoinCoopBattle(session.SessionId),
                negativeAction: null));
        }

        private void LaunchJoinedCoopBattleMission(CoopBattleSession session)
        {
            _localIsJoiningCoop = true;
            try
            {
                var mainParty = MobileParty.MainParty;
                if (mainParty == null) return;

                if (mainParty.CurrentSettlement != null)
                {
                    try { LeaveSettlementAction.ApplyForParty(mainParty); } catch { }
                }

                // Move party smoothly next to the battle coordinates
                if (session.MapPosX != 0f || session.MapPosY != 0f)
                {
                    try
                    {
                        mainParty.Position = new CampaignVec2(new Vec2(session.MapPosX, session.MapPosY), isOnLand: true);
                        mainParty.SetMoveModeHold();
                    }
                    catch { }
                }

                if (PlayerEncounter.Current != null)
                {
                    try { PlayerEncounter.Finish(true); } catch { }
                }

                // Attempt to join the exact target enemy party or besieged settlement encounter
                MobileParty? enemyParty = !string.IsNullOrEmpty(session.TargetPartyId)
                    ? NetworkObjectRegistry.Instance.ResolveParty(session.TargetPartyId)
                    : null;
                Settlement? settlement = !string.IsNullOrEmpty(session.SettlementId)
                    ? NetworkObjectRegistry.Instance.ResolveSettlement(session.SettlementId)
                    : null;

                if (session.BattleKind == 1 && settlement != null && settlement.Party != null)
                {
                    PlayerEncounter.RestartPlayerEncounter(settlement.Party, PartyBase.MainParty, forcePlayerOutFromSettlement: true);
                    if (settlement.SiegeEvent?.BesiegerCamp != null)
                    {
                        try { mainParty.BesiegerCamp = settlement.SiegeEvent.BesiegerCamp; } catch { }
                    }
                    PlayerEncounter.StartBattle();
                    PlayerEncounter.StartAttackMission();
                }
                else if (enemyParty != null && enemyParty.IsActive && enemyParty.Party != null)
                {
                    PlayerEncounter.RestartPlayerEncounter(enemyParty.Party, PartyBase.MainParty, forcePlayerOutFromSettlement: true);
                    PlayerEncounter.StartBattle();
                    PlayerEncounter.StartAttackMission();
                }
                else
                {
                    // Fallback: if enemy party already resolved on client map, open field battle at host's location
                    var hostParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(session.HostPeerId);
                    if (hostParty?.MapEvent != null)
                    {
                        PartyBase? foe = hostParty.MapEvent.DefenderSide?.LeaderParty ?? hostParty.MapEvent.AttackerSide?.LeaderParty;
                        if (foe != null && foe != PartyBase.MainParty)
                        {
                            PlayerEncounter.RestartPlayerEncounter(foe, PartyBase.MainParty, forcePlayerOutFromSettlement: true);
                            PlayerEncounter.StartBattle();
                            PlayerEncounter.StartAttackMission();
                        }
                    }
                }

                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Calradia-Connected] {Loc.F("{0} ile ortak savaşa katıldın! Dost ateşi KAPALI.", "Joined {0}'s Co-op battle! Friendly fire between allies is OFF.", session.HostName)}",
                    Colors.Green));
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] LaunchJoinedCoopBattleMission hatası: {ex.Message}");
            }
            finally
            {
                _localIsJoiningCoop = false;
            }
        }

        private void PublishMissionCoopState(CoopBattleSession session, bool isHost)
        {
            ModContext.Events.Publish(new CoopBattleSessionUpdatedEvent
            {
                IsActive = true,
                SessionId = session.SessionId,
                IsHost = isHost,
                HostPeerId = session.HostPeerId,
                FriendlyFireEnabled = false,
                AlliedPeerIds = session.AlliedPeerIds.ToArray()
            });
        }

        private void ClearLocalCoopState()
        {
            _localSessionId = string.Empty;
            ModContext.Events.Publish(new CoopBattleSessionUpdatedEvent
            {
                IsActive = false,
                SessionId = string.Empty,
                IsHost = false,
                HostPeerId = 0,
                FriendlyFireEnabled = false,
                AlliedPeerIds = Array.Empty<int>()
            });
        }

        #endregion
    }
}
