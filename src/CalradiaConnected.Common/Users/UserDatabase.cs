#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Common.Users
{
    public class UserDatabaseData
    {
        [JsonProperty("allowAutoRegistration")]
        public bool AllowAutoRegistration { get; set; } = true;

        [JsonProperty("users")]
        public List<UserAccount> Users { get; set; } = new List<UserAccount>();
    }

    public class UserDatabase
    {
        private static readonly Lazy<UserDatabase> _instance = new Lazy<UserDatabase>(() => new UserDatabase());
        public static UserDatabase Instance => _instance.Value;

        private readonly object _lock = new object();
        private readonly Dictionary<string, UserAccount> _users = new Dictionary<string, UserAccount>(StringComparer.OrdinalIgnoreCase);
        private bool _allowAutoRegistration = true;
        private string? _dbFilePath;
        private DateTime _lastLoadedWriteUtc = DateTime.MinValue;

        public bool AllowAutoRegistration
        {
            get
            {
                lock (_lock)
                {
                    RefreshIfModifiedUnlocked();
                    return _allowAutoRegistration;
                }
            }
            set
            {
                lock (_lock)
                {
                    RefreshIfModifiedUnlocked();
                    _allowAutoRegistration = value;
                    SaveUnlocked();
                }
            }
        }

        public string DbFilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_dbFilePath))
                {
                    _dbFilePath = ResolveDbPath();
                }
                return _dbFilePath!;
            }
            set => _dbFilePath = value;
        }

        public UserDatabase()
        {
            Load();
        }

        public static string ResolveDbPath()
        {
            var envPath = Environment.GetEnvironmentVariable("COOP_USER_DB");
            if (!string.IsNullOrEmpty(envPath)) return envPath;

            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(docs))
                {
                    var dir = Path.Combine(docs, "Mount and Blade II Bannerlord", "CoopServer");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    return Path.Combine(dir, "users.json");
                }
            }
            catch
            {
                // Fallback to local directory
            }

            var localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData");
            if (!Directory.Exists(localDir))
            {
                try { Directory.CreateDirectory(localDir); } catch { }
            }
            return Path.Combine(localDir, "users.json");
        }

        public void Load()
        {
            lock (_lock)
            {
                LoadUnlocked();
            }
        }

        private void RefreshIfModifiedUnlocked()
        {
            try
            {
                var path = DbFilePath;
                if (!File.Exists(path)) return;
                var wt = File.GetLastWriteTimeUtc(path);
                if (wt != _lastLoadedWriteUtc)
                {
                    LoadUnlocked();
                }
            }
            catch { }
        }

        private void LoadUnlocked()
        {
            var path = DbFilePath;

            if (!File.Exists(path))
            {
                _users.Clear();
                // Create default admin user if database is new
                var defaultSalt = GenerateSalt();
                var adminAccount = new UserAccount
                {
                    Username = "admin",
                    Salt = defaultSalt,
                    PasswordHash = HashPassword("admin", defaultSalt),
                    AccountId = 1001,
                    IsAdmin = true,
                    CreatedAt = DateTime.UtcNow
                };
                _users[adminAccount.Username] = adminAccount;
                SaveUnlocked();
                return;
            }

            try
            {
                string json;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    json = sr.ReadToEnd();
                }

                var data = JsonConvert.DeserializeObject<UserDatabaseData>(json);
                if (data != null)
                {
                    _users.Clear();
                    _allowAutoRegistration = data.AllowAutoRegistration;
                    if (data.Users != null)
                    {
                        int nextId = 1001;
                        foreach (var u in data.Users)
                        {
                            if (!string.IsNullOrWhiteSpace(u.Username))
                            {
                                if (u.AccountId <= 0)
                                {
                                    u.AccountId = nextId;
                                }
                                if (u.AccountId >= nextId)
                                {
                                    nextId = u.AccountId + 1;
                                }
                                _users[u.Username] = u;
                            }
                        }
                    }
                    _lastLoadedWriteUtc = File.GetLastWriteTimeUtc(path);
                }
            }
            catch (Exception)
            {
                // Keep memory state if file read collides momentarily
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                SaveUnlocked();
            }
        }

        private void SaveUnlocked()
        {
            try
            {
                var path = DbFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var data = new UserDatabaseData
                {
                    AllowAutoRegistration = _allowAutoRegistration,
                    Users = _users.Values.ToList()
                };

                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                var tmpPath = path + $".{System.Diagnostics.Process.GetCurrentProcess().Id}.tmp";
                File.WriteAllText(tmpPath, json, Encoding.UTF8);
                try
                {
                    File.Copy(tmpPath, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
                _lastLoadedWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                // Ignore write collisions
            }
        }

        private int GetNextAccountId()
        {
            int max = 1000;
            foreach (var u in _users.Values)
            {
                if (u.AccountId > max) max = u.AccountId;
            }
            return max + 1;
        }

        public bool ValidateLogin(string username, string password, out string errorReason)
        {
            errorReason = string.Empty;
            if (string.IsNullOrWhiteSpace(username))
            {
                errorReason = "Kullanıcı adı boş olamaz.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                errorReason = "Hesap şifresi boş bırakılamaz! Lütfen şifrenizi girin.";
                return false;
            }

            username = username.Trim();
            password = password.Trim();

            // Reload from disk so changes made in ServerManager (new users, password resets, bans) take effect immediately
            Load();

            lock (_lock)
            {
                if (!_users.TryGetValue(username, out var account))
                {
                    if (_allowAutoRegistration)
                    {
                        // Auto-register new user
                        var salt = GenerateSalt();
                        account = new UserAccount
                        {
                            Username = username,
                            Salt = salt,
                            PasswordHash = HashPassword(password, salt),
                            AccountId = GetNextAccountId(),
                            IsAdmin = false,
                            CreatedAt = DateTime.UtcNow,
                            LastLogin = DateTime.UtcNow
                        };
                        _users[username] = account;
                        Save();
                        return true;
                    }

                    errorReason = "Kullanıcı bulunamadı! Bu sunucuda otomatik kayıt kapalıdır.";
                    return false;
                }

                if (account.IsBanned)
                {
                    errorReason = string.IsNullOrEmpty(account.BanReason)
                        ? "Hesabınız bu sunucudan banlanmıştır."
                        : $"Hesabınız banlanmıştır: {account.BanReason}";
                    return false;
                }

                var calculatedHash = HashPassword(password, account.Salt);
                var sha256OfRaw = ComputePlainSha256(password);
                var calculatedFromSha256 = HashPassword(sha256OfRaw, account.Salt);

                bool match = string.Equals(calculatedHash, account.PasswordHash, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(calculatedFromSha256, account.PasswordHash, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(password, account.PasswordHash, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(sha256OfRaw, account.PasswordHash, StringComparison.OrdinalIgnoreCase);

                if (!match)
                {
                    errorReason = $"'{username}' hesabı için girdiğiniz şifre YANLIŞ! Lütfen şifrenizi kontrol edin.";
                    return false;
                }

                account.LastLogin = DateTime.UtcNow;
                Save();
                return true;
            }
        }

        public static string ComputePlainSha256(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        public bool Register(string username, string password, bool isAdmin, out string errorReason)
        {
            errorReason = string.Empty;
            if (string.IsNullOrWhiteSpace(username))
            {
                errorReason = "Kullanıcı adı boş olamaz.";
                return false;
            }

            username = username.Trim();
            password = password ?? string.Empty;

            lock (_lock)
            {
                LoadUnlocked();
                if (_users.ContainsKey(username))
                {
                    errorReason = "Bu kullanıcı adı zaten kullanılıyor.";
                    return false;
                }

                var salt = GenerateSalt();
                var account = new UserAccount
                {
                    Username = username,
                    Salt = salt,
                    PasswordHash = HashPassword(password, salt),
                    AccountId = GetNextAccountId(),
                    IsAdmin = isAdmin,
                    CreatedAt = DateTime.UtcNow
                };

                _users[username] = account;
                SaveUnlocked();
                return true;
            }
        }

        public bool ResetPassword(string username, string newPassword)
        {
            lock (_lock)
            {
                LoadUnlocked();
                if (!_users.TryGetValue(username, out var account)) return false;

                account.Salt = GenerateSalt();
                account.PasswordHash = HashPassword(newPassword ?? string.Empty, account.Salt);
                SaveUnlocked();
                return true;
            }
        }

        public bool SetBan(string username, bool isBanned, string reason)
        {
            lock (_lock)
            {
                LoadUnlocked();
                if (!_users.TryGetValue(username, out var account)) return false;

                account.IsBanned = isBanned;
                account.BanReason = reason ?? string.Empty;
                SaveUnlocked();
                return true;
            }
        }

        public bool DeleteUser(string username)
        {
            lock (_lock)
            {
                LoadUnlocked();
                if (_users.Remove(username))
                {
                    SaveUnlocked();
                    return true;
                }
                return false;
            }
        }

        public bool UpdateHeroInfo(string username, string heroId, string? heroName, string? clanName)
        {
            lock (_lock)
            {
                LoadUnlocked();
                if (!_users.TryGetValue(username, out var account)) return false;

                account.HeroId = heroId ?? string.Empty;
                if (!string.IsNullOrEmpty(heroName)) account.HeroName = heroName!;
                if (!string.IsNullOrEmpty(clanName)) account.ClanName = clanName!;
                SaveUnlocked();
                return true;
            }
        }

        public void ResetAllHeroInfo()
        {
            lock (_lock)
            {
                LoadUnlocked();
                foreach (var account in _users.Values)
                {
                    account.HeroId = string.Empty;
                    account.HeroName = string.Empty;
                    account.ClanName = string.Empty;
                }
                SaveUnlocked();
            }
        }

        public UserAccount? GetUser(string username)
        {
            lock (_lock)
            {
                RefreshIfModifiedUnlocked();
                if (_users.TryGetValue(username, out var account)) return account;
                return null;
            }
        }

        public bool IsBanned(string username)
        {
            lock (_lock)
            {
                RefreshIfModifiedUnlocked();
                return _users.TryGetValue(username, out var account) && account.IsBanned;
            }
        }

        public bool IsAdmin(string username)
        {
            lock (_lock)
            {
                RefreshIfModifiedUnlocked();
                return _users.TryGetValue(username, out var account) && account.IsAdmin;
            }
        }

        public bool SetAdmin(string username, bool isAdmin)
        {
            lock (_lock)
            {
                LoadUnlocked();
                if (!_users.TryGetValue(username, out var account)) return false;
                account.IsAdmin = isAdmin;
                SaveUnlocked();
                return true;
            }
        }

        public List<UserAccount> GetAllUsers()
        {
            lock (_lock)
            {
                RefreshIfModifiedUnlocked();
                return _users.Values.OrderBy(u => u.Username).ToList();
            }
        }

        public static string GenerateSalt()
        {
            byte[] bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static string HashPassword(string password, string salt)
        {
            using (var sha = SHA256.Create())
            {
                var input = Encoding.UTF8.GetBytes(salt + ":" + password);
                var hash = sha.ComputeHash(input);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
