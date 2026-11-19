#nullable enable
using System;
using System.IO;
using CalradiaConnected.Common.Packets;
using Newtonsoft.Json.Linq;

namespace CalradiaConnected.Common.Config
{
    /// <summary>
    /// Authoritative runtime server settings shared between Server Manager (mod-config.json),
    /// Dedicated Server, and all Connected Clients (via ServerConfigSyncPacket).
    /// </summary>
    public sealed class ServerRuntimeSettings
    {
        public static ServerRuntimeSettings Current { get; } = new ServerRuntimeSettings();

        public static event Action<ServerRuntimeSettings>? OnSettingsChanged;

        // 1. Game & World Rules (Tab: Game & Hardware)
        public int BattleSize { get; set; } = 400;
        public int DamageDifficulty { get; set; } = 1; // 0 = Easy (33%), 1 = Balanced (66%), 2 = Realistic (100%)
        public int CombatAiDifficulty { get; set; } = 1; // 0 = Easy, 1 = Normal, 2 = Veteran
        public int HeroDeathInBattle { get; set; } = 0; // 0 = Disabled, 1 = Companions Only, 2 = All Heroes
        public int AutoSaveIntervalMinutes { get; set; } = 15; // 0 = Disabled, 5..60 min
        public int NumberOfCorpses { get; set; } = 20; // 0..200
        public bool PvpEnabled { get; set; } = true;
        public bool PersistentWorldTime { get; set; } = true;
        public bool ArenaContinuousPracticeRespawn { get; set; } = true;
        public bool ArenaPvpDuelsEnabled { get; set; } = true;

        // 2. Lobby & Network Settings
        public string ServerName { get; set; } = "Calradia Connected";
        public string ServerPassword { get; set; } = string.Empty;
        public string ServerVisibility { get; set; } = "Public"; // Public, LAN, None
        public int MaxPlayers { get; set; } = 16;
        public int ServerTickRateHz { get; set; } = 60;
        public int DisconnectTimeoutSec { get; set; } = 60;
        public int NetworkPollIntervalMs { get; set; } = 20;
        public int MaxBandwidthMbps { get; set; } = 50;

        // 3. Starting & Progression Settings (Tab: Starting & Progression)
        public int StartingLevel { get; set; } = 1;
        public int StartingFocusPoints { get; set; } = 5;
        public int StartingAttributePoints { get; set; } = 2;
        public int StartingAllSkillsLevel { get; set; } = 0;
        public float CharacterXpMultiplier { get; set; } = 1.0f;
        public float SkillXpMultiplier { get; set; } = 1.0f;
        public int StartingGold { get; set; } = 1000;
        public int StartingRenown { get; set; } = 0;
        public int StartingInfluence { get; set; } = 0;
        public int StartingEquipmentPreset { get; set; } = 0;
        public int StartingTroopsPreset { get; set; } = 0;
        public bool StartingFoodSupplies { get; set; } = true;

        // 4. Client session privilege & sync state
        public bool IsLocalPlayerAdmin { get; set; } = false;
        public bool HasReceivedServerSync { get; set; } = false;
        public string LastLoadedConfigPath { get; private set; } = string.Empty;

        public static string[] GetCandidateConfigDirs()
        {
            var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[]
            {
                Environment.GetEnvironmentVariable("CALRADIA_CONNECTED_DATA_DIR") ?? string.Empty,
                Environment.GetEnvironmentVariable("COOP_DATA_DIR") ?? string.Empty,
                Path.Combine(myDocs, "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                Path.Combine(myDocs, "Mount and Blade II Bannerlord", "CoopData"),
                Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "CoopData"),
                Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "CoopData"),
                Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "CoopData")
            };
        }

        public bool LoadFromDisk()
        {
            try
            {
                foreach (var dir in GetCandidateConfigDirs())
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string configPath = Path.Combine(dir, "mod-config.json");
                    if (!File.Exists(configPath)) continue;

                    string json = File.ReadAllText(configPath);
                    var root = JObject.Parse(json);

                    if (root["gameRules"] is JObject rules)
                    {
                        BattleSize = ReadInt(rules["battleSize"], BattleSize);
                        DamageDifficulty = Clamp(ReadInt(rules["damageDifficulty"], DamageDifficulty), 0, 2);
                        CombatAiDifficulty = Clamp(ReadInt(rules["combatAiDifficulty"], CombatAiDifficulty), 0, 2);
                        HeroDeathInBattle = Clamp(ReadInt(rules["heroDeathInBattle"], HeroDeathInBattle), 0, 2);
                        AutoSaveIntervalMinutes = Math.Max(0, ReadInt(rules["autoSaveIntervalMinutes"], AutoSaveIntervalMinutes));
                        NumberOfCorpses = Math.Max(0, ReadInt(rules["numberOfCorpses"], NumberOfCorpses));
                        PvpEnabled = ReadBool(rules["pvpEnabled"], PvpEnabled);
                        PersistentWorldTime = ReadBool(rules["persistentWorldTime"], PersistentWorldTime);
                        ArenaContinuousPracticeRespawn = ReadBool(rules["arenaContinuousPracticeRespawn"], ArenaContinuousPracticeRespawn);
                        ArenaPvpDuelsEnabled = ReadBool(rules["arenaPvpDuelsEnabled"], ArenaPvpDuelsEnabled);
                    }
                    else if (root["modOptions"] is JObject modOpts)
                    {
                        BattleSize = ReadInt(modOpts["battleSize"], BattleSize);
                    }

                    if (root["lobbySettings"] is JObject lobby)
                    {
                        ServerName = ReadString(lobby["serverName"], ServerName);
                        ServerPassword = ReadString(lobby["serverPassword"], ServerPassword);
                        ServerVisibility = ReadString(lobby["serverVisibility"], ServerVisibility);
                        MaxPlayers = Math.Max(2, ReadInt(lobby["maxPlayers"], MaxPlayers));
                        ServerTickRateHz = Clamp(ReadInt(lobby["serverTickRateHz"], ServerTickRateHz), 20, 120);
                        DisconnectTimeoutSec = Clamp(ReadInt(lobby["disconnectTimeoutSec"], DisconnectTimeoutSec), 15, 300);
                        NetworkPollIntervalMs = Clamp(ReadInt(lobby["networkPollIntervalMs"], NetworkPollIntervalMs), 5, 100);
                        MaxBandwidthMbps = Math.Max(0, ReadInt(lobby["maxBandwidthMbps"], MaxBandwidthMbps));
                    }

                    if (root["starterProgression"] is JObject starter)
                    {
                        StartingLevel = Clamp(ReadInt(starter["startingLevel"], StartingLevel), 1, 62);
                        StartingFocusPoints = Math.Max(0, ReadInt(starter["startingFocusPoints"], StartingFocusPoints));
                        StartingAttributePoints = Math.Max(0, ReadInt(starter["startingAttributePoints"], StartingAttributePoints));
                        StartingAllSkillsLevel = Clamp(ReadInt(starter["startingAllSkillsLevel"], StartingAllSkillsLevel), 0, 300);
                        CharacterXpMultiplier = ClampFloat(ReadFloat(starter["characterXpMultiplier"], CharacterXpMultiplier), 0.1f, 50f);
                        SkillXpMultiplier = ClampFloat(ReadFloat(starter["skillXpMultiplier"], SkillXpMultiplier), 0.1f, 50f);
                        StartingGold = Math.Max(0, ReadInt(starter["startingGold"], StartingGold));
                        StartingRenown = Math.Max(0, ReadInt(starter["startingRenown"], StartingRenown));
                        StartingInfluence = Math.Max(0, ReadInt(starter["startingInfluence"], StartingInfluence));
                        StartingEquipmentPreset = Clamp(ReadInt(starter["startingEquipmentPreset"], StartingEquipmentPreset), 0, 4);
                        StartingTroopsPreset = ParseTroopsPreset(starter["startingTroopsPreset"], StartingTroopsPreset);
                        StartingFoodSupplies = ReadBool(starter["startingFoodSupplies"], StartingFoodSupplies);
                    }

                    LastLoadedConfigPath = configPath;
                    NotifyChanged();
                    return true;
                }
            }
            catch { }

            return false;
        }

        public ServerConfigSyncPacket ToSyncPacket(bool isLocalPlayerAdmin)
        {
            return new ServerConfigSyncPacket
            {
                BattleSize = BattleSize,
                DamageDifficulty = DamageDifficulty,
                CombatAiDifficulty = CombatAiDifficulty,
                HeroDeathInBattle = HeroDeathInBattle,
                AutoSaveIntervalMinutes = AutoSaveIntervalMinutes,
                NumberOfCorpses = NumberOfCorpses,
                PvpEnabled = PvpEnabled,
                PersistentWorldTime = PersistentWorldTime,
                ArenaContinuousPracticeRespawn = ArenaContinuousPracticeRespawn,
                ArenaPvpDuelsEnabled = ArenaPvpDuelsEnabled,

                StartingLevel = StartingLevel,
                StartingFocusPoints = StartingFocusPoints,
                StartingAttributePoints = StartingAttributePoints,
                StartingAllSkillsLevel = StartingAllSkillsLevel,
                CharacterXpMultiplier = CharacterXpMultiplier,
                SkillXpMultiplier = SkillXpMultiplier,
                StartingGold = StartingGold,
                StartingRenown = StartingRenown,
                StartingInfluence = StartingInfluence,
                StartingEquipmentPreset = StartingEquipmentPreset,
                StartingTroopsPreset = StartingTroopsPreset,
                StartingFoodSupplies = StartingFoodSupplies,

                IsLocalPlayerAdmin = isLocalPlayerAdmin
            };
        }

        public void ApplyFromSyncPacket(ServerConfigSyncPacket pkt)
        {
            if (pkt == null) return;

            BattleSize = pkt.BattleSize > 0 ? pkt.BattleSize : 400;
            DamageDifficulty = Clamp(pkt.DamageDifficulty, 0, 2);
            CombatAiDifficulty = Clamp(pkt.CombatAiDifficulty, 0, 2);
            HeroDeathInBattle = Clamp(pkt.HeroDeathInBattle, 0, 2);
            AutoSaveIntervalMinutes = Math.Max(0, pkt.AutoSaveIntervalMinutes);
            NumberOfCorpses = Math.Max(0, pkt.NumberOfCorpses);
            PvpEnabled = pkt.PvpEnabled;
            PersistentWorldTime = pkt.PersistentWorldTime;
            ArenaContinuousPracticeRespawn = pkt.ArenaContinuousPracticeRespawn;
            ArenaPvpDuelsEnabled = pkt.ArenaPvpDuelsEnabled;

            StartingLevel = Clamp(pkt.StartingLevel, 1, 62);
            StartingFocusPoints = Math.Max(0, pkt.StartingFocusPoints);
            StartingAttributePoints = Math.Max(0, pkt.StartingAttributePoints);
            StartingAllSkillsLevel = Clamp(pkt.StartingAllSkillsLevel, 0, 300);
            CharacterXpMultiplier = ClampFloat(pkt.CharacterXpMultiplier, 0.1f, 50f);
            SkillXpMultiplier = ClampFloat(pkt.SkillXpMultiplier, 0.1f, 50f);
            StartingGold = Math.Max(0, pkt.StartingGold);
            StartingRenown = Math.Max(0, pkt.StartingRenown);
            StartingInfluence = Math.Max(0, pkt.StartingInfluence);
            StartingEquipmentPreset = Clamp(pkt.StartingEquipmentPreset, 0, 4);
            StartingTroopsPreset = Clamp(pkt.StartingTroopsPreset, 0, 3);
            StartingFoodSupplies = pkt.StartingFoodSupplies;

            IsLocalPlayerAdmin = pkt.IsLocalPlayerAdmin;
            HasReceivedServerSync = true;
            NotifyChanged();
        }

        public void NotifyChanged()
        {
            try
            {
                OnSettingsChanged?.Invoke(this);
            }
            catch { }
        }

        public static int MapBattleSizeToIndex(int battleSize)
        {
            if (battleSize <= 200) return 0;
            if (battleSize <= 300) return 1;
            if (battleSize <= 400) return 2;
            if (battleSize <= 500) return 3;
            if (battleSize <= 600) return 4;
            if (battleSize <= 800) return 5;
            return 6;
        }

        public static int MapCorpseCountToIndex(int corpseCount)
        {
            if (corpseCount <= 0) return 0;
            if (corpseCount <= 5) return 1;
            if (corpseCount <= 20) return 2;
            if (corpseCount <= 50) return 3;
            if (corpseCount <= 100) return 4;
            return 5;
        }

        private static int ParseTroopsPreset(JToken? token, int fallback)
        {
            if (token == null) return fallback;
            if (token.Type == JTokenType.Integer)
            {
                int val = token.Value<int>();
                if (val <= 3) return Clamp(val, 0, 3);
                if (val <= 15) return 1;
                if (val <= 35) return 2;
                return 3;
            }
            string s = token.ToString();
            if (int.TryParse(s, out int parsed)) return Clamp(parsed, 0, 3);
            if (s.Equals("Small", StringComparison.OrdinalIgnoreCase)) return 1;
            if (s.Equals("Medium", StringComparison.OrdinalIgnoreCase)) return 2;
            if (s.Equals("Large", StringComparison.OrdinalIgnoreCase)) return 3;
            return fallback;
        }

        private static int ReadInt(JToken? token, int fallback)
        {
            if (token == null) return fallback;
            try
            {
                if (token.Type == JTokenType.Boolean) return token.Value<bool>() ? 1 : 0;
                return token.Value<int>();
            }
            catch { return fallback; }
        }

        private static float ReadFloat(JToken? token, float fallback)
        {
            if (token == null) return fallback;
            try { return token.Value<float>(); }
            catch { return fallback; }
        }

        private static bool ReadBool(JToken? token, bool fallback)
        {
            if (token == null) return fallback;
            try
            {
                if (token.Type == JTokenType.Boolean) return token.Value<bool>();
                if (token.Type == JTokenType.Integer) return token.Value<int>() != 0;
                if (bool.TryParse(token.ToString(), out bool b)) return b;
                if (int.TryParse(token.ToString(), out int i)) return i != 0;
            }
            catch { }
            return fallback;
        }

        private static string ReadString(JToken? token, string fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return token.Value<string>() ?? fallback; }
            catch { return fallback; }
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        private static float ClampFloat(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }
}
