using System;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Synchronizes siege camp initiation, progress, and completion across server and clients.
    /// </summary>
    public sealed class SiegeSyncService
    {
        private static SiegeSyncService? _instance;
        public static SiegeSyncService Instance => _instance ??= new SiegeSyncService();

        private bool _subscribed;

        public void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;

            ModContext.Events.Subscribe<SiegeStateChangedEvent>(OnSiegeStateChanged);
        }

        private void OnSiegeStateChanged(SiegeStateChangedEvent evt)
        {
            var packet = new SiegeStateSyncPacket
            {
                PeerId = ModContext.IsServerRunning ? 0 : (ModContext.Client?.LocalPeerId ?? 1001),
                SettlementId = evt.SettlementId,
                BesiegerPartyId = evt.BesiegerPartyId,
                IsUnderSiege = evt.IsUnderSiege,
                Progress = evt.Progress
            };

            if (ModContext.IsServerRunning)
            {
                ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else if (ModContext.IsClientConnected)
            {
                ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        public void HandlePacket(SiegeStateSyncPacket packet)
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

                    var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId);
                    var besieger = NetworkObjectRegistry.Instance.ResolveParty(packet.BesiegerPartyId);
                    if (settlement == null) return;

                    if (packet.IsUnderSiege)
                    {
                        if (settlement.SiegeEvent == null && besieger != null && besieger.IsActive)
                        {
                            try
                            {
                                TaleWorlds.CampaignSystem.Campaign.Current.SiegeEventManager?.StartSiegeEvent(settlement, besieger);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        if (settlement.SiegeEvent != null)
                        {
                            try
                            {
                                settlement.SiegeEvent.FinalizeSiegeEvent();
                            }
                            catch { }
                        }
                    }

                    settlement.Party?.SetVisualAsDirty();

                    if (ModContext.IsServerRunning)
                    {
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] SiegeSyncService hatası: {ex.Message}");
                }
            });
        }
    }
}
