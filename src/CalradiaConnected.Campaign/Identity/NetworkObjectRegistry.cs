using System;
using System.Collections.Concurrent;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using GameCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace CalradiaConnected.Campaign.Identity
{
    /// <summary>
    /// Centralized O(1) object identity registry (ObjectManager equivalent) that resolves
    /// Heroes, MobileParties, Settlements, Clans, and Kingdoms across Server and Clients
    /// using deterministic StringIds.
    /// </summary>
    public sealed class NetworkObjectRegistry
    {
        private static NetworkObjectRegistry? _instance;
        public static NetworkObjectRegistry Instance => _instance ??= new NetworkObjectRegistry();

        public const string HeroPrefix = "cc_hero_";
        public const string ClanPrefix = "cc_clan_";
        public const string PartyPrefix = "cc_party_";

        private GameCampaign? _boundCampaign;
        private readonly ConcurrentDictionary<string, Hero> _heroes = new ConcurrentDictionary<string, Hero>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, MobileParty> _parties = new ConcurrentDictionary<string, MobileParty>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Clan> _clans = new ConcurrentDictionary<string, Clan>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Settlement> _settlements = new ConcurrentDictionary<string, Settlement>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Kingdom> _kingdoms = new ConcurrentDictionary<string, Kingdom>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, float> _syncedPartySpeeds = new ConcurrentDictionary<string, float>(StringComparer.Ordinal);

        public static string GetPlayerHeroId(int accountPeerId) => $"{HeroPrefix}{accountPeerId}";
        public static string GetPlayerClanId(int accountPeerId) => $"{ClanPrefix}{accountPeerId}";
        public static string GetPlayerPartyId(int accountPeerId) => $"{PartyPrefix}{accountPeerId}";

        public void SetSyncedPartySpeed(string? partyId, float speed)
        {
            if (string.IsNullOrEmpty(partyId) || float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0.05f) return;
            _syncedPartySpeeds[partyId!] = speed;
        }

        public bool TryGetSyncedPartySpeed(string? partyId, out float speed)
        {
            speed = 0f;
            if (string.IsNullOrEmpty(partyId)) return false;
            return _syncedPartySpeeds.TryGetValue(partyId!, out speed) && speed > 0.05f;
        }

        public static bool IsPlayerNetworkParty(MobileParty? party)
        {
            return party?.StringId != null && party.StringId.StartsWith(PartyPrefix, StringComparison.Ordinal);
        }

        public static bool IsPlayerNetworkHero(Hero? hero)
        {
            if (hero == null) return false;
            if (!string.IsNullOrEmpty(hero.StringId) && hero.StringId.StartsWith(HeroPrefix, StringComparison.Ordinal))
            {
                return true;
            }
            return hero.PartyBelongedTo != null && IsPlayerNetworkParty(hero.PartyBelongedTo);
        }

        public static bool TryExtractPeerId(string? networkId, out int peerId)
        {
            peerId = 0;
            if (string.IsNullOrEmpty(networkId)) return false;

            string? numPart = null;
            if (networkId!.StartsWith(PartyPrefix, StringComparison.Ordinal))
                numPart = networkId.Substring(PartyPrefix.Length);
            else if (networkId.StartsWith(HeroPrefix, StringComparison.Ordinal))
                numPart = networkId.Substring(HeroPrefix.Length);
            else if (networkId.StartsWith(ClanPrefix, StringComparison.Ordinal))
                numPart = networkId.Substring(ClanPrefix.Length);

            return numPart != null && int.TryParse(numPart, out peerId);
        }

        private void EnsureCampaignBound()
        {
            var current = GameCampaign.Current;
            if (current == null)
            {
                Clear();
                _boundCampaign = null;
                return;
            }

            if (!ReferenceEquals(_boundCampaign, current))
            {
                Clear();
                _boundCampaign = current;
            }
        }

        public void Clear()
        {
            _heroes.Clear();
            _parties.Clear();
            _clans.Clear();
            _settlements.Clear();
            _kingdoms.Clear();
            _syncedPartySpeeds.Clear();
        }

        public void RegisterPlayerEntities(int accountPeerId, Hero? hero, Clan? clan, MobileParty? party)
        {
            EnsureCampaignBound();
            if (hero != null && !string.IsNullOrEmpty(hero.StringId))
            {
                _heroes[hero.StringId] = hero;
            }
            if (clan != null && !string.IsNullOrEmpty(clan.StringId))
            {
                _clans[clan.StringId] = clan;
            }
            if (party != null && !string.IsNullOrEmpty(party.StringId))
            {
                _parties[party.StringId] = party;
            }
        }

        public void UnregisterPlayerParty(int accountPeerId)
        {
            string partyId = GetPlayerPartyId(accountPeerId);
            _parties.TryRemove(partyId, out _);
            _syncedPartySpeeds.TryRemove(partyId, out _);
        }

        public Hero? ResolveHero(string? heroId)
        {
            if (string.IsNullOrEmpty(heroId)) return null;
            EnsureCampaignBound();
            var campaign = GameCampaign.Current;
            if (campaign == null) return null;

            if (_heroes.TryGetValue(heroId!, out var cached) && cached != null && cached.IsAlive)
            {
                return cached;
            }

            var found = campaign.CampaignObjectManager?.Find<Hero>(heroId)
                        ?? campaign.AliveHeroes?.FirstOrDefault(h => h != null && h.StringId == heroId);
            if (found != null)
            {
                _heroes[heroId!] = found;
            }
            return found;
        }

        public Hero? ResolvePlayerHero(int accountPeerId) => ResolveHero(GetPlayerHeroId(accountPeerId));

        public MobileParty? ResolveParty(string? partyId)
        {
            if (string.IsNullOrEmpty(partyId)) return null;
            EnsureCampaignBound();
            var campaign = GameCampaign.Current;
            if (campaign == null) return null;

            if (_parties.TryGetValue(partyId!, out var cached) && cached != null && cached.IsActive)
            {
                return cached;
            }

            var found = campaign.CampaignObjectManager?.Find<MobileParty>(partyId)
                        ?? MobileParty.All?.FirstOrDefault(p => p != null && p.StringId == partyId);

            if (found == null && TryExtractPeerId(partyId, out int peerId))
            {
                var hero = ResolvePlayerHero(peerId);
                found = hero?.PartyBelongedTo;
            }

            if (found != null && found.IsActive)
            {
                _parties[partyId!] = found;
            }
            return found;
        }

        public MobileParty? ResolvePlayerParty(int accountPeerId) => ResolveParty(GetPlayerPartyId(accountPeerId));

        public Settlement? ResolveSettlement(string? settlementId)
        {
            if (string.IsNullOrEmpty(settlementId)) return null;
            EnsureCampaignBound();
            var campaign = GameCampaign.Current;
            if (campaign == null) return null;

            if (_settlements.TryGetValue(settlementId!, out var cached) && cached != null)
            {
                return cached;
            }

            var found = campaign.CampaignObjectManager?.Find<Settlement>(settlementId)
                        ?? campaign.ObjectManager?.GetObject<Settlement>(settlementId)
                        ?? Settlement.All?.FirstOrDefault(s => s != null && s.StringId == settlementId);

            if (found != null)
            {
                _settlements[settlementId!] = found;
            }
            return found;
        }

        public Clan? ResolveClan(string? clanId)
        {
            if (string.IsNullOrEmpty(clanId)) return null;
            EnsureCampaignBound();
            var campaign = GameCampaign.Current;
            if (campaign == null) return null;

            if (_clans.TryGetValue(clanId!, out var cached) && cached != null)
            {
                return cached;
            }

            var found = campaign.CampaignObjectManager?.Find<Clan>(clanId)
                        ?? campaign.ObjectManager?.GetObject<Clan>(clanId)
                        ?? Clan.All?.FirstOrDefault(c => c != null && c.StringId == clanId);

            if (found != null)
            {
                _clans[clanId!] = found;
            }
            return found;
        }

        public Kingdom? ResolveKingdom(string? kingdomId)
        {
            if (string.IsNullOrEmpty(kingdomId)) return null;
            EnsureCampaignBound();
            var campaign = GameCampaign.Current;
            if (campaign == null) return null;

            if (_kingdoms.TryGetValue(kingdomId!, out var cached) && cached != null)
            {
                return cached;
            }

            var found = campaign.CampaignObjectManager?.Find<Kingdom>(kingdomId)
                        ?? campaign.ObjectManager?.GetObject<Kingdom>(kingdomId)
                        ?? Kingdom.All?.FirstOrDefault(k => k != null && k.StringId == kingdomId);

            if (found != null)
            {
                _kingdoms[kingdomId!] = found;
            }
            return found;
        }

        /// <summary>
        /// Resolves an existing player's Hero, Clan, and MobileParty on reconnect.
        /// Checks both deterministic accountPeerId and UserDatabase heroId fallback.
        /// </summary>
        public bool TryResolveExistingPlayer(int accountPeerId, string? username, out Hero? hero, out Clan? clan, out MobileParty? party)
        {
            hero = ResolvePlayerHero(accountPeerId);

            if (hero == null && !string.IsNullOrEmpty(username))
            {
                var userRecord = global::Common.Users.UserDatabase.Instance.GetUser(username!);
                if (userRecord != null && !string.IsNullOrEmpty(userRecord.HeroId))
                {
                    hero = ResolveHero(userRecord.HeroId);
                }
            }

            if (hero == null)
            {
                clan = null;
                party = null;
                return false;
            }

            clan = hero.Clan ?? ResolveClan(GetPlayerClanId(accountPeerId));
            party = hero.PartyBelongedTo ?? ResolvePlayerParty(accountPeerId);

            RegisterPlayerEntities(accountPeerId, hero, clan, party);
            return true;
        }
    }
}
