using System;
using System.Collections.Generic;
using System.Text;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Sync;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// War Sails (NavalDLC v1.2.8) Phase 1 & Phase 2:
    /// Optional DLC runtime detection + synchronizes player fleets (PartyBase.Ships, hull, HP, sail HP,
    /// figureheads, sail patterns, upgrade pieces), AnchorPoint state, and sea/naval navigation state across server and clients.
    /// </summary>
    public sealed class NavalSyncService
    {
        private static NavalSyncService? _instance;
        public static NavalSyncService Instance => _instance ??= new NavalSyncService();

        public static bool IsNavalDlcActive
        {
            get
            {
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(asm.GetName().Name, "NavalDLC", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    var hulls = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObjectTypeList<ShipHull>();
                    return hulls != null && hulls.Count > 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static MobileParty.NavigationType SanitizeNavigationType(MobileParty party, byte rawNavType, bool isAtSea)
        {
            if (party == null) return MobileParty.NavigationType.Default;

            var requested = rawNavType switch
            {
                0 => MobileParty.NavigationType.None,
                2 => MobileParty.NavigationType.Naval,
                3 => MobileParty.NavigationType.All,
                _ => MobileParty.NavigationType.Default
            };

            if ((requested == MobileParty.NavigationType.Naval || requested == MobileParty.NavigationType.All) &&
                !party.HasNavalNavigationCapability)
            {
                return party.HasLandNavigationCapability
                    ? MobileParty.NavigationType.Default
                    : MobileParty.NavigationType.None;
            }

            if (requested == MobileParty.NavigationType.Default && isAtSea && party.HasNavalNavigationCapability)
            {
                return MobileParty.NavigationType.Naval;
            }

            if (requested == MobileParty.NavigationType.None && (party.HasLandNavigationCapability || party.HasNavalNavigationCapability))
            {
                return party.HasNavalNavigationCapability && isAtSea
                    ? MobileParty.NavigationType.Naval
                    : MobileParty.NavigationType.Default;
            }

            return requested;
        }

        public static string ComputeFleetSignature(MobileParty? party)
        {
            if (party == null || TaleWorlds.CampaignSystem.Campaign.Current == null)
            {
                return string.Empty;
            }

            try
            {
                var sb = new StringBuilder(128);
                sb.Append(party.IsCurrentlyAtSea ? '1' : '0')
                  .Append('|')
                  .Append((byte)party.DesiredAiNavigationType)
                  .Append('|');

                var anchor = party.Anchor;
                if (anchor != null && anchor.IsValid && !anchor.IsDisabled)
                {
                    var apos = anchor.Position.ToVec2();
                    sb.Append('A').Append((int)(apos.X * 10f)).Append(',').Append((int)(apos.Y * 10f));
                }
                else
                {
                    sb.Append("A0");
                }

                var ships = party.Ships;
                if (ships != null)
                {
                    for (int i = 0; i < ships.Count; i++)
                    {
                        var ship = ships[i];
                        if (ship == null) continue;
                        var hull = ((IShipOrigin)ship).Hull;
                        sb.Append(';')
                          .Append(hull?.StringId ?? string.Empty)
                          .Append(':')
                          .Append(ship.Name?.ToString() ?? string.Empty)
                          .Append(':')
                          .Append((int)ship.HitPoints)
                          .Append(':')
                          .Append((int)ship.SailHitPoints)
                          .Append(':')
                          .Append(ship.Figurehead?.StringId ?? string.Empty)
                          .Append(':')
                          .Append(ship.CustomSailPatternId ?? string.Empty)
                          .Append(':');

                        if (hull?.AvailableSlots != null)
                        {
                            foreach (string slotTag in hull.AvailableSlots.Keys)
                            {
                                var piece = ship.GetPieceAtSlot(slotTag);
                                if (piece != null)
                                {
                                    sb.Append(slotTag).Append('=').Append(piece.StringId).Append(',');
                                }
                            }
                        }
                    }
                }

                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        public PartyFleetSyncPacket? BuildFleetPacket(MobileParty? party, int peerId)
        {
            if (party == null || TaleWorlds.CampaignSystem.Campaign.Current == null) return null;

            bool hasAnchor = party.Anchor != null && party.Anchor.IsValid && !party.Anchor.IsDisabled;
            Vec2 anchorPos = hasAnchor ? party.Anchor!.Position.ToVec2() : Vec2.Zero;

            var packet = new PartyFleetSyncPacket
            {
                PeerId = peerId,
                PartyId = NetworkObjectRegistry.GetPlayerPartyId(peerId),
                IsAtSea = party.IsCurrentlyAtSea,
                NavigationType = (byte)party.DesiredAiNavigationType,
                HasAnchor = hasAnchor && anchorPos.IsValid,
                AnchorX = anchorPos.IsValid ? anchorPos.X : 0f,
                AnchorY = anchorPos.IsValid ? anchorPos.Y : 0f,
                Ships = new List<NavalShipSnapshotData>()
            };

            var ships = party.Ships;
            if (ships != null)
            {
                for (int i = 0; i < ships.Count; i++)
                {
                    var ship = ships[i];
                    if (ship == null) continue;

                    var hull = ((IShipOrigin)ship).Hull;
                    if (hull == null || string.IsNullOrEmpty(hull.StringId)) continue;

                    string upgradesCsv = string.Empty;
                    if (hull.AvailableSlots != null)
                    {
                        var upgradeParts = new List<string>();
                        foreach (string slotTag in hull.AvailableSlots.Keys)
                        {
                            var piece = ship.GetPieceAtSlot(slotTag);
                            if (piece != null && !string.IsNullOrEmpty(piece.StringId))
                            {
                                upgradeParts.Add($"{slotTag}={piece.StringId}");
                            }
                        }
                        if (upgradeParts.Count > 0)
                        {
                            upgradesCsv = string.Join(";", upgradeParts);
                        }
                    }

                    packet.Ships.Add(new NavalShipSnapshotData
                    {
                        HullId = hull.StringId,
                        Name = ship.Name?.ToString() ?? string.Empty,
                        HitPoints = ship.HitPoints,
                        SailHitPoints = ship.SailHitPoints,
                        FigureheadId = ship.Figurehead?.StringId ?? string.Empty,
                        CustomSailPatternId = ship.CustomSailPatternId ?? string.Empty,
                        UpgradesCsv = upgradesCsv
                    });
                }
            }

            return packet;
        }

        public void SyncLocalPlayerFleetToServer()
        {
            try
            {
                if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
                if (TaleWorlds.CampaignSystem.Campaign.Current == null || MobileParty.MainParty == null) return;

                int peerId = ModContext.Client?.LocalPeerId ?? 1001;
                var packet = BuildFleetPacket(MobileParty.MainParty, peerId);
                if (packet != null)
                {
                    ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] SyncLocalPlayerFleetToServer hatası: {ex.Message}");
            }
        }

        public void BroadcastAllExistingPlayerFleets()
        {
            if (!ModContext.IsServerRunning || TaleWorlds.CampaignSystem.Campaign.Current == null) return;

            try
            {
                foreach (var party in MobileParty.All)
                {
                    if (party == null || !party.IsActive || party.IsMainParty) continue;
                    if (!NetworkObjectRegistry.TryExtractPeerId(party.StringId, out int peerId)) continue;

                    var packet = BuildFleetPacket(party, peerId);
                    if (packet != null && (packet.Ships.Count > 0 || packet.IsAtSea || packet.HasAnchor))
                    {
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] BroadcastAllExistingPlayerFleets error: {ex.Message}");
            }
        }

        public void HandleFleetPacket(PartyFleetSyncPacket packet)
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

                    if (!ModContext.IsServerRunning && packet.PeerId == (ModContext.Client?.LocalPeerId ?? -1))
                    {
                        return;
                    }

                    if (WorldSyncManager.IsRecentlyDisconnectedParty(NetworkObjectRegistry.GetPlayerPartyId(packet.PeerId)))
                    {
                        return;
                    }

                    var party = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.PeerId)
                                ?? NetworkObjectRegistry.Instance.ResolveParty(packet.PartyId);

                    if (party != null && !party.IsMainParty)
                    {
                        ApplyFleetToParty(party, packet);
                    }

                    if (ModContext.IsServerRunning)
                    {
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] HandleFleetPacket hatası: {ex.Message}");
                }
            });
        }

        public static bool ApplyFleetToParty(MobileParty party, PartyFleetSyncPacket packet)
        {
            if (party == null || packet == null || TaleWorlds.CampaignSystem.Campaign.Current == null)
            {
                return false;
            }

            bool visualDirty = false;
            var objMgr = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager;

            if (party.IsCurrentlyAtSea != packet.IsAtSea)
            {
                try
                {
                    party.IsCurrentlyAtSea = packet.IsAtSea;
                    visualDirty = true;
                }
                catch { }
            }

            // Reconcile ships list if hulls exist in ObjectManager (e.g., War Sails DLC active)
            var currentShips = party.Ships;
            bool hullMismatch = currentShips == null || currentShips.Count != packet.Ships.Count;
            if (!hullMismatch && currentShips != null)
            {
                for (int i = 0; i < currentShips.Count; i++)
                {
                    var curHull = ((IShipOrigin)currentShips[i]).Hull;
                    if (!string.Equals(curHull?.StringId ?? string.Empty, packet.Ships[i].HullId ?? string.Empty, StringComparison.Ordinal))
                    {
                        hullMismatch = true;
                        break;
                    }
                }
            }

            if (hullMismatch && party.Party != null)
            {
                if (currentShips != null && currentShips.Count > 0)
                {
                    var toRemove = new List<Ship>(currentShips);
                    foreach (var oldShip in toRemove)
                    {
                        try { DestroyShipAction.ApplyByDiscard(oldShip); } catch { }
                    }
                }

                foreach (var dto in packet.Ships)
                {
                    if (string.IsNullOrEmpty(dto.HullId)) continue;
                    var hull = objMgr.GetObject<ShipHull>(dto.HullId);
                    if (hull == null) continue;

                    try
                    {
                        var ship = new Ship(hull);
                        ChangeShipOwnerAction.ApplyByMobilePartyCreation(party.Party, ship);
                    }
                    catch { }
                }
                visualDirty = true;
            }

            // Apply per-ship state (HP, Sail HP, Name, Figurehead, Sail pattern, Upgrade pieces)
            var reconciledShips = party.Ships;
            if (reconciledShips != null)
            {
                int count = Math.Min(reconciledShips.Count, packet.Ships.Count);
                for (int i = 0; i < count; i++)
                {
                    var ship = reconciledShips[i];
                    var dto = packet.Ships[i];
                    if (ship == null) continue;

                    if (!string.IsNullOrWhiteSpace(dto.Name) &&
                        !string.Equals(ship.Name?.ToString(), dto.Name, StringComparison.Ordinal))
                    {
                        try { ship.SetName(new TextObject(dto.Name)); } catch { }
                    }

                    try
                    {
                        ship.HitPoints = Math.Max(0f, Math.Min(ship.MaxHitPoints, dto.HitPoints));
                        ship.SailHitPoints = Math.Max(0f, Math.Min(ship.MaxSailHitPoints, dto.SailHitPoints));
                    }
                    catch { }

                    if (!string.Equals(ship.CustomSailPatternId ?? string.Empty, dto.CustomSailPatternId ?? string.Empty, StringComparison.Ordinal))
                    {
                        try
                        {
                            ship.CustomSailPatternId = dto.CustomSailPatternId ?? string.Empty;
                            visualDirty = true;
                        }
                        catch { }
                    }

                    string curFigureheadId = ship.Figurehead?.StringId ?? string.Empty;
                    if (!string.Equals(curFigureheadId, dto.FigureheadId ?? string.Empty, StringComparison.Ordinal))
                    {
                        try
                        {
                            var fh = !string.IsNullOrEmpty(dto.FigureheadId)
                                ? objMgr.GetObject<Figurehead>(dto.FigureheadId)
                                : null;
                            ship.ChangeFigurehead(fh);
                            visualDirty = true;
                        }
                        catch { }
                    }

                    var desiredUpgrades = ParseUpgradesCsv(dto.UpgradesCsv);
                    var hull = ((IShipOrigin)ship).Hull;
                    if (hull?.AvailableSlots != null)
                    {
                        foreach (string slotTag in hull.AvailableSlots.Keys)
                        {
                            desiredUpgrades.TryGetValue(slotTag, out string? targetPieceId);
                            targetPieceId ??= string.Empty;
                            string curPieceId = ship.GetPieceAtSlot(slotTag)?.StringId ?? string.Empty;
                            if (!string.Equals(curPieceId, targetPieceId, StringComparison.Ordinal))
                            {
                                try
                                {
                                    var piece = !string.IsNullOrEmpty(targetPieceId)
                                        ? objMgr.GetObject<ShipUpgradePiece>(targetPieceId)
                                        : null;
                                    ship.EquipUpgradePiece(slotTag, piece);
                                    visualDirty = true;
                                }
                                catch { }
                            }
                        }
                    }
                }
            }

            // Reconcile AnchorPoint position
            if (party.Anchor != null)
            {
                try
                {
                    if (packet.HasAnchor)
                    {
                        var anchorVec = new Vec2(packet.AnchorX, packet.AnchorY);
                        if (anchorVec.IsValid)
                        {
                            party.Anchor.Position = new CampaignVec2(anchorVec, isOnLand: false);
                        }
                    }
                    else if (party.Anchor.IsValid)
                    {
                        party.Anchor.ResetPosition();
                    }
                }
                catch { }
            }

            try
            {
                party.DesiredAiNavigationType = SanitizeNavigationType(party, packet.NavigationType, packet.IsAtSea);
            }
            catch { }

            if (visualDirty)
            {
                try { party.SetNavalVisualAsDirty(); } catch { }
                party.Party?.SetVisualAsDirty();
                party.Party?.SetLevelMaskIsDirty();
            }

            return visualDirty;
        }

        private static Dictionary<string, string> ParseUpgradesCsv(string? csv)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(csv)) return dict;

            var parts = csv!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq <= 0 || eq >= part.Length - 1) continue;
                string slot = part.Substring(0, eq).Trim();
                string pieceId = part.Substring(eq + 1).Trim();
                if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(pieceId))
                {
                    dict[slot] = pieceId;
                }
            }
            return dict;
        }
    }
}
