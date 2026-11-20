using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Engine;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace CalradiaConnected.Mod.UI
{
    public class ClientCredentials
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4200;
        public string Username { get; set; } = "Oyuncu";
        public string RawPassword { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string ServerPassword { get; set; } = "";
        public bool RememberMe { get; set; } = true;
    }

    public class DiscoveredLobbyServer
    {
        public string ServerName { get; set; } = "Calradia Connected";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4200;
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; } = 16;
        public bool HasPassword { get; set; }
        public string SourceTag { get; set; } = "LAN";

        public string ToDisplayLine()
        {
            string lockTag = HasPassword ? Loc.T("[ŞİFRELİ]", "[LOCKED]") : Loc.T("[AÇIK]", "[OPEN]");
            string playerLabel = Loc.T("Oyuncu", "Players");
            return $"{lockTag}  {ServerName}  |  {playerLabel}: {CurrentPlayers}/{MaxPlayers}  |  {Host}:{Port} ({SourceTag})";
        }
    }

    public class CalradiaConnectedLoginVM : ViewModel
    {
        private readonly Action<string, int, string, string, string, bool> _onConnect;
        private readonly Action _onCancel;

        private string _serverIp;
        private string _serverPort;
        private string _username;
        private string _password;
        private string _serverPassword;
        private bool _rememberMe;
        private string _statusText;

        private bool _isLobbyVisible;
        private bool _isCreditsVisible;
        private bool _isLanguageMenuOpen;
        private string _lobbyStatusText = Loc.T(
            "Ağdaki ve Steam'deki açık sunucular taranıyor...",
            "Scanning LAN and Steam servers...");
        private readonly List<DiscoveredLobbyServer> _discoveredServers = new List<DiscoveredLobbyServer>();

        public CalradiaConnectedLoginVM(
            ClientCredentials creds,
            Action<string, int, string, string, string, bool> onConnect,
            Action onCancel)
        {
            _onConnect = onConnect;
            _onCancel = onCancel;

            _serverIp = string.IsNullOrWhiteSpace(creds.Host) ? "127.0.0.1" : creds.Host.Trim();
            _serverPort = (creds.Port > 0 ? creds.Port : 4200).ToString();
            _username = creds.RememberMe && !string.IsNullOrWhiteSpace(creds.Username)
                ? creds.Username
                : (Environment.UserName ?? Loc.T("Oyuncu", "Player"));
            _password = creds.RememberMe ? (creds.RawPassword ?? "") : "";
            _serverPassword = creds.RememberMe ? (creds.ServerPassword ?? "") : "";
            _rememberMe = creds.RememberMe;
            _statusText = Loc.T(
                "Hesap şifrenizi girin (ilk girişte hesabınız otomatik oluşturulur).",
                "Enter your account password (a new account is created on first login).");
        }

        [DataSourceProperty]
        public string TitleText => _isLobbyVisible
            ? Loc.T("KALRADYA LOBİSİ (AĞ & STEAM)", "CALRADIA LOBBY (LAN & STEAM)")
            : Loc.T("KALRADYA'YA KATIL", "JOIN CALRADIA");

        [DataSourceProperty]
        public string DirectTabButtonText => !_isLobbyVisible
            ? Loc.T("▶ Doğrudan Bağlan", "▶ Direct Connect")
            : Loc.T("Doğrudan Bağlan", "Direct Connect");

        [DataSourceProperty]
        public string LobbyTabButtonText => _isLobbyVisible
            ? Loc.T("▶ Lobi (Ağ / Steam)", "▶ Lobby (LAN / Steam)")
            : Loc.T("Lobi (Ağ / Steam)", "Lobby (LAN / Steam)");

        [DataSourceProperty]
        public bool IsMainPanelVisible => !_isCreditsVisible;

        [DataSourceProperty]
        public bool IsLoginFormVisible => !_isLobbyVisible && !_isCreditsVisible;

        [DataSourceProperty]
        public bool IsLobbyVisible => _isLobbyVisible && !_isCreditsVisible;

        [DataSourceProperty]
        public bool IsCreditsVisible
        {
            get => _isCreditsVisible;
            set
            {
                if (_isCreditsVisible != value)
                {
                    _isCreditsVisible = value;
                    OnPropertyChangedWithValue(_isCreditsVisible, nameof(IsCreditsVisible));
                    OnPropertyChangedWithValue(IsMainPanelVisible, nameof(IsMainPanelVisible));
                    OnPropertyChangedWithValue(IsLoginFormVisible, nameof(IsLoginFormVisible));
                    OnPropertyChangedWithValue(IsLobbyVisible, nameof(IsLobbyVisible));
                }
            }
        }

        [DataSourceProperty]
        public string ServerIpLabel => Loc.T("Sunucu IP Adresi:", "Server IP Address:");

        [DataSourceProperty]
        public string ServerPortLabel => Loc.T("Port:", "Port:");

        [DataSourceProperty]
        public string UsernameLabel => Loc.T("Kullanıcı Adı:", "Username:");

        [DataSourceProperty]
        public string PasswordLabel => Loc.T("Hesap Şifresi:", "Account Password:");

        [DataSourceProperty]
        public string ServerPasswordLabel => Loc.T("Sunucu Şifresi (Varsa):", "Server Password (Optional):");

        [DataSourceProperty]
        public string CancelButtonText => Loc.T("İptal", "Cancel");

        [DataSourceProperty]
        public string ConnectButtonText => Loc.T("Bağlan", "Connect");

        [DataSourceProperty]
        public string RefreshLobbyButtonText => Loc.T("Sunucuları Yeniden Ara", "Rescan Servers");

        [DataSourceProperty]
        public string BackToLoginButtonText => Loc.T("Giriş Ekranına Dön", "Back to Login");

        [DataSourceProperty]
        public string CloseCreditsButtonText => Loc.T("Kapat", "Close");

        [DataSourceProperty]
        public string LanguageButtonText => Loc.GetNativeLanguageName(Loc.CurrentLanguage);

        [DataSourceProperty]
        public bool IsLanguageMenuOpen
        {
            get => _isLanguageMenuOpen;
            set
            {
                if (_isLanguageMenuOpen != value)
                {
                    _isLanguageMenuOpen = value;
                    OnPropertyChangedWithValue(_isLanguageMenuOpen, nameof(IsLanguageMenuOpen));
                }
            }
        }

        [DataSourceProperty] public bool IsFlagTR => string.Equals(Loc.CurrentLanguage, "TR", StringComparison.OrdinalIgnoreCase);
        [DataSourceProperty] public bool IsFlagEN => string.Equals(Loc.CurrentLanguage, "EN", StringComparison.OrdinalIgnoreCase);
        [DataSourceProperty] public bool IsFlagPL => string.Equals(Loc.CurrentLanguage, "PL", StringComparison.OrdinalIgnoreCase);
        [DataSourceProperty] public bool IsFlagDE => string.Equals(Loc.CurrentLanguage, "DE", StringComparison.OrdinalIgnoreCase);
        [DataSourceProperty] public bool IsFlagRU => string.Equals(Loc.CurrentLanguage, "RU", StringComparison.OrdinalIgnoreCase);
        [DataSourceProperty] public bool IsFlagFR => string.Equals(Loc.CurrentLanguage, "FR", StringComparison.OrdinalIgnoreCase);
        [DataSourceProperty] public bool IsFlagIT => string.Equals(Loc.CurrentLanguage, "IT", StringComparison.OrdinalIgnoreCase);
        [DataSourceProperty] public bool IsFlagZH => string.Equals(Loc.CurrentLanguage, "ZH", StringComparison.OrdinalIgnoreCase);

        [DataSourceProperty]
        public string DeveloperFooterText => Loc.T("Geliştirici: konasoglu", "Developer: konasoglu");

        [DataSourceProperty]
        public string CreditsButtonText => Loc.T("Emeği Geçenler", "Credits");

        [DataSourceProperty]
        public string CreditsTitle => "Calradia Connected";

        [DataSourceProperty]
        public string CreditsDeveloperRole => Loc.T("Geliştirici", "Developer");

        [DataSourceProperty]
        public string CreditsDeveloperName => "konasoglu";

        [DataSourceProperty]
        public string GithubButtonText => "GitHub";

        [DataSourceProperty]
        public string ServerAddress
        {
            get => $"{_serverIp}:{_serverPort}";
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var parts = value.Split(':');
                    ServerIp = parts[0].Trim();
                    if (parts.Length > 1) ServerPort = parts[1].Trim();
                }
            }
        }

        [DataSourceProperty]
        public string ServerIp
        {
            get => _serverIp;
            set
            {
                if (_serverIp != value)
                {
                    _serverIp = value ?? "";
                    OnPropertyChangedWithValue(_serverIp, nameof(ServerIp));
                    OnPropertyChangedWithValue(ServerAddress, nameof(ServerAddress));
                }
            }
        }

        [DataSourceProperty]
        public string ServerPort
        {
            get => _serverPort;
            set
            {
                if (_serverPort != value)
                {
                    _serverPort = value ?? "4200";
                    OnPropertyChangedWithValue(_serverPort, nameof(ServerPort));
                    OnPropertyChangedWithValue(ServerAddress, nameof(ServerAddress));
                }
            }
        }

        [DataSourceProperty]
        public string Username
        {
            get => _username;
            set
            {
                if (_username != value)
                {
                    _username = value ?? "";
                    OnPropertyChangedWithValue(_username, nameof(Username));
                }
            }
        }

        [DataSourceProperty]
        public string Password
        {
            get => _password;
            set
            {
                if (_password != value)
                {
                    _password = value ?? "";
                    OnPropertyChangedWithValue(_password, nameof(Password));
                }
            }
        }

        [DataSourceProperty]
        public string ServerPassword
        {
            get => _serverPassword;
            set
            {
                if (_serverPassword != value)
                {
                    _serverPassword = value ?? "";
                    OnPropertyChangedWithValue(_serverPassword, nameof(ServerPassword));
                }
            }
        }

        [DataSourceProperty]
        public bool RememberMe
        {
            get => _rememberMe;
            set
            {
                if (_rememberMe != value)
                {
                    _rememberMe = value;
                    OnPropertyChangedWithValue(_rememberMe, nameof(RememberMe));
                    OnPropertyChangedWithValue(RememberMeButtonText, nameof(RememberMeButtonText));
                }
            }
        }

        [DataSourceProperty]
        public string RememberMeButtonText => _rememberMe
            ? Loc.T("[X] Beni Hatırla (Aktif)", "[X] Remember Me (On)")
            : Loc.T("[  ] Beni Hatırla (Kapalı)", "[  ] Remember Me (Off)");

        public void ExecuteToggleLanguageMenu()
        {
            IsLanguageMenuOpen = !IsLanguageMenuOpen;
        }

        public void ExecuteSelectLangTR() => SelectLanguageAndRefresh("TR");
        public void ExecuteSelectLangEN() => SelectLanguageAndRefresh("EN");
        public void ExecuteSelectLangPL() => SelectLanguageAndRefresh("PL");
        public void ExecuteSelectLangDE() => SelectLanguageAndRefresh("DE");
        public void ExecuteSelectLangRU() => SelectLanguageAndRefresh("RU");
        public void ExecuteSelectLangFR() => SelectLanguageAndRefresh("FR");
        public void ExecuteSelectLangIT() => SelectLanguageAndRefresh("IT");
        public void ExecuteSelectLangZH() => SelectLanguageAndRefresh("ZH");

        private void SelectLanguageAndRefresh(string langCode)
        {
            Loc.SetLanguageManually(langCode);
            IsLanguageMenuOpen = false;
            RefreshLocalizedProperties();
        }

        public void ExecuteToggleLanguage()
        {
            Loc.ToggleLanguage();
            IsLanguageMenuOpen = false;
            RefreshLocalizedProperties();
        }

        private void RefreshLocalizedProperties()
        {
            _statusText = Loc.T(
                "Hesap şifrenizi girin (ilk girişte hesabınız otomatik oluşturulur).",
                "Enter your account password (a new account is created on first login).");
            OnPropertyChangedWithValue(TitleText, nameof(TitleText));
            OnPropertyChangedWithValue(DirectTabButtonText, nameof(DirectTabButtonText));
            OnPropertyChangedWithValue(LobbyTabButtonText, nameof(LobbyTabButtonText));
            OnPropertyChangedWithValue(ServerIpLabel, nameof(ServerIpLabel));
            OnPropertyChangedWithValue(ServerPortLabel, nameof(ServerPortLabel));
            OnPropertyChangedWithValue(UsernameLabel, nameof(UsernameLabel));
            OnPropertyChangedWithValue(PasswordLabel, nameof(PasswordLabel));
            OnPropertyChangedWithValue(ServerPasswordLabel, nameof(ServerPasswordLabel));
            OnPropertyChangedWithValue(RememberMeButtonText, nameof(RememberMeButtonText));
            OnPropertyChangedWithValue(CancelButtonText, nameof(CancelButtonText));
            OnPropertyChangedWithValue(ConnectButtonText, nameof(ConnectButtonText));
            OnPropertyChangedWithValue(RefreshLobbyButtonText, nameof(RefreshLobbyButtonText));
            OnPropertyChangedWithValue(BackToLoginButtonText, nameof(BackToLoginButtonText));
            OnPropertyChangedWithValue(CloseCreditsButtonText, nameof(CloseCreditsButtonText));
            OnPropertyChangedWithValue(LanguageButtonText, nameof(LanguageButtonText));
            OnPropertyChangedWithValue(IsFlagTR, nameof(IsFlagTR));
            OnPropertyChangedWithValue(IsFlagEN, nameof(IsFlagEN));
            OnPropertyChangedWithValue(IsFlagPL, nameof(IsFlagPL));
            OnPropertyChangedWithValue(IsFlagDE, nameof(IsFlagDE));
            OnPropertyChangedWithValue(IsFlagRU, nameof(IsFlagRU));
            OnPropertyChangedWithValue(IsFlagFR, nameof(IsFlagFR));
            OnPropertyChangedWithValue(IsFlagIT, nameof(IsFlagIT));
            OnPropertyChangedWithValue(IsFlagZH, nameof(IsFlagZH));
            OnPropertyChangedWithValue(DeveloperFooterText, nameof(DeveloperFooterText));
            OnPropertyChangedWithValue(CreditsButtonText, nameof(CreditsButtonText));
            OnPropertyChangedWithValue(CreditsDeveloperRole, nameof(CreditsDeveloperRole));
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
            RefreshLobbySlotsProperties();
        }

        [DataSourceProperty]
        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value ?? "";
                    OnPropertyChangedWithValue(_statusText, nameof(StatusText));
                }
            }
        }

        [DataSourceProperty]
        public string LobbyStatusText
        {
            get => _lobbyStatusText;
            set
            {
                if (_lobbyStatusText != value)
                {
                    _lobbyStatusText = value ?? "";
                    OnPropertyChangedWithValue(_lobbyStatusText, nameof(LobbyStatusText));
                }
            }
        }

        [DataSourceProperty]
        public bool HasLobbySlot0 => _discoveredServers.Count > 0;
        [DataSourceProperty]
        public string LobbySlot0Text => _discoveredServers.Count > 0 ? _discoveredServers[0].ToDisplayLine() : "";

        [DataSourceProperty]
        public bool HasLobbySlot1 => _discoveredServers.Count > 1;
        [DataSourceProperty]
        public string LobbySlot1Text => _discoveredServers.Count > 1 ? _discoveredServers[1].ToDisplayLine() : "";

        [DataSourceProperty]
        public bool HasLobbySlot2 => _discoveredServers.Count > 2;
        [DataSourceProperty]
        public string LobbySlot2Text => _discoveredServers.Count > 2 ? _discoveredServers[2].ToDisplayLine() : "";

        [DataSourceProperty]
        public bool HasLobbySlot3 => _discoveredServers.Count > 3;
        [DataSourceProperty]
        public string LobbySlot3Text => _discoveredServers.Count > 3 ? _discoveredServers[3].ToDisplayLine() : "";

        private void RefreshLobbySlotsProperties()
        {
            OnPropertyChangedWithValue(HasLobbySlot0, nameof(HasLobbySlot0));
            OnPropertyChangedWithValue(LobbySlot0Text, nameof(LobbySlot0Text));
            OnPropertyChangedWithValue(HasLobbySlot1, nameof(HasLobbySlot1));
            OnPropertyChangedWithValue(LobbySlot1Text, nameof(LobbySlot1Text));
            OnPropertyChangedWithValue(HasLobbySlot2, nameof(HasLobbySlot2));
            OnPropertyChangedWithValue(LobbySlot2Text, nameof(LobbySlot2Text));
            OnPropertyChangedWithValue(HasLobbySlot3, nameof(HasLobbySlot3));
            OnPropertyChangedWithValue(LobbySlot3Text, nameof(LobbySlot3Text));
        }

        public void ExecuteToggleRememberMe()
        {
            RememberMe = !RememberMe;
        }

        public void ExecuteShowDirectConnect()
        {
            _isLobbyVisible = false;
            IsCreditsVisible = false;
            OnPropertyChangedWithValue(TitleText, nameof(TitleText));
            OnPropertyChangedWithValue(DirectTabButtonText, nameof(DirectTabButtonText));
            OnPropertyChangedWithValue(LobbyTabButtonText, nameof(LobbyTabButtonText));
            OnPropertyChangedWithValue(IsLoginFormVisible, nameof(IsLoginFormVisible));
            OnPropertyChangedWithValue(IsLobbyVisible, nameof(IsLobbyVisible));
        }

        public void ExecuteOpenLobby()
        {
            _isLobbyVisible = true;
            IsCreditsVisible = false;
            OnPropertyChangedWithValue(TitleText, nameof(TitleText));
            OnPropertyChangedWithValue(DirectTabButtonText, nameof(DirectTabButtonText));
            OnPropertyChangedWithValue(LobbyTabButtonText, nameof(LobbyTabButtonText));
            OnPropertyChangedWithValue(IsLoginFormVisible, nameof(IsLoginFormVisible));
            OnPropertyChangedWithValue(IsLobbyVisible, nameof(IsLobbyVisible));
            ExecuteRefreshLobby();
        }

        public void ExecuteRefreshLobby()
        {
            LobbyStatusText = Loc.T(
                "Yerel ağ (LAN) ve Steam arkadaş ağı üzerindeki sunucular aranıyor...",
                "Scanning LAN and Steam friend networks for servers...");
            _discoveredServers.Clear();
            RefreshLobbySlotsProperties();

            string currentIp = (_serverIp ?? "127.0.0.1").Trim();
            int currentPort = int.TryParse((_serverPort ?? "4200").Trim(), out int p) && p > 0 ? p : 4200;

            Task.Run(async () =>
            {
                var found = await ScanNetworkAndSteamServersAsync(currentIp, currentPort);
                ModContext.Dispatcher.Enqueue(() =>
                {
                    _discoveredServers.Clear();
                    _discoveredServers.AddRange(found);
                    RefreshLobbySlotsProperties();

                    if (_discoveredServers.Count > 0)
                    {
                        LobbyStatusText = Loc.F(
                            "{0} aktif sunucu bulundu! Katılmak istediğiniz sunucuya tıklayın:",
                            "{0} active server(s) found! Click a server to select it:",
                            _discoveredServers.Count);
                    }
                    else
                    {
                        LobbyStatusText = Loc.T(
                            "Açık sunucu bulunamadı. Sunucunun açık olduğundan emin olup 'Yeniden Ara'ya basın.",
                            "No open servers found. Make sure a server is running and click 'Rescan Servers'.");
                    }
                });
            });
        }

        private void SelectLobbyServerAtIndex(int index)
        {
            if (index < 0 || index >= _discoveredServers.Count) return;
            var srv = _discoveredServers[index];

            ServerIp = srv.Host;
            ServerPort = srv.Port.ToString();

            ExecuteShowDirectConnect();

            if (srv.HasPassword)
            {
                StatusText = Loc.F(
                    "[ŞİFRELİ SUNUCU] '{0}' seçildi. Lütfen 'Sunucu Şifresi' ve 'Hesap Şifresi' alanlarını doldurun.",
                    "[LOCKED SERVER] '{0}' selected. Enter both Server Password and Account Password.",
                    srv.ServerName);
            }
            else
            {
                ServerPassword = "";
                StatusText = Loc.F(
                    "[AÇIK SUNUCU] '{0}' ({1}:{2}) seçildi. Hesap şifrenizle Bağlan'a basabilirsiniz.",
                    "[OPEN SERVER] '{0}' ({1}:{2}) selected. Enter your Account Password and click Connect.",
                    srv.ServerName, srv.Host, srv.Port);
            }
        }

        public void ExecuteSelectLobbySlot0() => SelectLobbyServerAtIndex(0);
        public void ExecuteSelectLobbySlot1() => SelectLobbyServerAtIndex(1);
        public void ExecuteSelectLobbySlot2() => SelectLobbyServerAtIndex(2);
        public void ExecuteSelectLobbySlot3() => SelectLobbyServerAtIndex(3);

        public void ExecuteConnect()
        {
            string host = (_serverIp ?? "").Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                StatusText = Loc.T("HATA: Sunucu IP adresi boş bırakılamaz!", "ERROR: Server IP address cannot be empty!");
                return;
            }

            if (host.Contains(":"))
            {
                var parts = host.Split(':');
                host = parts[0].Trim();
                if (parts.Length > 1)
                {
                    ServerIp = host;
                    ServerPort = parts[1].Trim();
                }
            }

            if (!int.TryParse((_serverPort ?? "").Trim(), out int port) || port <= 0 || port > 65535)
            {
                StatusText = Loc.T("HATA: Geçerli bir Port numarası girin (Örn: 4200)!", "ERROR: Enter a valid Port number (e.g. 4200)!");
                return;
            }

            string cleanUser = (_username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cleanUser) || cleanUser.Length < 2)
            {
                StatusText = Loc.T("HATA: Kullanıcı adı en az 2 karakter olmalıdır!", "ERROR: Username must be at least 2 characters!");
                return;
            }

            string rawPass = (_password ?? "").Trim();
            if (string.IsNullOrWhiteSpace(rawPass))
            {
                StatusText = Loc.T("HATA: Hesap şifresi boş bırakılamaz! Lütfen şifrenizi girin.", "ERROR: Account password cannot be empty!");
                return;
            }

            string rawServerPass = (_serverPassword ?? "").Trim();
            StatusText = Loc.F("{0}:{1} sunucusuna bağlanılıyor ve şifre kontrol ediliyor...", "Connecting to {0}:{1} and verifying credentials...", host, port);
            _onConnect?.Invoke(host, port, cleanUser, rawPass, rawServerPass, _rememberMe);
        }

        public void ExecuteCancel()
        {
            _onCancel?.Invoke();
        }

        public void ExecuteShowCredits()
        {
            IsCreditsVisible = true;
        }

        public void ExecuteCloseCredits()
        {
            IsCreditsVisible = false;
        }

        public void ExecuteOpenGithub()
        {
            const string githubUrl = "https://github.com/konasoglu/Calradia_Connected";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = githubUrl,
                    UseShellExecute = true
                });
                StatusText = Loc.T(
                    "GitHub sayfası tarayıcıda açıldı: github.com/konasoglu/Calradia_Connected",
                    "Opened GitHub page in browser: github.com/konasoglu/Calradia_Connected");
            }
            catch
            {
                StatusText = "GitHub: https://github.com/konasoglu/Calradia_Connected";
            }
        }

        private static async Task<List<DiscoveredLobbyServer>> ScanNetworkAndSteamServersAsync(string targetIp, int targetPort)
        {
            var results = new Dictionary<string, DiscoveredLobbyServer>(StringComparer.OrdinalIgnoreCase);

            // 1. Collect candidate endpoints (Localhost, Configured IP, LAN Broadcast, and Steam Friends Rich Presence)
            var candidateHosts = new List<(string host, int port, string source)>
            {
                ("127.0.0.1", targetPort, Loc.T("Yerel", "Local")),
                ("127.0.0.1", 4200, Loc.T("Yerel", "Local"))
            };

            if (!string.IsNullOrWhiteSpace(targetIp) && targetIp != "127.0.0.1" && !targetIp.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                candidateHosts.Add((targetIp, targetPort, Loc.T("Kaydedilen IP", "Saved IP")));
            }

            // Probe Steam Friends playing Bannerlord (AppId 261550) via Steamworks.NET reflection if loaded
            try
            {
                foreach (var friendHost in ProbeSteamFriendServers())
                {
                    candidateHosts.Add((friendHost.host, friendHost.port, Loc.T("Steam Ağı", "Steam Network")));
                }
            }
            catch { }

            // 2. UDP Unconnected Broadcast & Direct Probe (LiteNetLib CC_LOBBY_QUERY wire format)
            try
            {
                using (var udp = new UdpClient(0))
                {
                    udp.EnableBroadcast = true;
                    var writer = new NetDataWriter();
                    writer.Put("CC_LOBBY_QUERY");
                    byte[] rawQuery = writer.CopyData();

                    // LiteNetLib UnconnectedMessage header byte is 0x0A (PacketProperty.UnconnectedMessage = 10)
                    byte[] lnlPacket = new byte[rawQuery.Length + 1];
                    lnlPacket[0] = 10;
                    Buffer.BlockCopy(rawQuery, 0, lnlPacket, 1, rawQuery.Length);

                    var broadcastPorts = new[] { targetPort, 4200, 4202 }.Distinct();
                    foreach (int p in broadcastPorts)
                    {
                        try { await udp.SendAsync(lnlPacket, lnlPacket.Length, new IPEndPoint(IPAddress.Broadcast, p)); } catch { }
                        try { await udp.SendAsync(lnlPacket, lnlPacket.Length, new IPEndPoint(IPAddress.Loopback, p)); } catch { }
                    }

                    foreach (var (h, p, _) in candidateHosts)
                    {
                        if (IPAddress.TryParse(h, out var ip))
                        {
                            try { await udp.SendAsync(lnlPacket, lnlPacket.Length, new IPEndPoint(ip, p)); } catch { }
                        }
                    }

                    var waitStop = DateTime.UtcNow.AddMilliseconds(450);
                    while (DateTime.UtcNow < waitStop)
                    {
                        if (udp.Available > 0)
                        {
                            var res = await udp.ReceiveAsync();
                            if (res.Buffer != null && res.Buffer.Length > 5)
                            {
                                var reader = new NetDataReader(res.Buffer, 1, res.Buffer.Length - 1);
                                string magic = reader.GetString();
                                if (magic == "CC_LOBBY_INFO")
                                {
                                    string sName = reader.GetString();
                                    int sPort = reader.GetInt();
                                    int curP = reader.GetInt();
                                    int maxP = reader.GetInt();
                                    bool hasPass = reader.GetBool();
                                    string hostStr = res.RemoteEndPoint.Address.ToString();
                                    string key = $"{hostStr}:{sPort}";
                                    results[key] = new DiscoveredLobbyServer
                                    {
                                        ServerName = sName,
                                        Host = hostStr,
                                        Port = sPort,
                                        CurrentPlayers = curP,
                                        MaxPlayers = maxP,
                                        HasPassword = hasPass,
                                        SourceTag = hostStr == "127.0.0.1" ? Loc.T("Bu Bilgisayar", "This PC") : Loc.T("LAN / Ağ", "LAN / Network")
                                    };
                                }
                            }
                        }
                        else
                        {
                            await Task.Delay(30);
                        }
                    }
                }
            }
            catch { }

            // 3. HTTP FastDL / Lobby Info Probe (Port + 1, e.g., 4201)
            foreach (var (host, port, sourceTag) in candidateHosts.Distinct())
            {
                string key = $"{host}:{port}";
                if (results.ContainsKey(key)) continue;

                int fastDlPort = port + 1;
                foreach (string path in new[] { $"http://{host}:{fastDlPort}/api/info", $"http://{host}:{fastDlPort}/" })
                {
                    try
                    {
                        var req = WebRequest.CreateHttp(path);
                        req.Timeout = 450;
                        req.Method = "GET";
                        using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                        using (var sr = new StreamReader(resp.GetResponseStream()))
                        {
                            string json = await sr.ReadToEndAsync();
                            var obj = JObject.Parse(json);
                            if (obj["status"] != null)
                            {
                                string sName = obj["serverName"]?.ToString() ?? "Calradia Connected Sunucusu";
                                int gPort = obj["gamePort"]?.Value<int>() ?? port;
                                int curP = obj["currentPlayers"]?.Value<int>() ?? 0;
                                int maxP = obj["maxPlayers"]?.Value<int>() ?? 16;
                                bool hasPass = obj["hasPassword"]?.Value<bool>() ?? false;

                                results[$"{host}:{gPort}"] = new DiscoveredLobbyServer
                                {
                                    ServerName = sName,
                                    Host = host,
                                    Port = gPort,
                                    CurrentPlayers = curP,
                                    MaxPlayers = maxP,
                                    HasPassword = hasPass,
                                    SourceTag = sourceTag
                                };
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }

            return results.Values.ToList();
        }

        private static List<(string host, int port)> ProbeSteamFriendServers()
        {
            var list = new List<(string host, int port)>();
            try
            {
                var steamAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name.IndexOf("Steamworks", StringComparison.OrdinalIgnoreCase) >= 0);
                if (steamAsm == null) return list;

                var friendsType = steamAsm.GetType("Steamworks.SteamFriends");
                if (friendsType == null) return list;

                var getCount = friendsType.GetMethod("GetFriendCount", BindingFlags.Public | BindingFlags.Static);
                var getByIndex = friendsType.GetMethod("GetFriendByIndex", BindingFlags.Public | BindingFlags.Static);
                var getRichPresence = friendsType.GetMethod("GetFriendRichPresence", BindingFlags.Public | BindingFlags.Static);
                if (getCount == null || getByIndex == null || getRichPresence == null) return list;

                int count = (int)getCount.Invoke(null, new object[] { 4 })!; // k_EFriendFlagImmediate = 4
                for (int i = 0; i < count; i++)
                {
                    object friendId = getByIndex.Invoke(null, new object[] { i, 4 })!;
                    string? ccEndpoint = getRichPresence.Invoke(null, new object[] { friendId, "cc_server" }) as string;
                    if (string.IsNullOrWhiteSpace(ccEndpoint))
                    {
                        ccEndpoint = getRichPresence.Invoke(null, new object[] { friendId, "connect" }) as string;
                    }

                    if (!string.IsNullOrWhiteSpace(ccEndpoint))
                    {
                        string clean = ccEndpoint!.Replace("+connect", "").Trim();
                        var parts = clean.Split(':');
                        string h = parts[0].Trim();
                        int p = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int parsed) ? parsed : 4200;
                        if (!string.IsNullOrEmpty(h))
                        {
                            list.Add((h, p));
                        }
                    }
                }
            }
            catch { }

            return list;
        }
    }

    public class ClientLoginManager
    {
        private static ClientLoginManager? _instance;
        public static ClientLoginManager Instance => _instance ??= new ClientLoginManager();

        private readonly string _credentialsPath;
        private ClientCredentials _credentials;

        private ScreenBase? _parentScreen;
        private GauntletLayer? _gauntletLayer;
        private GauntletMovieIdentifier? _movie;
        private CalradiaConnectedLoginVM? _dataSource;

        public string CurrentUsername => _credentials?.Username ?? Environment.UserName ?? "Oyuncu";
        public bool IsOverlayOpen => _gauntletLayer != null && _dataSource != null;

        public ClientLoginManager()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord",
                "Configs");

            if (!Directory.Exists(folder))
            {
                try { Directory.CreateDirectory(folder); } catch { }
            }

            _credentialsPath = Path.Combine(folder, "calradiaconnected_auth.json");
            _credentials = LoadCredentials();
        }

        private ClientCredentials LoadCredentials()
        {
            try
            {
                if (File.Exists(_credentialsPath))
                {
                    string json = File.ReadAllText(_credentialsPath);
                    var creds = JsonConvert.DeserializeObject<ClientCredentials>(json);
                    if (creds != null) return creds;
                }
            }
            catch { }

            return new ClientCredentials
            {
                Username = Environment.UserName ?? "Oyuncu",
                RememberMe = true
            };
        }

        public void SaveCredentials(string host, int port, string username, string rawPassword, string passwordHash, string serverPassword, bool rememberMe)
        {
            try
            {
                _credentials.Host = host;
                _credentials.Port = port;
                _credentials.Username = username;
                _credentials.RememberMe = rememberMe;
                _credentials.RawPassword = rememberMe ? rawPassword : "";
                _credentials.PasswordHash = rememberMe ? passwordHash : "";
                _credentials.ServerPassword = rememberMe ? serverPassword : "";

                string json = JsonConvert.SerializeObject(_credentials, Formatting.Indented);
                File.WriteAllText(_credentialsPath, json);
            }
            catch { }
        }

        public static string ComputeSha256(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder();
                foreach (byte b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public void SetStatus(string statusMessage)
        {
            if (_dataSource != null)
            {
                _dataSource.StatusText = statusMessage;
            }
        }

        public void PromptLogin(Action<string, int, string, string, string> onCredentialsReady)
        {
            CalradiaConnectedSubModule.SyncLanguageFromBannerlord();
            _credentials = LoadCredentials();
            CloseOverlay();

            var topScreen = ScreenManager.TopScreen;
            if (topScreen == null)
            {
                return;
            }

            try
            {
                _parentScreen = topScreen;
                _dataSource = new CalradiaConnectedLoginVM(
                    _credentials,
                    onConnect: (host, port, username, rawPass, serverPass, rememberMe) =>
                    {
                        string passHash = ComputeSha256(rawPass);
                        SaveCredentials(host, port, username, rawPass, passHash, serverPass, rememberMe);
                        _credentials.Username = username;

                        // Do NOT close overlay yet: keep open while connecting so password validation result is displayed!
                        onCredentialsReady?.Invoke(host, port, username, rawPass, serverPass);
                    },
                    onCancel: () =>
                    {
                        CloseOverlay();
                    });

                try { UIResourceManager.LoadSpriteCategory("ui_group1"); } catch { }

                _gauntletLayer = new GauntletLayer("CalradiaConnectedLogin", 4500, false);
                _movie = _gauntletLayer.LoadMovie("CalradiaConnectedLogin", _dataSource);
                _gauntletLayer.IsFocusLayer = true;
                _gauntletLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                _parentScreen.AddLayer(_gauntletLayer);
                _gauntletLayer.IsFocusLayer = true;
                _gauntletLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                ScreenManager.TrySetFocus(_gauntletLayer);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Gauntlet Login UI yüklenemedi: {ex.Message}");
                CloseOverlay();
            }
        }

        public void CloseOverlay()
        {
            try
            {
                if (_gauntletLayer != null)
                {
                    if (_movie != null)
                    {
                        try { _gauntletLayer.ReleaseMovie(_movie); } catch { }
                        _movie = null;
                    }

                    try { _gauntletLayer.InputRestrictions.ResetInputRestrictions(); } catch { }
                    try { ScreenManager.SetSuspendLayer(_gauntletLayer, true); } catch { }

                    if (_parentScreen != null)
                    {
                        try { _parentScreen.RemoveLayer(_gauntletLayer); } catch { }
                    }
                    _gauntletLayer = null;
                }

                _dataSource?.OnFinalize();
                _dataSource = null;
                _parentScreen = null;
            }
            catch { }
        }
    }
}
