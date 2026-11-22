using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using GameCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Synchronizes player & AI kingdom creation, vassalage, mercenary service, settlement conquests,
    /// war/peace treaties, and full late-join diplomatic snapshots across Server and Clients.
    /// </summary>
    public sealed class KingdomDiplomacySyncService
    {
        private static KingdomDiplomacySyncService? _instance;
        public static KingdomDiplomacySyncService Instance => _instance ??= new KingdomDiplomacySyncService();

        private GameCampaign? _boundCampaign;
        private int _applyDepth;
        private readonly ConcurrentDictionary<string, string> _kingdomAliasMap = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public bool IsApplyingRemoteDiplomacy => _applyDepth > 0;

        private void EnsureCampaignBound()
        {
            var current = GameCampaign.Current;
            if (!ReferenceEquals(_boundCampaign, current))
            {
                _kingdomAliasMap.Clear();
                _boundCampaign = current;
            }
        }

        private bool IsCampaignReady()
        {
            var campaign = GameCampaign.Current;
            return campaign != null && campaign.GameStarted && campaign.SaveHandler?.IsSaving != true;
        }

        private static bool IsLocalPlayerFaction(IFaction? faction)
        {
            if (faction == null || Clan.PlayerClan == null) return false;
            if (ReferenceEquals(faction, Clan.PlayerClan)) return true;
            var playerKingdom = Clan.PlayerClan.Kingdom;
            if (playerKingdom != null && ReferenceEquals(faction, playerKingdom) && ReferenceEquals(playerKingdom.RulingClan, Clan.PlayerClan))
            {
                return true;
            }
            return false;
        }

        private static string GetCanonicalClanId(Clan? clan)
        {
            if (clan == null) return string.Empty;
            if (!ModContext.IsServerRunning && ReferenceEquals(clan, Clan.PlayerClan))
            {
                int localPeerId = ModContext.Client?.LocalPeerId ?? 0;
                if (localPeerId > 0)
                {
                    return NetworkObjectRegistry.GetPlayerClanId(localPeerId);
                }
            }
            return clan.StringId ?? string.Empty;
        }

        private static string GetCanonicalFactionId(IFaction? faction)
        {
            if (faction == null) return string.Empty;
            if (faction is Clan clan)
            {
                return GetCanonicalClanId(clan);
            }
            return faction.StringId ?? string.Empty;
        }

        private static string GetCanonicalHeroId(Hero? hero)
        {
            if (hero == null) return string.Empty;
            if (!ModContext.IsServerRunning && ReferenceEquals(hero, Hero.MainHero))
            {
                int localPeerId = ModContext.Client?.LocalPeerId ?? 0;
                if (localPeerId > 0)
                {
                    return NetworkObjectRegistry.GetPlayerHeroId(localPeerId);
                }
            }
            return hero.StringId ?? string.Empty;
        }

        private void SendOrBroadcast(KingdomDiplomacySyncPacket packet)
        {
            if (ModContext.IsServerRunning)
            {
                ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
            }
            else if (ModContext.IsClientConnected)
            {
                ModContext.Client?.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }

        #region Local Campaign Event Hooks

        public void OnLocalKingdomCreated(Kingdom? kingdom)
        {
            try
            {
                if (IsApplyingRemoteDiplomacy || !IsCampaignReady() || kingdom == null) return;
                EnsureCampaignBound();

                bool isServer = ModContext.IsServerRunning;
                if (!isServer)
                {
                    if (!ModContext.IsClientConnected) return;
                    if (kingdom.RulingClan != Clan.PlayerClan && kingdom.RulingClan?.Leader != Hero.MainHero) return;
                }

                int actorPeerId = isServer ? 0 : (ModContext.Client?.LocalPeerId ?? 0);
                var packet = new KingdomDiplomacySyncPacket
                {
                    ActionType = 1,
                    ActorPeerId = actorPeerId,
                    ClanId = GetCanonicalClanId(kingdom.RulingClan),
                    KingdomId = kingdom.StringId ?? string.Empty,
                    KingdomName = kingdom.Name?.ToString() ?? string.Empty,
                    InformalName = kingdom.InformalName?.ToString() ?? kingdom.Name?.ToString() ?? string.Empty,
                    CultureId = kingdom.Culture?.StringId ?? string.Empty,
                    BannerKey = kingdom.Banner?.Serialize() ?? string.Empty,
                    SettlementId = kingdom.InitialHomeSettlement?.StringId ?? string.Empty,
                    PrimaryColor = kingdom.Color,
                    SecondaryColor = kingdom.Color2
                };

                SendOrBroadcast(packet);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] KingdomDiplomacySync.OnLocalKingdomCreated hatası: {ex.Message}");
            }
        }

        public void OnLocalClanChangedKingdom(
            Clan? clan,
            Kingdom? oldKingdom,
            Kingdom? newKingdom,
            ChangeKingdomAction.ChangeKingdomActionDetail detail,
            bool showNotification)
        {
            try
            {
                if (IsApplyingRemoteDiplomacy || !IsCampaignReady() || clan == null) return;
                EnsureCampaignBound();

                // Kingdom creation already fires KingdomCreatedEvent with full metadata.
                if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.CreateKingdom) return;

                bool isServer = ModContext.IsServerRunning;
                if (!isServer)
                {
                    if (!ModContext.IsClientConnected) return;
                    if (clan != Clan.PlayerClan) return;
                }

                byte actionType;
                byte detailCode = 0;
                string kingdomId;
                string kingdomName;
                int mercMult = 0;

                if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.JoinAsMercenary)
                {
                    if (newKingdom == null) return;
                    actionType = 3;
                    kingdomId = newKingdom.StringId ?? string.Empty;
                    kingdomName = newKingdom.Name?.ToString() ?? string.Empty;
                    mercMult = clan.MercenaryAwardMultiplier;
                }
                else if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.JoinKingdom ||
                         detail == ChangeKingdomAction.ChangeKingdomActionDetail.JoinKingdomByDefection)
                {
                    if (newKingdom == null) return;
                    actionType = 2;
                    detailCode = (byte)(detail == ChangeKingdomAction.ChangeKingdomActionDetail.JoinKingdomByDefection ? 3 : 0);
                    kingdomId = newKingdom.StringId ?? string.Empty;
                    kingdomName = newKingdom.Name?.ToString() ?? string.Empty;
                }
                else
                {
                    // LeaveKingdom, LeaveAsMercenary, LeaveWithRebellion, LeaveByKingdomDestruction, LeaveByClanDestruction
                    actionType = 4;
                    if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveAsMercenary) detailCode = 4;
                    else if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveWithRebellion) detailCode = 2;
                    kingdomId = oldKingdom?.StringId ?? string.Empty;
                    kingdomName = oldKingdom?.Name?.ToString() ?? string.Empty;
                }

                var packet = new KingdomDiplomacySyncPacket
                {
                    ActionType = actionType,
                    ActorPeerId = isServer ? 0 : (ModContext.Client?.LocalPeerId ?? 0),
                    ClanId = GetCanonicalClanId(clan),
                    KingdomId = kingdomId,
                    KingdomName = kingdomName,
                    MercenaryAwardMultiplier = mercMult,
                    DetailCode = detailCode
                };

                SendOrBroadcast(packet);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] KingdomDiplomacySync.OnLocalClanChangedKingdom hatası: {ex.Message}");
            }
        }

        public void OnLocalSettlementOwnerChanged(
            Settlement? settlement,
            bool openToClaim,
            Hero? newOwner,
            Hero? oldOwner,
            Hero? capturerHero,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            try
            {
                if (IsApplyingRemoteDiplomacy || !IsCampaignReady() || settlement == null || newOwner == null) return;
                EnsureCampaignBound();

                // Villages automatically follow their bound town/castle.
                if (!settlement.IsTown && !settlement.IsCastle) return;

                bool isServer = ModContext.IsServerRunning;
                if (!isServer)
                {
                    if (!ModContext.IsClientConnected) return;
                    bool involvesLocalPlayer =
                        ReferenceEquals(newOwner, Hero.MainHero) ||
                        ReferenceEquals(newOwner.Clan, Clan.PlayerClan) ||
                        ReferenceEquals(capturerHero, Hero.MainHero) ||
                        (Clan.PlayerClan?.Kingdom != null &&
                         ReferenceEquals(Clan.PlayerClan.Kingdom.RulingClan, Clan.PlayerClan) &&
                         ReferenceEquals(newOwner.Clan?.Kingdom, Clan.PlayerClan.Kingdom));

                    if (!involvesLocalPlayer) return;
                }

                byte detailCode = 0;
                if (detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege) detailCode = 1;
                else if (detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByRebellion) detailCode = 2;

                var packet = new KingdomDiplomacySyncPacket
                {
                    ActionType = 5,
                    ActorPeerId = isServer ? 0 : (ModContext.Client?.LocalPeerId ?? 0),
                    SettlementId = settlement.StringId ?? string.Empty,
                    ClanId = GetCanonicalClanId(newOwner.Clan),
                    TargetFactionId = GetCanonicalHeroId(newOwner),
                    KingdomId = newOwner.Clan?.Kingdom?.StringId ?? string.Empty,
                    DetailCode = detailCode
                };

                SendOrBroadcast(packet);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] KingdomDiplomacySync.OnLocalSettlementOwnerChanged hatası: {ex.Message}");
            }
        }

        public void OnLocalWarDeclared(
            IFaction? faction1,
            IFaction? faction2,
            DeclareWarAction.DeclareWarDetail detail)
        {
            try
            {
                if (IsApplyingRemoteDiplomacy || !IsCampaignReady() || faction1 == null || faction2 == null) return;
                if (faction1.IsBanditFaction || faction2.IsBanditFaction) return;
                EnsureCampaignBound();

                bool isServer = ModContext.IsServerRunning;
                if (!isServer)
                {
                    if (!ModContext.IsClientConnected) return;
                    bool playerInitiatedHostility =
                        detail == DeclareWarAction.DeclareWarDetail.CausedByPlayerHostility ||
                        detail == DeclareWarAction.DeclareWarDetail.CausedByKingdomCreation ||
                        detail == DeclareWarAction.DeclareWarDetail.CausedByRebellion;
                    bool involvesPlayerKingdom = Clan.PlayerClan?.Kingdom != null &&
                                                 (ReferenceEquals(faction1, Clan.PlayerClan.Kingdom) || ReferenceEquals(faction2, Clan.PlayerClan.Kingdom));

                    if (!IsLocalPlayerFaction(faction1) && !IsLocalPlayerFaction(faction2) &&
                        !(playerInitiatedHostility && involvesPlayerKingdom))
                    {
                        return;
                    }
                }

                byte detailCode = (byte)(detail == DeclareWarAction.DeclareWarDetail.CausedByKingdomCreation ? 5 : 0);
                var packet = new KingdomDiplomacySyncPacket
                {
                    ActionType = 6,
                    ActorPeerId = isServer ? 0 : (ModContext.Client?.LocalPeerId ?? 0),
                    ClanId = GetCanonicalFactionId(faction1),
                    TargetFactionId = GetCanonicalFactionId(faction2),
                    DetailCode = detailCode
                };

                SendOrBroadcast(packet);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] KingdomDiplomacySync.OnLocalWarDeclared hatası: {ex.Message}");
            }
        }

        public void OnLocalMakePeace(
            IFaction? faction1,
            IFaction? faction2,
            MakePeaceAction.MakePeaceDetail detail)
        {
            try
            {
                if (IsApplyingRemoteDiplomacy || !IsCampaignReady() || faction1 == null || faction2 == null) return;
                if (faction1.IsBanditFaction || faction2.IsBanditFaction) return;
                EnsureCampaignBound();

                bool isServer = ModContext.IsServerRunning;
                if (!isServer)
                {
                    if (!ModContext.IsClientConnected) return;
                    if (!IsLocalPlayerFaction(faction1) && !IsLocalPlayerFaction(faction2)) return;
                }

                var packet = new KingdomDiplomacySyncPacket
                {
                    ActionType = 7,
                    ActorPeerId = isServer ? 0 : (ModContext.Client?.LocalPeerId ?? 0),
                    ClanId = GetCanonicalFactionId(faction1),
                    TargetFactionId = GetCanonicalFactionId(faction2)
                };

                SendOrBroadcast(packet);
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] KingdomDiplomacySync.OnLocalMakePeace hatası: {ex.Message}");
            }
        }

        #endregion

        #region Inbound Packet Application

        public void HandlePacket(KingdomDiplomacySyncPacket packet)
        {
            if (packet == null) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (!IsCampaignReady()) return;
                    EnsureCampaignBound();

                    bool isServer = ModContext.IsServerRunning;
                    int localPeerId = ModContext.Client?.LocalPeerId ?? -1;

                    // Ignore our own echoed actions on client (except authoritative full snapshots)
                    if (!isServer && packet.ActionType != 8 && packet.ActorPeerId > 0 && packet.ActorPeerId == localPeerId)
                    {
                        return;
                    }

                    // Server-side validation to prevent clients from overwriting unrelated AI diplomacy
                    if (isServer && packet.ActorPeerId > 0 && !ValidateClientPacketOnServer(packet))
                    {
                        return;
                    }

                    bool applied = false;
                    _applyDepth++;
                    try
                    {
                        switch (packet.ActionType)
                        {
                            case 1:
                                applied = ApplyCreateKingdom(packet, notify: true);
                                break;
                            case 2:
                                applied = ApplyJoinKingdom(packet, asMercenary: false, notify: true);
                                break;
                            case 3:
                                applied = ApplyJoinKingdom(packet, asMercenary: true, notify: true);
                                break;
                            case 4:
                                applied = ApplyLeaveKingdom(packet, notify: true);
                                break;
                            case 5:
                                applied = ApplySettlementOwnerChanged(packet, notify: true);
                                break;
                            case 6:
                                applied = ApplyWarDeclared(packet, notify: true);
                                break;
                            case 7:
                                applied = ApplyPeaceDeclared(packet, notify: true);
                                break;
                            case 8:
                                if (!isServer)
                                {
                                    ApplyFullStateSnapshot(packet.SnapshotPayload);
                                }
                                break;
                        }
                    }
                    finally
                    {
                        _applyDepth = Math.Max(0, _applyDepth - 1);
                    }

                    if (isServer && packet.ActionType != 8 && applied)
                    {
                        ModContext.Server?.Broadcast(packet, DeliveryMethod.ReliableOrdered);
                    }
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] KingdomDiplomacySync.HandlePacket({packet.ActionType}) hatası: {ex.Message}");
                }
            });
        }

        private bool ValidateClientPacketOnServer(KingdomDiplomacySyncPacket packet)
        {
            int actorPeerId = packet.ActorPeerId;
            if (actorPeerId <= 0) return false;

            var senderHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(actorPeerId);
            var senderClan = senderHero?.Clan ?? NetworkObjectRegistry.Instance.ResolveClan(NetworkObjectRegistry.GetPlayerClanId(actorPeerId));
            if (senderClan == null) return false;

            switch (packet.ActionType)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                {
                    var targetClan = ResolveClan(packet.ClanId, actorPeerId);
                    return targetClan != null && ReferenceEquals(targetClan, senderClan);
                }
                case 5:
                {
                    var newOwnerClan = ResolveClan(packet.ClanId, actorPeerId);
                    if (newOwnerClan == null) return false;
                    if (ReferenceEquals(newOwnerClan, senderClan)) return true;
                    return senderClan.Kingdom != null &&
                           ReferenceEquals(senderClan.Kingdom.RulingClan, senderClan) &&
                           ReferenceEquals(newOwnerClan.Kingdom, senderClan.Kingdom);
                }
                case 6:
                case 7:
                {
                    var f1 = ResolveFaction(packet.ClanId);
                    var f2 = ResolveFaction(packet.TargetFactionId);
                    if (f1 == null || f2 == null) return false;
                    bool senderMatches1 = ReferenceEquals(f1, senderClan) || (senderClan.Kingdom != null && ReferenceEquals(f1, senderClan.Kingdom));
                    bool senderMatches2 = ReferenceEquals(f2, senderClan) || (senderClan.Kingdom != null && ReferenceEquals(f2, senderClan.Kingdom));
                    if (!senderMatches1 && !senderMatches2) return false;

                    // For peace, require sender to be either the independent clan or ruling clan of their kingdom
                    if (packet.ActionType == 7)
                    {
                        bool canMakePeace1 = ReferenceEquals(f1, senderClan) ||
                                             (senderClan.Kingdom != null && ReferenceEquals(f1, senderClan.Kingdom) && ReferenceEquals(senderClan.Kingdom.RulingClan, senderClan));
                        bool canMakePeace2 = ReferenceEquals(f2, senderClan) ||
                                             (senderClan.Kingdom != null && ReferenceEquals(f2, senderClan.Kingdom) && ReferenceEquals(senderClan.Kingdom.RulingClan, senderClan));
                        return canMakePeace1 || canMakePeace2;
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        private bool ApplyCreateKingdom(KingdomDiplomacySyncPacket packet, bool notify)
        {
            var founderClan = ResolveClan(packet.ClanId, packet.ActorPeerId);
            if (founderClan == null) return false;

            string displayName = string.IsNullOrWhiteSpace(packet.KingdomName)
                ? $"{founderClan.Name}"
                : packet.KingdomName.Trim();
            string informalName = string.IsNullOrWhiteSpace(packet.InformalName)
                ? displayName
                : packet.InformalName.Trim();

            // Check if this exact kingdom already exists for this founder clan
            var existing = FindExistingKingdomForCreation(packet.KingdomId, displayName, founderClan);
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(packet.KingdomId) && !string.Equals(existing.StringId, packet.KingdomId, StringComparison.Ordinal))
                {
                    _kingdomAliasMap[packet.KingdomId] = existing.StringId;
                }
                if (!ReferenceEquals(founderClan.Kingdom, existing))
                {
                    LeaveCurrentKingdomIfNeeded(founderClan);
                    ChangeKingdomAction.ApplyByCreateKingdom(founderClan, existing, showNotification: false);
                }
                return true;
            }

            // Leave any prior kingdom first to keep Bannerlord invariants clean
            LeaveCurrentKingdomIfNeeded(founderClan);

            var campaign = GameCampaign.Current;
            if (campaign?.KingdomManager == null) return false;

            var culture = ResolveCulture(packet.CultureId, founderClan);
            if (culture == null) return false;

            Banner? banner = null;
            if (!string.IsNullOrWhiteSpace(packet.BannerKey))
            {
                try { banner = new Banner(packet.BannerKey); } catch { }
            }
            banner ??= founderClan.Banner ?? Banner.CreateRandomClanBanner();

            string desiredStringId = !string.IsNullOrWhiteSpace(packet.KingdomId)
                ? packet.KingdomId
                : $"cc_kingdom_{Math.Max(1, packet.ActorPeerId)}_{Math.Abs(displayName.GetHashCode()) % 10000}";

            // Avoid StringId collision with an unrelated existing kingdom
            if (Kingdom.All.Any(k => k != null && string.Equals(k.StringId, desiredStringId, StringComparison.Ordinal)))
            {
                desiredStringId = $"cc_kingdom_{Math.Max(1, packet.ActorPeerId)}_{Environment.TickCount & 0xFFFF}";
            }

            uint? c1 = packet.PrimaryColor != 0 ? packet.PrimaryColor : (uint?)founderClan.Color;
            uint? c2 = packet.SecondaryColor != 0 ? packet.SecondaryColor : (uint?)founderClan.Color2;

            var created = campaign.KingdomManager.CreateKingdom(
                kingdomName: new TextObject(displayName),
                informalName: new TextObject(informalName),
                culture: culture,
                founderClan: founderClan,
                initialPolicies: null,
                encyclopediaText: null,
                formalName: new TextObject(displayName),
                encyclopediaRulerTitle: null,
                stringId: desiredStringId,
                banner: banner,
                color1: c1,
                color2: c2,
                primaryBannerColor: c1,
                secondaryBannerColor: c2);

            if (created == null) return false;

            if (!string.IsNullOrEmpty(packet.KingdomId) && !string.Equals(created.StringId, packet.KingdomId, StringComparison.Ordinal))
            {
                _kingdomAliasMap[packet.KingdomId] = created.StringId;
            }

            // Keep packet.KingdomId aligned with the authoritative server StringId before broadcast
            if (ModContext.IsServerRunning && !string.IsNullOrEmpty(created.StringId))
            {
                packet.KingdomId = created.StringId;
            }

            if (!ReferenceEquals(founderClan.Kingdom, created))
            {
                ChangeKingdomAction.ApplyByCreateKingdom(founderClan, created, showNotification: false);
            }

            if (notify)
            {
                string actorName = founderClan.Leader?.Name?.ToString() ?? founderClan.Name?.ToString() ?? "Lord";
                DisplayDiplomacyMessage(
                    Loc.F("{0} yeni bir krallık kurdu: {1}!", "{0} founded a new kingdom: {1}!", actorName, displayName),
                    Colors.Yellow);
            }

            return true;
        }

        private bool ApplyJoinKingdom(KingdomDiplomacySyncPacket packet, bool asMercenary, bool notify)
        {
            var clan = ResolveClan(packet.ClanId, packet.ActorPeerId);
            var kingdom = ResolveKingdom(packet.KingdomId, packet.KingdomName, null);
            if (clan == null || kingdom == null || kingdom.IsEliminated) return false;

            bool alreadyInTarget = ReferenceEquals(clan.Kingdom, kingdom) && clan.IsUnderMercenaryService == asMercenary;
            if (alreadyInTarget) return true;

            LeaveCurrentKingdomIfNeeded(clan);

            if (asMercenary)
            {
                int awardMult = packet.MercenaryAwardMultiplier > 0 ? packet.MercenaryAwardMultiplier : 100;
                ChangeKingdomAction.ApplyByJoinFactionAsMercenary(clan, kingdom, CampaignTime.Zero, awardMult, showNotification: false);
                if (notify)
                {
                    DisplayDiplomacyMessage(
                        Loc.F("{0}, {1} krallığına paralı asker olarak katıldı.", "{0} entered mercenary service for {1}.",
                            clan.Name?.ToString() ?? clan.StringId, kingdom.Name?.ToString() ?? kingdom.StringId),
                        Colors.Cyan);
                }
            }
            else
            {
                ChangeKingdomAction.ApplyByJoinToKingdom(clan, kingdom, CampaignTime.Zero, showNotification: false);
                if (notify)
                {
                    DisplayDiplomacyMessage(
                        Loc.F("{0}, {1} krallığına vasal olarak katıldı.", "{0} swore fealty to {1} as a vassal.",
                            clan.Name?.ToString() ?? clan.StringId, kingdom.Name?.ToString() ?? kingdom.StringId),
                        Colors.Cyan);
                }
            }

            return true;
        }

        private bool ApplyLeaveKingdom(KingdomDiplomacySyncPacket packet, bool notify)
        {
            var clan = ResolveClan(packet.ClanId, packet.ActorPeerId);
            if (clan == null) return false;
            if (clan.Kingdom == null) return true;

            string oldKingdomName = clan.Kingdom.Name?.ToString() ?? packet.KingdomName ?? string.Empty;

            if (clan.IsUnderMercenaryService || packet.DetailCode == 4)
            {
                ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(clan, showNotification: false);
            }
            else if (packet.DetailCode == 2)
            {
                ChangeKingdomAction.ApplyByLeaveWithRebellionAgainstKingdom(clan, showNotification: false);
            }
            else
            {
                ChangeKingdomAction.ApplyByLeaveKingdom(clan, showNotification: false);
            }

            if (notify && !string.IsNullOrEmpty(oldKingdomName))
            {
                DisplayDiplomacyMessage(
                    Loc.F("{0}, {1} krallığından ayrıldı.", "{0} left {1}.",
                        clan.Name?.ToString() ?? clan.StringId, oldKingdomName),
                    Colors.Yellow);
            }

            return true;
        }

        private bool ApplySettlementOwnerChanged(KingdomDiplomacySyncPacket packet, bool notify)
        {
            var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId);
            if (settlement == null || (!settlement.IsTown && !settlement.IsCastle)) return false;

            var newOwnerClan = ResolveClan(packet.ClanId, packet.ActorPeerId);
            Hero? newOwner = !string.IsNullOrEmpty(packet.TargetFactionId)
                ? ResolveHero(packet.TargetFactionId, packet.ActorPeerId)
                : null;
            newOwner ??= newOwnerClan?.Leader;
            if (newOwner == null) return false;

            if (ReferenceEquals(settlement.OwnerClan, newOwner.Clan)) return true;

            if (packet.DetailCode == 1)
            {
                ChangeOwnerOfSettlementAction.ApplyBySiege(newOwner, newOwner, settlement);
            }
            else
            {
                ChangeOwnerOfSettlementAction.ApplyByDefault(newOwner, settlement);
            }

            try { settlement.Party?.SetVisualAsDirty(); } catch { }

            if (notify)
            {
                string ownerLabel = newOwner.Clan?.Kingdom?.Name?.ToString()
                                    ?? newOwner.Clan?.Name?.ToString()
                                    ?? newOwner.Name?.ToString()
                                    ?? "Lord";
                string settlementName = settlement.Name?.ToString() ?? settlement.StringId;

                if (packet.DetailCode == 1)
                {
                    DisplayDiplomacyMessage(
                        Loc.F("{0}, {1} yerleşkesini fethetti!", "{0} conquered {1}!", ownerLabel, settlementName),
                        Colors.Magenta);
                }
                else
                {
                    DisplayDiplomacyMessage(
                        Loc.F("{1} artık {0} yönetimine geçti.", "{0} now rules {1}.", ownerLabel, settlementName),
                        Colors.Cyan);
                }
            }

            return true;
        }

        private bool ApplyWarDeclared(KingdomDiplomacySyncPacket packet, bool notify)
        {
            var f1 = ResolveFaction(packet.ClanId);
            var f2 = ResolveFaction(packet.TargetFactionId);
            if (f1 == null || f2 == null || ReferenceEquals(f1, f2)) return false;
            if (f1.IsEliminated || f2.IsEliminated) return false;

            if (FactionManager.IsAtWarAgainstFaction(f1, f2)) return true;

            DeclareWarAction.ApplyByDefault(f1, f2);

            if (notify)
            {
                DisplayDiplomacyMessage(
                    Loc.F("{0}, {1} tarafına savaş ilan etti!", "{0} declared war on {1}!",
                        f1.Name?.ToString() ?? f1.StringId, f2.Name?.ToString() ?? f2.StringId),
                    Colors.Red);
            }

            return true;
        }

        private bool ApplyPeaceDeclared(KingdomDiplomacySyncPacket packet, bool notify)
        {
            var f1 = ResolveFaction(packet.ClanId);
            var f2 = ResolveFaction(packet.TargetFactionId);
            if (f1 == null || f2 == null || ReferenceEquals(f1, f2)) return false;

            if (!FactionManager.IsAtWarAgainstFaction(f1, f2)) return true;

            MakePeaceAction.Apply(f1, f2);

            if (notify)
            {
                DisplayDiplomacyMessage(
                    Loc.F("{0} ile {1} barış yaptı.", "{0} and {1} made peace.",
                        f1.Name?.ToString() ?? f1.StringId, f2.Name?.ToString() ?? f2.StringId),
                    Colors.Green);
            }

            return true;
        }

        #endregion

        #region Late-Join Full State Snapshot

        public void SendFullStateToPeer(int peerId)
        {
            if (!ModContext.IsServerRunning || peerId <= 0) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (!IsCampaignReady()) return;
                    string payload = BuildFullStateSnapshotPayload();
                    if (string.IsNullOrEmpty(payload)) return;

                    var packet = new KingdomDiplomacySyncPacket
                    {
                        ActionType = 8,
                        ActorPeerId = 0,
                        SnapshotPayload = payload
                    };
                    ModContext.Server?.SendTo(peerId, packet, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] KingdomDiplomacySync.SendFullStateToPeer({peerId}) hatası: {ex.Message}");
                }
            });
        }

        public string BuildFullStateSnapshotPayload()
        {
            if (GameCampaign.Current == null) return string.Empty;
            var sb = new StringBuilder(4096);

            // 1. Custom / player-founded kingdoms
            if (Kingdom.All != null)
            {
                foreach (var k in Kingdom.All)
                {
                    if (k == null || k.IsEliminated || k.RulingClan == null) continue;
                    bool isCustomOrPlayer =
                        IsPlayerClan(k.RulingClan) ||
                        (k.StringId != null && (k.StringId.StartsWith("new_kingdom", StringComparison.Ordinal) ||
                                                k.StringId.StartsWith("cc_kingdom_", StringComparison.Ordinal)));
                    if (!isCustomOrPlayer) continue;

                    sb.Append("K|")
                      .Append(Sanitize(k.StringId)).Append('|')
                      .Append(Sanitize(k.Name?.ToString())).Append('|')
                      .Append(Sanitize(k.InformalName?.ToString() ?? k.Name?.ToString())).Append('|')
                      .Append(Sanitize(k.Culture?.StringId)).Append('|')
                      .Append(Sanitize(k.RulingClan.StringId)).Append('|')
                      .Append(Sanitize(k.Banner?.Serialize())).Append('|')
                      .Append(k.Color.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(k.Color2.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(Sanitize(k.InitialHomeSettlement?.StringId))
                      .Append('\n');
                }
            }

            // 2. Clan kingdom memberships (all active player clans + non-bandit noble clans)
            if (Clan.All != null)
            {
                foreach (var c in Clan.All)
                {
                    if (c == null || c.IsEliminated || c.IsBanditFaction) continue;
                    if (!IsPlayerClan(c) && !c.IsNoble && !c.IsClanTypeMercenary) continue;

                    string kId = c.Kingdom != null && !c.Kingdom.IsEliminated ? c.Kingdom.StringId : "NONE";
                    sb.Append("M|")
                      .Append(Sanitize(c.StringId)).Append('|')
                      .Append(Sanitize(kId)).Append('|')
                      .Append(c.IsUnderMercenaryService ? "1" : "0").Append('|')
                      .Append(c.MercenaryAwardMultiplier.ToString(CultureInfo.InvariantCulture))
                      .Append('\n');
                }
            }

            // 3. Town & Castle owners
            if (Settlement.All != null)
            {
                foreach (var s in Settlement.All)
                {
                    if (s == null || (!s.IsTown && !s.IsCastle) || s.OwnerClan == null) continue;
                    sb.Append("S|")
                      .Append(Sanitize(s.StringId)).Append('|')
                      .Append(Sanitize(s.OwnerClan.StringId)).Append('|')
                      .Append(Sanitize(s.OwnerClan.Leader?.StringId))
                      .Append('\n');
                }
            }

            // 4. Active wars among non-eliminated Kingdoms and independent Player Clans
            var trackedFactions = CollectTrackedDiplomaticFactions();
            var emittedPairs = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < trackedFactions.Count; i++)
            {
                var f1 = trackedFactions[i];
                for (int j = i + 1; j < trackedFactions.Count; j++)
                {
                    var f2 = trackedFactions[j];
                    if (FactionManager.IsAtWarAgainstFaction(f1, f2))
                    {
                        string pairKey = MakeCanonicalPairKey(f1.StringId, f2.StringId);
                        if (emittedPairs.Add(pairKey))
                        {
                            sb.Append("W|")
                              .Append(Sanitize(f1.StringId)).Append('|')
                              .Append(Sanitize(f2.StringId))
                              .Append('\n');
                        }
                    }
                }
            }

            return sb.ToString();
        }

        private void ApplyFullStateSnapshot(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return;

            var lines = payload!.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var serverWarPairs = new HashSet<string>(StringComparer.Ordinal);

            // Pass 1: Custom kingdoms
            foreach (string rawLine in lines)
            {
                if (!rawLine.StartsWith("K|", StringComparison.Ordinal)) continue;
                var parts = rawLine.Split('|');
                if (parts.Length < 10) continue;

                uint.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint c1);
                uint.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint c2);

                ApplyCreateKingdom(new KingdomDiplomacySyncPacket
                {
                    ActionType = 1,
                    KingdomId = Unsanitize(parts[1]),
                    KingdomName = Unsanitize(parts[2]),
                    InformalName = Unsanitize(parts[3]),
                    CultureId = Unsanitize(parts[4]),
                    ClanId = Unsanitize(parts[5]),
                    BannerKey = Unsanitize(parts[6]),
                    PrimaryColor = c1,
                    SecondaryColor = c2,
                    SettlementId = Unsanitize(parts[9])
                }, notify: false);
            }

            // Pass 2: Clan memberships
            foreach (string rawLine in lines)
            {
                if (!rawLine.StartsWith("M|", StringComparison.Ordinal)) continue;
                var parts = rawLine.Split('|');
                if (parts.Length < 5) continue;

                string clanId = Unsanitize(parts[1]);
                string kingdomId = Unsanitize(parts[2]);
                bool isMerc = parts[3] == "1";
                int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mercMult);

                if (string.Equals(kingdomId, "NONE", StringComparison.Ordinal))
                {
                    ApplyLeaveKingdom(new KingdomDiplomacySyncPacket
                    {
                        ActionType = 4,
                        ClanId = clanId
                    }, notify: false);
                }
                else
                {
                    ApplyJoinKingdom(new KingdomDiplomacySyncPacket
                    {
                        ActionType = (byte)(isMerc ? 3 : 2),
                        ClanId = clanId,
                        KingdomId = kingdomId,
                        MercenaryAwardMultiplier = mercMult
                    }, asMercenary: isMerc, notify: false);
                }
            }

            // Pass 3: Settlement owners
            foreach (string rawLine in lines)
            {
                if (!rawLine.StartsWith("S|", StringComparison.Ordinal)) continue;
                var parts = rawLine.Split('|');
                if (parts.Length < 4) continue;

                ApplySettlementOwnerChanged(new KingdomDiplomacySyncPacket
                {
                    ActionType = 5,
                    SettlementId = Unsanitize(parts[1]),
                    ClanId = Unsanitize(parts[2]),
                    TargetFactionId = Unsanitize(parts[3]),
                    DetailCode = 0
                }, notify: false);
            }

            // Pass 4: Wars & Peace reconciliation
            foreach (string rawLine in lines)
            {
                if (!rawLine.StartsWith("W|", StringComparison.Ordinal)) continue;
                var parts = rawLine.Split('|');
                if (parts.Length < 3) continue;

                string id1 = Unsanitize(parts[1]);
                string id2 = Unsanitize(parts[2]);

                var f1 = ResolveFaction(id1);
                var f2 = ResolveFaction(id2);
                if (f1 != null && f2 != null && !ReferenceEquals(f1, f2))
                {
                    serverWarPairs.Add(MakeCanonicalPairKey(f1.StringId, f2.StringId));
                    if (!FactionManager.IsAtWarAgainstFaction(f1, f2))
                    {
                        try { DeclareWarAction.ApplyByDefault(f1, f2); } catch { }
                    }
                }
            }

            // Reconcile any local wars that are NOT active on the authoritative server
            var trackedFactions = CollectTrackedDiplomaticFactions();
            for (int i = 0; i < trackedFactions.Count; i++)
            {
                var f1 = trackedFactions[i];
                for (int j = i + 1; j < trackedFactions.Count; j++)
                {
                    var f2 = trackedFactions[j];
                    if (FactionManager.IsAtWarAgainstFaction(f1, f2))
                    {
                        string pairKey = MakeCanonicalPairKey(f1.StringId, f2.StringId);
                        if (!serverWarPairs.Contains(pairKey))
                        {
                            try { MakePeaceAction.Apply(f1, f2); } catch { }
                        }
                    }
                }
            }

            DisplayDiplomacyMessage(
                Loc.T("Krallık ve diplomasi durumu sunucu ile eşitlendi.", "Kingdom and diplomacy state synchronized with server."),
                Colors.Green);
        }

        #endregion

        #region Resolution & Helpers

        private static bool IsPlayerClan(Clan? clan)
        {
            if (clan == null) return false;
            if (ReferenceEquals(clan, Clan.PlayerClan)) return true;
            return !string.IsNullOrEmpty(clan.StringId) &&
                   clan.StringId.StartsWith(NetworkObjectRegistry.ClanPrefix, StringComparison.Ordinal);
        }

        private static List<IFaction> CollectTrackedDiplomaticFactions()
        {
            var list = new List<IFaction>();
            if (Kingdom.All != null)
            {
                foreach (var k in Kingdom.All)
                {
                    if (k != null && !k.IsEliminated && !k.IsBanditFaction)
                    {
                        list.Add(k);
                    }
                }
            }

            if (Clan.All != null)
            {
                foreach (var c in Clan.All)
                {
                    if (c != null && !c.IsEliminated && !c.IsBanditFaction && c.Kingdom == null && IsPlayerClan(c))
                    {
                        list.Add(c);
                    }
                }
            }

            return list;
        }

        private static string MakeCanonicalPairKey(string? id1, string? id2)
        {
            string a = id1 ?? string.Empty;
            string b = id2 ?? string.Empty;
            return string.CompareOrdinal(a, b) <= 0 ? $"{a}::{b}" : $"{b}::{a}";
        }

        private static void LeaveCurrentKingdomIfNeeded(Clan clan)
        {
            if (clan.Kingdom == null) return;
            if (clan.IsUnderMercenaryService)
            {
                ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(clan, showNotification: false);
            }
            else
            {
                ChangeKingdomAction.ApplyByLeaveKingdom(clan, showNotification: false);
            }
        }

        private Clan? ResolveClan(string? clanId, int actorPeerId)
        {
            if (!ModContext.IsServerRunning && actorPeerId > 0 && actorPeerId == (ModContext.Client?.LocalPeerId ?? -1))
            {
                if (Clan.PlayerClan != null) return Clan.PlayerClan;
            }

            if (!string.IsNullOrEmpty(clanId))
            {
                if (!ModContext.IsServerRunning &&
                    Clan.PlayerClan != null &&
                    string.Equals(GetCanonicalClanId(Clan.PlayerClan), clanId, StringComparison.Ordinal))
                {
                    return Clan.PlayerClan;
                }

                var resolved = NetworkObjectRegistry.Instance.ResolveClan(clanId);
                if (resolved != null) return resolved;

                if (NetworkObjectRegistry.TryExtractPeerId(clanId, out int extractedPeer))
                {
                    var hero = NetworkObjectRegistry.Instance.ResolvePlayerHero(extractedPeer);
                    if (hero?.Clan != null) return hero.Clan;
                }
            }

            if (actorPeerId > 0)
            {
                var actorHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(actorPeerId);
                if (actorHero?.Clan != null) return actorHero.Clan;
                return NetworkObjectRegistry.Instance.ResolveClan(NetworkObjectRegistry.GetPlayerClanId(actorPeerId));
            }

            return null;
        }

        private Hero? ResolveHero(string? heroId, int actorPeerId)
        {
            if (!ModContext.IsServerRunning && actorPeerId > 0 && actorPeerId == (ModContext.Client?.LocalPeerId ?? -1))
            {
                if (Hero.MainHero != null &&
                    (string.IsNullOrEmpty(heroId) || string.Equals(GetCanonicalHeroId(Hero.MainHero), heroId, StringComparison.Ordinal)))
                {
                    return Hero.MainHero;
                }
            }

            if (!string.IsNullOrEmpty(heroId))
            {
                var found = NetworkObjectRegistry.Instance.ResolveHero(heroId);
                if (found != null) return found;
            }

            return actorPeerId > 0 ? NetworkObjectRegistry.Instance.ResolvePlayerHero(actorPeerId) : null;
        }

        private Kingdom? ResolveKingdom(string? kingdomId, string? kingdomName, Clan? rulingClan)
        {
            if (!string.IsNullOrEmpty(kingdomId))
            {
                string lookupId = _kingdomAliasMap.TryGetValue(kingdomId!, out var mapped) ? mapped : kingdomId!;
                var byId = NetworkObjectRegistry.Instance.ResolveKingdom(lookupId);
                if (byId != null) return byId;
            }

            if (rulingClan != null && Kingdom.All != null)
            {
                var byRuler = Kingdom.All.FirstOrDefault(k => k != null && !k.IsEliminated && ReferenceEquals(k.RulingClan, rulingClan));
                if (byRuler != null) return byRuler;
            }

            if (!string.IsNullOrWhiteSpace(kingdomName) && Kingdom.All != null)
            {
                var byName = Kingdom.All.FirstOrDefault(k =>
                    k != null && !k.IsEliminated &&
                    string.Equals(k.Name?.ToString(), kingdomName, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
            }

            return null;
        }

        private Kingdom? FindExistingKingdomForCreation(string? kingdomId, string displayName, Clan founderClan)
        {
            if (Kingdom.All == null) return null;

            var byRuler = Kingdom.All.FirstOrDefault(k => k != null && !k.IsEliminated && ReferenceEquals(k.RulingClan, founderClan));
            if (byRuler != null) return byRuler;

            if (!string.IsNullOrEmpty(kingdomId))
            {
                string lookupId = _kingdomAliasMap.TryGetValue(kingdomId!, out var mapped) ? mapped : kingdomId!;
                var byId = NetworkObjectRegistry.Instance.ResolveKingdom(lookupId);
                if (byId != null && !byId.IsEliminated &&
                    (ReferenceEquals(byId.RulingClan, founderClan) ||
                     string.Equals(byId.Name?.ToString(), displayName, StringComparison.OrdinalIgnoreCase)))
                {
                    return byId;
                }
            }

            return null;
        }

        private IFaction? ResolveFaction(string? factionId)
        {
            if (string.IsNullOrWhiteSpace(factionId)) return null;

            var kingdom = ResolveKingdom(factionId, null, null);
            if (kingdom != null) return kingdom;

            var clan = ResolveClan(factionId, 0);
            if (clan != null) return clan;

            return null;
        }

        private static CultureObject? ResolveCulture(string? cultureId, Clan founderClan)
        {
            var campaign = GameCampaign.Current;
            if (!string.IsNullOrWhiteSpace(cultureId) && campaign?.ObjectManager != null)
            {
                var found = campaign.ObjectManager.GetObject<CultureObject>(cultureId);
                if (found != null) return found;
            }

            return founderClan.Culture
                   ?? Settlement.All?.FirstOrDefault(s => s?.Culture != null)?.Culture;
        }

        private static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value!
                .Replace("%", "%25")
                .Replace("|", "%7C")
                .Replace("\r", string.Empty)
                .Replace("\n", "%0A");
        }

        private static string Unsanitize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value!
                .Replace("%0A", "\n")
                .Replace("%7C", "|")
                .Replace("%25", "%");
        }

        private static void DisplayDiplomacyMessage(string message, Color color)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                InformationManager.DisplayMessage(new InformationMessage($"[Calradia-Connected] {message}", color));
            }
            catch { }
        }

        #endregion
    }
}
