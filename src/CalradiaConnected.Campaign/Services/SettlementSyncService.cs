using System;
using System.Linq;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Synchronizes settlement entries/exits, volunteer recruitment slots,
    /// and market trade stock across the server and connected clients.
    /// </summary>
    public sealed class SettlementSyncService
    {
        private static SettlementSyncService? _instance;
        public static SettlementSyncService Instance => _instance ??= new SettlementSyncService();

        private bool _subscribed;

        public void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;

            ModContext.Events.Subscribe<LocalVolunteerRecruitedEvent>(OnLocalVolunteerRecruited);
            ModContext.Events.Subscribe<LocalSettlementActionEvent>(OnLocalSettlementAction);
        }

        private void OnLocalVolunteerRecruited(LocalVolunteerRecruitedEvent evt)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;

            var packet = new SettlementInteractionPacket
            {
                PeerId = ModContext.Client?.LocalPeerId ?? 1001,
                SettlementId = evt.SettlementId,
                ActionType = 4, // Recruit
                NotableId = evt.NotableId,
                SlotIndex = evt.SlotIndex,
                ItemOrTroopId = evt.TroopId,
                Count = 1,
                GoldDelta = -Math.Abs(evt.Cost)
            };

            ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "VolunteerRecruited" });
        }

        public void BroadcastServerAiSettlementChange(MobileParty party, Settlement settlement, bool entered)
        {
            if (!ModContext.IsServerRunning || ModContext.Server == null || !ModContext.Server.IsRunning) return;
            if ((ModContext.Server.Sessions?.Count ?? 0) == 0) return;
            if (party == null || settlement == null || string.IsNullOrEmpty(party.StringId)) return;

            // Always broadcast AI settlement enter/leave reliably to all clients regardless of player distance
            // so caravans and lords never freeze at settlement gates when players later travel to that region.
            var packet = new SettlementInteractionPacket
            {
                PeerId = 0, // 0 identifies AI party sync
                SettlementId = settlement.StringId,
                ActionType = entered ? (byte)1 : (byte)2,
                SlotIndex = party.IsCurrentlyAtSea ? 1 : 0,
                Payload = party.StringId
            };
            ModContext.Server.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void BroadcastServerAiPartyDestroyed(MobileParty party)
        {
            if (!ModContext.IsServerRunning || ModContext.Server == null || !ModContext.Server.IsRunning) return;
            if ((ModContext.Server.Sessions?.Count ?? 0) == 0) return;
            if (party == null || string.IsNullOrEmpty(party.StringId)) return;
            if (party.IsMainParty || party.StringId == "cc_server_party" || party.IsGarrison || party.IsMilitia) return;
            if (NetworkObjectRegistry.IsPlayerNetworkParty(party)) return;

            var packet = new SettlementInteractionPacket
            {
                PeerId = 0,
                SettlementId = string.Empty,
                ActionType = 6, // AI party destroyed/disbanded
                SlotIndex = 0,
                Payload = party.StringId
            };
            ModContext.Server.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private void OnLocalSettlementAction(LocalSettlementActionEvent evt)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;

            var packet = new SettlementInteractionPacket
            {
                PeerId = ModContext.Client?.LocalPeerId ?? 1001,
                SettlementId = evt.SettlementId,
                ActionType = evt.ActionType,
                ItemOrTroopId = evt.ItemId,
                Count = evt.Count,
                GoldDelta = evt.GoldDelta
            };

            ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);

            if (evt.ActionType == 3)
            {
                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "MarketTrade" });
            }
        }

        public void HandlePacket(SettlementInteractionPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (TaleWorlds.CampaignSystem.Campaign.Current == null ||
                        !TaleWorlds.CampaignSystem.Campaign.Current.GameStarted ||
                        TaleWorlds.CampaignSystem.Campaign.Current.SaveHandler?.IsSaving == true)
                    {
                        return;
                    }

                    // Ignore echo back on originating client for player settlement actions
                    if (!ModContext.IsServerRunning && packet.PeerId > 0 && packet.PeerId == (ModContext.Client?.LocalPeerId ?? -1))
                    {
                        return;
                    }

                    var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId);

                    // AI party reliable Enter/Leave/Destroyed notification from Server
                    if (packet.PeerId == 0 && !string.IsNullOrEmpty(packet.Payload))
                    {
                        if (ModContext.IsServerRunning) return;
                        var aiParty = NetworkObjectRegistry.Instance.ResolveParty(packet.Payload);

                        if (packet.ActionType == 6)
                        {
                            Sync.WorldSyncManager.ApplyRemoteAiPartyDestroyed(aiParty, packet.Payload);
                            return;
                        }

                        Sync.WorldSyncManager.MarkClientAiPartySynced(packet.Payload);
                        if (aiParty == null || aiParty.IsMainParty || !aiParty.IsActive) return;

                        bool aiAtSea = packet.SlotIndex == 1;
                        if (packet.ActionType == 1 && settlement != null)
                        {
                            Vec2 entryPos = aiAtSea && settlement.HasPort && settlement.PortPosition.IsValid()
                                ? settlement.PortPosition.ToVec2()
                                : (settlement.GatePosition.IsValid() ? settlement.GatePosition.ToVec2() : settlement.GetPosition2D);
                            Sync.WorldSyncManager.ApplyRemotePartySettlementState(aiParty, settlement, entryPos, aiAtSea);
                        }
                        else if (packet.ActionType == 2 && settlement != null)
                        {
                            Vec2 exitPos = aiAtSea && settlement.HasPort && settlement.PortPosition.IsValid()
                                ? settlement.PortPosition.ToVec2()
                                : (settlement.GatePosition.IsValid() ? settlement.GatePosition.ToVec2() : settlement.GetPosition2D);
                            Sync.WorldSyncManager.ApplyRemotePartySettlementState(aiParty, null, exitPos, aiAtSea);
                        }
                        return;
                    }

                    var party = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.PeerId);
                    var hero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.PeerId);

                    switch (packet.ActionType)
                    {
                        case 1: // Enter Settlement
                            if (party != null && settlement != null)
                            {
                                bool isAtSea = party.IsCurrentlyAtSea;
                                Vec2 gateVec = isAtSea && settlement.HasPort && settlement.PortPosition.IsValid()
                                    ? settlement.PortPosition.ToVec2()
                                    : (settlement.GatePosition.IsValid() ? settlement.GatePosition.ToVec2() : settlement.GetPosition2D);
                                Sync.WorldSyncManager.ApplyRemotePartySettlementState(party, settlement, gateVec, isAtSea);

                                string pName = hero?.Name?.ToString() ?? party.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", packet.PeerId);
                                if (!ModContext.IsServerRunning &&
                                    (Settlement.CurrentSettlement == settlement || MobileParty.MainParty?.CurrentSettlement == settlement))
                                {
                                    TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage(
                                        Loc.F("[Calradia-Connected] Oyuncu '{0}' bulunduğunuz yerleşkeye ({1}) giriş yaptı!",
                                              "[Calradia-Connected] Player '{0}' entered your settlement ({1})!", pName, settlement.Name),
                                        TaleWorlds.Library.Colors.Cyan));
                                }
                            }
                            break;

                        case 2: // Leave Settlement
                            if (party != null && settlement != null)
                            {
                                bool isAtSea = party.IsCurrentlyAtSea;
                                Vec2 gateVec = isAtSea && settlement.HasPort && settlement.PortPosition.IsValid()
                                    ? settlement.PortPosition.ToVec2()
                                    : (settlement.GatePosition.IsValid() ? settlement.GatePosition.ToVec2() : settlement.GetPosition2D);
                                Sync.WorldSyncManager.ApplyRemotePartySettlementState(party, null, gateVec, isAtSea);
                                party.SetLandNavigationAccess(true);

                                string pName = hero?.Name?.ToString() ?? party.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", packet.PeerId);
                                if (!ModContext.IsServerRunning &&
                                    (Settlement.CurrentSettlement == settlement || MobileParty.MainParty?.CurrentSettlement == settlement))
                                {
                                    TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage(
                                        Loc.F("[Calradia-Connected] Oyuncu '{0}' yerleşkeden ({1}) ayrıldı.",
                                              "[Calradia-Connected] Player '{0}' left settlement ({1}).", pName, settlement.Name),
                                        TaleWorlds.Library.Colors.Yellow));
                                }
                            }
                            break;

                        case 3: // Market Trade
                            if (settlement != null && !string.IsNullOrEmpty(packet.ItemOrTroopId) && packet.Count != 0)
                            {
                                var item = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<ItemObject>(packet.ItemOrTroopId);
                                if (item != null)
                                {
                                    int currentCount = settlement.ItemRoster.GetItemNumber(item);
                                    int delta = packet.Count;
                                    if (delta < 0 && currentCount + delta < 0)
                                    {
                                        delta = -currentCount;
                                    }
                                    if (delta != 0)
                                    {
                                        settlement.ItemRoster.AddToCounts(item, delta);
                                    }
                                }
                            }
                            if (hero != null && packet.GoldDelta != 0)
                            {
                                hero.Gold = Math.Max(0, hero.Gold + packet.GoldDelta);
                            }
                            break;

                        case 4: // Recruit Volunteer
                            if (settlement != null)
                            {
                                var notable = !string.IsNullOrEmpty(packet.NotableId)
                                    ? settlement.Notables?.FirstOrDefault(n => n.StringId == packet.NotableId)
                                    : null;

                                if (notable?.VolunteerTypes != null)
                                {
                                    if (packet.SlotIndex >= 0 && packet.SlotIndex < notable.VolunteerTypes.Length)
                                    {
                                        notable.VolunteerTypes[packet.SlotIndex] = null;
                                    }
                                    else if (!string.IsNullOrEmpty(packet.ItemOrTroopId))
                                    {
                                        for (int i = 0; i < notable.VolunteerTypes.Length; i++)
                                        {
                                            if (notable.VolunteerTypes[i]?.StringId == packet.ItemOrTroopId)
                                            {
                                                notable.VolunteerTypes[i] = null;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            if (party != null && !string.IsNullOrEmpty(packet.ItemOrTroopId) && packet.Count > 0)
                            {
                                var troop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(packet.ItemOrTroopId);
                                if (troop != null)
                                {
                                    party.MemberRoster.AddToCounts(troop, packet.Count);
                                }
                            }

                            if (hero != null && packet.GoldDelta != 0)
                            {
                                hero.Gold = Math.Max(0, hero.Gold + packet.GoldDelta);
                            }
                            break;

                        case 5: // Port Fleet / Shipyard Action
                            if (settlement?.Town != null && packet.GoldDelta != 0)
                            {
                                int townDelta = Math.Max(-settlement.Town.Gold, -packet.GoldDelta);
                                if (townDelta != 0) settlement.Town.ChangeGold(townDelta);
                            }
                            if (hero != null && packet.GoldDelta != 0)
                            {
                                hero.Gold = Math.Max(0, hero.Gold + packet.GoldDelta);
                            }
                            party?.Party?.SetVisualAsDirty();
                            break;
                    }

                    // Server broadcasts settlement state mutations to all clients
                    if (ModContext.IsServerRunning)
                    {
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] SettlementSyncService hatası: {ex.Message}");
                }
            });
        }
    }
}
