using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Network.Auth;
using CalradiaConnected.Network.Session;
using LiteNetLib;

namespace CalradiaConnected.Network.Transport
{
    public class NetworkServer : INetworkServer, INetEventListener
    {
        private NetManager? _netManager;
        private readonly ConcurrentDictionary<int, PlayerSession> _sessions = new ConcurrentDictionary<int, PlayerSession>();
        private readonly ConcurrentDictionary<int, int> _peerToSessionId = new ConcurrentDictionary<int, int>();
        private string _connectionKey = "CalradiaConnected";

        public bool IsRunning => _netManager != null && _netManager.IsRunning;
        public int Port { get; private set; }
        public string ServerName { get; set; } = "Calradia Connected Sunucusu";
        public string RequiredServerPassword { get; set; } = string.Empty;
        public string ServerVisibility { get; set; } = "Public";
        public int MaxPlayers { get; set; } = 16;
        public int TickRateHz { get; private set; } = 60;
        public int DisconnectTimeoutSec { get; private set; } = 60;
        public IReadOnlyDictionary<int, PlayerSession> Sessions => _sessions;
        public PacketRouter Router { get; } = new PacketRouter();

        public void ApplyNetworkSettings(int tickRateHz, int timeoutSec, int pollIntervalMs = 20)
        {
            TickRateHz = Math.Max(20, Math.Min(120, tickRateHz));
            DisconnectTimeoutSec = Math.Max(15, Math.Min(300, timeoutSec));
            if (_netManager != null)
            {
                _netManager.DisconnectTimeout = DisconnectTimeoutSec * 1000;
            }
        }

        public bool KickSession(string target, string reason)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            string clean = target.Trim();
            if (clean.StartsWith("user_", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(5);
            }

            foreach (var session in _sessions.Values)
            {
                if (string.Equals(session.PlayerName, clean, StringComparison.OrdinalIgnoreCase) ||
                    session.PeerId.ToString() == clean)
                {
                    if (session.Peer != null)
                    {
                        try
                        {
                            var writer = new LiteNetLib.Utils.NetDataWriter();
                            writer.Put(string.IsNullOrEmpty(reason) ? "Sunucudan çıkarıldınız." : reason);
                            session.Peer.Disconnect(writer);
                        }
                        catch
                        {
                            try { session.Peer.Disconnect(); } catch { }
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        public event Action<PlayerSession>? OnPlayerJoined;
        public event Action<PlayerSession, string>? OnPlayerLeft;
        public event Action<PlayerSession, ChatMessagePacket>? OnChatMessage;

        public NetworkServer()
        {
            RegisterCoreHandlers();
        }

        private void RegisterCoreHandlers()
        {
            Router.Subscribe<HandshakeRequestPacket>(HandleHandshake);
            Router.Subscribe<HeartbeatPacket>(HandleHeartbeat);
            Router.Subscribe<ChatMessagePacket>(HandleChatMessage);
        }

        public bool Start(int port, string connectionKey = "CalradiaConnected")
        {
            if (IsRunning)
                return true;

            Port = port;
            _connectionKey = connectionKey;

            _netManager = new NetManager(this)
            {
                AutoRecycle = true,
                DisconnectTimeout = 60000,
                PingInterval = 2000,
                UnsyncedEvents = false,
                BroadcastReceiveEnabled = true,
                UnconnectedMessagesEnabled = true
            };

            bool started = _netManager.Start(port);
            return started;
        }

        public void Stop()
        {
            if (_netManager != null && _netManager.IsRunning)
            {
                _netManager.Stop();
                _netManager = null;
            }

            _sessions.Clear();
            _peerToSessionId.Clear();
        }

        public void Poll()
        {
            _netManager?.PollEvents();
        }

        public void Broadcast<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable
        {
            if (_netManager == null) return;
            Router.SendAll(_netManager, packet, deliveryMethod);
        }

        public void BroadcastExcept<T>(T packet, NetPeer? excludePeer, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable
        {
            if (_netManager == null) return;
            foreach (var session in _sessions.Values)
            {
                if (session.Peer != null && session.Peer != excludePeer)
                {
                    Router.Send(session.Peer, packet, deliveryMethod);
                }
            }
        }

        public void SendTo<T>(int peerId, T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, LiteNetLib.Utils.INetSerializable
        {
            if (_sessions.TryGetValue(peerId, out var session) && session.Peer != null)
            {
                Router.Send(session.Peer, packet, deliveryMethod);
            }
        }

        public int ResolveSessionPeerId(NetPeer? peer, int fallbackPeerId = 0)
        {
            if (peer != null && _peerToSessionId.TryGetValue(peer.Id, out int assignedId))
            {
                return assignedId;
            }
            return fallbackPeerId;
        }

        public bool TryGetSession(NetPeer? peer, out PlayerSession? session)
        {
            if (peer != null && _peerToSessionId.TryGetValue(peer.Id, out int assignedId) && _sessions.TryGetValue(assignedId, out var found))
            {
                session = found;
                return true;
            }
            session = null;
            return false;
        }

        private void SendRejectResponse(NetPeer peer, string reason)
        {
            var rejectResponse = new HandshakeResponsePacket
            {
                Success = false,
                AssignedPeerId = 0,
                ServerName = ServerName,
                TickRate = 60,
                FastDlPort = Port + 1,
                IsNewAccount = false,
                Reason = reason
            };

            Router.Send(peer, rejectResponse, DeliveryMethod.ReliableOrdered);
            try { _netManager?.TriggerUpdate(); } catch { }

            // Delay disconnect slightly so ReliableOrdered HandshakeResponsePacket is delivered before socket teardown
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(600).ConfigureAwait(false);
                    if (peer != null && peer.ConnectionState == ConnectionState.Connected)
                    {
                        var writer = new LiteNetLib.Utils.NetDataWriter();
                        writer.Put(reason);
                        peer.Disconnect(writer);
                    }
                }
                catch { }
            });
        }

        private void HandleHandshake(HandshakeRequestPacket req, NetPeer peer)
        {
            string username = string.IsNullOrWhiteSpace(req.PlayerName) ? $"Player_{peer.Id}" : req.PlayerName.Trim();
            string passwordHash = req.PasswordHash ?? string.Empty;

            // 0. Validate Server (Lobby) Password if required
            if (!string.IsNullOrWhiteSpace(RequiredServerPassword))
            {
                string providedServerPass = (req.ServerPassword ?? string.Empty).Trim();
                if (!string.Equals(providedServerPass, RequiredServerPassword.Trim(), StringComparison.Ordinal))
                {
                    SendRejectResponse(peer, string.IsNullOrEmpty(providedServerPass)
                        ? "Bu sunucu şifrelidir! Lütfen 'Sunucu Şifresi' kutucuğuna sunucu şifresini girin."
                        : "Sunucu (Lobi) şifresi YANLIŞ! Lütfen sunucu şifresini kontrol edin.");
                    return;
                }
            }

            // 1. Authenticate or Auto-Register
            bool authSuccess = AccountManager.Instance.AuthenticateOrRegister(
                username, 
                passwordHash, 
                out int accountId, 
                out bool isNewAccount, 
                out string authReason);

            if (!authSuccess)
            {
                SendRejectResponse(peer, authReason);
                return;
            }

            if (MaxPlayers > 0 && _sessions.Count >= MaxPlayers && !_sessions.ContainsKey(accountId))
            {
                SendRejectResponse(peer, $"Sunucu dolu ({_sessions.Count}/{MaxPlayers} Oyuncu)!");
                return;
            }

            // 2. Ghost Session Eviction: If another peer is already logged in with this account, kick old peer
            foreach (var existingSession in _sessions.Values)
            {
                if (existingSession.PeerId == accountId && existingSession.Peer != null && existingSession.Peer.Id != peer.Id)
                {
                    existingSession.Peer.Disconnect();
                    _sessions.TryRemove(accountId, out _);
                    _peerToSessionId.TryRemove(existingSession.Peer.Id, out _);
                    break;
                }
            }

            int assignedId = accountId;
            _peerToSessionId[peer.Id] = assignedId;

            var session = new PlayerSession(assignedId, peer)
            {
                PlayerId = req.PlayerId,
                PlayerName = username,
                IsAuthenticated = true
            };

            _sessions[assignedId] = session;

            // 3. Send acceptance response
            var response = new HandshakeResponsePacket
            {
                Success = true,
                AssignedPeerId = assignedId,
                ServerName = ServerName,
                TickRate = TickRateHz,
                FastDlPort = Port + 1,
                IsNewAccount = isNewAccount,
                Reason = authReason
            };

            Router.Send(peer, response, DeliveryMethod.ReliableOrdered);

            // 3b. Send authoritative Server Runtime Config (game rules, progression, XP multipliers & admin flag)
            try
            {
                bool isAdmin = global::Common.Users.UserDatabase.Instance.IsAdmin(username);
                var cfgPacket = CalradiaConnected.Common.Config.ServerRuntimeSettings.Current.ToSyncPacket(isAdmin);
                Router.Send(peer, cfgPacket, DeliveryMethod.ReliableOrdered);
            }
            catch { }

            // Notify all other clients of new player
            var joinedPacket = new PlayerJoinedPacket
            {
                PeerId = assignedId,
                PlayerName = session.PlayerName
            };
            Broadcast(joinedPacket, DeliveryMethod.ReliableOrdered);

            // Send existing players to the new client
            foreach (var existing in _sessions.Values)
            {
                if (existing.PeerId != assignedId)
                {
                    Router.Send(peer, new PlayerJoinedPacket
                    {
                        PeerId = existing.PeerId,
                        PlayerName = existing.PlayerName
                    }, DeliveryMethod.ReliableOrdered);
                }
            }

            OnPlayerJoined?.Invoke(session);
        }

        private void HandleHeartbeat(HeartbeatPacket packet, NetPeer peer)
        {
            if (_peerToSessionId.TryGetValue(peer.Id, out int sid) && _sessions.TryGetValue(sid, out var session))
            {
                session.LastSeenAt = DateTime.UtcNow;
                packet.ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Router.Send(peer, packet, DeliveryMethod.Unreliable);
            }
        }

        private void HandleChatMessage(ChatMessagePacket packet, NetPeer peer)
        {
            if (_peerToSessionId.TryGetValue(peer.Id, out int sid) && _sessions.TryGetValue(sid, out var session))
            {
                packet.SenderName = session.PlayerName;
                if (!string.IsNullOrWhiteSpace(packet.Message) && packet.Message.TrimStart().StartsWith("/"))
                {
                    OnChatMessage?.Invoke(session, packet);
                    return;
                }
                Broadcast(packet, DeliveryMethod.ReliableOrdered);
                OnChatMessage?.Invoke(session, packet);
            }
        }

        // INetEventListener implementations
        public void OnConnectionRequest(ConnectionRequest request)
        {
            request.AcceptIfKey(_connectionKey);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            // Peer connected; await HandshakeRequest
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (_peerToSessionId.TryRemove(peer.Id, out int sid) && _sessions.TryRemove(sid, out var session))
            {
                var leftPacket = new PlayerLeftPacket
                {
                    PeerId = sid,
                    Reason = disconnectInfo.Reason.ToString()
                };
                Broadcast(leftPacket, DeliveryMethod.ReliableOrdered);
                OnPlayerLeft?.Invoke(session, disconnectInfo.Reason.ToString());
            }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            Router.ReadPacket(reader, peer);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
        public void OnNetworkReceiveUnconnected(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            try
            {
                if (_netManager == null || reader.AvailableBytes <= 0) return;
                string magic = reader.GetString();
                if (magic == "CC_LOBBY_QUERY")
                {
                    if (string.Equals(ServerVisibility, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    if (string.Equals(ServerVisibility, "LAN", StringComparison.OrdinalIgnoreCase) && !IsPrivateOrLanAddress(endPoint.Address))
                    {
                        return;
                    }

                    var writer = new LiteNetLib.Utils.NetDataWriter();
                    writer.Put("CC_LOBBY_INFO");
                    writer.Put(ServerName ?? "Calradia Connected Sunucusu");
                    writer.Put(Port);
                    writer.Put(_sessions.Count);
                    writer.Put(MaxPlayers > 0 ? MaxPlayers : 16);
                    writer.Put(!string.IsNullOrWhiteSpace(RequiredServerPassword));
                    _netManager.SendUnconnectedMessage(writer, endPoint);
                }
            }
            catch { }
        }

        private static bool IsPrivateOrLanAddress(IPAddress? addr)
        {
            if (addr == null) return false;
            if (IPAddress.IsLoopback(addr)) return true;
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) return false;
            return b[0] == 10
                || b[0] == 127
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || b[0] == 26; // Radmin VPN virtual LAN
        }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }
}

