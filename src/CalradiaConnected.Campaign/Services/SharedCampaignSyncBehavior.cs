using System;
using System.Collections.Generic;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Bridges Bannerlord campaign events (recruitment, settlement visits, inventory trades,
    /// sieges, battles, and settlement lord menus) into the multiplayer synchronization pipeline.
    /// </summary>
    public sealed class SharedCampaignSyncBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            SettlementSyncService.Instance.EnsureSubscribed();
            RosterSyncService.Instance.EnsureSubscribed();
            SiegeSyncService.Instance.EnsureSubscribed();
            BattleSyncService.Instance.EnsureSubscribed();

            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.OnTroopRecruitedEvent.AddNonSerializedListener(this, OnTroopRecruited);
            CampaignEvents.OnUnitRecruitedEvent.AddNonSerializedListener(this, OnUnitRecruited);
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEntered);
            CampaignEvents.OnSettlementLeftEvent.AddNonSerializedListener(this, OnSettlementLeft);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnMobilePartyDestroyed);
            CampaignEvents.PlayerInventoryExchangeEvent.AddNonSerializedListener(this, OnPlayerInventoryExchange);
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, OnSiegeEventStarted);
            CampaignEvents.OnSiegeEventEndedEvent.AddNonSerializedListener(this, OnSiegeEventEnded);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.KingdomCreatedEvent.AddNonSerializedListener(this, KingdomDiplomacySyncService.Instance.OnLocalKingdomCreated);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, KingdomDiplomacySyncService.Instance.OnLocalClanChangedKingdom);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, KingdomDiplomacySyncService.Instance.OnLocalSettlementOwnerChanged);
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, KingdomDiplomacySyncService.Instance.OnLocalWarDeclared);
            CampaignEvents.MakePeace.AddNonSerializedListener(this, KingdomDiplomacySyncService.Instance.OnLocalMakePeace);
            CampaignEvents.ConversationEnded.AddNonSerializedListener(this, OnConversationEnded);
            CampaignEvents.BarterablesRequested.AddNonSerializedListener(this, PlayerInteractionService.Instance.AppendAffordablePlayerBarterables);
            CampaignEvents.OnShipCreatedEvent.AddNonSerializedListener(this, (ship, settlement) =>
            {
                if (ship?.Owner == PartyBase.MainParty)
                {
                    NotifyPortFleetChanged(settlement, ship?.Owner);
                }
            });
            CampaignEvents.OnShipOwnerChangedEvent.AddNonSerializedListener(this, (ship, oldOwner, detail) =>
            {
                if (oldOwner == PartyBase.MainParty || ship?.Owner == PartyBase.MainParty)
                {
                    NotifyFleetChangedIfLocal(PartyBase.MainParty);
                }
            });
            CampaignEvents.OnShipRepairedEvent.AddNonSerializedListener(this, (ship, settlement) =>
            {
                if (ship?.Owner == PartyBase.MainParty)
                {
                    NotifyPortFleetChanged(settlement, ship?.Owner);
                }
            });
            CampaignEvents.OnShipDestroyedEvent.AddNonSerializedListener(this, (party, ship, detail) => NotifyFleetChangedIfLocal(party));
        }

        private static void NotifyPortFleetChanged(Settlement? settlement, PartyBase? owner)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            if (owner != null && owner != PartyBase.MainParty) return;

            NavalSyncService.Instance.SyncLocalPlayerFleetToServer();
            if (settlement != null)
            {
                ModContext.Events.Publish(new LocalSettlementActionEvent
                {
                    ActionType = 5, // Port Fleet / Shipyard Action
                    SettlementId = settlement.StringId,
                    GoldDelta = 0
                });
            }
            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "PortFleetChanged" });
        }

        private static void NotifyFleetChangedIfLocal(PartyBase? owner)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            if (owner != null && owner != PartyBase.MainParty) return;
            NavalSyncService.Instance.SyncLocalPlayerFleetToServer();
        }

        private void OnConversationEnded(IEnumerable<CharacterObject> characters)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            PlayerInteractionService.Instance.NotifyNativeConversationEnded();
        }

        public override void SyncData(IDataStore dataStore)
        {
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            if (starter == null) return;

            DeveloperClanService.Instance.EnsureDeveloperClanExists();
            PlayerInteractionService.Instance.RegisterConversationDialogs(starter);

            try
            {
                foreach (string menuId in new[] { "town", "castle", "village" })
                {
                    starter.AddGameMenuOption(
                        menuId,
                        $"cc_interact_players_{menuId}",
                        Loc.T("Lordlarla İletişime Geç", "Contact Lords"),
                        (MenuCallbackArgs args) =>
                        {
                            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return false;
                            args.optionLeaveType = GameMenuOption.LeaveType.Conversation;
                            var here = PlayerInteractionService.Instance.GetOtherPlayersInSettlement(Settlement.CurrentSettlement);
                            if (here.Count > 0)
                            {
                                args.Tooltip = new TextObject(Loc.F(
                                    "Bu yerleşkede {0} lord bulunuyor. Görüşmek, ticaret yapmak veya düello teklif etmek için tıklayın.",
                                    "{0} other lord(s) are inside this settlement. Click to speak, trade, or duel.",
                                    here.Count));
                            }
                            else
                            {
                                args.Tooltip = new TextObject(Loc.T(
                                    "Diyardaki diğer lordlarla iletişime geçin.",
                                    "Speak with other lords across the realm."));
                            }
                            return true;
                        },
                        (MenuCallbackArgs args) =>
                        {
                            PlayerInteractionService.Instance.OpenSettlementPlayersMenu(Settlement.CurrentSettlement);
                        },
                        isLeave: false,
                        index: 1);
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] OnSessionLaunched menü kaydı hatası: {ex.Message}");
            }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            BattleSyncService.Instance.NotifyLocalMapEventEnded(mapEvent);
        }

        private void OnTroopRecruited(Hero recruiterHero, Settlement recruitmentSettlement, Hero recruitmentSource, CharacterObject troop, int amount)
        {
            try
            {
                if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
                if (recruiterHero != Hero.MainHero || troop == null) return;

                int slotIndex = -1;
                if (recruitmentSource?.VolunteerTypes != null)
                {
                    for (int i = 0; i < recruitmentSource.VolunteerTypes.Length; i++)
                    {
                        if (recruitmentSource.VolunteerTypes[i] == troop)
                        {
                            slotIndex = i;
                            break;
                        }
                    }
                }

                int cost = TaleWorlds.CampaignSystem.Campaign.Current?.Models?.PartyWageModel != null
                    ? (int)TaleWorlds.CampaignSystem.Campaign.Current.Models.PartyWageModel.GetTroopRecruitmentCost(troop, recruiterHero).ResultNumber * Math.Max(1, amount)
                    : 50 * Math.Max(1, amount);

                ModContext.Events.Publish(new LocalVolunteerRecruitedEvent
                {
                    SettlementId = recruitmentSettlement?.StringId ?? Hero.MainHero?.CurrentSettlement?.StringId ?? string.Empty,
                    NotableId = recruitmentSource?.StringId ?? string.Empty,
                    SlotIndex = slotIndex,
                    TroopId = troop.StringId,
                    Cost = cost
                });
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] OnTroopRecruited hatası: {ex.Message}");
            }
        }

        private void OnUnitRecruited(CharacterObject character, int amount)
        {
            if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "OnUnitRecruited" });
        }

        private void OnSettlementEntered(MobileParty party, Settlement settlement, Hero hero)
        {
            if (party == null || settlement == null) return;

            if (ModContext.IsServerRunning)
            {
                if (party.IsActive &&
                    !party.IsMainParty &&
                    party.StringId != "cc_server_party" &&
                    !party.IsGarrison &&
                    !party.IsMilitia &&
                    !NetworkObjectRegistry.IsPlayerNetworkParty(party))
                {
                    SettlementSyncService.Instance.BroadcastServerAiSettlementChange(party, settlement, entered: true);
                }
                return;
            }

            if (!ModContext.IsClientConnected || !party.IsMainParty) return;

            ModContext.Events.Publish(new LocalSettlementActionEvent
            {
                ActionType = 1, // Enter
                SettlementId = settlement.StringId
            });
        }

        private void OnSettlementLeft(MobileParty party, Settlement settlement)
        {
            if (party == null || settlement == null) return;

            if (ModContext.IsServerRunning)
            {
                if (party.IsActive &&
                    !party.IsMainParty &&
                    party.StringId != "cc_server_party" &&
                    !party.IsGarrison &&
                    !party.IsMilitia &&
                    !NetworkObjectRegistry.IsPlayerNetworkParty(party))
                {
                    SettlementSyncService.Instance.BroadcastServerAiSettlementChange(party, settlement, entered: false);
                }
                return;
            }

            if (!ModContext.IsClientConnected || !party.IsMainParty) return;

            ModContext.Events.Publish(new LocalSettlementActionEvent
            {
                ActionType = 2, // Leave
                SettlementId = settlement.StringId
            });
            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "SettlementLeft" });
        }

        private void OnMobilePartyDestroyed(MobileParty party, PartyBase destroyerParty)
        {
            if (!ModContext.IsServerRunning || party == null) return;
            SettlementSyncService.Instance.BroadcastServerAiPartyDestroyed(party);
        }

        private void OnPlayerInventoryExchange(List<(ItemRosterElement, int)> purchasedItems, List<(ItemRosterElement, int)> soldItems, bool isTrading)
        {
            try
            {
                if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;

                if (!isTrading)
                {
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "InventoryModified" });
                    return;
                }

                string settlementId = Settlement.CurrentSettlement?.StringId ?? Hero.MainHero?.CurrentSettlement?.StringId ?? string.Empty;
                if (string.IsNullOrEmpty(settlementId)) return;

                if (purchasedItems != null)
                {
                    foreach (var (elem, price) in purchasedItems)
                    {
                        var item = elem.EquipmentElement.Item;
                        if (item == null || elem.Amount <= 0) continue;

                        ModContext.Events.Publish(new LocalSettlementActionEvent
                        {
                            ActionType = 3, // Trade
                            SettlementId = settlementId,
                            ItemId = item.StringId,
                            Count = -elem.Amount, // Settlement loses purchased items
                            GoldDelta = -price
                        });
                    }
                }

                if (soldItems != null)
                {
                    foreach (var (elem, price) in soldItems)
                    {
                        var item = elem.EquipmentElement.Item;
                        if (item == null || elem.Amount <= 0) continue;

                        ModContext.Events.Publish(new LocalSettlementActionEvent
                        {
                            ActionType = 3, // Trade
                            SettlementId = settlementId,
                            ItemId = item.StringId,
                            Count = elem.Amount, // Settlement gains sold items
                            GoldDelta = price
                        });
                    }
                }

                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "InventoryExchange" });
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] OnPlayerInventoryExchange hatası: {ex.Message}");
            }
        }

        private void OnSiegeEventStarted(SiegeEvent siegeEvent)
        {
            try
            {
                if (siegeEvent?.BesiegedSettlement == null) return;
                var leaderParty = siegeEvent.BesiegerCamp?.LeaderParty;
                if (leaderParty == null) return;

                // Server broadcasts all sieges; Client sends only if local player started the siege
                if (ModContext.IsServerRunning || (ModContext.IsClientConnected && leaderParty.IsMainParty))
                {
                    ModContext.Events.Publish(new SiegeStateChangedEvent
                    {
                        SettlementId = siegeEvent.BesiegedSettlement.StringId,
                        BesiegerPartyId = leaderParty.StringId,
                        IsUnderSiege = true,
                        Progress = 0f
                    });
                }
            }
            catch { }
        }

        private void OnSiegeEventEnded(SiegeEvent siegeEvent)
        {
            try
            {
                if (siegeEvent?.BesiegedSettlement == null) return;
                var leaderParty = siegeEvent.BesiegerCamp?.LeaderParty;

                if (ModContext.IsServerRunning || (ModContext.IsClientConnected && (leaderParty == null || leaderParty.IsMainParty)))
                {
                    ModContext.Events.Publish(new SiegeStateChangedEvent
                    {
                        SettlementId = siegeEvent.BesiegedSettlement.StringId,
                        BesiegerPartyId = leaderParty?.StringId ?? string.Empty,
                        IsUnderSiege = false,
                        Progress = 0f
                    });
                }
            }
            catch { }
        }
    }
}
