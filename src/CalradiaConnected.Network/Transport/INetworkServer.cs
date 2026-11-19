using System;
using System.Collections.Generic;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Network.Session;
using LiteNetLib;

namespace CalradiaConnected.Network.Transport
{
    public interface INetworkServer
    {
        bool IsRunning { get; }
        int Port { get; }
        string ServerName { get; set; }
        string RequiredServerPassword { get; set; }
        string ServerVisibility { get; set; }
        int MaxPlayers { get; set; }
        int TickRateHz { get; }
        int DisconnectTimeoutSec { get; }
        IReadOnlyDictionary<int, PlayerSession> Sessions { get; }

        void ApplyNetworkSettings(int tickRateHz, int timeoutSec, int pollIntervalMs = 20);
        bool KickSession(string target, string reason);

        event Action<PlayerSession>? OnPlayerJoined;
        event Action<PlayerSession, string>? OnPlayerLeft;
        event Action<PlayerSession, ChatMessagePacket>? OnChatMessage;

        bool Start(int port, string connectionKey = "CalradiaConnected");
        void Stop();
        void Poll();
        void Broadcast<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable;
        void BroadcastExcept<T>(T packet, NetPeer? excludePeer, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable;
        void SendTo<T>(int peerId, T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable;
        int ResolveSessionPeerId(NetPeer? peer, int fallbackPeerId = 0);
        bool TryGetSession(NetPeer? peer, out PlayerSession? session);
        PacketRouter Router { get; }
    }
}

