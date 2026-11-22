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
}
