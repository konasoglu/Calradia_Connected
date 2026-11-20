using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Player;
using CalradiaConnected.Campaign.Sync;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Synchronizes player TroopRoster, ItemRoster, BattleEquipment,
    /// Hero stats, and Server Manager admin rewards between Server and Clients.
    /// </summary>
    public sealed class RosterSyncService
    {
        private static RosterSyncService? _instance;
        public static RosterSyncService Instance => _instance ??= new RosterSyncService();

        private bool _subscribed;
        private readonly Dictionary<int, PartyRosterSyncPacket> _lastKnownRosters = new();
        private readonly HashSet<int> _pendingApplyPeerIds = new();
        private readonly Dictionary<int, PartyRosterSyncPacket> _lastSyncedBroadcastPackets = new();
        private readonly ConcurrentDictionary<int, int> _syncedPartySizeLimits = new();

        public int GetSyncedPartySizeLimit(int peerId)
        {
            return _syncedPartySizeLimits.TryGetValue(peerId, out int lim) ? lim : 0;
        }

        public void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;

            ModContext.Events.Subscribe<LocalRosterChangedEvent>(_ => SyncLocalPlayerRosterToServer());
        }

        public PartyRosterSyncPacket? GetCachedRoster(int peerId)
        {
            lock (_lastKnownRosters)
            {
                return _lastKnownRosters.TryGetValue(peerId, out var pkt) ? pkt : null;
            }
        }

        public void ClearCachedRoster(int peerId)
        {
            lock (_lastKnownRosters)
            {
                _lastKnownRosters.Remove(peerId);
                _pendingApplyPeerIds.Remove(peerId);
            }
        }

        /// <summary>
        /// Ensures no hero entry in MemberRoster ever has Number > 1 (which causes both +1 campaign map headcount
        /// desync and Bannerlord's DefaultBattleMissionAgentSpawnLogic ReservedTroopsCount < InitialSpawnNumber
        /// zero-agent spawn deadlock in PvP battles).
        /// </summary>
        public static void NormalizePartyHeroRoster(MobileParty? party, Hero? leaderHero)
        {
            if (party?.MemberRoster == null) return;

            for (int i = party.MemberRoster.Count - 1; i >= 0; i--)
            {
                var elem = party.MemberRoster.GetElementCopyAtIndex(i);
                if (elem.Character == null || !elem.Character.IsHero) continue;

                if (elem.Number > 1)
                {
                    int excess = elem.Number - 1;
                    int excessWounded = Math.Max(0, elem.WoundedNumber - 1);
                    party.MemberRoster.AddToCounts(elem.Character, -excess, false, -excessWounded);
                }
            }

            if (leaderHero != null && leaderHero.IsAlive && !leaderHero.IsPrisoner && leaderHero.CharacterObject != null)
            {
                if (party.LeaderHero != leaderHero)
                {
                    try { party.ChangePartyLeader(leaderHero); } catch { }
                }

                int hIdx = party.MemberRoster.FindIndexOfTroop(leaderHero.CharacterObject);
                if (hIdx < 0)
                {
                    party.MemberRoster.AddToCounts(leaderHero.CharacterObject, 1, insertAtFront: true);
                }
                else
                {
                    int curCount = party.MemberRoster.GetElementNumber(hIdx);
                    if (curCount > 1)
                    {
                        int curWounded = party.MemberRoster.GetElementWoundedNumber(hIdx);
                        party.MemberRoster.AddToCounts(leaderHero.CharacterObject, -(curCount - 1), false, -Math.Max(0, curWounded - 1));
                    }
                }
            }
        }

        public static string ComputeRosterSignature(Hero? hero, MobileParty? party)
        {
            if (hero == null || party?.MemberRoster == null) return string.Empty;

            var sb = new System.Text.StringBuilder(256);
            sb.Append(hero.Gold).Append('|')
              .Append((int)(hero.Clan?.Renown ?? 0f)).Append('|')
              .Append((int)(hero.Clan?.Influence ?? 0f)).Append('|')
              .Append(hero.HitPoints).Append('/').Append(hero.MaxHitPoints).Append('|')
              .Append(party.MemberRoster.TotalManCount).Append(':')
              .Append(party.MemberRoster.TotalWounded).Append('|')
              .Append(party.ItemRoster?.Count ?? 0).Append(':')
              .Append(party.ItemRoster?.VersionNo ?? 0);

            for (int i = 0; i < party.MemberRoster.Count; i++)
            {
                var elem = party.MemberRoster.GetElementCopyAtIndex(i);
                if (elem.Character == null || (hero != null && elem.Character == hero.CharacterObject) || elem.Number <= 0) continue;
                sb.Append(';').Append(elem.Character.StringId).Append('=').Append(elem.Number).Append('/').Append(elem.WoundedNumber);
            }

            return sb.ToString();
        }

        public PartyRosterSyncPacket BuildRosterPacket(int peerId, Hero? hero, MobileParty? party)
        {
            var packet = new PartyRosterSyncPacket
            {
                PeerId = peerId,
                PartyId = NetworkObjectRegistry.GetPlayerPartyId(peerId),
                HeroGold = hero?.Gold ?? 0,
                TotalHealthy = party?.MemberRoster?.TotalHealthyCount ?? 0,
                TotalWounded = party?.MemberRoster?.TotalWounded ?? 0,
                EquipmentCsv = hero != null ? PlayerHeroManager.SerializeAllEquipments(hero) : string.Empty,
                ItemsCsv = SerializePartyItems(party),
                ClanRenown = hero?.Clan?.Renown ?? 0f,
                ClanInfluence = hero?.Clan?.Influence ?? 0f,
                HeroHp = hero?.HitPoints ?? 100,
                HeroMaxHp = hero?.MaxHitPoints ?? 100,
                PartySizeLimit = party?.Party?.PartySizeLimit ?? 0,
                Entries = new List<RosterEntryData>()
            };

            if (party?.MemberRoster != null)
            {
                for (int i = 0; i < party.MemberRoster.Count; i++)
                {
                    var elem = party.MemberRoster.GetElementCopyAtIndex(i);
                    if (elem.Character == null || (hero != null && elem.Character == hero.CharacterObject)) continue;
                    if (elem.Number <= 0) continue;

                    packet.Entries.Add(new RosterEntryData
                    {
                        ObjectId = elem.Character.StringId,
                        Count = elem.Number,
                        WoundedCount = elem.WoundedNumber,
                        IsItem = false
                    });
                }
            }

            return packet;
        }

        public void SyncLocalPlayerRosterToServer()
        {
            try
            {
                if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
                if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

                var mainParty = MobileParty.MainParty;
                var mainHero = Hero.MainHero;
                if (mainParty == null || mainHero == null) return;

                int peerId = ModContext.Client?.LocalPeerId ?? 1001;
                var packet = BuildRosterPacket(peerId, mainHero, mainParty);
                ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] SyncLocalPlayerRosterToServer hatası: {ex.Message}");
            }
        }

        public bool ApplyCachedRosterIfExists(int peerId, MobileParty? party)
        {
            if (peerId <= 0 || party == null || party.IsMainParty) return false;
            PartyRosterSyncPacket? cached;
            lock (_lastKnownRosters)
            {
                if (!_lastKnownRosters.TryGetValue(peerId, out cached) || cached == null) return false;
            }

            var hero = party.LeaderHero ?? NetworkObjectRegistry.Instance.ResolvePlayerHero(peerId);
            ApplyRosterPacketToParty(party, hero, cached);
            return cached.Entries.Count > 0 || cached.TotalHealthy > 0 || party.MemberRoster.TotalRegulars > 0;
        }

        private static void ApplyRosterPacketToParty(MobileParty? party, Hero? hero, PartyRosterSyncPacket packet)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null ||
                !TaleWorlds.CampaignSystem.Campaign.Current.GameStarted ||
                TaleWorlds.CampaignSystem.Campaign.Current.SaveHandler?.IsSaving == true)
            {
                return;
            }

            bool equipChanged = false;
            if (packet.PeerId > 0 && packet.PartySizeLimit > 0)
            {
                Instance._syncedPartySizeLimits[packet.PeerId] = packet.PartySizeLimit;
            }
            if (hero != null)
            {
                hero.Gold = Math.Max(0, packet.HeroGold);
                if (hero.Clan != null)
                {
                    if (packet.ClanRenown >= 0f && Math.Abs(hero.Clan.Renown - packet.ClanRenown) > 0.1f)
                    {
                        hero.Clan.Renown = packet.ClanRenown;
                    }
                    if (packet.ClanInfluence >= 0f && Math.Abs(hero.Clan.Influence - packet.ClanInfluence) > 0.1f)
                    {
                        hero.Clan.Influence = packet.ClanInfluence;
                    }
                }
                if (packet.HeroHp > 0 && hero.IsAlive && !hero.IsPrisoner)
                {
                    hero.HitPoints = Math.Min(Math.Max(1, hero.MaxHitPoints), packet.HeroHp);
                }
                else if (packet.TotalHealthy > 0 && hero.IsAlive && !hero.IsPrisoner && hero.HitPoints < 35)
                {
                    hero.HitPoints = 50;
                }
                equipChanged = PlayerHeroManager.ApplyAllEquipments(hero, packet.EquipmentCsv);
            }

            if (party != null && !party.IsMainParty && party.MemberRoster != null)
            {
                // Remove all non-hero troops cleanly and replace with authoritative snapshot
                for (int i = party.MemberRoster.Count - 1; i >= 0; i--)
                {
                    var elem = party.MemberRoster.GetElementCopyAtIndex(i);
                    if (elem.Character != null && !elem.Character.IsHero)
                    {
                        party.MemberRoster.AddToCounts(elem.Character, -elem.Number, false, -elem.WoundedNumber);
                    }
                }

                NormalizePartyHeroRoster(party, hero);

                if (hero != null && hero.IsAlive && !hero.IsPrisoner && packet.TotalHealthy > 0)
                {
                    int hIdx = party.MemberRoster.FindIndexOfTroop(hero.CharacterObject);
                    if (hIdx >= 0)
                    {
                        int hWounded = party.MemberRoster.GetElementWoundedNumber(hIdx);
                        if (hWounded > 0)
                        {
                            party.MemberRoster.AddToCounts(hero.CharacterObject, 0, false, -hWounded);
                        }
                    }
                }

                int fallbackCount = 0;
                int fallbackWounded = 0;

                foreach (var entry in packet.Entries)
                {
                    if (entry.IsItem || entry.Count <= 0 || string.IsNullOrEmpty(entry.ObjectId)) continue;
                    if (string.Equals(entry.ObjectId, "main_hero", StringComparison.OrdinalIgnoreCase) ||
                        entry.ObjectId.StartsWith("cc_hero_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    int wounded = Math.Min(entry.Count, Math.Max(0, entry.WoundedCount));
                    var character = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(entry.ObjectId);
                    if (character != null && !character.IsHero)
                    {
                        party.MemberRoster.AddToCounts(character, entry.Count, false, wounded);
                    }
                    else
                    {
                        // Preserve exact headcount even when dynamic companions or custom troop IDs are missing locally
                        fallbackCount += entry.Count;
                        fallbackWounded += wounded;
                    }
                }

                if (fallbackCount > 0)
                {
                    var fallbackTroop = (hero?.Culture ?? party.MapFaction?.Culture)?.BasicTroop;
                    if (fallbackTroop != null)
                    {
                        party.MemberRoster.AddToCounts(fallbackTroop, fallbackCount, false, Math.Min(fallbackCount, fallbackWounded));
                    }
                }

                if (!string.IsNullOrEmpty(packet.ItemsCsv))
                {
                    ApplyPartyItems(party, packet.ItemsCsv);
                }

                party.Party?.SetLevelMaskIsDirty();
                if (equipChanged)
                {
                    party.Party?.SetVisualAsDirty();
                }
            }
        }

        public bool HasPendingRosters
        {
            get
            {
                lock (_lastKnownRosters)
                {
                    return _pendingApplyPeerIds.Count > 0;
                }
            }
        }

        private static MobileParty? ResolvePartyForPeer(int peerId, PartyRosterSyncPacket? packet)
        {
            var hero = NetworkObjectRegistry.Instance.ResolvePlayerHero(peerId);
            if (hero?.PartyBelongedTo != null && hero.PartyBelongedTo.IsActive)
            {
                return hero.PartyBelongedTo;
            }

            string fallbackId = $"cc_party_{peerId}";
            string targetId = packet?.PartyId ?? string.Empty;
            foreach (var mp in MobileParty.All)
            {
                if (mp == null || !mp.IsActive) continue;
                if ((!string.IsNullOrEmpty(targetId) && string.Equals(mp.StringId, targetId, StringComparison.Ordinal)) ||
                    string.Equals(mp.StringId, fallbackId, StringComparison.Ordinal))
                {
                    return mp;
                }
            }
            return null;
        }

        private static bool DoesPartyDifferFromPacket(MobileParty party, PartyRosterSyncPacket packet)
        {
            var roster = party.MemberRoster;
            if (roster == null) return packet.TotalHealthy + packet.TotalWounded > 0;

            if (roster.TotalHealthyCount != packet.TotalHealthy || roster.TotalWounded != packet.TotalWounded)
            {
                return true;
            }

            if (party.LeaderHero != null && party.LeaderHero.Gold != Math.Max(0, packet.HeroGold))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(packet.ItemsCsv) &&
                !string.Equals(SerializePartyItems(party), packet.ItemsCsv, StringComparison.Ordinal))
            {
                return true;
            }

            // Also verify individual non-item troop counts so upgrades/swaps with same headcount sync too
            int expectedNonHeroTroops = 0;
            foreach (var entry in packet.Entries)
            {
                if (entry.IsItem || string.IsNullOrEmpty(entry.ObjectId)) continue;
                var ch = TaleWorlds.ObjectSystem.MBObjectManager.Instance?.GetObject<CharacterObject>(entry.ObjectId);
                if (ch == null || ch.IsHero) continue;

                expectedNonHeroTroops += entry.Count;
                int idx = roster.FindIndexOfTroop(ch);
                int actualTotal = idx >= 0 ? roster.GetElementNumber(idx) : 0;
                int actualWounded = idx >= 0 ? roster.GetElementWoundedNumber(idx) : 0;
                if (actualTotal != entry.Count || actualWounded != entry.WoundedCount)
                {
                    return true;
                }
            }

            int actualNonHeroTroops = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                var el = roster.GetElementCopyAtIndex(i);
                if (el.Character == null) continue;
                if (el.Character.IsHero)
                {
                    if (el.Number > 1) return true;
                }
                else
                {
                    actualNonHeroTroops += el.Number;
                }
            }

            return actualNonHeroTroops != expectedNonHeroTroops;
        }

        public int FlushPendingRosters()
        {
            var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
            if (campaign == null || !campaign.GameStarted || campaign.SaveHandler?.IsSaving == true)
            {
                return 0;
            }

            List<int> peerIds;
            lock (_lastKnownRosters)
            {
                if (_pendingApplyPeerIds.Count == 0 && _lastKnownRosters.Count == 0)
                {
                    return 0;
                }

                var merged = new HashSet<int>(_pendingApplyPeerIds);
                foreach (var k in _lastKnownRosters.Keys)
                {
                    merged.Add(k);
                }
                peerIds = new List<int>(merged);
            }

            int localPeerId = !ModContext.IsServerRunning ? (ModContext.Client?.LocalPeerId ?? -1) : -1;
            int applied = 0;

            foreach (int peerId in peerIds)
            {
                if (peerId <= 0 || peerId == localPeerId)
                {
                    lock (_lastKnownRosters) { _pendingApplyPeerIds.Remove(peerId); }
                    continue;
                }

                PartyRosterSyncPacket? pkt;
                bool wasPending;
                lock (_lastKnownRosters)
                {
                    _lastKnownRosters.TryGetValue(peerId, out pkt);
                    wasPending = _pendingApplyPeerIds.Contains(peerId);
                }

                if (pkt == null)
                {
                    lock (_lastKnownRosters) { _pendingApplyPeerIds.Remove(peerId); }
                    continue;
                }

                var party = ResolvePartyForPeer(peerId, pkt);
                if (party == null || party.IsMainParty)
                {
                    if (party != null && party.IsMainParty)
                    {
                        lock (_lastKnownRosters) { _pendingApplyPeerIds.Remove(peerId); }
                    }
                    continue;
                }

                try { party.IsInspected = true; } catch { }

                if (wasPending || DoesPartyDifferFromPacket(party, pkt))
                {
                    if (ApplyCachedRosterIfExists(peerId, party))
                    {
                        try { party.IsInspected = true; } catch { }
                        applied++;
                    }
                }

                lock (_lastKnownRosters)
                {
                    _pendingApplyPeerIds.Remove(peerId);
                }
            }

            return applied;
        }

        public void BroadcastAllKnownRosters(int excludePeerId = -1)
        {
            if (!ModContext.IsServerRunning || ModContext.Server == null) return;

            List<PartyRosterSyncPacket> snapshot;
            lock (_lastKnownRosters)
            {
                snapshot = new List<PartyRosterSyncPacket>(_lastKnownRosters.Values);
            }

            foreach (var pkt in snapshot)
            {
                if (pkt.PeerId <= 0 || pkt.PeerId == excludePeerId) continue;
                try
                {
                    ModContext.Server.Broadcast(pkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                catch { }
            }
        }

        public bool BroadcastRosterForPeer(int peerId)
        {
            if (!ModContext.IsServerRunning || ModContext.Server == null || peerId <= 0) return false;

            PartyRosterSyncPacket? pkt;
            lock (_lastKnownRosters)
            {
                _lastKnownRosters.TryGetValue(peerId, out pkt);
            }

            if (pkt == null) return false;
            try
            {
                ModContext.Server.Broadcast(pkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void HandlePacket(PartyRosterSyncPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    // Ignore echo on originating client
                    if (!ModContext.IsServerRunning && packet.PeerId == (ModContext.Client?.LocalPeerId ?? -1))
                    {
                        return;
                    }

                    if (WorldSyncManager.IsRecentlyDisconnectedParty(NetworkObjectRegistry.GetPlayerPartyId(packet.PeerId)))
                    {
                        return;
                    }

                    // Always cache on BOTH Server and Clients so loading / spawn order races never drop real troop counts
                    if (packet.PeerId > 0)
                    {
                        lock (_lastKnownRosters)
                        {
                            _lastKnownRosters[packet.PeerId] = packet;
                        }
                    }

                    if (TaleWorlds.CampaignSystem.Campaign.Current == null ||
                        !TaleWorlds.CampaignSystem.Campaign.Current.GameStarted ||
                        TaleWorlds.CampaignSystem.Campaign.Current.SaveHandler?.IsSaving == true)
                    {
                        if (packet.PeerId > 0)
                        {
                            int localPeerId = !ModContext.IsServerRunning ? (ModContext.Client?.LocalPeerId ?? -1) : -1;
                            if (packet.PeerId != localPeerId)
                            {
                                lock (_lastKnownRosters)
                                {
                                    _lastKnownRosters[packet.PeerId] = packet;
                                    _pendingApplyPeerIds.Add(packet.PeerId);
                                }
                            }
                        }
                        if (ModContext.IsServerRunning)
                        {
                            ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        }
                        return;
                    }

                    var party = ResolvePartyForPeer(packet.PeerId, packet);
                    var hero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.PeerId);

                    if (party == null)
                    {
                        if (packet.PeerId > 0)
                        {
                            lock (_lastKnownRosters)
                            {
                                _pendingApplyPeerIds.Add(packet.PeerId);
                            }
                        }
                    }
                    else
                    {
                        ApplyRosterPacketToParty(party, hero, packet);
                    }

                    if (ModContext.IsServerRunning)
                    {
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] RosterSyncService hatası: {ex.Message}");
                }
            });
        }

        public static string SerializePartyItems(MobileParty? party)
        {
            if (party?.ItemRoster == null || party.ItemRoster.Count == 0) return "__EMPTY__";
            var parts = new List<string>(party.ItemRoster.Count);
            for (int i = 0; i < party.ItemRoster.Count; i++)
            {
                var elem = party.ItemRoster.GetElementCopyAtIndex(i);
                var item = elem.EquipmentElement.Item;
                if (item == null || string.IsNullOrEmpty(item.StringId) || elem.Amount <= 0) continue;
                string modId = elem.EquipmentElement.ItemModifier?.StringId ?? string.Empty;
                parts.Add(string.IsNullOrEmpty(modId)
                    ? $"{item.StringId}:{elem.Amount}"
                    : $"{item.StringId}@{modId}:{elem.Amount}");
            }
            return parts.Count > 0 ? string.Join(",", parts) : "__EMPTY__";
        }

        public static void ApplyPartyItems(MobileParty? party, string? itemsCsv)
        {
            if (party?.ItemRoster == null || string.IsNullOrWhiteSpace(itemsCsv)) return;
            var objMgr = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager;
            if (objMgr == null) return;

            party.ItemRoster.Clear();
            if (string.Equals(itemsCsv, "__EMPTY__", StringComparison.Ordinal)) return;

            var entries = itemsCsv!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                int colon = entries[i].LastIndexOf(':');
                if (colon <= 0) continue;
                if (!int.TryParse(entries[i].Substring(colon + 1), out int count) || count <= 0) continue;

                string keyPart = entries[i].Substring(0, colon);
                string itemId = keyPart;
                string modId = string.Empty;
                int at = keyPart.IndexOf('@');
                if (at > 0)
                {
                    itemId = keyPart.Substring(0, at);
                    modId = keyPart.Substring(at + 1);
                }

                var item = objMgr.GetObject<ItemObject>(itemId);
                if (item != null)
                {
                    var mod = !string.IsNullOrEmpty(modId) ? objMgr.GetObject<ItemModifier>(modId) : null;
                    party.ItemRoster.AddToCounts(new EquipmentElement(item, mod), count);
                }
            }
        }

        public void HandleAdminRewardPacket(ServerAdminRewardPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
                    if (TaleWorlds.CampaignSystem.Campaign.Current == null ||
                        !TaleWorlds.CampaignSystem.Campaign.Current.GameStarted ||
                        TaleWorlds.CampaignSystem.Campaign.Current.SaveHandler?.IsSaving == true)
                    {
                        return;
                    }

                    int localPeerId = ModContext.Client?.LocalPeerId ?? 1001;
                    if (packet.TargetPeerId != 0 && packet.TargetPeerId != localPeerId)
                    {
                        return;
                    }

                    var mainHero = Hero.MainHero;
                    var mainParty = MobileParty.MainParty;
                    var playerClan = Clan.PlayerClan;
                    if (mainHero == null) return;

                    switch (packet.RewardType)
                    {
                        case 1: // Gold
                            mainHero.ChangeHeroGold(packet.IntAmount);
                            InformationManager.DisplayMessage(new InformationMessage(
                                Loc.F("[Sunucu Yönetimi] Hesabınıza +{0:N0} Altın eklendi! (Toplam: {1:N0})",
                                      "[Server Admin] +{0:N0} Gold added to your account! (Total: {1:N0})",
                                      packet.IntAmount, mainHero.Gold),
                                Colors.Green));
                            break;

                        case 2: // Renown (AddRenown automatically levels up Clan Tier!)
                            if (playerClan != null)
                            {
                                playerClan.AddRenown(packet.FloatAmount, true);
                                InformationManager.DisplayMessage(new InformationMessage(
                                    Loc.F("[Sunucu Yönetimi] Klanınıza +{0:0.#} Nam eklendi! (Toplam: {1:0.#}, Kademe: {2})",
                                          "[Server Admin] +{0:0.#} Renown added to your clan! (Total: {1:0.#}, Tier: {2})",
                                          packet.FloatAmount, playerClan.Renown, playerClan.Tier),
                                    Colors.Green));
                            }
                            break;

                        case 3: // Influence
                            if (playerClan != null)
                            {
                                if (playerClan.Kingdom != null)
                                {
                                    ChangeClanInfluenceAction.Apply(playerClan, packet.FloatAmount);
                                }
                                else
                                {
                                    playerClan.Influence += packet.FloatAmount;
                                }
                                InformationManager.DisplayMessage(new InformationMessage(
                                    Loc.F("[Sunucu Yönetimi] Klanınıza +{0:0.#} Nüfuz eklendi! (Toplam: {1:0.#})",
                                          "[Server Admin] +{0:0.#} Influence added to your clan! (Total: {1:0.#})",
                                          packet.FloatAmount, playerClan.Influence),
                                    Colors.Green));
                            }
                            break;

                        case 4: // Heal Hero
                            mainHero.HitPoints = mainHero.MaxHitPoints;
                            if (mainParty?.MemberRoster != null && mainHero.CharacterObject != null)
                            {
                                int hIdx = mainParty.MemberRoster.FindIndexOfTroop(mainHero.CharacterObject);
                                if (hIdx >= 0)
                                {
                                    int hWounded = mainParty.MemberRoster.GetElementWoundedNumber(hIdx);
                                    if (hWounded > 0)
                                    {
                                        mainParty.MemberRoster.AddToCountsAtIndex(hIdx, 0, -hWounded, 0, true);
                                    }
                                }
                            }
                            InformationManager.DisplayMessage(new InformationMessage(
                                Loc.F("[Sunucu Yönetimi] Kahramanınız tamamen iyileştirildi ({0} HP)!",
                                      "[Server Admin] Your hero is fully healed ({0} HP)!",
                                      mainHero.HitPoints),
                                Colors.Green));
                            break;

                        case 5: // Heal Party
                            mainHero.HitPoints = mainHero.MaxHitPoints;
                            if (mainParty?.MemberRoster != null)
                            {
                                int cleared = 0;
                                for (int i = 0; i < mainParty.MemberRoster.Count; i++)
                                {
                                    int wounded = mainParty.MemberRoster.GetElementWoundedNumber(i);
                                    if (wounded > 0)
                                    {
                                        mainParty.MemberRoster.AddToCountsAtIndex(i, 0, -wounded, 0, true);
                                        cleared += wounded;
                                    }
                                }
                                InformationManager.DisplayMessage(new InformationMessage(
                                    Loc.F("[Sunucu Yönetimi] Ordunuzdaki {0} yaralı asker ve kahramanınız iyileştirildi!",
                                          "[Server Admin] {0} wounded troops and your hero have been healed!",
                                          cleared),
                                    Colors.Green));
                            }
                            break;

                        case 6: // Give Troop
                            if (mainParty?.MemberRoster != null && !string.IsNullOrEmpty(packet.TroopId) && packet.IntAmount > 0)
                            {
                                var troopObj = ResolveTroopById(packet.TroopId);
                                if (troopObj != null)
                                {
                                    mainParty.MemberRoster.AddToCounts(troopObj, packet.IntAmount);
                                    InformationManager.DisplayMessage(new InformationMessage(
                                        Loc.F("[Sunucu Yönetimi] Ordunuza +{0}x {1} eklendi!",
                                              "[Server Admin] +{0}x {1} reinforced into your party!",
                                              packet.IntAmount, troopObj.Name),
                                        Colors.Green));
                                }
                            }
                            break;

                        case 7: // Duplicate Troops (adds +IntAmount to EVERY non-hero troop type in party, or basic troop if empty)
                            if (mainParty?.MemberRoster != null && packet.IntAmount > 0)
                            {
                                var existingStacks = new List<CharacterObject>();
                                for (int i = 0; i < mainParty.MemberRoster.Count; i++)
                                {
                                    var c = mainParty.MemberRoster.GetCharacterAtIndex(i);
                                    if (c != null && !c.IsHero && !existingStacks.Contains(c))
                                    {
                                        existingStacks.Add(c);
                                    }
                                }

                                if (existingStacks.Count == 0 && mainHero.Culture?.BasicTroop != null)
                                {
                                    existingStacks.Add(mainHero.Culture.BasicTroop);
                                }

                                int totalAdded = 0;
                                foreach (var stackTroop in existingStacks)
                                {
                                    mainParty.MemberRoster.AddToCounts(stackTroop, packet.IntAmount);
                                    totalAdded += packet.IntAmount;
                                }

                                if (totalAdded > 0)
                                {
                                    InformationManager.DisplayMessage(new InformationMessage(
                                        Loc.F("[Sunucu Yönetimi] Ordunuzdaki {0} birlik türüne +{1} asker eklendi (Toplam +{2})!",
                                              "[Server Admin] Duplicated +{1} troops across {0} unit type(s) (Total +{2})!",
                                              existingStacks.Count, packet.IntAmount, totalAdded),
                                        Colors.Green));
                                }
                            }
                            break;
                    }

                    SyncLocalPlayerRosterToServer();
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] HandleAdminRewardPacket hatası: {ex.Message}");
                }
            });
        }

        public static CharacterObject? ResolveTroopById(string? troopId)
        {
            if (string.IsNullOrWhiteSpace(troopId)) return null;
            string clean = troopId!.Trim();
            var obj = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager.GetObject<CharacterObject>(clean);
            if (obj != null) return obj;

            // Legacy alias support & case-insensitive fallback
            if (string.Equals(clean, "sturgian_heavy_axeman", StringComparison.OrdinalIgnoreCase))
            {
                obj = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager.GetObject<CharacterObject>("sturgian_veteran_warrior");
                if (obj != null) return obj;
            }

            try
            {
                return CharacterObject.All?.FirstOrDefault(c =>
                    c != null && !c.IsHero &&
                    (string.Equals(c.StringId, clean, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.Name?.ToString(), clean, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                return null;
            }
        }
    }
}
