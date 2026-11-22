using System.Collections.Generic;
using LiteNetLib.Utils;

namespace CalradiaConnected.Common.Packets
{
    /// <summary>
    /// Synchronizes kingdom creation, vassalage, mercenary contracts, settlement conquests,
    /// war/peace diplomacy, and full late-join diplomatic snapshots across Server and Clients.
    /// ActionType:
    /// 1 = CreateKingdom
    /// 2 = JoinAsVassal
    /// 3 = JoinAsMercenary
    /// 4 = LeaveKingdom
    /// 5 = SettlementOwnerChanged
    /// 6 = WarDeclared
    /// 7 = PeaceDeclared
    /// 8 = FullStateSnapshot
    /// </summary>
    public class KingdomDiplomacySyncPacket : IPacket
    {
        public PacketType Type => PacketType.KingdomDiplomacySync;

        public byte ActionType { get; set; }
        public int ActorPeerId { get; set; }
        public string ClanId { get; set; } = string.Empty;
        public string KingdomId { get; set; } = string.Empty;
        public string KingdomName { get; set; } = string.Empty;
        public string InformalName { get; set; } = string.Empty;
        public string CultureId { get; set; } = string.Empty;
        public string BannerKey { get; set; } = string.Empty;
        public string TargetFactionId { get; set; } = string.Empty;
        public string SettlementId { get; set; } = string.Empty;
        public uint PrimaryColor { get; set; }
        public uint SecondaryColor { get; set; }
        public int MercenaryAwardMultiplier { get; set; }
        public byte DetailCode { get; set; }
        public string SnapshotPayload { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ActionType);
            writer.Put(ActorPeerId);
            writer.Put(ClanId ?? string.Empty);
            writer.Put(KingdomId ?? string.Empty);
            writer.Put(KingdomName ?? string.Empty);
            writer.Put(InformalName ?? string.Empty);
            writer.Put(CultureId ?? string.Empty);
            writer.Put(BannerKey ?? string.Empty);
            writer.Put(TargetFactionId ?? string.Empty);
            writer.Put(SettlementId ?? string.Empty);
            writer.Put(PrimaryColor);
            writer.Put(SecondaryColor);
            writer.Put(MercenaryAwardMultiplier);
            writer.Put(DetailCode);
            writer.Put(SnapshotPayload ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            ActionType = reader.GetByte();
            ActorPeerId = reader.GetInt();
            ClanId = reader.GetString();
            KingdomId = reader.GetString();
            KingdomName = reader.GetString();
            InformalName = reader.GetString();
            CultureId = reader.GetString();
            BannerKey = reader.GetString();
            TargetFactionId = reader.GetString();
            SettlementId = reader.GetString();
            PrimaryColor = reader.GetUInt();
            SecondaryColor = reader.GetUInt();
            MercenaryAwardMultiplier = reader.GetInt();
            DetailCode = reader.GetByte();
            SnapshotPayload = reader.GetString();
        }
    }

    /// <summary>
    /// Synchronizes player armies (same-kingdom native Army & independent Co-op Escort Warband),
    /// invitations, joining/leaving, disbanding, and automatic joint siege camp participation.
    /// ActionType:
    /// 1 = InviteToArmy
    /// 2 = AcceptArmyInvite
    /// 3 = DeclineArmyInvite
    /// 4 = LeaveArmy
    /// 5 = DisbandArmy
    /// 6 = SyncArmyState
    /// 7 = JoinJointSiege
    /// </summary>
    public class PlayerArmySyncPacket : IPacket
    {
        public PacketType Type => PacketType.PlayerArmySync;

        public byte ActionType { get; set; }
        public string ArmyId { get; set; } = string.Empty;
        public int LeaderPeerId { get; set; }
        public int TargetPeerId { get; set; }
        public string LeaderName { get; set; } = string.Empty;
        public string TargetName { get; set; } = string.Empty;
        public string KingdomId { get; set; } = string.Empty;
        /// <summary>1 = Native Kingdom Army, 2 = Independent Co-op Escort Warband</summary>
        public byte ArmyMode { get; set; }
        public float Cohesion { get; set; } = 100f;
        public string SiegeSettlementId { get; set; } = string.Empty;
        public List<int> MemberPeerIds { get; set; } = new List<int>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ActionType);
            writer.Put(ArmyId ?? string.Empty);
            writer.Put(LeaderPeerId);
            writer.Put(TargetPeerId);
            writer.Put(LeaderName ?? string.Empty);
            writer.Put(TargetName ?? string.Empty);
            writer.Put(KingdomId ?? string.Empty);
            writer.Put(ArmyMode);
            writer.Put(Cohesion);
            writer.Put(SiegeSettlementId ?? string.Empty);

            int count = MemberPeerIds?.Count ?? 0;
            writer.Put((ushort)count);
            for (int i = 0; i < count; i++)
            {
                writer.Put(MemberPeerIds![i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ActionType = reader.GetByte();
            ArmyId = reader.GetString();
            LeaderPeerId = reader.GetInt();
            TargetPeerId = reader.GetInt();
            LeaderName = reader.GetString();
            TargetName = reader.GetString();
            KingdomId = reader.GetString();
            ArmyMode = reader.GetByte();
            Cohesion = reader.GetFloat();
            SiegeSettlementId = reader.GetString();

            ushort count = reader.GetUShort();
            MemberPeerIds = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                MemberPeerIds.Add(reader.GetInt());
            }
        }
    }

    /// <summary>
    /// Synchronizes Co-op shared battles against AI lords, bandits, sieges, and hideouts.
    /// FriendlyFireEnabled defaults to false so allied players cannot harm each other.
    /// ActionType:
    /// 1 = BattleCreated (host engages AI party/siege/hideout -> server invites nearby/army allies)
    /// 2 = JoinBattleRequest (ally requests to join an active Co-op battle)
    /// 3 = JoinBattleApproved (server confirms join & broadcasts updated participant roster)
    /// 4 = LeaveBattle (participant withdraws/disconnects)
    /// 5 = BattleEnded (host/server finalizes shared victory, loot, renown & map cleanup)
    /// 6 = SyncAiEnemies (Battle-Host authoritative AI enemy snapshot batch)
    /// 7 = CoopDamageReport (co-op ally reports hit/kill on an AI enemy unit to Battle-Host)
    /// </summary>
    public class CoopBattleSyncPacket : IPacket
    {
        public PacketType Type => PacketType.CoopBattleSync;

        public byte ActionType { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public int HostPeerId { get; set; }
        public int ActorPeerId { get; set; }
        public string HostName { get; set; } = string.Empty;
        public string ActorName { get; set; } = string.Empty;
        /// <summary>0 = Field Battle, 1 = Siege Battle, 2 = Hideout Raid</summary>
        public byte BattleKind { get; set; }
        /// <summary>0 = Host/Ally Side, 1 = Enemy Side</summary>
        public byte Side { get; set; }
        public string TargetPartyId { get; set; } = string.Empty;
        public string TargetPartyName { get; set; } = string.Empty;
        public string SettlementId { get; set; } = string.Empty;
        public string SceneName { get; set; } = string.Empty;
        public float MapPosX { get; set; }
        public float MapPosY { get; set; }
        public bool FriendlyFireEnabled { get; set; } = false;
        public bool HostSideWon { get; set; }
        public int SharedGoldLoot { get; set; }
        public int SharedRenown { get; set; }
        public ushort TargetEnemyUnitId { get; set; }
        public float DamageAmount { get; set; }
        public bool IsKill { get; set; }
        public List<int> AlliedPeerIds { get; set; } = new List<int>();
        public List<TroopUnitSnapshot> EnemyUnits { get; set; } = new List<TroopUnitSnapshot>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ActionType);
            writer.Put(SessionId ?? string.Empty);
            writer.Put(HostPeerId);
            writer.Put(ActorPeerId);
            writer.Put(HostName ?? string.Empty);
            writer.Put(ActorName ?? string.Empty);
            writer.Put(BattleKind);
            writer.Put(Side);
            writer.Put(TargetPartyId ?? string.Empty);
            writer.Put(TargetPartyName ?? string.Empty);
            writer.Put(SettlementId ?? string.Empty);
            writer.Put(SceneName ?? string.Empty);
            writer.Put(MapPosX);
            writer.Put(MapPosY);
            writer.Put(FriendlyFireEnabled);
            writer.Put(HostSideWon);
            writer.Put(SharedGoldLoot);
            writer.Put(SharedRenown);
            writer.Put(TargetEnemyUnitId);
            writer.Put(DamageAmount);
            writer.Put(IsKill);

            int allyCount = AlliedPeerIds?.Count ?? 0;
            writer.Put((ushort)allyCount);
            for (int i = 0; i < allyCount; i++)
            {
                writer.Put(AlliedPeerIds![i]);
            }

            int enemyCount = EnemyUnits?.Count ?? 0;
            writer.Put((ushort)enemyCount);
            for (int i = 0; i < enemyCount; i++)
            {
                var u = EnemyUnits![i] ?? new TroopUnitSnapshot();
                u.Serialize(writer);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ActionType = reader.GetByte();
            SessionId = reader.GetString();
            HostPeerId = reader.GetInt();
            ActorPeerId = reader.GetInt();
            HostName = reader.GetString();
            ActorName = reader.GetString();
            BattleKind = reader.GetByte();
            Side = reader.GetByte();
            TargetPartyId = reader.GetString();
            TargetPartyName = reader.GetString();
            SettlementId = reader.GetString();
            SceneName = reader.GetString();
            MapPosX = reader.GetFloat();
            MapPosY = reader.GetFloat();
            FriendlyFireEnabled = reader.GetBool();
            HostSideWon = reader.GetBool();
            SharedGoldLoot = reader.GetInt();
            SharedRenown = reader.GetInt();
            TargetEnemyUnitId = reader.GetUShort();
            DamageAmount = reader.GetFloat();
            IsKill = reader.GetBool();

            ushort allyCount = reader.GetUShort();
            AlliedPeerIds = new List<int>(allyCount);
            for (int i = 0; i < allyCount; i++)
            {
                AlliedPeerIds.Add(reader.GetInt());
            }

            ushort enemyCount = reader.GetUShort();
            EnemyUnits = new List<TroopUnitSnapshot>(enemyCount);
            for (int i = 0; i < enemyCount; i++)
            {
                var u = new TroopUnitSnapshot();
                u.Deserialize(reader);
                EnemyUnits.Add(u);
            }
        }
    }

    /// <summary>
    /// Synchronizes Co-op Town Tournaments (shared bracket, live player rounds/finals, winner & prize sync)
    /// and Co-op Bandit Hideout Raids (lobby, synchronized ambush launch, shared clear & reward).
    /// ActionType:
    /// 1 = TournamentLobbyOpen
    /// 2 = TournamentJoin
    /// 3 = TournamentStart (with deterministic BracketSeed)
    /// 4 = TournamentPvpMatchStart (live arena match between 2 players in the same tournament)
    /// 5 = TournamentMatchResult
    /// 6 = TournamentFinished (winner, prize item & gold)
    /// 10 = HideoutLobbyOpen
    /// 11 = HideoutJoin
    /// 12 = HideoutLaunch (synchronized OpenHideoutBattleMission)
    /// 13 = HideoutCompleted (shared clear, gold & renown)
    /// 20 = CancelActivity
    /// </summary>
    public class CoopActivitySyncPacket : IPacket
    {
        public PacketType Type => PacketType.CoopActivitySync;

        public byte ActionType { get; set; }
        public string ActivityId { get; set; } = string.Empty;
        public string SettlementId { get; set; } = string.Empty;
        public string SettlementName { get; set; } = string.Empty;
        public int HostPeerId { get; set; }
        public int ActorPeerId { get; set; }
        public int OpponentPeerId { get; set; }
        public string HostName { get; set; } = string.Empty;
        public string ActorName { get; set; } = string.Empty;
        public int BracketSeed { get; set; }
        public byte RoundNumber { get; set; }
        public bool ActorWon { get; set; }
        public string PrizeItemId { get; set; } = string.Empty;
        public string PrizeName { get; set; } = string.Empty;
        public int RewardGold { get; set; }
        public int RewardRenown { get; set; }
        public List<int> ParticipantPeerIds { get; set; } = new List<int>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ActionType);
            writer.Put(ActivityId ?? string.Empty);
            writer.Put(SettlementId ?? string.Empty);
            writer.Put(SettlementName ?? string.Empty);
            writer.Put(HostPeerId);
            writer.Put(ActorPeerId);
            writer.Put(OpponentPeerId);
            writer.Put(HostName ?? string.Empty);
            writer.Put(ActorName ?? string.Empty);
            writer.Put(BracketSeed);
            writer.Put(RoundNumber);
            writer.Put(ActorWon);
            writer.Put(PrizeItemId ?? string.Empty);
            writer.Put(PrizeName ?? string.Empty);
            writer.Put(RewardGold);
            writer.Put(RewardRenown);

            int count = ParticipantPeerIds?.Count ?? 0;
            writer.Put((ushort)count);
            for (int i = 0; i < count; i++)
            {
                writer.Put(ParticipantPeerIds![i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ActionType = reader.GetByte();
            ActivityId = reader.GetString();
            SettlementId = reader.GetString();
            SettlementName = reader.GetString();
            HostPeerId = reader.GetInt();
            ActorPeerId = reader.GetInt();
            OpponentPeerId = reader.GetInt();
            HostName = reader.GetString();
            ActorName = reader.GetString();
            BracketSeed = reader.GetInt();
            RoundNumber = reader.GetByte();
            ActorWon = reader.GetBool();
            PrizeItemId = reader.GetString();
            PrizeName = reader.GetString();
            RewardGold = reader.GetInt();
            RewardRenown = reader.GetInt();

            ushort count = reader.GetUShort();
            ParticipantPeerIds = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                ParticipantPeerIds.Add(reader.GetInt());
            }
        }
    }

    /// <summary>
    /// Synchronizes server-spawned AI parties (Caravans, Villagers, Bandits, Looters, Lords)
    /// to all connected clients with identical StringIds and coordinates, and allows clients
    /// to request spawn metadata for any newly spawned AI party missing locally.
    /// PartyKind:
    /// 0 = RequestMissingFromServer (Client -> Server)
    /// 1 = Caravan
    /// 2 = Villager
    /// 3 = Bandit
    /// 4 = Looter
    /// 5 = Lord
    /// </summary>
    public class AiPartySpawnSyncPacket : IPacket
    {
        public PacketType Type => PacketType.AiPartySpawnSync;

        public byte PartyKind { get; set; }
        public string PartyId { get; set; } = string.Empty;
        public string OwnerHeroId { get; set; } = string.Empty;
        public string LeaderHeroId { get; set; } = string.Empty;
        public string ClanId { get; set; } = string.Empty;
        public string SettlementId { get; set; } = string.Empty;
        public string TemplateId { get; set; } = string.Empty;
        public float PosX { get; set; }
        public float PosY { get; set; }
        public bool IsAtSea { get; set; }
        public bool IsElite { get; set; }
        public bool IsBossParty { get; set; }
        public int TroopCount { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PartyKind);
            writer.Put(PartyId ?? string.Empty);
            writer.Put(OwnerHeroId ?? string.Empty);
            writer.Put(LeaderHeroId ?? string.Empty);
            writer.Put(ClanId ?? string.Empty);
            writer.Put(SettlementId ?? string.Empty);
            writer.Put(TemplateId ?? string.Empty);
            writer.Put(PosX);
            writer.Put(PosY);
            writer.Put(IsAtSea);
            writer.Put(IsElite);
            writer.Put(IsBossParty);
            writer.Put(TroopCount);
        }

        public void Deserialize(NetDataReader reader)
        {
            PartyKind = reader.GetByte();
            PartyId = reader.GetString();
            OwnerHeroId = reader.GetString();
            LeaderHeroId = reader.GetString();
            ClanId = reader.GetString();
            SettlementId = reader.GetString();
            TemplateId = reader.GetString();
            PosX = reader.GetFloat();
            PosY = reader.GetFloat();
            IsAtSea = reader.GetBool();
            IsElite = reader.GetBool();
            IsBossParty = reader.GetBool();
            TroopCount = reader.GetInt();
        }
    }
}
