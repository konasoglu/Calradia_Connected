using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CoopServerManager.Services
{
    public class ServerStatusDto
    {
        public string TimeMode { get; set; } = "Bilinmiyor";
        public bool Persistent { get; set; } = true;
        public int PlayersCount { get; set; }
        public string Players { get; set; } = string.Empty;
        public double Day { get; set; }
        public int TickRate { get; set; } = 60;
        public int Timeout { get; set; } = 60;
    }

    public class PlayerInfoModel : INotifyPropertyChanged
    {
        private string controllerId = string.Empty;
        private string username = string.Empty;
        private string platformName = string.Empty;
        private string heroName = string.Empty;
        private string clanName = string.Empty;
        private int gold;
        private double renown;
        private double influence;
        private int partyCount;
        private int partyLimit;
        private int woundedCount;
        private int hp = 100;
        private int maxHp = 100;
        private bool isAdmin;
        private bool isConnected;

        public string ControllerId
        {
            get => controllerId;
            set { if (controllerId != value) { controllerId = value; OnPropertyChanged(); } }
        }

        public string Username
        {
            get => username;
            set { if (username != value) { username = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
        }

        public string PlatformName
        {
            get => platformName;
            set { if (platformName != value) { platformName = value; OnPropertyChanged(); } }
        }

        public string HeroName
        {
            get => heroName;
            set { if (heroName != value) { heroName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
        }

        public string ClanName
        {
            get => clanName;
            set { if (clanName != value) { clanName = value; OnPropertyChanged(); } }
        }

        public int Gold
        {
            get => gold;
            set { if (gold != value) { gold = value; OnPropertyChanged(); } }
        }

        public double Renown
        {
            get => renown;
            set { if (Math.Abs(renown - value) > 0.01) { renown = value; OnPropertyChanged(); } }
        }

        public double Influence
        {
            get => influence;
            set { if (Math.Abs(influence - value) > 0.01) { influence = value; OnPropertyChanged(); } }
        }

        public int PartyCount
        {
            get => partyCount;
            set { if (partyCount != value) { partyCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ArmyStatus)); } }
        }

        public int PartyLimit
        {
            get => partyLimit;
            set { if (partyLimit != value) { partyLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(ArmyStatus)); } }
        }

        public int WoundedCount
        {
            get => woundedCount;
            set { if (woundedCount != value) { woundedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ArmyStatus)); } }
        }

        public int Hp
        {
            get => hp;
            set { if (hp != value) { hp = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthStatus)); } }
        }

        public int MaxHp
        {
            get => maxHp;
            set { if (maxHp != value) { maxHp = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthStatus)); } }
        }

        public bool IsAdmin
        {
            get => isAdmin;
            set { if (isAdmin != value) { isAdmin = value; OnPropertyChanged(); } }
        }

        public bool IsConnected
        {
            get => isConnected;
            set { if (isConnected != value) { isConnected = value; OnPropertyChanged(); } }
        }

        public string DisplayName => !string.IsNullOrEmpty(HeroName) && HeroName != Username
            ? $"{HeroName} ({Username})"
            : Username;

        public string ArmyStatus => $"{PartyCount}/{PartyLimit} ({CalradiaConnected.Common.Localization.Loc.T("Yaralı", "Wounded")}: {WoundedCount})";
        public string HealthStatus => $"{Hp}/{MaxHp} HP";

        public void UpdateFrom(PlayerInfoModel other)
        {
            if (other == null) return;
            ControllerId = other.ControllerId;
            Username = other.Username;
            PlatformName = other.PlatformName;
            HeroName = other.HeroName;
            ClanName = other.ClanName;
            Gold = other.Gold;
            Renown = other.Renown;
            Influence = other.Influence;
            PartyCount = other.PartyCount;
            PartyLimit = other.PartyLimit;
            WoundedCount = other.WoundedCount;
            Hp = other.Hp;
            MaxHp = other.MaxHp;
            IsAdmin = other.IsAdmin;
            IsConnected = other.IsConnected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class ServerControlIpcService
    {
        private const int IpcTcpPort = 4205;
        public string? LastError { get; private set; }

        public async Task<string?> SendCommandAsync(string command, int timeoutMs = 3000)
        {
            return await SendCommandInternalAsync(command, timeoutMs).ConfigureAwait(false);
        }

        public async Task<bool> PingAsync(int timeoutMs = 1200)
        {
            var res = await SendCommandInternalAsync("PING", timeoutMs).ConfigureAwait(false);
            return res != null && res.Trim().Equals("PONG", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string?> SendCommandInternalAsync(string command, int timeoutMs)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient();
                var workTask = ExecuteTcpRequestAsync(tcp, command);
                var completed = await Task.WhenAny(workTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != workTask)
                {
                    LastError = $"Zaman aşımı ({timeoutMs}ms)";
                    return null;
                }

                var result = await workTask.ConfigureAwait(false);
                LastError = null;
                return result;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
            finally
            {
                try { tcp?.Dispose(); } catch { }
            }
        }

        private static async Task<string?> ExecuteTcpRequestAsync(TcpClient tcp, string command)
        {
            await tcp.ConnectAsync(IPAddress.Loopback, IpcTcpPort).ConfigureAwait(false);
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            using var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true) { AutoFlush = true };

            await writer.WriteLineAsync(command).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            return await reader.ReadLineAsync().ConfigureAwait(false);
        }

        public async Task<bool> SetTimeSpeedAsync(int mode)
        {
            var res = await SendCommandAsync($"SET_TIME {mode}", 3000);
            return res != null && res.StartsWith("OK");
        }

        public async Task<bool> SetPersistentWorldTimeAsync(bool enabled)
        {
            var res = await SendCommandAsync($"SET_PERSISTENT_TIME {(enabled ? "1" : "0")}", 3000);
            return res != null && res.StartsWith("OK");
        }

        public async Task<bool> SetNetworkSettingsAsync(int tickRateHz, int timeoutSec, int pollIntervalMs = 20)
        {
            var res = await SendCommandAsync($"SET_NETWORK {tickRateHz} {timeoutSec} {pollIntervalMs}", 3000);
            return res != null && res.StartsWith("OK");
        }

        public async Task<bool> SaveGameAsync()
        {
            var res = await SendCommandAsync("SAVE", 5000);
            return res != null && res.StartsWith("OK");
        }

        public async Task<bool> KickPlayerAsync(string username)
        {
            var res = await SendCommandAsync($"KICK {username}", 3000);
            return res != null && res.StartsWith("OK");
        }

        public async Task<ServerStatusDto?> GetServerStatusAsync()
        {
            try
            {
                var res = await SendCommandInternalAsync("GET_STATUS", 1500).ConfigureAwait(false);
                if (string.IsNullOrEmpty(res) || res.StartsWith("ERR")) return null;

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<ServerStatusDto>(res, options);
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<PlayerInfoModel>> GetPlayersAsync()
        {
            var res = await SendCommandInternalAsync("GET_PLAYERS", 2000).ConfigureAwait(false);
            if (string.IsNullOrEmpty(res) || res.StartsWith("ERR")) return new List<PlayerInfoModel>();

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<List<PlayerInfoModel>>(res, options) ?? new List<PlayerInfoModel>();
            }
            catch
            {
                return new List<PlayerInfoModel>();
            }
        }

        public async Task<string?> GiveGoldAsync(string target, int amount)
        {
            return await SendCommandAsync($"GIVE_GOLD {target} {amount}", 4000);
        }

        public async Task<string?> GiveRenownAsync(string target, float amount)
        {
            return await SendCommandAsync($"GIVE_RENOWN {target} {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 4000);
        }

        public async Task<string?> GiveInfluenceAsync(string target, float amount)
        {
            return await SendCommandAsync($"GIVE_INFLUENCE {target} {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 4000);
        }

        public async Task<string?> GiveTroopAsync(string target, string troopId, int count)
        {
            return await SendCommandAsync($"GIVE_TROOP {target} {troopId} {count}", 5000);
        }

        public async Task<string?> HealHeroAsync(string target)
        {
            return await SendCommandAsync($"HEAL_HERO {target}", 4000);
        }

        public async Task<string?> HealPartyAsync(string target)
        {
            return await SendCommandAsync($"HEAL_PARTY {target}", 4000);
        }

        public async Task<string?> DuplicateTroopsAsync(string target, int count)
        {
            return await SendCommandAsync($"DUPLICATE_TROOPS {target} {count}", 4000);
        }

        public async Task<string?> SetAdminAsync(string username, bool isAdmin)
        {
            return await SendCommandAsync($"SET_ADMIN {username} {(isAdmin ? "1" : "0")}", 4000);
        }

        public async Task<bool> ReloadConfigAsync()
        {
            var res = await SendCommandAsync("RELOAD_CONFIG", 4000);
            return res != null && res.StartsWith("OK");
        }

        public async Task<bool> ReloadUsersAsync()
        {
            var res = await SendCommandAsync("RELOAD_USERS", 4000);
            return res != null && res.StartsWith("OK");
        }

        public async Task<bool> ResetProgressionAsync()
        {
            var res = await SendCommandAsync("RESET_PROGRESSION", 4000);
            return res != null && res.StartsWith("OK");
        }
    }
}
