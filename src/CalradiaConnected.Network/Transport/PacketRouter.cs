using System;
using LiteNetLib;
using LiteNetLib.Utils;
using CalradiaConnected.Common.Packets;

namespace CalradiaConnected.Network.Transport
{
    public class PacketRouter
    {
        private readonly NetPacketProcessor _processor = new NetPacketProcessor();

        public PacketRouter()
        {
            // Register nested serializables
            _processor.RegisterNestedType<PartySnapshotData>(() => new PartySnapshotData());
            _processor.RegisterNestedType<RosterEntryData>(() => new RosterEntryData());
            _processor.RegisterNestedType<NavalShipSnapshotData>(() => new NavalShipSnapshotData());
            _processor.RegisterNestedType<TroopUnitSnapshot>(() => new TroopUnitSnapshot());

            RegisterPacket<HandshakeRequestPacket>();
            RegisterPacket<HandshakeResponsePacket>();
            RegisterPacket<HeartbeatPacket>();
            RegisterPacket<PlayerJoinedPacket>();
            RegisterPacket<PlayerLeftPacket>();
            RegisterPacket<ChatMessagePacket>();
            RegisterPacket<PlayerMoveOrderPacket>();
            RegisterPacket<WorldStateSnapshotPacket>();
            RegisterPacket<SettlementInteractionPacket>();
            RegisterPacket<PlayerHeroCreatedPacket>();
            RegisterPacket<CampaignTimeSpeedPacket>();
            RegisterPacket<PartyRosterSyncPacket>();
            RegisterPacket<SiegeStateSyncPacket>();
            RegisterPacket<ServerAdminRewardPacket>();
            RegisterPacket<PlayerInteractionPacket>();
            RegisterPacket<BattleSessionSyncPacket>();
            RegisterPacket<MissionAgentSpawnPacket>();
            RegisterPacket<MissionAgentStatePacket>();
            RegisterPacket<MissionBlowSyncPacket>();
            RegisterPacket<P2PDialogPacket>();
            RegisterPacket<HeroExecutionPacket>();
            RegisterPacket<PartyFleetSyncPacket>();
            RegisterPacket<MissionNavalShipStatePacket>();
            RegisterPacket<MissionTroopsSyncPacket>();
            RegisterPacket<MissionDuelRequestPacket>();
            RegisterPacket<MissionDuelResponsePacket>();
            RegisterPacket<MissionDuelRoundResetPacket>();
            RegisterPacket<WorldSaveRequestPacket>();
            RegisterPacket<WorldSaveChunkPacket>();
            RegisterPacket<ServerConfigSyncPacket>();
            RegisterPacket<KingdomDiplomacySyncPacket>();
            RegisterPacket<PlayerArmySyncPacket>();
            RegisterPacket<CoopBattleSyncPacket>();
            RegisterPacket<CoopActivitySyncPacket>();
            RegisterPacket<AiPartySpawnSyncPacket>();
        }

        public void RegisterPacket<T>() where T : class, INetSerializable, new()
        {
            _processor.RegisterNestedType<T>(() => new T());
        }

        public void Subscribe<T>(Action<T, NetPeer> onReceive) where T : class, INetSerializable, new()
        {
            _processor.SubscribeNetSerializable<T, NetPeer>(onReceive);
        }

        public void Subscribe<T>(Action<T> onReceive) where T : class, INetSerializable, new()
        {
            _processor.SubscribeNetSerializable<T>(onReceive);
        }

        public void Send<T>(NetPeer peer, T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, INetSerializable
        {
            var writer = new NetDataWriter();
            _processor.WriteNetSerializable(writer, ref packet);
            peer.Send(writer, deliveryMethod);
        }

        public void SendAll<T>(NetManager netManager, T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, INetSerializable
        {
            var writer = new NetDataWriter();
            _processor.WriteNetSerializable(writer, ref packet);
            netManager.SendToAll(writer, deliveryMethod);
        }

        public void ReadPacket(NetPacketReader reader, NetPeer peer)
        {
            _processor.ReadAllPackets(reader, peer);
        }
    }
}

