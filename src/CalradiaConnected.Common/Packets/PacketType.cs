using LiteNetLib.Utils;

namespace CalradiaConnected.Common.Packets
{
    public enum PacketType : byte
    {
        None = 0,
        
        // Handshake & Session
        HandshakeRequest = 1,
        HandshakeResponse = 2,
        Heartbeat = 3,
        PlayerJoined = 4,
        PlayerLeft = 5,
        ChatMessage = 6,

        // Campaign World
        PlayerMoveOrder = 10,
        WorldStateSnapshot = 11,
        SettlementInteraction = 12,
        PlayerHeroCreated = 13,
        CampaignTimeSpeed = 14,
        PartyRosterSync = 15,
        SiegeStateSync = 16,
        ServerAdminReward = 17,
        PlayerInteraction = 18,
        BattleSessionSync = 19,

        // Missions & Arena
        MissionAgentSpawn = 20,
        MissionAgentState = 21,
        MissionBlowSync = 22,
        P2PDialog = 25,
        HeroExecution = 26,
        PartyFleetSync = 27,
        MissionNavalShipState = 28,
        MissionTroopsSync = 29,
        MissionDuelRequest = 30,
        MissionDuelResponse = 31,
        MissionDuelRoundReset = 32,

        // World Save Transfer (UDP fallback when FastDL HTTP 4201 is unreachable)
        WorldSaveRequest = 33,
        WorldSaveChunk = 34,

        // Authoritative Server Config, Game Rules & Progression Sync
        ServerConfigSync = 35
    }

    public interface IPacket : INetSerializable
    {
        PacketType Type { get; }
    }
}

