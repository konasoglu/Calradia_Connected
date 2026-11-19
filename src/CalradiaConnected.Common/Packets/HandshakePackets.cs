using LiteNetLib.Utils;

namespace CalradiaConnected.Common.Packets
{
    public class HandshakeRequestPacket : IPacket
    {
        public PacketType Type => PacketType.HandshakeRequest;

        public string ModVersion { get; set; } = string.Empty;
        public string PlayerId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string ServerPassword { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ModVersion);
            writer.Put(PlayerId);
            writer.Put(PlayerName);
            writer.Put(PasswordHash ?? string.Empty);
            writer.Put(ServerPassword ?? string.Empty);
            writer.Put(AuthToken ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            ModVersion = reader.GetString();
            PlayerId = reader.GetString();
            PlayerName = reader.GetString();
            PasswordHash = reader.GetString();
            ServerPassword = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            AuthToken = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
        }
    }

    public class HandshakeResponsePacket : IPacket
    {
        public PacketType Type => PacketType.HandshakeResponse;

        public bool Success { get; set; }
        public int AssignedPeerId { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public int TickRate { get; set; }
        public int FastDlPort { get; set; }
        public bool IsNewAccount { get; set; }
        public string Reason { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Success);
            writer.Put(AssignedPeerId);
            writer.Put(ServerName);
            writer.Put(TickRate);
            writer.Put(FastDlPort);
            writer.Put(IsNewAccount);
            writer.Put(Reason);
        }

        public void Deserialize(NetDataReader reader)
        {
            Success = reader.GetBool();
            AssignedPeerId = reader.GetInt();
            ServerName = reader.GetString();
            TickRate = reader.GetInt();
            FastDlPort = reader.GetInt();
            IsNewAccount = reader.GetBool();
            Reason = reader.GetString();
        }
    }

    public class HeartbeatPacket : IPacket
    {
        public PacketType Type => PacketType.Heartbeat;

        public long ClientTimestamp { get; set; }
        public long ServerTimestamp { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ClientTimestamp);
            writer.Put(ServerTimestamp);
        }

        public void Deserialize(NetDataReader reader)
        {
            ClientTimestamp = reader.GetLong();
            ServerTimestamp = reader.GetLong();
        }
    }

    public class PlayerJoinedPacket : IPacket
    {
        public PacketType Type => PacketType.PlayerJoined;

        public int PeerId { get; set; }
        public string PlayerName { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(PlayerName);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            PlayerName = reader.GetString();
        }
    }

    public class PlayerLeftPacket : IPacket
    {
        public PacketType Type => PacketType.PlayerLeft;

        public int PeerId { get; set; }
        public string Reason { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(Reason);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            Reason = reader.GetString();
        }
    }

    public class ChatMessagePacket : IPacket
    {
        public PacketType Type => PacketType.ChatMessage;

        public int SenderPeerId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool IsSystem { get; set; }
        /// <summary>0 = Global/Campaign, 1 = Active Battle/Mission, 2 = Whisper</summary>
        public byte Channel { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SenderName ?? string.Empty);
            writer.Put(Message ?? string.Empty);
            writer.Put(IsSystem);
            writer.Put(SenderPeerId);
            writer.Put(Channel);
        }

        public void Deserialize(NetDataReader reader)
        {
            SenderName = reader.GetString();
            Message = reader.GetString();
            IsSystem = reader.GetBool();
            SenderPeerId = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            Channel = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)0;
        }
    }
}

