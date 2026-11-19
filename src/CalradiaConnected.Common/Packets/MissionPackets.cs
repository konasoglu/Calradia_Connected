using System.Collections.Generic;
using LiteNetLib.Utils;

namespace CalradiaConnected.Common.Packets
{
    public class MissionAgentSpawnPacket : IPacket
    {
        public PacketType Type => PacketType.MissionAgentSpawn;

        public int PeerId { get; set; }
        public string CharacterId { get; set; } = string.Empty;
        public float SpawnPosX { get; set; }
        public float SpawnPosY { get; set; }
        public float SpawnPosZ { get; set; }
        public float Rotation { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(CharacterId);
            writer.Put(SpawnPosX);
            writer.Put(SpawnPosY);
            writer.Put(SpawnPosZ);
            writer.Put(Rotation);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            CharacterId = reader.GetString();
            SpawnPosX = reader.GetFloat();
            SpawnPosY = reader.GetFloat();
            SpawnPosZ = reader.GetFloat();
            Rotation = reader.GetFloat();
        }
    }

    public class MissionAgentStatePacket : IPacket
    {
        public PacketType Type => PacketType.MissionAgentState;

        public int PeerId { get; set; }
        public string HeroId { get; set; } = string.Empty;
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float LookDirX { get; set; }
        public float LookDirY { get; set; }
        public float LookDirZ { get; set; }
        public float MoveX { get; set; }
        public float MoveY { get; set; }
        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public int ActionIndex { get; set; }
        public int MovementFlags { get; set; }
        public byte CombatDirection { get; set; } // Attack/Defend direction or action stage
        public string ActionName { get; set; } = string.Empty;
        public float VelX { get; set; }
        public float VelY { get; set; }
        public float VelZ { get; set; }
        public float MountLookX { get; set; }
        public float MountLookY { get; set; }
        public string LowerActionName { get; set; } = string.Empty;
        public float LowerActionProgress { get; set; }
        public float UpperActionProgress { get; set; }
        public string MountActionName { get; set; } = string.Empty;
        public float MountActionProgress { get; set; }
        public sbyte MainHandSlot { get; set; } = -1;
        public sbyte OffHandSlot { get; set; } = -1;
        public byte StateFlags { get; set; } // bit0=HasMount, bit1=CrouchMode, bit2=WalkMode, bit3=ExtendedAnimPayload
        public float MountHealth { get; set; } = -1f;
        public float MountMaxHealth { get; set; } = 100f;
        public short ShieldHitPoints { get; set; } = -1;
        public short ShieldMaxHitPoints { get; set; } = 0;
        public byte AgentLifeState { get; set; } // 0=Active, 1=Unconscious, 2=Killed

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(PosX);
            writer.Put(PosY);
            writer.Put(PosZ);
            writer.Put(LookDirX);
            writer.Put(LookDirY);
            writer.Put(LookDirZ);
            writer.Put(Health);
            writer.Put(ActionIndex);
            writer.Put(HeroId ?? string.Empty);
            writer.Put(MoveX);
            writer.Put(MoveY);
            writer.Put(MaxHealth);
            writer.Put(MovementFlags);
            writer.Put(CombatDirection);
            writer.Put(ActionName ?? string.Empty);
            writer.Put(VelX);
            writer.Put(VelY);
            writer.Put(VelZ);
            writer.Put(MountLookX);
            writer.Put(MountLookY);
            writer.Put(LowerActionName ?? string.Empty);
            writer.Put(LowerActionProgress);
            writer.Put(UpperActionProgress);
            writer.Put(MountActionName ?? string.Empty);
            writer.Put(MountActionProgress);
            writer.Put(MainHandSlot);
            writer.Put(OffHandSlot);
            writer.Put(StateFlags);
            writer.Put(MountHealth);
            writer.Put(MountMaxHealth);
            writer.Put(ShieldHitPoints);
            writer.Put(ShieldMaxHitPoints);
            writer.Put(AgentLifeState);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            PosX = reader.GetFloat();
            PosY = reader.GetFloat();
            PosZ = reader.GetFloat();
            LookDirX = reader.GetFloat();
            LookDirY = reader.GetFloat();
            LookDirZ = reader.GetFloat();
            Health = reader.GetFloat();
            ActionIndex = reader.GetInt();
            HeroId = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            MoveX = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            MoveY = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            MaxHealth = reader.AvailableBytes >= 4 ? reader.GetFloat() : 100f;
            MovementFlags = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            CombatDirection = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)0;
            ActionName = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            VelX = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            VelY = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            VelZ = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            MountLookX = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            MountLookY = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            LowerActionName = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            LowerActionProgress = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            UpperActionProgress = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            MountActionName = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            MountActionProgress = reader.AvailableBytes >= 4 ? reader.GetFloat() : 0f;
            MainHandSlot = reader.AvailableBytes >= 1 ? reader.GetSByte() : (sbyte)-1;
            OffHandSlot = reader.AvailableBytes >= 1 ? reader.GetSByte() : (sbyte)-1;
            StateFlags = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)0;
            MountHealth = reader.AvailableBytes >= 4 ? reader.GetFloat() : -1f;
            MountMaxHealth = reader.AvailableBytes >= 4 ? reader.GetFloat() : 100f;
            ShieldHitPoints = reader.AvailableBytes >= 2 ? reader.GetShort() : (short)-1;
            ShieldMaxHitPoints = reader.AvailableBytes >= 2 ? reader.GetShort() : (short)0;
            AgentLifeState = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)0;
        }
    }

    public class MissionBlowSyncPacket : IPacket
    {
        public PacketType Type => PacketType.MissionBlowSync;

        public int AttackerPeerId { get; set; }
        public int VictimPeerId { get; set; }
        public float Damage { get; set; }
        public float RemainingHealth { get; set; }
        public bool IsFatal { get; set; }
        public bool IsBlocked { get; set; }
        public byte HitTargetKind { get; set; } // 0=PlayerHero, 1=PlayerMount, 2=PlayerShield, 3=TroopUnit
        public ushort TargetTroopUnitId { get; set; }
        public short ShieldDamage { get; set; }
        public bool IsAuthoritativeKill { get; set; } // True ONLY when sent by the victim's own client confirming KO/death
        public string AttackerActionName { get; set; } = string.Empty;
        public byte VictimBodyPart { get; set; }
        public byte StrikeType { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(AttackerPeerId);
            writer.Put(VictimPeerId);
            writer.Put(Damage);
            writer.Put(RemainingHealth);
            writer.Put(IsFatal);
            writer.Put(IsBlocked);
            writer.Put(HitTargetKind);
            writer.Put(TargetTroopUnitId);
            writer.Put(ShieldDamage);
            writer.Put(IsAuthoritativeKill);
            writer.Put(AttackerActionName ?? string.Empty);
            writer.Put(VictimBodyPart);
            writer.Put(StrikeType);
        }

        public void Deserialize(NetDataReader reader)
        {
            AttackerPeerId = reader.GetInt();
            VictimPeerId = reader.GetInt();
            Damage = reader.GetFloat();
            RemainingHealth = reader.GetFloat();
            IsFatal = reader.GetBool();
            IsBlocked = reader.GetBool();
            HitTargetKind = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)0;
            TargetTroopUnitId = reader.AvailableBytes >= 2 ? reader.GetUShort() : (ushort)0;
            ShieldDamage = reader.AvailableBytes >= 2 ? reader.GetShort() : (short)0;
            IsAuthoritativeKill = reader.AvailableBytes >= 1 && reader.GetBool();
            AttackerActionName = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
            VictimBodyPart = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)2; // Chest
            StrikeType = reader.AvailableBytes >= 1 ? reader.GetByte() : (byte)0;
        }
    }

    public class TroopUnitSnapshot : INetSerializable
    {
        public ushort UnitId { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public byte YawByte { get; set; }
        public short Health { get; set; }
        public short MaxHealth { get; set; }
        public byte Flags { get; set; } // bit0=Alive, bit1=HasMount, bit2=Moving, bit3=Attacking, bit4=Blocking, bit5=Unconscious

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(UnitId);
            writer.Put(PosX);
            writer.Put(PosY);
            writer.Put(PosZ);
            writer.Put(YawByte);
            writer.Put(Health);
            writer.Put(MaxHealth);
            writer.Put(Flags);
        }

        public void Deserialize(NetDataReader reader)
        {
            UnitId = reader.GetUShort();
            PosX = reader.GetFloat();
            PosY = reader.GetFloat();
            PosZ = reader.GetFloat();
            YawByte = reader.GetByte();
            Health = reader.GetShort();
            MaxHealth = reader.GetShort();
            Flags = reader.GetByte();
        }
    }

    public class MissionTroopsSyncPacket : IPacket
    {
        public PacketType Type => PacketType.MissionTroopsSync;

        public int SenderPeerId { get; set; }
        public byte TeamSide { get; set; } // 0=Defender, 1=Attacker
        public List<TroopUnitSnapshot> Units { get; set; } = new List<TroopUnitSnapshot>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SenderPeerId);
            writer.Put(TeamSide);
            ushort count = (ushort)(Units?.Count ?? 0);
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                Units![i].Serialize(writer);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            SenderPeerId = reader.GetInt();
            TeamSide = reader.GetByte();
            ushort count = reader.AvailableBytes >= 2 ? reader.GetUShort() : (ushort)0;
            Units = new List<TroopUnitSnapshot>(count);
            for (int i = 0; i < count; i++)
            {
                if (reader.AvailableBytes < 20) break;
                var u = new TroopUnitSnapshot();
                u.Deserialize(reader);
                Units.Add(u);
            }
        }
    }

    public class MissionDuelRequestPacket : IPacket
    {
        public PacketType Type => PacketType.MissionDuelRequest;

        public int ChallengerPeerId { get; set; }
        public int TargetPeerId { get; set; }
        public string ChallengerName { get; set; } = string.Empty;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChallengerPeerId);
            writer.Put(TargetPeerId);
            writer.Put(ChallengerName);
        }

        public void Deserialize(NetDataReader reader)
        {
            ChallengerPeerId = reader.GetInt();
            TargetPeerId = reader.GetInt();
            ChallengerName = reader.GetString();
        }
    }

    public class MissionDuelResponsePacket : IPacket
    {
        public PacketType Type => PacketType.MissionDuelResponse;

        public int ChallengerPeerId { get; set; }
        public int TargetPeerId { get; set; }
        public bool Accepted { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChallengerPeerId);
            writer.Put(TargetPeerId);
            writer.Put(Accepted);
        }

        public void Deserialize(NetDataReader reader)
        {
            ChallengerPeerId = reader.GetInt();
            TargetPeerId = reader.GetInt();
            Accepted = reader.GetBool();
        }
    }

    public class MissionDuelRoundResetPacket : IPacket
    {
        public PacketType Type => PacketType.MissionDuelRoundReset;

        public int RoundNumber { get; set; }
        public int WinnerPeerId { get; set; }
        public bool MatchFinished { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(RoundNumber);
            writer.Put(WinnerPeerId);
            writer.Put(MatchFinished);
        }

        public void Deserialize(NetDataReader reader)
        {
            RoundNumber = reader.GetInt();
            WinnerPeerId = reader.GetInt();
            MatchFinished = reader.GetBool();
        }
    }

    public class MissionNavalShipStatePacket : IPacket
    {
        public PacketType Type => PacketType.MissionNavalShipState;

        public int PeerId { get; set; }
        public int ShipIndex { get; set; }
        public string HullId { get; set; } = string.Empty;
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float DirX { get; set; }
        public float DirY { get; set; }
        public float VelX { get; set; }
        public float VelY { get; set; }
        public float HitPoints { get; set; }
        public float SailHitPoints { get; set; }
        public float SailSetting { get; set; }
        public float RudderRotation { get; set; }
        public bool IsAnchored { get; set; }
        public bool IsSinking { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(ShipIndex);
            writer.Put(HullId ?? string.Empty);
            writer.Put(PosX);
            writer.Put(PosY);
            writer.Put(PosZ);
            writer.Put(DirX);
            writer.Put(DirY);
            writer.Put(VelX);
            writer.Put(VelY);
            writer.Put(HitPoints);
            writer.Put(SailHitPoints);
            writer.Put(SailSetting);
            writer.Put(RudderRotation);
            writer.Put(IsAnchored);
            writer.Put(IsSinking);
        }

        public void Deserialize(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            ShipIndex = reader.GetInt();
            HullId = reader.GetString();
            PosX = reader.GetFloat();
            PosY = reader.GetFloat();
            PosZ = reader.GetFloat();
            DirX = reader.GetFloat();
            DirY = reader.GetFloat();
            VelX = reader.GetFloat();
            VelY = reader.GetFloat();
            HitPoints = reader.GetFloat();
            SailHitPoints = reader.GetFloat();
            SailSetting = reader.GetFloat();
            RudderRotation = reader.GetFloat();
            IsAnchored = reader.AvailableBytes >= 1 && reader.GetBool();
            IsSinking = reader.AvailableBytes >= 1 && reader.GetBool();
        }
    }
}

