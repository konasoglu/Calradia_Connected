using System;
using CalradiaConnected.Common.Packets;
using LiteNetLib;

namespace CalradiaConnected.Network.Transport
{
    public interface INetworkClient
    {
        bool IsConnected { get; }
        int LocalPeerId { get; }
        int Ping { get; }
        PacketRouter Router { get; }

        event Action<HandshakeResponsePacket>? OnConnected;
        event Action<string>? OnDisconnected;
        event Action<ChatMessagePacket>? OnChatMessage;
        event Action<PlayerJoinedPacket>? OnPlayerJoined;
        event Action<PlayerLeftPacket>? OnPlayerLeft;

        void Connect(string host, int port, string connectionKey = "CalradiaConnected", string playerName = "Player", string passwordHash = "", string playerId = "", string serverPassword = "");
        void Disconnect();
        void Poll();
        void Send<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable;
    }
}

