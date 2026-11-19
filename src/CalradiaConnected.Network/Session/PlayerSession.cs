using System;
using LiteNetLib;

namespace CalradiaConnected.Network.Session
{
    public class PlayerSession
    {
        public int PeerId { get; }
        public NetPeer Peer { get; }
        public string PlayerId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public bool IsAuthenticated { get; set; }
        public DateTime ConnectedAt { get; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        public PlayerSession(int peerId, NetPeer peer)
        {
            PeerId = peerId;
            Peer = peer;
        }
    }
}

