using System;
using System.Collections.Generic;
using System.Linq;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Library;
using GameCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Synchronizes player-led armies and independent Co-op escort warbands across Server and Clients,
    /// including invitations, joining/leaving, disbanding, map escorting, and automatic joint siege camps.
    /// </summary>
    public sealed class PlayerArmyService
    {
        private static PlayerArmyService? _instance;
        public static PlayerArmyService Instance => _instance ??= new PlayerArmyService();

        public sealed class PlayerArmyState
        {
            public string ArmyId = string.Empty;
            public int LeaderPeerId;
            public string LeaderName = string.Empty;
            public string KingdomId = string.Empty;
            public byte ArmyMode = 2; // 1 = Native Kingdom Army, 2 = Independent Co-op Escort Warband
            public float Cohesion = 100f;
            public string ActiveSiegeSettlementId = string.Empty;
            public readonly HashSet<int> MemberPeerIds = new HashSet<int>();
        }

        private readonly Dictionary<string, PlayerArmyState> _armies = new Dictionary<string, PlayerArmyState>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _peerToArmyId = new Dictionary<int, string>();
        private float _escortTickTimer;
        private bool _isEscortingLeader;
        private int _pendingInviteLeaderPeerId;
        private string _pendingInviteArmyId = string.Empty;

        private static bool IsCampaignReady()
        {
            var c = GameCampaign.Current;
            return c != null && c.GameStarted && c.SaveHandler?.IsSaving != true;
        }

        public bool IsPeerInAnyArmy(int peerId) => peerId > 0 && _peerToArmyId.ContainsKey(peerId);

        public bool IsPeerArmyLeader(int peerId)
        {
            return TryGetArmyForPeer(peerId, out var army) && army != null && army.LeaderPeerId == peerId;
        }

        public bool TryGetArmyForPeer(int peerId, out PlayerArmyState? army)
        {
            army = null;
            if (peerId <= 0) return false;
            if (_peerToArmyId.TryGetValue(peerId, out string? armyId) &&
                !string.IsNullOrEmpty(armyId) &&
                _armies.TryGetValue(armyId!, out var found))
            {
                army = found;
                return true;
            }
            return false;
        }

        public bool ArePeersInSameArmy(int peerA, int peerB)
        {
            if (peerA <= 0 || peerB <= 0 || peerA == peerB) return false;
            return _peerToArmyId.TryGetValue(peerA, out var a) &&
                   _peerToArmyId.TryGetValue(peerB, out var b) &&
                   string.Equals(a, b, StringComparison.Ordinal);
        }

        public List<int> GetArmyMembersForPeer(int peerId)
        {
            if (!TryGetArmyForPeer(peerId, out var army) || army == null)
            {
                return new List<int>();
            }
            var list = new List<int> { army.LeaderPeerId };
            foreach (int m in army.MemberPeerIds)
            {
                if (m != army.LeaderPeerId) list.Add(m);
            }
            return list;
        }

        #region Local Player Actions

        public void InvitePeerToArmy(int targetPeerId)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (myPeerId <= 0 || targetPeerId <= 0 || targetPeerId == myPeerId) return;

            var targetHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(targetPeerId);
            string targetName = targetHero?.Name?.ToString() ?? $"Lord #{targetPeerId}";
            string myName = Hero.MainHero?.Name?.ToString() ?? $"Lord #{myPeerId}";

            string armyId = TryGetArmyForPeer(myPeerId, out var existing) && existing != null && existing.LeaderPeerId == myPeerId
                ? existing.ArmyId
                : $"cc_army_{myPeerId}";

            byte mode = ResolvePreferredArmyMode(Hero.MainHero?.Clan, targetHero?.Clan, out string kingdomId);

            ModContext.Client?.Send(new PlayerArmySyncPacket
            {
                ActionType = 1, // InviteToArmy
                ArmyId = armyId,
                LeaderPeerId = myPeerId,
                TargetPeerId = targetPeerId,
                LeaderName = myName,
                TargetName = targetName,
                KingdomId = kingdomId,
                ArmyMode = mode,
                Cohesion = 100f
            }, DeliveryMethod.ReliableOrdered);

            InformationManager.DisplayMessage(new InformationMessage(
                $"[Calradia-Connected] {Loc.F("{0} orduna davet edildi.", "Army invitation sent to {0}.", targetName)}",
                Colors.Cyan));
        }

        public void LeaveLocalArmy()
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (!TryGetArmyForPeer(myPeerId, out var army) || army == null) return;

            if (army.LeaderPeerId == myPeerId)
            {
                DisbandLocalArmy();
                return;
            }

            ModContext.Client?.Send(new PlayerArmySyncPacket
            {
                ActionType = 4, // LeaveArmy
                ArmyId = army.ArmyId,
                LeaderPeerId = army.LeaderPeerId,
                TargetPeerId = myPeerId,
                TargetName = Hero.MainHero?.Name?.ToString() ?? string.Empty
            }, DeliveryMethod.ReliableOrdered);

            _isEscortingLeader = false;
        }

        public void DisbandLocalArmy()
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (!TryGetArmyForPeer(myPeerId, out var army) || army == null || army.LeaderPeerId != myPeerId) return;

            ModContext.Client?.Send(new PlayerArmySyncPacket
            {
                ActionType = 5, // DisbandArmy
                ArmyId = army.ArmyId,
                LeaderPeerId = myPeerId,
                LeaderName = Hero.MainHero?.Name?.ToString() ?? string.Empty
            }, DeliveryMethod.ReliableOrdered);
        }

        public void NotifyLocalSiegeStarted(Settlement? settlement)
        {
            if (settlement == null || !IsCampaignReady()) return;

            if (ModContext.IsServerRunning)
            {
                // Server checks if besieger is a player army leader and syncs members into the siege camp
                var leaderParty = settlement.SiegeEvent?.BesiegerCamp?.LeaderParty;
                if (leaderParty != null && NetworkObjectRegistry.TryExtractPeerId(leaderParty.StringId, out int leaderPeerId))
                {
                    if (TryGetArmyForPeer(leaderPeerId, out var army) && army != null && army.LeaderPeerId == leaderPeerId)
                    {
                        army.ActiveSiegeSettlementId = settlement.StringId;
                        AttachArmyMembersToSiege(army, settlement);
                        BroadcastArmyStateFromServer(army, actionType: 7);
                    }
                }
                return;
            }

            if (!ModContext.IsClientConnected) return;
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (TryGetArmyForPeer(myPeerId, out var myArmy) && myArmy != null && myArmy.LeaderPeerId == myPeerId)
            {
                myArmy.ActiveSiegeSettlementId = settlement.StringId;
                ModContext.Client?.Send(new PlayerArmySyncPacket
                {
                    ActionType = 7, // JoinJointSiege
                    ArmyId = myArmy.ArmyId,
                    LeaderPeerId = myPeerId,
                    SiegeSettlementId = settlement.StringId
                }, DeliveryMethod.ReliableOrdered);
            }
        }

        public void TickArmyEscort(float dt)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected || !IsCampaignReady()) return;

            _escortTickTimer += dt;
            if (_escortTickTimer < 0.65f) return;
            _escortTickTimer = 0f;

            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;
            if (!TryGetArmyForPeer(myPeerId, out var army) || army == null || army.LeaderPeerId == myPeerId)
            {
                _isEscortingLeader = false;
                return;
            }

            var mainParty = MobileParty.MainParty;
            if (mainParty == null || !mainParty.IsActive || mainParty.MapEvent != null || mainParty.BesiegerCamp != null) return;

            var leaderParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(army.LeaderPeerId);
            if (leaderParty == null || !leaderParty.IsActive) return;

            // If leader is actively besieging a settlement, automatically join the siege camp!
            if (leaderParty.BesiegerCamp != null && leaderParty.BesiegerCamp.SiegeEvent != null)
            {
                try
                {
                    mainParty.BesiegerCamp = leaderParty.BesiegerCamp;
                }
                catch { }
                return;
            }

            // For Mode 2 (Escort Warband) or detached native army member, keep following leader smoothly
            if (_isEscortingLeader && mainParty.CurrentSettlement == null && leaderParty.CurrentSettlement == null)
            {
                try
                {
                    float distSq = (mainParty.GetPosition2D - leaderParty.GetPosition2D).LengthSquared;
                    if (distSq > 0.35f)
                    {
                        mainParty.SetMoveEscortParty(leaderParty, MobileParty.NavigationType.Default, isTargetingPort: false);
                    }
                }
                catch { }
            }
        }

        public void HandlePeerDisconnected(int peerId)
        {
            if (peerId <= 0) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (!TryGetArmyForPeer(peerId, out var army) || army == null) return;

                    if (army.LeaderPeerId == peerId)
                    {
                        DisbandArmyLocal(army, notify: true);
                        if (ModContext.IsServerRunning)
                        {
                            ModContext.Server?.Broadcast(new PlayerArmySyncPacket
                            {
                                ActionType = 5,
                                ArmyId = army.ArmyId,
                                LeaderPeerId = army.LeaderPeerId,
                                LeaderName = army.LeaderName
                            }, DeliveryMethod.ReliableOrdered);
                        }
                    }
                    else
                    {
                        RemoveMemberLocal(army, peerId, notify: true);
                        if (ModContext.IsServerRunning)
                        {
                            BroadcastArmyStateFromServer(army, actionType: 6);
                        }
                    }
                }
                catch { }
            });
        }

        #endregion

        #region Inbound Packet Handling

        public void HandlePacket(PlayerArmySyncPacket packet)
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
                    Debug.PrintError($"[Calradia-Connected] PlayerArmyService.HandlePacket({packet.ActionType}) hatası: {ex.Message}");
                }
            });
        }

        private void HandlePacketOnServer(PlayerArmySyncPacket packet)
        {
            switch (packet.ActionType)
            {
                case 1: // InviteToArmy -> forward invite to target peer
                {
                    if (packet.LeaderPeerId <= 0 || packet.TargetPeerId <= 0 || packet.LeaderPeerId == packet.TargetPeerId) return;
                    ModContext.Server?.SendTo(packet.TargetPeerId, packet, DeliveryMethod.ReliableOrdered);
                    break;
                }
                case 2: // AcceptArmyInvite
                {
                    if (packet.LeaderPeerId <= 0 || packet.TargetPeerId <= 0 || packet.LeaderPeerId == packet.TargetPeerId) return;

                    // Remove target from any previous army
                    if (TryGetArmyForPeer(packet.TargetPeerId, out var oldArmy) && oldArmy != null && oldArmy.LeaderPeerId != packet.LeaderPeerId)
                    {
                        RemoveMemberLocal(oldArmy, packet.TargetPeerId, notify: false);
                        BroadcastArmyStateFromServer(oldArmy, actionType: 6);
                    }

                    if (!TryGetArmyForPeer(packet.LeaderPeerId, out var army) || army == null || army.LeaderPeerId != packet.LeaderPeerId)
                    {
                        var leaderHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.LeaderPeerId);
                        var targetHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.TargetPeerId);
                        byte mode = ResolvePreferredArmyMode(leaderHero?.Clan, targetHero?.Clan, out string kingdomId);

                        army = new PlayerArmyState
                        {
                            ArmyId = !string.IsNullOrEmpty(packet.ArmyId) ? packet.ArmyId : $"cc_army_{packet.LeaderPeerId}",
                            LeaderPeerId = packet.LeaderPeerId,
                            LeaderName = leaderHero?.Name?.ToString() ?? packet.LeaderName,
                            KingdomId = kingdomId,
                            ArmyMode = mode,
                            Cohesion = 100f
                        };
                        _armies[army.ArmyId] = army;
                        _peerToArmyId[army.LeaderPeerId] = army.ArmyId;
                    }

                    army.MemberPeerIds.Add(packet.TargetPeerId);
                    _peerToArmyId[packet.TargetPeerId] = army.ArmyId;

                    ApplyArmyMembershipToWorld(army);
                    BroadcastArmyStateFromServer(army, actionType: 6, newlyJoinedName: packet.TargetName);
                    break;
                }
                case 3: // DeclineArmyInvite -> forward decline to leader
                {
                    if (packet.LeaderPeerId > 0)
                    {
                        ModContext.Server?.SendTo(packet.LeaderPeerId, packet, DeliveryMethod.ReliableOrdered);
                    }
                    break;
                }
                case 4: // LeaveArmy
                {
                    if (TryGetArmyForPeer(packet.TargetPeerId, out var army) && army != null)
                    {
                        if (army.LeaderPeerId == packet.TargetPeerId)
                        {
                            DisbandArmyLocal(army, notify: false);
                            ModContext.Server?.Broadcast(new PlayerArmySyncPacket
                            {
                                ActionType = 5,
                                ArmyId = army.ArmyId,
                                LeaderPeerId = army.LeaderPeerId,
                                LeaderName = army.LeaderName
                            }, DeliveryMethod.ReliableOrdered);
                        }
                        else
                        {
                            RemoveMemberLocal(army, packet.TargetPeerId, notify: false);
                            BroadcastArmyStateFromServer(army, actionType: 6, leftMemberName: packet.TargetName);
                        }
                    }
                    break;
                }
                case 5: // DisbandArmy
                {
                    if (TryGetArmyForPeer(packet.LeaderPeerId, out var army) && army != null && army.LeaderPeerId == packet.LeaderPeerId)
                    {
                        DisbandArmyLocal(army, notify: false);
                        ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    }
                    break;
                }
                case 7: // JoinJointSiege
                {
                    if (TryGetArmyForPeer(packet.LeaderPeerId, out var army) && army != null && army.LeaderPeerId == packet.LeaderPeerId)
                    {
                        army.ActiveSiegeSettlementId = packet.SiegeSettlementId ?? string.Empty;
                        var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(army.ActiveSiegeSettlementId);
                        if (settlement != null)
                        {
                            AttachArmyMembersToSiege(army, settlement);
                        }
                        BroadcastArmyStateFromServer(army, actionType: 7);
                    }
                    break;
                }
            }
        }

        private void HandlePacketOnClient(PlayerArmySyncPacket packet)
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? 0;

            switch (packet.ActionType)
            {
                case 1: // InviteToArmy
                {
                    if (packet.TargetPeerId != myPeerId) return;
                    _pendingInviteLeaderPeerId = packet.LeaderPeerId;
                    _pendingInviteArmyId = packet.ArmyId;

                    string leaderName = !string.IsNullOrWhiteSpace(packet.LeaderName)
                        ? packet.LeaderName
                        : (NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.LeaderPeerId)?.Name?.ToString() ?? $"Lord #{packet.LeaderPeerId}");

                    InformationManager.ShowInquiry(new InquiryData(
                        titleText: Loc.T("Ordu Daveti", "Army Invitation"),
                        text: Loc.F("{0} seni ortak sefer için ordusuna davet ediyor!",
                                    "{0} invites you to join their army for a joint campaign!",
                                    leaderName),
                        isAffirmativeOptionShown: true,
                        isNegativeOptionShown: true,
                        affirmativeText: Loc.T("Orduya Katıl", "Join Army"),
                        negativeText: Loc.T("Reddet", "Decline"),
                        affirmativeAction: () =>
                        {
                            _isEscortingLeader = true;
                            ModContext.Client?.Send(new PlayerArmySyncPacket
                            {
                                ActionType = 2, // AcceptArmyInvite
                                ArmyId = _pendingInviteArmyId,
                                LeaderPeerId = _pendingInviteLeaderPeerId,
                                TargetPeerId = myPeerId,
                                LeaderName = leaderName,
                                TargetName = Hero.MainHero?.Name?.ToString() ?? string.Empty
                            }, DeliveryMethod.ReliableOrdered);
                        },
                        negativeAction: () =>
                        {
                            ModContext.Client?.Send(new PlayerArmySyncPacket
                            {
                                ActionType = 3, // DeclineArmyInvite
                                ArmyId = _pendingInviteArmyId,
                                LeaderPeerId = _pendingInviteLeaderPeerId,
                                TargetPeerId = myPeerId,
                                TargetName = Hero.MainHero?.Name?.ToString() ?? string.Empty
                            }, DeliveryMethod.ReliableOrdered);
                        }));
                    break;
                }
                case 3: // DeclineArmyInvite
                {
                    if (packet.LeaderPeerId == myPeerId)
                    {
                        string name = !string.IsNullOrWhiteSpace(packet.TargetName) ? packet.TargetName : $"Lord #{packet.TargetPeerId}";
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[Calradia-Connected] {Loc.F("{0} ordu davetini reddetti.", "{0} declined your army invitation.", name)}",
                            Colors.Yellow));
                    }
                    break;
                }
                case 5: // DisbandArmy
                {
                    if (_armies.TryGetValue(packet.ArmyId, out var army))
                    {
                        bool iWasInArmy = army.LeaderPeerId == myPeerId || army.MemberPeerIds.Contains(myPeerId);
                        DisbandArmyLocal(army, notify: iWasInArmy);
                        if (iWasInArmy) _isEscortingLeader = false;
                    }
                    break;
                }
                case 6: // SyncArmyState
                case 7: // JoinJointSiege
                {
                    ApplySyncedArmyStateOnClient(packet, myPeerId);
                    break;
                }
            }
        }

        private void ApplySyncedArmyStateOnClient(PlayerArmySyncPacket packet, int myPeerId)
        {
            if (string.IsNullOrEmpty(packet.ArmyId) || packet.LeaderPeerId <= 0) return;

            bool existed = _armies.TryGetValue(packet.ArmyId, out var army);
            if (army == null)
            {
                army = new PlayerArmyState { ArmyId = packet.ArmyId };
                _armies[packet.ArmyId] = army;
            }

            // Clear stale reverse mappings for this army
            foreach (var kvp in _peerToArmyId.Where(x => string.Equals(x.Value, packet.ArmyId, StringComparison.Ordinal)).ToList())
            {
                _peerToArmyId.Remove(kvp.Key);
            }

            var prevMembers = new HashSet<int>(army.MemberPeerIds);
            army.LeaderPeerId = packet.LeaderPeerId;
            army.LeaderName = packet.LeaderName;
            army.KingdomId = packet.KingdomId;
            army.ArmyMode = packet.ArmyMode;
            army.Cohesion = packet.Cohesion;
            army.ActiveSiegeSettlementId = packet.SiegeSettlementId ?? string.Empty;
            army.MemberPeerIds.Clear();

            _peerToArmyId[army.LeaderPeerId] = army.ArmyId;
            if (packet.MemberPeerIds != null)
            {
                foreach (int m in packet.MemberPeerIds)
                {
                    if (m > 0 && m != army.LeaderPeerId)
                    {
                        army.MemberPeerIds.Add(m);
                        _peerToArmyId[m] = army.ArmyId;
                    }
                }
            }

            bool iAmParticipant = army.LeaderPeerId == myPeerId || army.MemberPeerIds.Contains(myPeerId);
            if (army.MemberPeerIds.Contains(myPeerId))
            {
                _isEscortingLeader = true;
            }
            else if (prevMembers.Contains(myPeerId))
            {
                _isEscortingLeader = false;
            }

            ApplyArmyMembershipToWorld(army);

            if (packet.ActionType == 7 && !string.IsNullOrEmpty(army.ActiveSiegeSettlementId))
            {
                var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(army.ActiveSiegeSettlementId);
                if (settlement != null)
                {
                    AttachArmyMembersToSiege(army, settlement);
                    if (iAmParticipant)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[Calradia-Connected] {Loc.F("Ordunuz {0} kuşatmasına katıldı!", "Your army joined the siege of {0}!", settlement.Name?.ToString() ?? settlement.StringId)}",
                            Colors.Magenta));
                    }
                }
            }
            else if (iAmParticipant && !string.IsNullOrWhiteSpace(packet.TargetName))
            {
                if (packet.MemberPeerIds != null && packet.MemberPeerIds.Count > prevMembers.Count || !existed)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Calradia-Connected] {Loc.F("{0}, {1} ordusuna katıldı!", "{0} joined {1}'s army!", packet.TargetName, army.LeaderName)}",
                        Colors.Green));
                }
                else
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Calradia-Connected] {Loc.F("{0} ordudan ayrıldı.", "{0} left the army.", packet.TargetName)}",
                        Colors.Yellow));
                }
            }
        }

        #endregion

        #region World Application & Helpers

        private void BroadcastArmyStateFromServer(PlayerArmyState army, byte actionType, string newlyJoinedName = "", string leftMemberName = "")
        {
            var packet = new PlayerArmySyncPacket
            {
                ActionType = actionType,
                ArmyId = army.ArmyId,
                LeaderPeerId = army.LeaderPeerId,
                LeaderName = army.LeaderName,
                TargetName = !string.IsNullOrEmpty(newlyJoinedName) ? newlyJoinedName : leftMemberName,
                KingdomId = army.KingdomId,
                ArmyMode = army.ArmyMode,
                Cohesion = army.Cohesion,
                SiegeSettlementId = army.ActiveSiegeSettlementId,
                MemberPeerIds = army.MemberPeerIds.ToList()
            };
            ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
        }

        private static byte ResolvePreferredArmyMode(Clan? leaderClan, Clan? memberClan, out string kingdomId)
        {
            kingdomId = string.Empty;
            if (leaderClan?.Kingdom != null &&
                memberClan?.Kingdom != null &&
                ReferenceEquals(leaderClan.Kingdom, memberClan.Kingdom) &&
                !leaderClan.Kingdom.IsEliminated)
            {
                kingdomId = leaderClan.Kingdom.StringId ?? string.Empty;
                return 1; // Native Kingdom Army
            }
            return 2; // Independent Co-op Escort Warband
        }

        private MobileParty? ResolvePeerParty(int peerId)
        {
            if (!ModContext.IsServerRunning && peerId == (ModContext.Client?.LocalPeerId ?? -1))
            {
                return MobileParty.MainParty;
            }
            return NetworkObjectRegistry.Instance.ResolvePlayerParty(peerId);
        }

        private void ApplyArmyMembershipToWorld(PlayerArmyState army)
        {
            var leaderParty = ResolvePeerParty(army.LeaderPeerId);
            if (leaderParty == null || !leaderParty.IsActive) return;

            if (army.ArmyMode == 1)
            {
                var kingdom = !string.IsNullOrEmpty(army.KingdomId)
                    ? NetworkObjectRegistry.Instance.ResolveKingdom(army.KingdomId)
                    : leaderParty.ActualClan?.Kingdom;

                if (kingdom != null && !kingdom.IsEliminated)
                {
                    try
                    {
                        Army nativeArmy = leaderParty.Army ?? new Army(kingdom, leaderParty, Army.ArmyTypes.Patrolling);
                        nativeArmy.Cohesion = Math.Max(nativeArmy.Cohesion, army.Cohesion);

                        foreach (int memberPeerId in army.MemberPeerIds)
                        {
                            var mp = ResolvePeerParty(memberPeerId);
                            if (mp == null || !mp.IsActive || mp == leaderParty) continue;
                            if (mp.ActualClan?.Kingdom != kingdom) continue;

                            if (mp.Army != nativeArmy)
                            {
                                mp.Army = nativeArmy;
                                try { nativeArmy.AddPartyToMergedParties(mp); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.PrintError($"[Calradia-Connected] Native Army attach fallback -> Escort: {ex.Message}");
                    }
                }
            }
            else
            {
                // Independent Co-op Escort Warband: set escort order on member parties
                foreach (int memberPeerId in army.MemberPeerIds)
                {
                    var mp = ResolvePeerParty(memberPeerId);
                    if (mp == null || !mp.IsActive || mp == leaderParty || mp.CurrentSettlement != null) continue;
                    try
                    {
                        mp.SetMoveEscortParty(leaderParty, MobileParty.NavigationType.Default, isTargetingPort: false);
                    }
                    catch { }
                }
            }
        }

        private void AttachArmyMembersToSiege(PlayerArmyState army, Settlement settlement)
        {
            if (settlement.SiegeEvent?.BesiegerCamp == null) return;
            var camp = settlement.SiegeEvent.BesiegerCamp;

            foreach (int memberPeerId in army.MemberPeerIds)
            {
                var mp = ResolvePeerParty(memberPeerId);
                if (mp == null || !mp.IsActive) continue;
                try
                {
                    if (mp.BesiegerCamp != camp)
                    {
                        mp.BesiegerCamp = camp;
                    }
                }
                catch { }
            }
        }

        private void RemoveMemberLocal(PlayerArmyState army, int memberPeerId, bool notify)
        {
            army.MemberPeerIds.Remove(memberPeerId);
            _peerToArmyId.Remove(memberPeerId);

            var mp = ResolvePeerParty(memberPeerId);
            if (mp != null)
            {
                try
                {
                    if (mp.Army != null && mp.Army.LeaderParty != mp)
                    {
                        mp.Army = null;
                    }
                    if (mp == MobileParty.MainParty)
                    {
                        mp.SetMoveModeHold();
                    }
                }
                catch { }
            }

            if (notify)
            {
                string name = NetworkObjectRegistry.Instance.ResolvePlayerHero(memberPeerId)?.Name?.ToString() ?? $"Lord #{memberPeerId}";
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Calradia-Connected] {Loc.F("{0} ordudan ayrıldı.", "{0} left the army.", name)}",
                    Colors.Yellow));
            }
        }

        private void DisbandArmyLocal(PlayerArmyState army, bool notify)
        {
            var leaderParty = ResolvePeerParty(army.LeaderPeerId);
            foreach (int memberPeerId in army.MemberPeerIds.ToList())
            {
                RemoveMemberLocal(army, memberPeerId, notify: false);
            }

            _peerToArmyId.Remove(army.LeaderPeerId);
            _armies.Remove(army.ArmyId);

            if (leaderParty != null && leaderParty.Army != null && leaderParty.Army.LeaderParty == leaderParty)
            {
                try { leaderParty.Army = null; } catch { }
            }

            if (notify)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Calradia-Connected] {Loc.F("{0} orduyu dağıttı.", "{0} disbanded the army.", army.LeaderName)}",
                    Colors.Yellow));
            }
        }

        #endregion
    }
}
