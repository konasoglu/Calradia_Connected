using System.Collections.Generic;
using LiteNetLib.Utils;

namespace CalradiaConnected.Common.Packets
{
    public class PartySnapshotData : INetSerializable
    {
        public string PartyId { get; set; } = string.Empty;
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public float Heading { get; set; }
        public float HeadingY { get; set; }
        public float Speed { get; set; }
        public bool IsVisible { get; set; }
        public string CurrentSettlementId { get; set; } = string.Empty;
        public bool IsAtSea { get; set; }
        public byte NavigationType { get; set; } // 0=None, 1=Default, 2=Naval, 3=All

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PartyId ?? string.Empty);
            writer.Put(PosX);
            writer.Put(PosY);
            writer.Put(TargetX);
            writer.Put(TargetY);
            writer.Put(Heading);
            writer.Put(HeadingY);
            writer.Put(Speed);
            writer.Put(IsVisible);
            writer.Put(CurrentSettlementId ?? string.Empty);
            writer.Put(IsAtSea);
            writer.Put(NavigationType);
        }

        public void Deserialize(NetDataReader reader)
        {
            PartyId = reader.GetString();
            PosX = reader.GetFloat();
            PosY = reader.GetFloat();
            TargetX = reader.GetFloat();
            TargetY = reader.GetFloat();
            Heading = reader.GetFloat();
            HeadingY = reader.GetFloat();
            Speed = reader.GetFloat();
            IsVisible = reader.GetBool();
            CurrentSettlementId = reader.GetString();
            IsAtSea = reader.AvailableBytes >= 1 && reader.GetBool();
            NavigationType = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)1;
        }
    }

    public class PlayerMoveOrderPacket : IPacket
    {
        public PacketType Type => PacketType.PlayerMoveOrder;

        public int PeerId { get; set; }
        public byte MoveType { get; set; } // 0 = GoToPoint, 1 = TargetSettlement, 2 = TargetParty, 3 = LivePositionSync
        public float CurrentX { get; set; }
        public float CurrentY { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public string TargetPartyId { get; set; } = string.Empty;
        public string TargetSettlementId { get; set; } = string.Empty;
        public float Speed { get; set; }
        public bool IsAtSea { get; set; }
        public byte NavigationType { get; set; } // 0=None, 1=Default, 2=Naval, 3=All
        public bool IsTargetingPort { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(MoveType);
            writer.Put(CurrentX);
            writer.Put(CurrentY);
            writer.Put(TargetX);
            writer.Put(TargetY);
            writer.Put(TargetPartyId ?? string.Empty);
            writer.Put(TargetSettlementId ?? string.Empty);
            writer.Put(Speed);
            writer.Put(IsAtSea);
            writer.Put(NavigationType);
            writer.Put(IsTargetingPort);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            MoveType = reader.GetByte();
            CurrentX = reader.GetFloat();
            CurrentY = reader.GetFloat();
            TargetX = reader.GetFloat();
            TargetY = reader.GetFloat();
            TargetPartyId = reader.GetString();
            TargetSettlementId = reader.GetString();
            Speed = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            IsAtSea = reader.AvailableBytes >= 1 && reader.GetBool();
            NavigationType = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)1;
            IsTargetingPort = reader.AvailableBytes >= 1 && reader.GetBool();
        }
    }

    public class CampaignTimeSpeedPacket : IPacket
    {
        public PacketType Type => PacketType.CampaignTimeSpeed;

        public byte SpeedMode { get; set; } // 1 = Normal, 2 = FastForward
        public long ServerTick { get; set; }
        public long ServerCampaignTicks { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SpeedMode);
            writer.Put(ServerTick);
            writer.Put(ServerCampaignTicks);
        }

        public void Deserialize(NetDataReader reader)
        {
            SpeedMode = reader.GetByte();
            ServerTick = reader.GetLong();
            ServerCampaignTicks = reader.GetLong();
        }
    }

    public class WorldStateSnapshotPacket : IPacket
    {
        public PacketType Type => PacketType.WorldStateSnapshot;

        public long ServerTick { get; set; }
        public List<PartySnapshotData> Parties { get; set; } = new List<PartySnapshotData>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ServerTick);
            writer.Put(Parties.Count);
            for (int i = 0; i < Parties.Count; i++)
            {
                Parties[i].Serialize(writer);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ServerTick = reader.GetLong();
            int count = reader.GetInt();
            Parties = new List<PartySnapshotData>(count);
            for (int i = 0; i < count; i++)
            {
                var snap = new PartySnapshotData();
                snap.Deserialize(reader);
                Parties.Add(snap);
            }
        }
    }

    public class SettlementInteractionPacket : IPacket
    {
        public PacketType Type => PacketType.SettlementInteraction;

        public int PeerId { get; set; }
        public string SettlementId { get; set; } = string.Empty;
        public byte ActionType { get; set; } // 1 = Enter, 2 = Leave, 3 = Trade, 4 = Recruit
        public string NotableId { get; set; } = string.Empty;
        public int SlotIndex { get; set; } = -1;
        public string ItemOrTroopId { get; set; } = string.Empty;
        public int Count { get; set; }
        public int GoldDelta { get; set; }
        public string Payload { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(SettlementId ?? string.Empty);
            writer.Put(ActionType);
            writer.Put(NotableId ?? string.Empty);
            writer.Put(SlotIndex);
            writer.Put(ItemOrTroopId ?? string.Empty);
            writer.Put(Count);
            writer.Put(GoldDelta);
            writer.Put(Payload ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            SettlementId = reader.GetString();
            ActionType = reader.GetByte();
            NotableId = reader.GetString();
            SlotIndex = reader.GetInt();
            ItemOrTroopId = reader.GetString();
            Count = reader.GetInt();
            GoldDelta = reader.GetInt();
            Payload = reader.GetString();
        }
    }

    public class RosterEntryData : INetSerializable
    {
        public string ObjectId { get; set; } = string.Empty;
        public int Count { get; set; }
        public int WoundedCount { get; set; }
        public bool IsItem { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ObjectId ?? string.Empty);
            writer.Put(Count);
            writer.Put(WoundedCount);
            writer.Put(IsItem);
        }

        public void Deserialize(NetDataReader reader)
        {
            ObjectId = reader.GetString();
            Count = reader.GetInt();
            WoundedCount = reader.GetInt();
            IsItem = reader.GetBool();
        }
    }

    public class PartyRosterSyncPacket : IPacket
    {
        public PacketType Type => PacketType.PartyRosterSync;

        public int PeerId { get; set; }
        public string PartyId { get; set; } = string.Empty;
        public int HeroGold { get; set; }
        public int TotalHealthy { get; set; }
        public int TotalWounded { get; set; }
        public string EquipmentCsv { get; set; } = string.Empty;
        public string ItemsCsv { get; set; } = string.Empty;
        public float ClanRenown { get; set; }
        public float ClanInfluence { get; set; }
        public int HeroHp { get; set; } = 100;
        public int HeroMaxHp { get; set; } = 100;
        public int PartySizeLimit { get; set; }
        public List<RosterEntryData> Entries { get; set; } = new List<RosterEntryData>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(PartyId ?? string.Empty);
            writer.Put(HeroGold);
            writer.Put(TotalHealthy);
            writer.Put(TotalWounded);
            writer.Put(EquipmentCsv ?? string.Empty);
            writer.Put(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                Entries[i].Serialize(writer);
            }
            writer.Put(ItemsCsv ?? string.Empty);
            writer.Put(ClanRenown);
            writer.Put(ClanInfluence);
            writer.Put(HeroHp);
            writer.Put(HeroMaxHp);
            writer.Put(PartySizeLimit);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            PartyId = reader.GetString();
            HeroGold = reader.GetInt();
            TotalHealthy = reader.GetInt();
            TotalWounded = reader.GetInt();
            EquipmentCsv = reader.GetString();
            int count = reader.GetInt();
            Entries = new List<RosterEntryData>(count);
            for (int i = 0; i < count; i++)
            {
                var entry = new RosterEntryData();
                entry.Deserialize(reader);
                Entries.Add(entry);
            }
            ItemsCsv = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            ClanRenown = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            ClanInfluence = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            HeroHp = reader.AvailableBytes >= 4 ? reader.GetInt() : 100;
            HeroMaxHp = reader.AvailableBytes >= 4 ? reader.GetInt() : 100;
            PartySizeLimit = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
        }
    }

    public class SiegeStateSyncPacket : IPacket
    {
        public PacketType Type => PacketType.SiegeStateSync;

        public int PeerId { get; set; }
        public string SettlementId { get; set; } = string.Empty;
        public string BesiegerPartyId { get; set; } = string.Empty;
        public bool IsUnderSiege { get; set; }
        public float Progress { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(SettlementId ?? string.Empty);
            writer.Put(BesiegerPartyId ?? string.Empty);
            writer.Put(IsUnderSiege);
            writer.Put(Progress);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            SettlementId = reader.GetString();
            BesiegerPartyId = reader.GetString();
            IsUnderSiege = reader.GetBool();
            Progress = reader.GetFloat();
        }
    }

    public class ServerAdminRewardPacket : IPacket
    {
        public PacketType Type => PacketType.ServerAdminReward;

        /// <summary>
        /// 0 = All connected players, otherwise specific PeerId (e.g. 1001).
        /// </summary>
        public int TargetPeerId { get; set; }

        /// <summary>
        /// 1 = Gold, 2 = Renown, 3 = Influence, 4 = HealHero, 5 = HealParty, 6 = GiveTroop, 7 = DuplicateTroops
        /// </summary>
        public byte RewardType { get; set; }
        public int IntAmount { get; set; }
        public float FloatAmount { get; set; }
        public string TroopId { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(TargetPeerId);
            writer.Put(RewardType);
            writer.Put(IntAmount);
            writer.Put(FloatAmount);
            writer.Put(TroopId ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            TargetPeerId = reader.GetInt();
            RewardType = reader.GetByte();
            IntAmount = reader.GetInt();
            FloatAmount = reader.GetFloat();
            TroopId = reader.GetString();
        }
    }

    public class PlayerHeroCreatedPacket : IPacket
    {
        public PacketType Type => PacketType.PlayerHeroCreated;

        public int PeerId { get; set; }
        public string HeroName { get; set; } = string.Empty;
        public string ClanName { get; set; } = string.Empty;
        public string CultureId { get; set; } = "empire";
        public string BannerKey { get; set; } = string.Empty;
        public string StartingSettlementId { get; set; } = string.Empty;
        public int StartingGold { get; set; }
        public int StartingTroops { get; set; }
        public string EquipmentCsv { get; set; } = string.Empty;
        public float CurrentX { get; set; }
        public float CurrentY { get; set; }
        public bool IsFemale { get; set; }
        public string BodyPropertiesStr { get; set; } = string.Empty;
        public bool IsAtSea { get; set; }
        public byte NavigationType { get; set; } = 1;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(HeroName ?? string.Empty);
            writer.Put(ClanName ?? string.Empty);
            writer.Put(CultureId ?? "empire");
            writer.Put(BannerKey ?? string.Empty);
            writer.Put(StartingSettlementId ?? string.Empty);
            writer.Put(StartingGold);
            writer.Put(StartingTroops);
            writer.Put(EquipmentCsv ?? string.Empty);
            writer.Put(CurrentX);
            writer.Put(CurrentY);
            writer.Put(IsFemale);
            writer.Put(BodyPropertiesStr ?? string.Empty);
            writer.Put(IsAtSea);
            writer.Put(NavigationType);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            HeroName = reader.GetString();
            ClanName = reader.GetString();
            CultureId = reader.GetString();
            BannerKey = reader.GetString();
            StartingSettlementId = reader.GetString();
            StartingGold = reader.GetInt();
            StartingTroops = reader.GetInt();
            EquipmentCsv = reader.GetString();
            CurrentX = reader.GetFloat();
            CurrentY = reader.GetFloat();
            IsFemale = reader.AvailableBytes >= 1 && reader.GetBool();
            BodyPropertiesStr = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            IsAtSea = reader.AvailableBytes >= 1 && reader.GetBool();
            NavigationType = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)1;
        }
    }

    public class PlayerInteractionPacket : IPacket
    {
        public PacketType Type => PacketType.PlayerInteraction;

        public int SenderPeerId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public int TargetPeerId { get; set; }
        public string TargetName { get; set; } = string.Empty;

        /// <summary>
        /// 1 = SendGold, 2 = SendTroops, 3 = DuelRequest, 4 = DuelResponse, 5 = DuelResult, 6 = RallyCall, 7 = DirectMessage, 8 = RelationSync, 9 = DiplomacyAction
        /// </summary>
        public byte InteractionType { get; set; }
        public int IntAmount { get; set; }
        public string ObjectId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SenderPeerId);
            writer.Put(SenderName ?? string.Empty);
            writer.Put(TargetPeerId);
            writer.Put(TargetName ?? string.Empty);
            writer.Put(InteractionType);
            writer.Put(IntAmount);
            writer.Put(ObjectId ?? string.Empty);
            writer.Put(Message ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            SenderPeerId = reader.GetInt();
            SenderName = reader.GetString();
            TargetPeerId = reader.GetInt();
            TargetName = reader.GetString();
            InteractionType = reader.GetByte();
            IntAmount = reader.GetInt();
            ObjectId = reader.GetString();
            Message = reader.GetString();
        }
    }

    public class BattleSessionSyncPacket : IPacket
    {
        public PacketType Type => PacketType.BattleSessionSync;

        public int AttackerPeerId { get; set; }
        public string AttackerPartyId { get; set; } = string.Empty;
        public string DefenderPartyId { get; set; } = string.Empty;

        /// <summary>
        /// 1 = BattleStarted, 2 = AttackerWon, 3 = DefenderWon, 4 = Retreated,
        /// 10 = PvpBattleRequest, 11 = PvpBattleStart, 12 = PvpBattleEnded
        /// </summary>
        /// <summary>1 = BattleStarted, 2 = Won, 3 = Lost, 10 = PvpRequest, 11 = PvpStart, 12 = PvpPlayerReady, 13 = PvpCombatBegin, 14 = PvpReadyStatusSync, 15 = PvpEnded, 16 = PvpRequestDeclined, 17 = PvpRequestAccepted, 18 = PvpRequestCancelled</summary>
        public byte BattleStage { get; set; }
        public int DefenderPeerId { get; set; }
        public int DefenderTroopsLost { get; set; }
        public bool DisbandDefender { get; set; }
        public bool AttackerReady { get; set; }
        public bool DefenderReady { get; set; }
        public bool IsNavalBattle { get; set; }
        public int WindSeed { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(AttackerPeerId);
            writer.Put(AttackerPartyId ?? string.Empty);
            writer.Put(DefenderPartyId ?? string.Empty);
            writer.Put(BattleStage);
            writer.Put(DefenderTroopsLost);
            writer.Put(DisbandDefender);
            writer.Put(DefenderPeerId);
            writer.Put(AttackerReady);
            writer.Put(DefenderReady);
            writer.Put(IsNavalBattle);
            writer.Put(WindSeed);
        }

        public void Deserialize(NetDataReader reader)
        {
            AttackerPeerId = reader.GetInt();
            AttackerPartyId = reader.GetString();
            DefenderPartyId = reader.GetString();
            BattleStage = reader.GetByte();
            DefenderTroopsLost = reader.GetInt();
            DisbandDefender = reader.GetBool();
            DefenderPeerId = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            AttackerReady = reader.AvailableBytes >= 1 && reader.GetBool();
            DefenderReady = reader.AvailableBytes >= 1 && reader.GetBool();
            IsNavalBattle = reader.AvailableBytes >= 1 && reader.GetBool();
            WindSeed = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
        }
    }

    public class P2PDialogPacket : IPacket
    {
        public PacketType Type => PacketType.P2PDialog;

        public string SessionId { get; set; } = string.Empty;
        public int InitiatorPeerId { get; set; }
        public int TargetPeerId { get; set; }
        public int SenderPeerId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        /// <summary>1 = Open, 2 = LineSelected, 3 = Proposal, 4 = Response, 5 = Close, 6 = TradeOfferUpdate, 7 = TradeToggleAccept, 8 = TradeCommitted, 9 = TradeReset, 10 = OpenTrade, 11 = CloseTrade, 12 = NativeTradeOffer, 13 = NativeTradeAccept, 14 = NativeTradeDecline, 15 = LiveBarterOpen, 16 = LiveBarterSync, 17 = LiveBarterAccept, 18 = LiveBarterCommitted, 19 = LiveBarterClosed</summary>
        public byte Stage { get; set; }
        public string OptionKey { get; set; } = string.Empty;
        public string SpeakerName { get; set; } = string.Empty;
        public string DialogText { get; set; } = string.Empty;
        public int OfferAmount { get; set; }
        public string OfferObjectId { get; set; } = string.Empty;

        // Server-Authoritative Mutual Trade State
        public int InitiatorTradeGold { get; set; }
        public string InitiatorTradeTroopId { get; set; } = string.Empty;
        public int InitiatorTradeTroopCount { get; set; }
        public string InitiatorTradeItemId { get; set; } = string.Empty;
        public int InitiatorTradeItemCount { get; set; }
        public int InitiatorTradeShipIndex { get; set; } = -1;
        public string InitiatorTradeShipDesc { get; set; } = string.Empty;
        public bool InitiatorTradeAccepted { get; set; }
        public int TargetTradeGold { get; set; }
        public string TargetTradeTroopId { get; set; } = string.Empty;
        public int TargetTradeTroopCount { get; set; }
        public string TargetTradeItemId { get; set; } = string.Empty;
        public int TargetTradeItemCount { get; set; }
        public int TargetTradeShipIndex { get; set; } = -1;
        public string TargetTradeShipDesc { get; set; } = string.Empty;
        public bool TargetTradeAccepted { get; set; }
        public string BoughtItemsCsv { get; set; } = string.Empty;
        public string SoldItemsCsv { get; set; } = string.Empty;
        public int NetGoldDelta { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SessionId ?? string.Empty);
            writer.Put(InitiatorPeerId);
            writer.Put(TargetPeerId);
            writer.Put(SenderPeerId);
            writer.Put(SenderName ?? string.Empty);
            writer.Put(Stage);
            writer.Put(OptionKey ?? string.Empty);
            writer.Put(SpeakerName ?? string.Empty);
            writer.Put(DialogText ?? string.Empty);
            writer.Put(OfferAmount);
            writer.Put(OfferObjectId ?? string.Empty);
            writer.Put(InitiatorTradeGold);
            writer.Put(InitiatorTradeTroopId ?? string.Empty);
            writer.Put(InitiatorTradeTroopCount);
            writer.Put(InitiatorTradeAccepted);
            writer.Put(TargetTradeGold);
            writer.Put(TargetTradeTroopId ?? string.Empty);
            writer.Put(TargetTradeTroopCount);
            writer.Put(TargetTradeAccepted);
            writer.Put(InitiatorTradeItemId ?? string.Empty);
            writer.Put(InitiatorTradeItemCount);
            writer.Put(TargetTradeItemId ?? string.Empty);
            writer.Put(TargetTradeItemCount);
            writer.Put(InitiatorTradeShipIndex);
            writer.Put(InitiatorTradeShipDesc ?? string.Empty);
            writer.Put(TargetTradeShipIndex);
            writer.Put(TargetTradeShipDesc ?? string.Empty);
            writer.Put(BoughtItemsCsv ?? string.Empty);
            writer.Put(SoldItemsCsv ?? string.Empty);
            writer.Put(NetGoldDelta);
        }

        public void Deserialize(NetDataReader reader)
        {
            SessionId = reader.GetString();
            InitiatorPeerId = reader.GetInt();
            TargetPeerId = reader.GetInt();
            SenderPeerId = reader.GetInt();
            SenderName = reader.GetString();
            Stage = reader.GetByte();
            OptionKey = reader.GetString();
            SpeakerName = reader.GetString();
            DialogText = reader.GetString();
            OfferAmount = reader.GetInt();
            OfferObjectId = reader.GetString();
            InitiatorTradeGold = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            InitiatorTradeTroopId = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            InitiatorTradeTroopCount = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            InitiatorTradeAccepted = reader.AvailableBytes >= 1 && reader.GetBool();
            TargetTradeGold = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            TargetTradeTroopId = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            TargetTradeTroopCount = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            TargetTradeAccepted = reader.AvailableBytes >= 1 && reader.GetBool();
            InitiatorTradeItemId = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            InitiatorTradeItemCount = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            TargetTradeItemId = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            TargetTradeItemCount = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            InitiatorTradeShipIndex = reader.AvailableBytes >= 4 ? reader.GetInt() : -1;
            InitiatorTradeShipDesc = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            TargetTradeShipIndex = reader.AvailableBytes >= 4 ? reader.GetInt() : -1;
            TargetTradeShipDesc = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            BoughtItemsCsv = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            SoldItemsCsv = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            NetGoldDelta = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
        }
    }

    public class HeroExecutionPacket : IPacket
    {
        public PacketType Type => PacketType.HeroExecution;

        public int VictimPeerId { get; set; }
        public int KillerPeerId { get; set; }
        public string KillerName { get; set; } = string.Empty;
        public string VictimHeroId { get; set; } = string.Empty;
        public string VictimHeroName { get; set; } = string.Empty;
        /// <summary>1 = Executed, 2 = KilledInBattle, 3 = RespawnResetRequest, 4 = RespawnResetApproved</summary>
        public byte ReasonStage { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(VictimPeerId);
            writer.Put(KillerPeerId);
            writer.Put(KillerName ?? string.Empty);
            writer.Put(VictimHeroId ?? string.Empty);
            writer.Put(VictimHeroName ?? string.Empty);
            writer.Put(ReasonStage);
        }

        public void Deserialize(NetDataReader reader)
        {
            VictimPeerId = reader.GetInt();
            KillerPeerId = reader.GetInt();
            KillerName = reader.GetString();
            VictimHeroId = reader.GetString();
            VictimHeroName = reader.GetString();
            ReasonStage = reader.GetByte();
        }
    }

    public class NavalShipSnapshotData : INetSerializable
    {
        public string HullId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public float HitPoints { get; set; }
        public float SailHitPoints { get; set; }
        public string FigureheadId { get; set; } = string.Empty;
        public string CustomSailPatternId { get; set; } = string.Empty;
        public string UpgradesCsv { get; set; } = string.Empty; // slotTag=pieceId;...

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(HullId ?? string.Empty);
            writer.Put(Name ?? string.Empty);
            writer.Put(HitPoints);
            writer.Put(SailHitPoints);
            writer.Put(FigureheadId ?? string.Empty);
            writer.Put(CustomSailPatternId ?? string.Empty);
            writer.Put(UpgradesCsv ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            HullId = reader.GetString();
            Name = reader.GetString();
            HitPoints = reader.GetFloat();
            SailHitPoints = reader.GetFloat();
            FigureheadId = reader.GetString();
            CustomSailPatternId = reader.GetString();
            UpgradesCsv = reader.GetString();
        }
    }

    public class PartyFleetSyncPacket : IPacket
    {
        public PacketType Type => PacketType.PartyFleetSync;

        public int PeerId { get; set; }
        public string PartyId { get; set; } = string.Empty;
        public bool IsAtSea { get; set; }
        public byte NavigationType { get; set; } // 0=None, 1=Default, 2=Naval, 3=All
        public bool HasAnchor { get; set; }
        public float AnchorX { get; set; }
        public float AnchorY { get; set; }
        public List<NavalShipSnapshotData> Ships { get; set; } = new List<NavalShipSnapshotData>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(PartyId ?? string.Empty);
            writer.Put(IsAtSea);
            writer.Put(NavigationType);
            writer.Put(HasAnchor);
            writer.Put(AnchorX);
            writer.Put(AnchorY);
            writer.Put(Ships.Count);
            for (int i = 0; i < Ships.Count; i++)
            {
                Ships[i].Serialize(writer);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            PartyId = reader.GetString();
            IsAtSea = reader.GetBool();
            NavigationType = reader.GetByte();
            HasAnchor = reader.GetBool();
            AnchorX = reader.GetFloat();
            AnchorY = reader.GetFloat();
            int count = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            Ships = new List<NavalShipSnapshotData>(count);
            for (int i = 0; i < count; i++)
            {
                var ship = new NavalShipSnapshotData();
                ship.Deserialize(reader);
                Ships.Add(ship);
            }
        }
    }

    public class WorldSaveRequestPacket : IPacket
    {
        public PacketType Type => PacketType.WorldSaveRequest;

        public int StartChunk { get; set; }
        public int BatchCount { get; set; } = 8;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(StartChunk);
            writer.Put(BatchCount);
        }

        public void Deserialize(NetDataReader reader)
        {
            StartChunk = reader.GetInt();
            BatchCount = reader.AvailableBytes >= 4 ? reader.GetInt() : 8;
        }
    }

    public class WorldSaveChunkPacket : IPacket
    {
        public PacketType Type => PacketType.WorldSaveChunk;

        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; }
        public int TotalCompressedBytes { get; set; }
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public byte[] Data { get; set; } = System.Array.Empty<byte>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChunkIndex);
            writer.Put(TotalChunks);
            writer.Put(TotalCompressedBytes);
            writer.Put(IsSuccess);
            writer.Put(ErrorMessage ?? string.Empty);
            writer.PutBytesWithLength(Data ?? System.Array.Empty<byte>());
        }

        public void Deserialize(NetDataReader reader)
        {
            ChunkIndex = reader.GetInt();
            TotalChunks = reader.GetInt();
            TotalCompressedBytes = reader.GetInt();
            IsSuccess = reader.GetBool();
            ErrorMessage = reader.GetString();
            Data = reader.AvailableBytes > 0 ? reader.GetBytesWithLength() : System.Array.Empty<byte>();
        }
    }

    public class ServerConfigSyncPacket : IPacket
    {
        public PacketType Type => PacketType.ServerConfigSync;

        public int BattleSize { get; set; } = 400;
        public int DamageDifficulty { get; set; } = 1;
        public int CombatAiDifficulty { get; set; } = 1;
        public int HeroDeathInBattle { get; set; } = 0;
        public int AutoSaveIntervalMinutes { get; set; } = 15;
        public int NumberOfCorpses { get; set; } = 20;
        public bool PvpEnabled { get; set; } = true;
        public bool PersistentWorldTime { get; set; } = true;
        public bool ArenaContinuousPracticeRespawn { get; set; } = true;
        public bool ArenaPvpDuelsEnabled { get; set; } = true;

        public int StartingLevel { get; set; } = 1;
        public int StartingFocusPoints { get; set; } = 5;
        public int StartingAttributePoints { get; set; } = 2;
        public int StartingAllSkillsLevel { get; set; } = 0;
        public float CharacterXpMultiplier { get; set; } = 1.0f;
        public float SkillXpMultiplier { get; set; } = 1.0f;
        public int StartingGold { get; set; } = 1000;
        public int StartingRenown { get; set; } = 0;
        public int StartingInfluence { get; set; } = 0;
        public int StartingEquipmentPreset { get; set; } = 0;
        public int StartingTroopsPreset { get; set; } = 0;
        public bool StartingFoodSupplies { get; set; } = true;

        public bool IsLocalPlayerAdmin { get; set; } = false;
        public bool ForceReapplyStarter { get; set; } = false;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(BattleSize);
            writer.Put(DamageDifficulty);
            writer.Put(CombatAiDifficulty);
            writer.Put(HeroDeathInBattle);
            writer.Put(AutoSaveIntervalMinutes);
            writer.Put(NumberOfCorpses);
            writer.Put(PvpEnabled);
            writer.Put(PersistentWorldTime);
            writer.Put(ArenaContinuousPracticeRespawn);
            writer.Put(ArenaPvpDuelsEnabled);

            writer.Put(StartingLevel);
            writer.Put(StartingFocusPoints);
            writer.Put(StartingAttributePoints);
            writer.Put(StartingAllSkillsLevel);
            writer.Put(CharacterXpMultiplier);
            writer.Put(SkillXpMultiplier);
            writer.Put(StartingGold);
            writer.Put(StartingRenown);
            writer.Put(StartingInfluence);
            writer.Put(StartingEquipmentPreset);
            writer.Put(StartingTroopsPreset);
            writer.Put(StartingFoodSupplies);

            writer.Put(IsLocalPlayerAdmin);
            writer.Put(ForceReapplyStarter);
        }

        public void Deserialize(NetDataReader reader)
        {
            BattleSize = reader.GetInt();
            DamageDifficulty = reader.GetInt();
            CombatAiDifficulty = reader.GetInt();
            HeroDeathInBattle = reader.GetInt();
            AutoSaveIntervalMinutes = reader.GetInt();
            NumberOfCorpses = reader.GetInt();
            PvpEnabled = reader.GetBool();
            PersistentWorldTime = reader.GetBool();
            ArenaContinuousPracticeRespawn = reader.GetBool();
            ArenaPvpDuelsEnabled = reader.GetBool();

            StartingLevel = reader.GetInt();
            StartingFocusPoints = reader.GetInt();
            StartingAttributePoints = reader.GetInt();
            StartingAllSkillsLevel = reader.GetInt();
            CharacterXpMultiplier = reader.GetFloat();
            SkillXpMultiplier = reader.GetFloat();
            StartingGold = reader.GetInt();
            StartingRenown = reader.GetInt();
            StartingInfluence = reader.GetInt();
            StartingEquipmentPreset = reader.GetInt();
            StartingTroopsPreset = reader.GetInt();
            StartingFoodSupplies = reader.GetBool();

            IsLocalPlayerAdmin = reader.AvailableBytes >= 1 && reader.GetBool();
            ForceReapplyStarter = reader.AvailableBytes >= 1 && reader.GetBool();
        }
    }
}

