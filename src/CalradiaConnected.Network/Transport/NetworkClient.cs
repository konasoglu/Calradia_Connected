using System;
using System.Net;
using System.Net.Sockets;
using CalradiaConnected.Common.Packets;
using LiteNetLib;

namespace CalradiaConnected.Network.Transport
{
    public class NetworkClient : INetworkClient, INetEventListener
    {
        private NetManager? _netManager;
        private NetPeer? _serverPeer;
        private string _playerName = "Player";
        private string _passwordHash = "";
        private string _serverPassword = "";
        private string _playerId = "";
        private bool _handshakeHandled = false;

        public bool IsConnected => _serverPeer != null && _serverPeer.ConnectionState == ConnectionState.Connected;
        public int LocalPeerId { get; private set; }
        public int Ping => _serverPeer?.Ping ?? 0;
        public PacketRouter Router { get; } = new PacketRouter();

        public event Action<HandshakeResponsePacket>? OnConnected;
        public event Action<string>? OnDisconnected;
        public event Action<ChatMessagePacket>? OnChatMessage;
        public event Action<PlayerJoinedPacket>? OnPlayerJoined;
        public event Action<PlayerLeftPacket>? OnPlayerLeft;

        public NetworkClient()
        {
            RegisterCoreHandlers();
        }

        private void RegisterCoreHandlers()
        {
            Router.Subscribe<HandshakeResponsePacket>((response, peer) => HandleHandshakeResponse(response, peer));
            Router.Subscribe<ChatMessagePacket>((packet, peer) => OnChatMessage?.Invoke(packet));
            Router.Subscribe<PlayerJoinedPacket>((packet, peer) => OnPlayerJoined?.Invoke(packet));
            Router.Subscribe<PlayerLeftPacket>((packet, peer) => OnPlayerLeft?.Invoke(packet));
        }

        public void Connect(string host, int port, string connectionKey = "CalradiaConnected", string playerName = "Player", string passwordHash = "", string playerId = "", string serverPassword = "")
        {
            Disconnect();

            _playerName = playerName;
            _passwordHash = passwordHash ?? string.Empty;
            _serverPassword = serverPassword ?? string.Empty;
            _playerId = string.IsNullOrEmpty(playerId) ? Guid.NewGuid().ToString("N") : playerId;
            _handshakeHandled = false;

            _netManager = new NetManager(this)
            {
                AutoRecycle = true,
                DisconnectTimeout = 60000, // 60 seconds to survive heavy campaign load pauses
                PingInterval = 2000,
                UnsyncedEvents = false
            };

            _netManager.Start();
            _serverPeer = _netManager.Connect(host, port, connectionKey);
        }

        public void Disconnect()
        {
            if (_netManager != null && _netManager.IsRunning)
            {
                _serverPeer?.Disconnect();
                _netManager.Stop();
                _netManager = null;
                _serverPeer = null;
            }
        }

        public void Poll()
        {
            _netManager?.PollEvents();
        }

        public void Send<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable
        {
            if (_serverPeer != null && IsConnected)
            {
                Router.Send(_serverPeer, packet, deliveryMethod);
            }
        }

        private void HandleHandshakeResponse(HandshakeResponsePacket response, NetPeer peer)
        {
            _handshakeHandled = true;
            if (response.Success)
            {
                LocalPeerId = response.AssignedPeerId;
                OnConnected?.Invoke(response);
            }
            else
            {
                OnConnected?.Invoke(response);
                Disconnect();
            }
        }

        // INetEventListener implementations
        public void OnPeerConnected(NetPeer peer)
        {
            _serverPeer = peer;

            // Send HandshakeRequest immediately with credentials
            var handshake = new HandshakeRequestPacket
            {
                ModVersion = "1.0.0",
                PlayerId = _playerId,
                PlayerName = _playerName,
                PasswordHash = _passwordHash,
                ServerPassword = _serverPassword,
                AuthToken = string.Empty
            };

            Router.Send(peer, handshake, DeliveryMethod.ReliableOrdered);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _serverPeer = null;
            if (_handshakeHandled) return;

            string customReason = string.Empty;
            try
            {
                if (disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
                {
                    customReason = disconnectInfo.AdditionalData.GetString();
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(customReason))
            {
                OnDisconnected?.Invoke(customReason);
            }
            else
            {
                OnDisconnected?.Invoke(disconnectInfo.Reason.ToString());
            }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            Router.ReadPacket(reader, peer);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            OnDisconnected?.Invoke($"Socket error: {socketError}");
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) { }
    }
}

