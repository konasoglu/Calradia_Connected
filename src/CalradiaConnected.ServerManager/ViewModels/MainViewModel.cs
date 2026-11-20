using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CalradiaConnected.Common.Localization;
using CoopServerManager.Localization;
using CoopServerManager.Models;
using CoopServerManager.Services;
using Common.Users;
using Microsoft.Win32;

namespace CoopServerManager.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ModuleScannerService moduleScanner;
        private readonly SaveGameService saveService;
        private readonly ModPackagerService packagerService;
        private readonly FastDlServerService fastDlServer;
        private readonly ServerRunnerService serverRunner;
        private readonly ClientSyncService clientSync;
        private readonly ServerControlIpcService serverControl;
        private readonly GitHubUpdateService githubUpdater;
        private readonly DispatcherTimer statusPollTimer;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> pendingLogs = new();
        private readonly DispatcherTimer logFlushTimer;

        private string bannerlordPath = string.Empty;
        private string serverName = "Calradia Connected Server";
        private int serverPort = 4200;
        private int fastDlPort = 4201;
        private bool enableFastDl = true;
        private string? selectedSave = null;
        private string statusMessage = Loc.T("Sunucu hazır durumda.", "Server is ready.");
        private bool isServerRunning;
        private bool isBusy;
        private string filterText = string.Empty;

        // Performance & Network Settings
        private int serverTickRateHz = 60;
        private int disconnectTimeoutSec = 60;
        private int networkPollIntervalMs = 20;
        private int maxBandwidthMbps = 50;

        // Time Control & MMORPG World State
        private string serverTimeSpeed = Loc.T("Bilinmiyor", "Unknown");
        private double campaignDay = 0.0;
        private int connectedPlayerCount = 0;
        private bool persistentWorldTime = true;

        // Donanım, RAM, VRAM & İşlemci (CPU) Tahsisi
        private int maxRamLimitGb = 0;
        private int vramBudgetLevel = 0;
        private int serverMaxFps = 20;
        private int cpuCoresAllocated = 0;
        private string processPriority = "Normal";
        private bool autoRamCleanEnabled = true;
        private bool serverDisableSound = true;

        // Kalradya Oyun & Zorluk Ayarları
        private int battleSize = 400;
        private int damageDifficulty = 1;
        private int combatAiDifficulty = 1;
        private int heroDeathInBattle = 0;
        private int autoSaveIntervalMinutes = 15;
        private int numberOfCorpses = 20;
        private bool pvpEnabled = true;
        private bool arenaContinuousPracticeRespawn = true;
        private bool arenaPvpDuelsEnabled = true;

        // Lobi & Güvenlik
        private string serverPassword = string.Empty;
        private string serverVisibility = "Public";
        private int maxPlayers = 16;

        // Karakter Başlangıç & İlerleme Ayarları
        private int startingLevel = 1;
        private int startingFocusPoints = 5;
        private int startingAttributePoints = 2;
        private int startingAllSkillsLevel = 0;
        private double characterXpMultiplier = 1.0;
        private double skillXpMultiplier = 1.0;
        private int startingGold = 1000;
        private int startingRenown = 0;
        private int startingInfluence = 0;
        private int startingEquipmentPreset = 0;
        private int startingTroopsPreset = 0;
        private bool startingFoodSupplies = true;

        // Canlı Donanım Metrikleri
        private string serverLiveRamDisplay = Loc.T("Sunucu Kapalı", "Server Offline");
        private string serverLiveCpuDisplay = Loc.T("Sunucu Kapalı", "Server Offline");

        public ServerManagerLoc Lang => ServerManagerLoc.Instance;

        public ObservableCollection<LanguageOption> AvailableLanguages { get; } = new();
        private LanguageOption? selectedLanguage;
        private bool isInitializing = true;
        private bool languageManuallySet = false;
        public LanguageOption? SelectedLanguage
        {
            get => selectedLanguage;
            set
            {
                if (value != null && !ReferenceEquals(selectedLanguage, value))
                {
                    selectedLanguage = value;
                    OnPropertyChanged();
                    if (!isInitializing)
                    {
                        languageManuallySet = true;
                    }
                    if (!string.Equals(Loc.CurrentLanguage, value.Code, StringComparison.OrdinalIgnoreCase))
                    {
                        ServerManagerLoc.Instance.SetLanguage(value.Code);
                        NotifyLanguageChanged();
                        if (!isInitializing)
                        {
                            SaveConfig();
                        }
                    }
                }
            }
        }

        // User Management properties
        private string userSearchText = string.Empty;
        private string newUsername = string.Empty;
        private string newPassword = string.Empty;
        private bool newIsAdmin = false;
        private UserAccount? selectedUser;
        private string resetPasswordText = string.Empty;
        private DateTime lastUsersFileWriteUtc = DateTime.MinValue;

        // Client Join / Mod Sync properties
        private string connectServerIp = "127.0.0.1";
        private int connectFastDlPort = 4201;
        private int connectGamePort = 4200;
        private string clientSyncStatus = Loc.T("Sunucu IP adresi girip modları kontrol edin.", "Enter a server IP address and check server mods.");
        private double clientSyncProgress = 0;
        private bool isClientSyncing;
        private FastDlManifest? lastFetchedManifest;

        public ObservableCollection<ModuleInfoModel> AllModules { get; } = new();
        public ObservableCollection<ModuleInfoModel> FilteredModules { get; } = new();
        public ObservableCollection<string> SaveFiles { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();
        private string consoleText = string.Empty;
        public string ConsoleText
        {
            get => consoleText;
            set { if (consoleText != value) { consoleText = value; OnPropertyChanged(); } }
        }
        public ObservableCollection<FastDlModuleEntry> ClientMissingModules { get; } = new();

        public ObservableCollection<UserAccount> AllUsers { get; } = new();
        public ObservableCollection<UserAccount> FilteredUsers { get; } = new();
        public ObservableCollection<PlayerInfoModel> OnlinePlayers { get; } = new();
        public ObservableCollection<TroopPresetModel> AvailableTroops { get; } = new();

        public string BannerlordPath
        {
            get => bannerlordPath;
            set
            {
                if (bannerlordPath != value)
                {
                    bannerlordPath = value;
                    OnPropertyChanged();
                    ReloadModules();
                    SaveConfig();
                }
            }
        }

        public string ServerName
        {
            get => serverName;
            set { serverName = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int ServerPort
        {
            get => serverPort;
            set { serverPort = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int FastDlPort
        {
            get => fastDlPort;
            set { fastDlPort = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool EnableFastDl
        {
            get => enableFastDl;
            set { enableFastDl = value; OnPropertyChanged(); SaveConfig(); }
        }

        public string? SelectedSave
        {
            get => selectedSave;
            set { selectedSave = value; OnPropertyChanged(); SaveConfig(); }
        }

        public string StatusMessage
        {
            get => statusMessage;
            set { statusMessage = value; OnPropertyChanged(); }
        }

        public bool IsServerRunning
        {
            get => isServerRunning;
            set
            {
                isServerRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanStartServer));
                OnPropertyChanged(nameof(CanStopServer));
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(StartServerVisibility));
                OnPropertyChanged(nameof(StopServerVisibility));
            }
        }

        private bool isServerConnected;
        public bool IsServerConnected
        {
            get => isServerConnected;
            set
            {
                if (isServerConnected == value) return;
                isServerConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }

        public Visibility StartServerVisibility => IsServerRunning ? Visibility.Collapsed : Visibility.Visible;
        public Visibility StopServerVisibility => IsServerRunning ? Visibility.Visible : Visibility.Collapsed;

        public bool IsBusy
        {
            get => isBusy;
            set
            {
                isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanStartServer));
            }
        }

        public bool CanStartServer => !IsServerRunning && !IsBusy && !string.IsNullOrWhiteSpace(BannerlordPath);
        public bool CanStopServer => IsServerRunning && !IsBusy;

        public string StatusBadgeText => (IsServerRunning || IsServerConnected)
            ? CalradiaConnected.Common.Localization.Loc.T("ÇEVRİMİÇİ (ONLINE)", "ONLINE")
            : (IsBusy ? CalradiaConnected.Common.Localization.Loc.T("İŞLENİYOR...", "WORKING...")
                      : CalradiaConnected.Common.Localization.Loc.T("ÇEVRİMDIŞI (OFFLINE)", "OFFLINE"));
        public string StatusBadgeColor => (IsServerRunning || IsServerConnected) ? "#10B981" : (IsBusy ? "#F59E0B" : "#EF4444");

        public string LocalIpAddresses => GetLocalIpSummary();
        public string PrimaryLocalIp => GetPrimaryLocalIp();

        public string FilterText
        {
            get => filterText;
            set
            {
                filterText = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        // Performance & Network Properties
        public int ServerTickRateHz
        {
            get => serverTickRateHz;
            set
            {
                serverTickRateHz = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ServerTickRateDisplay));
                SaveConfig();
            }
        }

        public int DisconnectTimeoutSec
        {
            get => disconnectTimeoutSec;
            set
            {
                disconnectTimeoutSec = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BandwidthDisplay));
                SaveConfig();
            }
        }

        public int NetworkPollIntervalMs
        {
            get => networkPollIntervalMs;
            set
            {
                networkPollIntervalMs = value;
                OnPropertyChanged();
                SaveConfig();
            }
        }

        public int MaxBandwidthMbps
        {
            get => maxBandwidthMbps;
            set
            {
                maxBandwidthMbps = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BandwidthDisplay));
                SaveConfig();
            }
        }

        public string ServerTickRateDisplay => $"{serverTickRateHz} Hz ({(1000 / Math.Max(1, serverTickRateHz))} ms)";
        public string BandwidthDisplay => $"{(maxBandwidthMbps > 0 ? $"{maxBandwidthMbps} Mbps" : CalradiaConnected.Common.Localization.Loc.T("Sınırsız", "Unlimited"))} / {disconnectTimeoutSec}s";

        // Time control & persistent properties
        public string ServerTimeSpeed
        {
            get => serverTimeSpeed;
            set { serverTimeSpeed = value; OnPropertyChanged(); }
        }

        public double CampaignDay
        {
            get => campaignDay;
            set { campaignDay = value; OnPropertyChanged(); }
        }

        public int ConnectedPlayerCount
        {
            get => connectedPlayerCount;
            set { connectedPlayerCount = value; OnPropertyChanged(); }
        }

        public bool PersistentWorldTime
        {
            get => persistentWorldTime;
            set
            {
                persistentWorldTime = value;
                OnPropertyChanged();
                _ = serverControl.SetPersistentWorldTimeAsync(value);
                SaveConfig();
            }
        }

        // ==========================================
        // Donanım, RAM, VRAM & CPU Özellikleri
        // ==========================================
        public int MaxRamLimitGb
        {
            get => maxRamLimitGb;
            set { maxRamLimitGb = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int VramBudgetLevel
        {
            get => vramBudgetLevel;
            set { vramBudgetLevel = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int ServerMaxFps
        {
            get => serverMaxFps;
            set { serverMaxFps = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int CpuCoresAllocated
        {
            get => cpuCoresAllocated;
            set { cpuCoresAllocated = value; OnPropertyChanged(); SaveConfig(); }
        }

        public string ProcessPriority
        {
            get => processPriority;
            set { processPriority = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool AutoRamCleanEnabled
        {
            get => autoRamCleanEnabled;
            set { autoRamCleanEnabled = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool ServerDisableSound
        {
            get => serverDisableSound;
            set { serverDisableSound = value; OnPropertyChanged(); SaveConfig(); }
        }

        // ==========================================
        // Kalradya Oyun & Zorluk Ayarları
        // ==========================================
        public int BattleSize
        {
            get => battleSize;
            set { battleSize = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int DamageDifficulty
        {
            get => damageDifficulty;
            set { damageDifficulty = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int CombatAiDifficulty
        {
            get => combatAiDifficulty;
            set { combatAiDifficulty = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int HeroDeathInBattle
        {
            get => heroDeathInBattle;
            set { heroDeathInBattle = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int AutoSaveIntervalMinutes
        {
            get => autoSaveIntervalMinutes;
            set { autoSaveIntervalMinutes = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int NumberOfCorpses
        {
            get => numberOfCorpses;
            set { numberOfCorpses = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool PvpEnabled
        {
            get => pvpEnabled;
            set { pvpEnabled = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool ArenaContinuousPracticeRespawn
        {
            get => arenaContinuousPracticeRespawn;
            set { arenaContinuousPracticeRespawn = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool ArenaPvpDuelsEnabled
        {
            get => arenaPvpDuelsEnabled;
            set { arenaPvpDuelsEnabled = value; OnPropertyChanged(); SaveConfig(); }
        }

        // ==========================================
        // Lobi & Güvenlik
        // ==========================================
        public string ServerPassword
        {
            get => serverPassword;
            set { serverPassword = value; OnPropertyChanged(); SaveConfig(); }
        }

        public string ServerVisibility
        {
            get => serverVisibility;
            set { serverVisibility = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int MaxPlayers
        {
            get => maxPlayers;
            set { maxPlayers = value; OnPropertyChanged(); SaveConfig(); }
        }

        // ==========================================
        // Karakter Başlangıç & İlerleme Özellikleri
        // ==========================================
        public int StartingLevel
        {
            get => startingLevel;
            set { startingLevel = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingFocusPoints
        {
            get => startingFocusPoints;
            set { startingFocusPoints = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingAttributePoints
        {
            get => startingAttributePoints;
            set { startingAttributePoints = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingAllSkillsLevel
        {
            get => startingAllSkillsLevel;
            set { startingAllSkillsLevel = value; OnPropertyChanged(); SaveConfig(); }
        }

        public double CharacterXpMultiplier
        {
            get => characterXpMultiplier;
            set { characterXpMultiplier = value; OnPropertyChanged(); SaveConfig(); }
        }

        public double SkillXpMultiplier
        {
            get => skillXpMultiplier;
            set { skillXpMultiplier = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingGold
        {
            get => startingGold;
            set { startingGold = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingRenown
        {
            get => startingRenown;
            set { startingRenown = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingInfluence
        {
            get => startingInfluence;
            set { startingInfluence = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingEquipmentPreset
        {
            get => startingEquipmentPreset;
            set { startingEquipmentPreset = value; OnPropertyChanged(); SaveConfig(); }
        }

        public int StartingTroopsPreset
        {
            get => startingTroopsPreset;
            set { startingTroopsPreset = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool StartingFoodSupplies
        {
            get => startingFoodSupplies;
            set { startingFoodSupplies = value; OnPropertyChanged(); SaveConfig(); }
        }

        // Canlı Donanım İstatistikleri
        public string ServerLiveRamDisplay
        {
            get => serverLiveRamDisplay;
            set { if (serverLiveRamDisplay != value) { serverLiveRamDisplay = value; OnPropertyChanged(); } }
        }

        public string ServerLiveCpuDisplay
        {
            get => serverLiveCpuDisplay;
            set { if (serverLiveCpuDisplay != value) { serverLiveCpuDisplay = value; OnPropertyChanged(); } }
        }

        // ==========================================
        // ComboBox Seçenek Listeleri (TR / EN)
        // ==========================================
        public List<SettingOption<int>> RamOptions => new()
        {
            new SettingOption<int>(Loc.T("Sınırsız / Sistem Otomatik (Önerilen)", "Unlimited / System Auto (Recommended)"), 0),
            new SettingOption<int>(Loc.T("4 GB RAM Limiti (Düşük Donanım)", "4 GB RAM Limit (Low Spec)"), 4),
            new SettingOption<int>(Loc.T("6 GB RAM Limiti", "6 GB RAM Limit"), 6),
            new SettingOption<int>(Loc.T("8 GB RAM Limiti (Dengeli)", "8 GB RAM Limit (Balanced)"), 8),
            new SettingOption<int>(Loc.T("12 GB RAM Limiti (Geniş Sefer)", "12 GB RAM Limit (Large Campaign)"), 12),
            new SettingOption<int>(Loc.T("16 GB RAM Limiti (Yüksek Performans)", "16 GB RAM Limit (High Performance)"), 16),
            new SettingOption<int>(Loc.T("24 GB RAM Limiti", "24 GB RAM Limit"), 24),
            new SettingOption<int>(Loc.T("32 GB RAM Limiti (Maksimum)", "32 GB RAM Limit (Max)"), 32)
        };

        public List<SettingOption<int>> VramOptions => new()
        {
            new SettingOption<int>(Loc.T("Ultra Düşük (512 MB - 1 GB VRAM / Ekstra Tasarruf)", "Ultra Low (512 MB - 1 GB VRAM)"), 0),
            new SettingOption<int>(Loc.T("Düşük (1 GB - 2 GB VRAM)", "Low (1 GB - 2 GB VRAM)"), 1),
            new SettingOption<int>(Loc.T("Orta (2 GB - 4 GB VRAM - Önerilen)", "Medium (2 GB - 4 GB VRAM - Recommended)"), 2),
            new SettingOption<int>(Loc.T("Yüksek (4 GB - 6 GB VRAM)", "High (4 GB - 6 GB VRAM)"), 3),
            new SettingOption<int>(Loc.T("Maksimum (8+ GB VRAM / Tam Grafik)", "Max (8+ GB VRAM / Full Textures)"), 4)
        };

        public List<SettingOption<int>> FpsOptions => new()
        {
            new SettingOption<int>(Loc.T("20 FPS (Ultra Hafif / En Düşük PC Yükü - Önerilen)", "20 FPS (Ultra Light / Minimum PC Load - Recommended)"), 20),
            new SettingOption<int>(Loc.T("30 FPS (Maksimum Tasarruf / Düşük GPU & CPU)", "30 FPS (Power Saver / Low GPU & CPU)"), 30),
            new SettingOption<int>(Loc.T("60 FPS (Dengeli)", "60 FPS (Balanced)"), 60),
            new SettingOption<int>(Loc.T("120 FPS (Yüksek Tepki Süresi)", "120 FPS (High Responsiveness)"), 120),
            new SettingOption<int>(Loc.T("200 FPS (Sınırsız)", "200 FPS (Uncapped)"), 200)
        };

        public List<SettingOption<int>> CpuCoreOptions => new()
        {
            new SettingOption<int>(Loc.F("Tüm Çekirdekler (Otomatik - {0} Çekirdek)", "All Cores (Auto - {0} Cores)", Environment.ProcessorCount), 0),
            new SettingOption<int>(Loc.T("2 Çekirdek (Host oyuncunun oyunu için yer bırakır)", "2 Cores (Leave room for local client)"), 2),
            new SettingOption<int>(Loc.T("4 Çekirdek (Önerilen dengeli tahsis)", "4 Cores (Recommended balanced)"), 4),
            new SettingOption<int>(Loc.T("6 Çekirdek", "6 Cores"), 6),
            new SettingOption<int>(Loc.T("8 Çekirdek", "8 Cores"), 8)
        };

        public List<SettingOption<string>> PriorityOptions => new()
        {
            new SettingOption<string>(Loc.T("Normal (Standart Windows Önceliği)", "Normal (Standard Windows Priority)"), "Normal"),
            new SettingOption<string>(Loc.T("Yüksek Öncelik (AboveNormal - Lag Önleme)", "Above Normal (Reduce Stutter)"), "AboveNormal"),
            new SettingOption<string>(Loc.T("Çok Yüksek (High)", "High"), "High"),
            new SettingOption<string>(Loc.T("Düşük (BelowNormal - Arka Plan Tasarrufu)", "Below Normal (Background Saver)"), "BelowNormal")
        };

        public List<SettingOption<int>> BattleSizeOptions => new()
        {
            new SettingOption<int>(Loc.T("200 Asker (Maksimum FPS / Düşük Sistem)", "200 Troops (Max FPS / Low Spec)"), 200),
            new SettingOption<int>(Loc.T("300 Asker", "300 Troops"), 300),
            new SettingOption<int>(Loc.T("400 Asker (Dengeli - Önerilen)", "400 Troops (Balanced - Recommended)"), 400),
            new SettingOption<int>(Loc.T("500 Asker", "500 Troops"), 500),
            new SettingOption<int>(Loc.T("600 Asker (Büyük Muharebeler)", "600 Troops (Large Battles)"), 600),
            new SettingOption<int>(Loc.T("800 Asker (Epik Meydan Savaşları)", "800 Troops (Epic Battles)"), 800),
            new SettingOption<int>(Loc.T("1000 Asker (Maksimum Bannerlord Limiti)", "1000 Troops (Bannerlord Max)"), 1000)
        };

        public List<SettingOption<int>> DamageDifficultyOptions => new()
        {
            new SettingOption<int>(Loc.T("Kolay (%33 Hasar Alınır - Çok Kolay)", "Easy (33% Damage Taken)"), 0),
            new SettingOption<int>(Loc.T("Dengeli (%66 Hasar - Önerilen)", "Balanced (66% Damage - Recommended)"), 1),
            new SettingOption<int>(Loc.T("Gerçekçi (%100 Tam Hasar - Klasik Bannerlord)", "Realistic (100% Full Damage)"), 2)
        };

        public List<SettingOption<int>> CombatAiOptions => new()
        {
            new SettingOption<int>(Loc.T("Kolay (Acemi / Yavaş Refleks)", "Poor / Easy AI"), 0),
            new SettingOption<int>(Loc.T("Normal (Dengeli Dövüş - Önerilen)", "Good / Balanced AI (Recommended)"), 1),
            new SettingOption<int>(Loc.T("Usta / Kıdemli (Gerçekçi Blok & Saldırı)", "Veteran / Challenging AI"), 2)
        };

        public List<SettingOption<int>> HeroDeathOptions => new()
        {
            new SettingOption<int>(Loc.T("Kapalı (Kahramanlar Yalnızca Bayılır)", "Disabled (Heroes Knocked Unconscious Only)"), 0),
            new SettingOption<int>(Loc.T("Sadece Yoldaşlar Ölebilir", "Companions Only Can Die"), 1),
            new SettingOption<int>(Loc.T("Herkes İçin Açık (Gerçekçi Ölüm & Miras)", "Enabled for All Heroes (Realistic Death)"), 2)
        };

        public List<SettingOption<int>> AutoSaveOptions => new()
        {
            new SettingOption<int>(Loc.T("5 Dakika", "5 Minutes"), 5),
            new SettingOption<int>(Loc.T("10 Dakika", "10 Minutes"), 10),
            new SettingOption<int>(Loc.T("15 Dakika (Önerilen)", "15 Minutes (Recommended)"), 15),
            new SettingOption<int>(Loc.T("30 Dakika", "30 Minutes"), 30),
            new SettingOption<int>(Loc.T("60 Dakika", "60 Minutes"), 60),
            new SettingOption<int>(Loc.T("Kapalı", "Disabled"), 0)
        };

        public List<SettingOption<int>> CorpseOptions => new()
        {
            new SettingOption<int>(Loc.T("0 Ceset (Sıfır Ragdoll - Maksimum FPS)", "0 Corpses (No Ragdolls - Max FPS)"), 0),
            new SettingOption<int>(Loc.T("5 Ceset", "5 Corpses"), 5),
            new SettingOption<int>(Loc.T("20 Ceset (Dengeli - Önerilen)", "20 Corpses (Balanced - Recommended)"), 20),
            new SettingOption<int>(Loc.T("50 Ceset", "50 Corpses"), 50),
            new SettingOption<int>(Loc.T("100 Ceset (Gerçekçi Savaş Alanı)", "100 Corpses"), 100),
            new SettingOption<int>(Loc.T("200 Ceset (Maksimum)", "200 Corpses (Max)"), 200)
        };

        public List<SettingOption<int>> MaxPlayersOptions => new()
        {
            new SettingOption<int>(Loc.T("2 Oyuncu (İkili)", "2 Players (Duo)"), 2),
            new SettingOption<int>(Loc.T("4 Oyuncu (Küçük Grup)", "4 Players (Small Group)"), 4),
            new SettingOption<int>(Loc.T("8 Oyuncu (Standart)", "8 Players (Standard)"), 8),
            new SettingOption<int>(Loc.T("16 Oyuncu (Geniş Sunucu - Önerilen)", "16 Players (Recommended)"), 16),
            new SettingOption<int>(Loc.T("24 Oyuncu", "24 Players"), 24),
            new SettingOption<int>(Loc.T("32 Oyuncu (Büyük Klan Seferi)", "32 Players (Large Campaign)"), 32),
            new SettingOption<int>(Loc.T("64 Oyuncu (Maksimum)", "64 Players (Max)"), 64)
        };

        public List<SettingOption<string>> VisibilityOptions => new()
        {
            new SettingOption<string>(Loc.T("Genel (Public - Herkese Açık Lobi)", "Public (Visible in Lobby)"), "Public"),
            new SettingOption<string>(Loc.T("Yerel Ağ (LAN - Yalnızca Ev/Hamachi/Radmin)", "LAN Only (Local Network)"), "LAN"),
            new SettingOption<string>(Loc.T("Gizli (Doğrudan IP / Gizli Sunucu)", "Private (Direct IP Only)"), "None")
        };

        // Karakter & İlerleme ComboBox Listeleri
        public List<SettingOption<int>> StartingLevelOptions => new()
        {
            new SettingOption<int>(Loc.T("Seviye 1 (Standart Başlangıç)", "Level 1 (Standard Start)"), 1),
            new SettingOption<int>(Loc.T("Seviye 5 (Acemi Savaşçı)", "Level 5 (Seasoned Fighter)"), 5),
            new SettingOption<int>(Loc.T("Seviye 10 (Deneyimli Savaşçı)", "Level 10 (Veteran Warrior)"), 10),
            new SettingOption<int>(Loc.T("Seviye 15 (Kıdemli Komutan)", "Level 15 (Commander)"), 15),
            new SettingOption<int>(Loc.T("Seviye 20 (Usta Lord)", "Level 20 (Master Lord)"), 20),
            new SettingOption<int>(Loc.T("Seviye 25 (Efsanevi Lider)", "Level 25 (Legendary Leader)"), 25),
            new SettingOption<int>(Loc.T("Seviye 30 (Zirve Hükümdar)", "Level 30 (Grand Sovereign)"), 30)
        };

        public List<SettingOption<int>> FocusPointsOptions => new()
        {
            new SettingOption<int>(Loc.T("0 Puan", "0 Points"), 0),
            new SettingOption<int>(Loc.T("5 Puan (Önerilen)", "5 Points (Recommended)"), 5),
            new SettingOption<int>(Loc.T("10 Puan", "10 Points"), 10),
            new SettingOption<int>(Loc.T("15 Puan", "15 Points"), 15),
            new SettingOption<int>(Loc.T("20 Puan (Hızlı Odaklanma)", "20 Points"), 20),
            new SettingOption<int>(Loc.T("30 Puan", "30 Points"), 30),
            new SettingOption<int>(Loc.T("50 Puan (Süper Statü)", "50 Points"), 50)
        };

        public List<SettingOption<int>> AttributePointsOptions => new()
        {
            new SettingOption<int>(Loc.T("0 Puan", "0 Points"), 0),
            new SettingOption<int>(Loc.T("2 Puan (Standart)", "2 Points (Standard)"), 2),
            new SettingOption<int>(Loc.T("4 Puan", "4 Points"), 4),
            new SettingOption<int>(Loc.T("6 Puan", "6 Points"), 6),
            new SettingOption<int>(Loc.T("8 Puan (Güçlü Başlangıç)", "8 Points (Strong Start)"), 8),
            new SettingOption<int>(Loc.T("12 Puan", "12 Points"), 12),
            new SettingOption<int>(Loc.T("20 Puan (Zirve Nitelik)", "20 Points (Max)"), 20)
        };

        public List<SettingOption<int>> AllSkillsLevelOptions => new()
        {
            new SettingOption<int>(Loc.T("0 (Karakter Geçmişine Bağlı Vanilla)", "0 (Vanilla Character Background)"), 0),
            new SettingOption<int>(Loc.T("25 (Temel Taban - Tüm Yetenekler Min 25)", "25 (All Skills Min 25)"), 25),
            new SettingOption<int>(Loc.T("50 (Usta Tabanı - Tüm Yetenekler Min 50)", "50 (All Skills Min 50)"), 50),
            new SettingOption<int>(Loc.T("100 (Kıdemli Tabanı - Tüm Yetenekler Min 100)", "100 (All Skills Min 100)"), 100),
            new SettingOption<int>(Loc.T("150 (Elit Savaşçı - Tüm Yetenekler Min 150)", "150 (All Skills Min 150)"), 150)
        };

        public List<SettingOption<double>> CharacterXpOptions => new()
        {
            new SettingOption<double>(Loc.T("1.0x (Standart Kalradya Hızı)", "1.0x (Standard Speed)"), 1.0),
            new SettingOption<double>(Loc.T("1.5x (%50 Daha Hızlı)", "1.5x (+50% Faster)"), 1.5),
            new SettingOption<double>(Loc.T("2.0x (2 Kat Hızlı Seviye Atlama)", "2.0x (2x Level Speed)"), 2.0),
            new SettingOption<double>(Loc.T("3.0x (3 Kat Hızlı Sefer)", "3.0x (3x Campaign)"), 3.0),
            new SettingOption<double>(Loc.T("5.0x (5 Kat Hızlı Aksiyon)", "5.0x (5x Fast)"), 5.0),
            new SettingOption<double>(Loc.T("10.0x (10 Kat Hızlı Gelişim)", "10.0x (10x Rapid)"), 10.0)
        };

        public List<SettingOption<double>> SkillXpOptions => new()
        {
            new SettingOption<double>(Loc.T("1.0x (Standart Yetenek Gelişimi)", "1.0x (Standard Skill Gain)"), 1.0),
            new SettingOption<double>(Loc.T("1.5x (%50 Daha Hızlı Yetenek Artışı)", "1.5x (+50% Skill Gain)"), 1.5),
            new SettingOption<double>(Loc.T("2.0x (2 Kat Hızlı Silah/Zanaat)", "2.0x (2x Skill Gain)"), 2.0),
            new SettingOption<double>(Loc.T("3.0x (3 Kat Hızlı Ustalık)", "3.0x (3x Mastery)"), 3.0),
            new SettingOption<double>(Loc.T("5.0x (5 Kat Hızlı)", "5.0x (5x Skill Gain)"), 5.0),
            new SettingOption<double>(Loc.T("10.0x (10 Kat Hızlı - Anında Ustalık)", "10.0x (10x Instant Mastery)"), 10.0)
        };

        public List<SettingOption<int>> StartingGoldOptions => new()
        {
            new SettingOption<int>(Loc.T("1.000 Dinar (Standart Başlangıç)", "1,000 Denars (Standard)"), 1000),
            new SettingOption<int>(Loc.T("5.000 Dinar (Mütevazı Başlangıç)", "5,000 Denars (Modest)"), 5000),
            new SettingOption<int>(Loc.T("10.000 Dinar (Dengeli Başlangıç)", "10,000 Denars (Balanced)"), 10000),
            new SettingOption<int>(Loc.T("25.000 Dinar (Rahat Başlangıç)", "25,000 Denars (Comfortable)"), 25000),
            new SettingOption<int>(Loc.T("50.000 Dinar (Zengin Lord)", "50,000 Denars (Wealthy Lord)"), 50000),
            new SettingOption<int>(Loc.T("100.000 Dinar (Hazine Sahibi)", "100,000 Denars (Treasury)"), 100000),
            new SettingOption<int>(Loc.T("500.000 Dinar (Krallık Kurucusu)", "500,000 Denars (Kingdom Founder)"), 500000)
        };

        public List<SettingOption<int>> StartingRenownOptions => new()
        {
            new SettingOption<int>(Loc.T("0 Ün (Klan Seviyesi 0 - Yalnız Gezgin)", "0 Renown (Clan Tier 0)"), 0),
            new SettingOption<int>(Loc.T("50 Ün (Klan Seviyesi 1 - Paralı Asker Olabilme)", "50 Renown (Clan Tier 1 - Mercenary)"), 50),
            new SettingOption<int>(Loc.T("150 Ün (Klan Seviyesi 2 - Krallığa Vassal Olabilme)", "150 Renown (Clan Tier 2 - Vassal)"), 150),
            new SettingOption<int>(Loc.T("350 Ün (Klan Seviyesi 3 - Krallık Kurabilme)", "350 Renown (Clan Tier 3 - Kingdom Ready)"), 350),
            new SettingOption<int>(Loc.T("900 Ün (Klan Seviyesi 4 - Saygın Büyük Hanedan)", "900 Renown (Clan Tier 4 - Great House)"), 900)
        };

        public List<SettingOption<int>> StartingInfluenceOptions => new()
        {
            new SettingOption<int>(Loc.T("0 Nüfuz", "0 Influence"), 0),
            new SettingOption<int>(Loc.T("50 Nüfuz (Temel Konsey Oyu)", "50 Influence (Council Vote)"), 50),
            new SettingOption<int>(Loc.T("100 Nüfuz (Yasa ve Tımar Talebi)", "100 Influence (Policy & Fiefs)"), 100),
            new SettingOption<int>(Loc.T("250 Nüfuz (Ordu Toplama Gücü)", "250 Influence (Raise Army)"), 250),
            new SettingOption<int>(Loc.T("500 Nüfuz (Yüksek Krallık Nüfuzu)", "500 Influence (High Authority)"), 500)
        };

        public List<SettingOption<int>> EquipmentPresetOptions => new()
        {
            new SettingOption<int>(Loc.T("Standart Vanilla Ekipman (Karakter Geçmişi)", "Standard Vanilla Equipment"), 0),
            new SettingOption<int>(Loc.T("Acemi Savaşçı Paketi (Tier 2-3 Kılıç, Kalkan, Yay, Zırh, At & Eyer)", "Recruit Kit (Tier 2-3 Sword, Shield, Bow, Armor, Horse)"), 1),
            new SettingOption<int>(Loc.T("Şövalye / Soylu Paketi (Tier 4-5 Çelik Levha Zırh, Şampiyon Kılıcı, Savaş Atı, Kargı)", "Knight / Noble Kit (Tier 4-5 Armor, Warhorse, Lance)"), 2),
            new SettingOption<int>(Loc.T("Elit Hükümdar Paketi (Tier 6 Tam Levha Zırh, Asil Yay, Efsanevi Kılıç, Safkan Savaş Atı)", "Royal Elite Kit (Tier 6 Full Plate, Noble Bow & Destrier)"), 3),
            new SettingOption<int>(Loc.T("Zengin Tüccar Paketi (Zırh, 16x Katır/Yük Beygiri, Yağ, Keten, Şarap, Bira, 50x Tahıl)", "Merchant Caravan Kit (16 Mules & Trade Goods)"), 4)
        };

        public List<SettingOption<int>> TroopsPresetOptions => new()
        {
            new SettingOption<int>(Loc.T("Yalnız Başla (0 Asker)", "Solo Start (0 Troops)"), 0),
            new SettingOption<int>(Loc.T("Küçük Muhafız Birliği (10 Kültür Askeri)", "Small Retinue (10 Culture Troops)"), 1),
            new SettingOption<int>(Loc.T("Orta Birlik (25 Asker: 20 Kültür Askeri, 5 Asil Süvari)", "Balanced Warband (20 Troops + 5 Noble Cavalry)"), 2),
            new SettingOption<int>(Loc.T("Güçlü Ordu Çekirdeği (50 Asker: 35 Kültür Askeri, 15 Elit Muhafız)", "Army Core (35 Troops + 15 Elite Guard)"), 3)
        };

        // User Management bindings
        public string UserSearchText
        {
            get => userSearchText;
            set
            {
                userSearchText = value;
                OnPropertyChanged();
                ApplyUserFilter();
            }
        }

        public bool AllowAutoRegistration
        {
            get => UserDatabase.Instance.AllowAutoRegistration;
            set
            {
                UserDatabase.Instance.AllowAutoRegistration = value;
                OnPropertyChanged();
                if (IsServerRunning || IsServerConnected)
                {
                    _ = serverControl.ReloadUsersAsync();
                }
                AddLog($"[Kullanıcı DB] Otomatik kayıt durumu: {(value ? "AÇIK" : "KAPALI")}");
            }
        }

        public string NewUsername
        {
            get => newUsername;
            set { newUsername = value; OnPropertyChanged(); }
        }

        public string NewPassword
        {
            get => newPassword;
            set { newPassword = value; OnPropertyChanged(); }
        }

        public bool NewIsAdmin
        {
            get => newIsAdmin;
            set { newIsAdmin = value; OnPropertyChanged(); }
        }

        public UserAccount? SelectedUser
        {
            get => selectedUser;
            set { selectedUser = value; OnPropertyChanged(); }
        }

        public string ResetPasswordText
        {
            get => resetPasswordText;
            set { resetPasswordText = value; OnPropertyChanged(); }
        }

        // Client Connect & Sync properties
        public string ConnectServerIp
        {
            get => connectServerIp;
            set { connectServerIp = value; OnPropertyChanged(); }
        }

        public int ConnectFastDlPort
        {
            get => connectFastDlPort;
            set { connectFastDlPort = value; OnPropertyChanged(); }
        }

        public int ConnectGamePort
        {
            get => connectGamePort;
            set { connectGamePort = value; OnPropertyChanged(); }
        }

        public string ClientSyncStatus
        {
            get => clientSyncStatus;
            set { clientSyncStatus = value; OnPropertyChanged(); }
        }

        public double ClientSyncProgress
        {
            get => clientSyncProgress;
            set { clientSyncProgress = value; OnPropertyChanged(); }
        }

        public bool IsClientSyncing
        {
            get => isClientSyncing;
            set
            {
                isClientSyncing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSyncMods));
                OnPropertyChanged(nameof(CanLaunchClient));
            }
        }

        // Admin Tool Properties
        private PlayerInfoModel? selectedPlayer;
        public PlayerInfoModel? SelectedPlayer
        {
            get => selectedPlayer;
            set
            {
                selectedPlayer = value;
                OnPropertyChanged();
                if (value != null && !string.IsNullOrWhiteSpace(value.Username))
                {
                    TargetPlayerName = value.Username;
                }
            }
        }

        private string targetPlayerName = "all";
        public string TargetPlayerName
        {
            get => targetPlayerName;
            set { targetPlayerName = value; OnPropertyChanged(); }
        }

        private int cheatGoldAmount = 50000;
        public int CheatGoldAmount
        {
            get => cheatGoldAmount;
            set { cheatGoldAmount = value; OnPropertyChanged(); }
        }

        private float cheatRenownAmount = 100;
        public float CheatRenownAmount
        {
            get => cheatRenownAmount;
            set { cheatRenownAmount = value; OnPropertyChanged(); }
        }

        private float cheatInfluenceAmount = 100;
        public float CheatInfluenceAmount
        {
            get => cheatInfluenceAmount;
            set { cheatInfluenceAmount = value; OnPropertyChanged(); }
        }

        private int cheatTroopCount = 25;
        public int CheatTroopCount
        {
            get => cheatTroopCount;
            set { cheatTroopCount = value; OnPropertyChanged(); }
        }

        private TroopPresetModel? selectedTroop;
        public TroopPresetModel? SelectedTroop
        {
            get => selectedTroop;
            set { selectedTroop = value; OnPropertyChanged(); }
        }

        private string customTroopId = string.Empty;
        public string CustomTroopId
        {
            get => customTroopId;
            set { customTroopId = value; OnPropertyChanged(); }
        }

        public bool CanSyncMods => !IsClientSyncing && !string.IsNullOrWhiteSpace(ConnectServerIp);
        public bool CanLaunchClient => !IsClientSyncing && ClientMissingModules.Count == 0 && !string.IsNullOrWhiteSpace(BannerlordPath);

        private bool isUpdatingMod;
        public bool IsUpdatingMod
        {
            get => isUpdatingMod;
            set
            {
                if (isUpdatingMod != value)
                {
                    isUpdatingMod = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UpdateButtonText));
                }
            }
        }

        private string? availableUpdateTag;
        public string? AvailableUpdateTag
        {
            get => availableUpdateTag;
            set
            {
                if (availableUpdateTag != value)
                {
                    availableUpdateTag = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasPendingUpdate));
                    OnPropertyChanged(nameof(UpdateButtonText));
                    OnPropertyChanged(nameof(UpdateButtonBackground));
                    OnPropertyChanged(nameof(UpdateButtonBorder));
                }
            }
        }

        public bool HasPendingUpdate => !string.IsNullOrWhiteSpace(availableUpdateTag);
        public string AppVersionDisplay => GitHubUpdateService.GetCurrentVersionDisplay();
        public string UpdateButtonText => IsUpdatingMod
            ? Lang["UpdatingModBtn"]
            : (HasPendingUpdate ? $"{Lang["UpdateModBtn"]} ({availableUpdateTag})" : Lang["UpdateModBtn"]);
        public string UpdateButtonBackground => HasPendingUpdate ? "#9A3412" : "#1B4332";
        public string UpdateButtonBorder => HasPendingUpdate ? "#F4D58D" : "#40916C";

        // Server Commands
        public ICommand ToggleLanguageCommand { get; }
        public ICommand UpdateModCommand { get; }
        public ICommand StartServerCommand { get; }
        public ICommand PlayOnThisPcCommand { get; }
        public ICommand StopServerCommand { get; }
        public ICommand RefreshModulesCommand { get; }
        public ICommand SelectAllCustomCommand { get; }
        public ICommand DeselectAllCustomCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand CopyLogsCommand { get; }
        public ICommand OpenModulesFolderCommand { get; }
        public ICommand BrowseBannerlordFolderCommand { get; }
        public ICommand ApplyNetworkSettingsCommand { get; }
        public ICommand CopyIpCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand ResetDefaultSettingsCommand { get; }
        public ICommand ResetAppliedProgressionCommand { get; }

        // Time Control Commands
        public ICommand SetTimePauseCommand { get; }
        public ICommand SetTime1xCommand { get; }
        public ICommand SetTime2xCommand { get; }
        public ICommand SetTime3xCommand { get; }
        public ICommand SaveWorldCommand { get; }

        // User Management Commands
        public ICommand AddUserCommand { get; }
        public ICommand ResetUserPasswordCommand { get; }
        public ICommand ToggleBanCommand { get; }
        public ICommand DeleteUserCommand { get; }
        public ICommand RefreshUsersCommand { get; }

        // Client Commands
        public ICommand CheckServerModsCommand { get; }
        public ICommand DownloadMissingModsCommand { get; }
        public ICommand LaunchClientCommand { get; }

        // Admin Commands
        public ICommand GiveGoldPresetCommand { get; }
        public ICommand GiveGoldCustomCommand { get; }
        public ICommand GiveRenownPresetCommand { get; }
        public ICommand GiveRenownCustomCommand { get; }
        public ICommand GiveInfluencePresetCommand { get; }
        public ICommand GiveInfluenceCustomCommand { get; }
        public ICommand GiveTroopsCommand { get; }
        public ICommand DuplicateTroopsCommand { get; }
        public ICommand HealHeroCommand { get; }
        public ICommand HealPartyCommand { get; }
        public ICommand ToggleAdminCommand { get; }
        public ICommand RefreshPlayersCommand { get; }

        public MainViewModel()
        {
            moduleScanner = new ModuleScannerService();
            saveService = new SaveGameService();
            packagerService = new ModPackagerService();
            fastDlServer = new FastDlServerService();
            serverRunner = new ServerRunnerService();
            clientSync = new ClientSyncService();
            serverControl = new ServerControlIpcService();
            githubUpdater = new GitHubUpdateService();

            fastDlServer.OnLog += msg => AddLog(msg);
            serverRunner.OnLog += msg => AddLog(msg);
            serverRunner.OnStateChanged += running =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsServerRunning = running;
                    StatusMessage = running
                        ? Loc.F("Sunucu aktif (Port: {0}).", "Server running (Port: {0}).", ServerPort)
                        : Loc.T("Sunucu durduruldu.", "Server stopped.");
                    if (!running)
                    {
                        ServerTimeSpeed = Loc.T("Sunucu Kapalı", "Server Offline");
                    }
                });
            };

            ToggleLanguageCommand = new RelayCommand(() =>
            {
                languageManuallySet = true;
                ServerManagerLoc.Instance.ToggleLanguage();
                SyncSelectedLanguageFromLoc();
                NotifyLanguageChanged();
                SaveConfig();
            });
            UpdateModCommand = new RelayCommand(async () => await UpdateModAsync(), () => !IsUpdatingMod);

            // Server commands
            StartServerCommand = new RelayCommand(async () => await StartServerAsync(), () => CanStartServer);
            PlayOnThisPcCommand = new RelayCommand(PlayOnThisPc);
            StopServerCommand = new RelayCommand(async () => await StopServerAsync(), () => CanStopServer);
            RefreshModulesCommand = new RelayCommand(ReloadModules);
            SelectAllCustomCommand = new RelayCommand(SelectAllCustom);
            DeselectAllCustomCommand = new RelayCommand(DeselectAllCustom);
            ClearLogsCommand = new RelayCommand(() =>
            {
                LogEntries.Clear();
                ConsoleText = string.Empty;
                AddLog("[Konsol] Log kayıtları temizlendi.");
            });
            CopyLogsCommand = new RelayCommand(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(ConsoleText))
                    {
                        Clipboard.SetText(ConsoleText);
                        AddLog("[Konsol] Tüm konsol çıktısı panoya kopyalandı.");
                    }
                }
                catch { }
            });
            OpenModulesFolderCommand = new RelayCommand(OpenModulesFolder);
            BrowseBannerlordFolderCommand = new RelayCommand(BrowseBannerlordFolder);
            ApplyNetworkSettingsCommand = new RelayCommand(async () => await ApplyNetworkSettingsAsync());
            CopyIpCommand = new RelayCommand(() =>
            {
                try
                {
                    Clipboard.SetText($"{GetPrimaryLocalIp()}:{ServerPort}");
                    AddLog($"[Ağ Bilgisi] Doğrudan Bağlantı IP adresi panoya kopyalandı: {GetPrimaryLocalIp()}:{ServerPort}");
                }
                catch { }
            });
            SaveSettingsCommand = new RelayCommand(async () =>
            {
                SaveConfig();
                var cfg = CreateCurrentServerConfig();
                serverRunner.ApplyConfigurationFiles(cfg);
                serverRunner.ApplyLiveProcessSettings(cfg);
                bool pushedLive = false;
                if (IsServerRunning || IsServerConnected)
                {
                    pushedLive = await serverControl.ReloadConfigAsync();
                }
                AddLog(pushedLive
                    ? Loc.F("[Ayarlar] Tüm ayarlar kaydedildi ve çalışan sunucuya + bağlı oyunculara anında aktarıldı (Seviye {0}, Altın {1:N0}, Savaş {2}, PvP: {3}).",
                            "[Settings] All settings saved and applied live to the running server & connected players (Level {0}, Gold {1:N0}, Battle {2}, PvP: {3}).",
                            StartingLevel, StartingGold, BattleSize, PvpEnabled ? "ON" : "OFF")
                    : Loc.F("[Ayarlar] Tüm sunucu, donanım ve Kalradya oyun ayarları kaydedildi (Seviye {0}, Altın {1:N0}).",
                            "[Settings] All server, hardware and Calradia game settings saved (Level {0}, Gold {1:N0}).",
                            StartingLevel, StartingGold));
                StatusMessage = Loc.T("Sunucu ve oyun ayarları kaydedildi ve uygulandı.", "Server and game settings saved and applied.");
            });
            ResetAppliedProgressionCommand = new RelayCommand(async () =>
            {
                try
                {
                    SaveConfig();
                    ServerRunnerService.ClearAppliedStarterRecords();
                    bool pushedLive = false;
                    if (IsServerRunning || IsServerConnected)
                    {
                        pushedLive = await serverControl.ResetProgressionAsync();
                    }

                    AddLog(pushedLive
                        ? Loc.T("[Başlangıç & İlerleme] Başlangıç geçmişi sıfırlandı ve çalışan sunucudaki oyunculara yeniden uygulandı.",
                                "[Progression] Starter history reset and reapplied to players on the running server.")
                        : Loc.T("[Başlangıç & İlerleme] Başlangıç geçmişi sıfırlandı. Yeni veya mevcut karakterler güncel başlangıç paketini alacaktır.",
                                "[Progression] Starter history reset. Characters will receive the updated starting package."));
                    StatusMessage = Loc.T("Başlangıç ilerleme geçmişi sıfırlandı.", "Starter progression history reset.");
                }
                catch (Exception ex)
                {
                    AddLog($"[Hata] Sıfırlama başarısız: {ex.Message}");
                }
            });
            ResetDefaultSettingsCommand = new RelayCommand(async () =>
            {
                MaxRamLimitGb = 0;
                VramBudgetLevel = 0;
                ServerMaxFps = 20;
                CpuCoresAllocated = 0;
                ProcessPriority = "Normal";
                AutoRamCleanEnabled = true;
                ServerDisableSound = true;

                BattleSize = 400;
                DamageDifficulty = 1;
                CombatAiDifficulty = 1;
                HeroDeathInBattle = 0;
                AutoSaveIntervalMinutes = 15;
                NumberOfCorpses = 20;
                PvpEnabled = true;
                ArenaContinuousPracticeRespawn = true;
                ArenaPvpDuelsEnabled = true;

                MaxPlayers = 16;
                ServerVisibility = "Public";

                StartingLevel = 1;
                StartingFocusPoints = 5;
                StartingAttributePoints = 2;
                StartingAllSkillsLevel = 0;
                CharacterXpMultiplier = 1.0;
                SkillXpMultiplier = 1.0;
                StartingGold = 1000;
                StartingRenown = 0;
                StartingInfluence = 0;
                StartingEquipmentPreset = 0;
                StartingTroopsPreset = 0;
                StartingFoodSupplies = true;

                SaveConfig();
                var cfg = CreateCurrentServerConfig();
                serverRunner.ApplyConfigurationFiles(cfg);
                serverRunner.ApplyLiveProcessSettings(cfg);
                if (IsServerRunning || IsServerConnected)
                {
                    await serverControl.ReloadConfigAsync();
                }
                AddLog(Loc.T("[Ayarlar] Tüm ayarlar önerilen dengeli varsayılanlara sıfırlandı.", "[Settings] All settings reset to recommended balanced defaults."));
                StatusMessage = Loc.T("Ayarlar varsayılanlara sıfırlandı.", "Settings reset to defaults.");
            });

            // Time Control commands
            SetTimePauseCommand = new RelayCommand(async () => await SetTimeSpeedAsync(0), () => IsServerRunning || IsServerConnected);
            SetTime1xCommand = new RelayCommand(async () => await SetTimeSpeedAsync(1), () => IsServerRunning || IsServerConnected);
            SetTime2xCommand = new RelayCommand(async () => await SetTimeSpeedAsync(2), () => IsServerRunning || IsServerConnected);
            SetTime3xCommand = new RelayCommand(async () => await SetTimeSpeedAsync(3), () => IsServerRunning || IsServerConnected);
            SaveWorldCommand = new RelayCommand(async () => await SaveWorldAsync(), () => IsServerRunning || IsServerConnected);

            // User Management commands
            AddUserCommand = new RelayCommand(AddUser);
            ResetUserPasswordCommand = new RelayCommand(ResetUserPassword);
            ToggleBanCommand = new RelayCommand(ToggleBan);
            DeleteUserCommand = new RelayCommand(DeleteUser);
            RefreshUsersCommand = new RelayCommand(ReloadUsers);

            // Client commands
            CheckServerModsCommand = new RelayCommand(async () => await CheckServerModsAsync(), () => CanSyncMods);
            DownloadMissingModsCommand = new RelayCommand(async () => await DownloadMissingModsAsync(), () => CanSyncMods);
            LaunchClientCommand = new RelayCommand(LaunchClientAndConnect, () => CanLaunchClient);

            // Populate localized elite Bannerlord troop list
            RefreshAvailableTroops();

            // Admin Commands
            GiveGoldPresetCommand = new RelayCommand<string>(async p => await GiveGoldAsync(int.TryParse(p, out var a) ? a : 50000));
            GiveGoldCustomCommand = new RelayCommand(async () => await GiveGoldAsync(CheatGoldAmount));
            GiveRenownPresetCommand = new RelayCommand<string>(async p => await GiveRenownAsync(float.TryParse(p, out var a) ? a : 100));
            GiveRenownCustomCommand = new RelayCommand(async () => await GiveRenownAsync(CheatRenownAmount));
            GiveInfluencePresetCommand = new RelayCommand<string>(async p => await GiveInfluenceAsync(float.TryParse(p, out var a) ? a : 100));
            GiveInfluenceCustomCommand = new RelayCommand(async () => await GiveInfluenceAsync(CheatInfluenceAmount));
            GiveTroopsCommand = new RelayCommand(async () => await GiveTroopsAsync());
            DuplicateTroopsCommand = new RelayCommand(async () => await DuplicateTroopsAsync());
            HealHeroCommand = new RelayCommand(async () => await HealHeroAsync());
            HealPartyCommand = new RelayCommand(async () => await HealPartyAsync());
            ToggleAdminCommand = new RelayCommand(async () => await ToggleAdminAsync());
            RefreshPlayersCommand = new RelayCommand(async () => await RefreshOnlinePlayersAsync());

            // Polling timer for live server status
            statusPollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            statusPollTimer.Tick += async (s, e) => await PollServerStatusAsync();
            statusPollTimer.Start();

            logFlushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            logFlushTimer.Tick += (s, e) => FlushPendingLogs();
            logFlushTimer.Start();

            // Always start in the user's Windows OS language unless they previously chose a manual override
            isInitializing = true;
            Loc.SyncFromSystemCulture();
            foreach (var langOpt in FlagIconHelper.BuildLanguageOptions())
            {
                AvailableLanguages.Add(langOpt);
            }
            SyncSelectedLanguageFromLoc();

            // Load saved configuration or auto-detect
            LoadConfig();
            SyncSelectedLanguageFromLoc();
            ServerManagerLoc.Instance.NotifyAll();
            NotifyLanguageChanged();

            if (string.IsNullOrWhiteSpace(BannerlordPath))
            {
                var detectedDir = moduleScanner.DetectBannerlordDirectory();
                BannerlordPath = detectedDir;
            }

            isInitializing = false;
            LoadSaves();
            ReloadUsers();
            _ = RefreshOnlinePlayersAsync();
            GitHubUpdateService.CleanupPreviousUpdateBackups();
            AddLog(Loc.F(
                "Calradia Connected Sunucu Yöneticisi hazır. Bağlantı IP: {0}:{1}",
                "Calradia Connected Server Manager ready. Connect IP: {0}:{1}",
                GetPrimaryLocalIp(), ServerPort));
            _ = CheckForUpdatesOnStartupAsync();
        }

        private async Task ApplyNetworkSettingsAsync()
        {
            var success = await serverControl.SetNetworkSettingsAsync(ServerTickRateHz, DisconnectTimeoutSec, NetworkPollIntervalMs);
            SaveConfig();

            var bwDesc = MaxBandwidthMbps > 0 ? $"{MaxBandwidthMbps} Mbps" : "Sınırsız";
            if (success)
            {
                AddLog($"[Ağ Performansı] Çalışan sunucuya anında uygulandı -> Hız: {ServerTickRateHz} Hz ({(1000 / Math.Max(1, ServerTickRateHz))} ms), Zaman Aşımı: {DisconnectTimeoutSec} sn, Bant Limiti: {bwDesc}");
            }
            else
            {
                AddLog($"[Ağ Performansı] Ayarlar yapılandırmaya kaydedildi -> Hız: {ServerTickRateHz} Hz, Zaman Aşımı: {DisconnectTimeoutSec} sn, Bant Limiti: {bwDesc}. (Sunucu başlatıldığında devreye girecektir)");
            }
        }

        private async Task SetTimeSpeedAsync(int mode)
        {
            var success = await serverControl.SetTimeSpeedAsync(mode);
            if (success)
            {
                var modeName = mode switch
                {
                    0 => Loc.T("Duraklatıldı (0x)", "Paused (0x)"),
                    1 => Loc.T("Normal Hız (1.0x)", "Normal (1.0x)"),
                    2 => Loc.T("Hızlı (2.0x)", "Fast (2.0x)"),
                    3 => Loc.T("Çok Hızlı (3.0x)", "Very Fast (3.0x)"),
                    _ => $"{mode}x"
                };
                AddLog($"[Zaman Kontrolü] Sunucu dünya zamanı ayarlandı: {modeName}");
                ServerTimeSpeed = modeName;
            }
            else
            {
                var errDetail = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : "";
                AddLog($"[Zaman Kontrolü] Zaman komutu sunucuya iletilemedi. Sunucu henüz hazır olmayabilir.{errDetail}");
            }
        }

        private async Task SaveWorldAsync()
        {
            var success = await serverControl.SaveGameAsync();
            if (success)
            {
                AddLog("[Dünya Kaydı] Sunucu dünya durumu kaydedildi.");
            }
            else
            {
                AddLog("[Dünya Kaydı] Kaydetme komutu iletilemedi.");
            }
        }

        private bool isPollingStatus;
        private async Task PollServerStatusAsync()
        {
            if (isPollingStatus) return;
            isPollingStatus = true;

            try
            {
                var isOnline = await Task.Run(async () => await serverControl.PingAsync(1000));
                if (isOnline != IsServerConnected)
                {
                    IsServerConnected = isOnline;
                }

                if (!IsServerRunning && !isOnline) return;

                var status = await Task.Run(async () => await serverControl.GetServerStatusAsync());
                if (status != null)
                {
                    ServerTimeSpeed = status.TimeMode switch
                    {
                        "Durduruldu" => Loc.T("Duraklatıldı (0x)", "Paused (0x)"),
                        "Hızlı" => Loc.T("Hızlı (2.0x)", "Fast (2.0x)"),
                        "Normal" => Loc.T("Normal Hız (1.0x)", "Normal (1.0x)"),
                        _ => status.TimeMode
                    };
                    CampaignDay = status.Day;
                    ConnectedPlayerCount = status.PlayersCount;
                    if (status.TickRate > 0 && status.TickRate != ServerTickRateHz)
                    {
                        serverTickRateHz = status.TickRate;
                        OnPropertyChanged(nameof(ServerTickRateHz));
                        OnPropertyChanged(nameof(ServerTickRateDisplay));
                    }
                }

                // Canlı Donanım (RAM / CPU) Takibi
                if (serverRunner.IsRunning && serverRunner.ServerProcess != null)
                {
                    try
                    {
                        var proc = serverRunner.ServerProcess;
                        proc.Refresh();
                        double ramMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                        double ramGb = ramMb / 1024.0;
                        string limitStr = MaxRamLimitGb > 0 ? $" / {MaxRamLimitGb} GB" : $" ({Loc.T("Sınırsız", "Unlimited")})";
                        ServerLiveRamDisplay = $"{ramGb:F2} GB{limitStr}";
                        string cores = CpuCoresAllocated > 0
                            ? $"{CpuCoresAllocated} {Loc.T("Çekirdek", "Cores")}"
                            : $"{Environment.ProcessorCount} {Loc.T("Çekirdek", "Cores")}";
                        ServerLiveCpuDisplay = $"{ProcessPriority} ({cores})";
                    }
                    catch { }
                }
                else
                {
                    ServerLiveRamDisplay = Loc.T("Sunucu Kapalı", "Server Offline");
                    ServerLiveCpuDisplay = Loc.T("Sunucu Kapalı", "Server Offline");
                }

                // Periodically refresh online players in-place (and clear online flag if everyone disconnected)
                var players = await Task.Run(async () => await serverControl.GetPlayersAsync());
                if (players != null && players.Count > 0)
                {
                    Application.Current.Dispatcher.Invoke(() => SyncOnlinePlayersInPlace(players));
                }
                else
                {
                    await RefreshOnlinePlayersAsync();
                }

                // Auto-refresh Accounts table whenever users.json changes on disk (e.g., new player auto-registered or created hero)
                try
                {
                    var dbPath = UserDatabase.Instance.DbFilePath;
                    if (File.Exists(dbPath))
                    {
                        var wt = File.GetLastWriteTimeUtc(dbPath);
                        if (wt != lastUsersFileWriteUtc)
                        {
                            lastUsersFileWriteUtc = wt;
                            Application.Current.Dispatcher.Invoke(() => ReloadUsers());
                        }
                    }
                }
                catch { }
            }
            catch { }
            finally
            {
                isPollingStatus = false;
            }
        }

        #region Admin Handlers

        private async Task GiveGoldAsync(int amount)
        {
            var target = string.IsNullOrWhiteSpace(TargetPlayerName) ? "all" : TargetPlayerName.Trim();
            var res = await serverControl.GiveGoldAsync(target, amount);
            if (res != null && res.StartsWith("OK"))
            {
                AddLog($"[Admin Hile] {target} hedefine +{amount:N0} Altın verildi.");
            }
            else
            {
                var err = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : (res ?? "");
                AddLog($"[Admin Hile] Altın verilemedi: {err}");
            }
            await RefreshOnlinePlayersAsync();
        }

        private async Task GiveRenownAsync(float amount)
        {
            var target = string.IsNullOrWhiteSpace(TargetPlayerName) ? "all" : TargetPlayerName.Trim();
            var res = await serverControl.GiveRenownAsync(target, amount);
            if (res != null && res.StartsWith("OK"))
            {
                AddLog($"[Admin Hile] {target} hedefine +{amount:0.#} Nam verildi.");
            }
            else
            {
                var err = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : (res ?? "");
                AddLog($"[Admin Hile] Nam verilemedi: {err}");
            }
            await RefreshOnlinePlayersAsync();
        }

        private async Task GiveInfluenceAsync(float amount)
        {
            var target = string.IsNullOrWhiteSpace(TargetPlayerName) ? "all" : TargetPlayerName.Trim();
            var res = await serverControl.GiveInfluenceAsync(target, amount);
            if (res != null && res.StartsWith("OK"))
            {
                AddLog($"[Admin Hile] {target} hedefine +{amount:0.#} Nüfuz verildi.");
            }
            else
            {
                var err = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : (res ?? "");
                AddLog($"[Admin Hile] Nüfuz verilemedi: {err}");
            }
            await RefreshOnlinePlayersAsync();
        }

        private async Task GiveTroopsAsync()
        {
            var target = string.IsNullOrWhiteSpace(TargetPlayerName) ? "all" : TargetPlayerName.Trim();
            var troopId = !string.IsNullOrWhiteSpace(CustomTroopId) ? CustomTroopId.Trim() : (SelectedTroop?.Id ?? "imperial_elite_cataphract");
            var count = CheatTroopCount > 0 ? CheatTroopCount : 10;
            var res = await serverControl.GiveTroopAsync(target, troopId, count);
            if (res != null && res.StartsWith("OK"))
            {
                AddLog($"[Admin Hile] {target} ordusuna +{count}x {troopId} askeri eklendi.");
            }
            else
            {
                var err = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : (res ?? "");
                AddLog($"[Admin Hile] Asker verilemedi: {err}");
            }
            await RefreshOnlinePlayersAsync();
        }

        private async Task DuplicateTroopsAsync()
        {
            var target = string.IsNullOrWhiteSpace(TargetPlayerName) ? "all" : TargetPlayerName.Trim();
            var res = await serverControl.DuplicateTroopsAsync(target, 10);
            if (res != null && res.StartsWith("OK"))
            {
                AddLog($"[Admin Hile] {target} ordusundaki tüm mevcut birliklere +10 asker çoğaltıldı.");
            }
            else
            {
                var err = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : (res ?? "");
                AddLog($"[Admin Hile] Birlikler çoğaltılamadı: {err}");
            }
            await RefreshOnlinePlayersAsync();
        }

        private async Task HealHeroAsync()
        {
            var target = string.IsNullOrWhiteSpace(TargetPlayerName) ? "all" : TargetPlayerName.Trim();
            var res = await serverControl.HealHeroAsync(target);
            if (res != null && res.StartsWith("OK"))
            {
                AddLog($"[Admin Hile] {target} kahramanı tamamen iyileştirildi (%100 Can).");
            }
            else
            {
                var err = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : (res ?? "");
                AddLog($"[Admin Hile] İyileştirilemedi: {err}");
            }
            await RefreshOnlinePlayersAsync();
        }

        private async Task HealPartyAsync()
        {
            var target = string.IsNullOrWhiteSpace(TargetPlayerName) ? "all" : TargetPlayerName.Trim();
            var res = await serverControl.HealPartyAsync(target);
            if (res != null && res.StartsWith("OK"))
            {
                AddLog($"[Admin Hile] {target} ordusundaki tüm yaralılar anında iyileştirildi.");
            }
            else
            {
                var err = !string.IsNullOrEmpty(serverControl.LastError) ? $" ({serverControl.LastError})" : (res ?? "");
                AddLog($"[Admin Hile] Ordu iyileştirilemedi: {err}");
            }
            await RefreshOnlinePlayersAsync();
        }

        private async Task ToggleAdminAsync()
        {
            var username = TargetPlayerName?.Trim();
            if (string.IsNullOrWhiteSpace(username) || username.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (SelectedPlayer != null)
                {
                    username = SelectedPlayer.Username;
                }
            }

            if (string.IsNullOrWhiteSpace(username) || username.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    Loc.T("Lütfen admin yetkisi verilecek veya alınacak belirli bir oyuncu seçin.", "Please select a specific player to grant or revoke admin privileges."),
                    Loc.T("Bilgi", "Info"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (username.StartsWith("user_", StringComparison.OrdinalIgnoreCase))
            {
                username = username.Substring(5);
            }

            bool currentAdmin = UserDatabase.Instance.IsAdmin(username);
            bool newAdmin = !currentAdmin;
            UserDatabase.Instance.SetAdmin(username, newAdmin);
            await serverControl.SetAdminAsync(username, newAdmin);

            AddLog($"[Admin Yetkisi] '{username}' kullanıcısı Admin = {newAdmin} yapıldı.");
            ReloadUsers();
            await RefreshOnlinePlayersAsync();
        }

        private async Task RefreshOnlinePlayersAsync()
        {
            List<PlayerInfoModel>? players = null;
            try
            {
                players = await Task.Run(async () => await serverControl.GetPlayersAsync());
            }
            catch { }

            Application.Current.Dispatcher.Invoke(() =>
            {
                var incoming = players;
                if (incoming == null || incoming.Count == 0)
                {
                    incoming = new List<PlayerInfoModel>();
                    try
                    {
                        UserDatabase.Instance.Load();
                        foreach (var u in UserDatabase.Instance.GetAllUsers())
                        {
                            incoming.Add(new PlayerInfoModel
                            {
                                ControllerId = "user_" + u.Username.ToLowerInvariant(),
                                Username = u.Username,
                                PlatformName = u.Username + " (" + Loc.T("Kayıtlı", "Registered") + ")",
                                HeroName = u.HeroName != null && u.HeroName.Length > 0 ? u.HeroName : u.Username,
                                ClanName = u.ClanName != null && u.ClanName.Length > 0 ? u.ClanName : "-",
                                Gold = 0,
                                Renown = 0,
                                Influence = 0,
                                PartyCount = 0,
                                PartyLimit = 0,
                                WoundedCount = 0,
                                Hp = 100,
                                MaxHp = 100,
                                IsAdmin = u.IsAdmin,
                                IsConnected = false
                            });
                        }
                    }
                    catch { }
                }

                SyncOnlinePlayersInPlace(incoming);
            });
        }

        private void SyncOnlinePlayersInPlace(List<PlayerInfoModel> incoming)
        {
            if (incoming == null) return;

            var incomingById = new HashSet<string>(incoming.Select(x => x.ControllerId), StringComparer.OrdinalIgnoreCase);
            for (int i = OnlinePlayers.Count - 1; i >= 0; i--)
            {
                if (!incomingById.Contains(OnlinePlayers[i].ControllerId))
                {
                    OnlinePlayers.RemoveAt(i);
                }
            }

            foreach (var item in incoming)
            {
                var existing = OnlinePlayers.FirstOrDefault(p => string.Equals(p.ControllerId, item.ControllerId, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.UpdateFrom(item);
                }
                else
                {
                    OnlinePlayers.Add(item);
                }
            }

            if (SelectedPlayer == null && OnlinePlayers.Count > 0)
            {
                SelectedPlayer = OnlinePlayers[0];
            }
        }

        #endregion

        public void ReloadUsers()
        {
            var prevSelectedName = SelectedUser?.Username;
            UserDatabase.Instance.Load();
            try
            {
                if (File.Exists(UserDatabase.Instance.DbFilePath))
                {
                    lastUsersFileWriteUtc = File.GetLastWriteTimeUtc(UserDatabase.Instance.DbFilePath);
                }
            }
            catch { }
            AllUsers.Clear();
            foreach (var u in UserDatabase.Instance.GetAllUsers())
            {
                AllUsers.Add(u);
            }
            ApplyUserFilter();
            OnPropertyChanged(nameof(AllowAutoRegistration));
            if (!string.IsNullOrEmpty(prevSelectedName))
            {
                SelectedUser = FilteredUsers.FirstOrDefault(u => string.Equals(u.Username, prevSelectedName, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void ApplyUserFilter()
        {
            FilteredUsers.Clear();
            var q = (UserSearchText ?? string.Empty).Trim().ToLowerInvariant();
            foreach (var u in AllUsers)
            {
                if (string.IsNullOrEmpty(q) ||
                    u.Username.ToLowerInvariant().Contains(q) ||
                    (!string.IsNullOrEmpty(u.HeroName) && u.HeroName.ToLowerInvariant().Contains(q)))
                {
                    FilteredUsers.Add(u);
                }
            }
        }

        private void AddUser()
        {
            if (string.IsNullOrWhiteSpace(NewUsername))
            {
                MessageBox.Show(Loc.T("Lütfen bir kullanıcı adı girin.", "Please enter a username."), Loc.T("Uyarı", "Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(NewPassword))
            {
                MessageBox.Show(Loc.T("Lütfen hesap için bir şifre girin (boş şifre ile oyun içi giriş yapılamaz).", "Please enter a password for the account (empty passwords are not allowed for login)."), Loc.T("Uyarı", "Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var createdName = NewUsername.Trim();
            var createdAdmin = NewIsAdmin;
            if (UserDatabase.Instance.Register(createdName, NewPassword.Trim(), createdAdmin, out var error))
            {
                AddLog(Loc.F("[Kullanıcı Eklendi] '{0}' hesabı oluşturuldu (Admin: {1}).", "[User Added] Account '{0}' created (Admin: {1}).", createdName, createdAdmin));
                NewUsername = string.Empty;
                NewPassword = string.Empty;
                NewIsAdmin = false;
                if (IsServerRunning || IsServerConnected)
                {
                    _ = serverControl.ReloadUsersAsync();
                }
                ReloadUsers();
                _ = RefreshOnlinePlayersAsync();
            }
            else
            {
                MessageBox.Show(error, Loc.T("Hata", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetUserPassword()
        {
            if (SelectedUser == null)
            {
                MessageBox.Show(Loc.T("Lütfen önce listeden bir kullanıcı seçin.", "Please select a user from the list first."), Loc.T("Bilgi", "Info"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(ResetPasswordText))
            {
                MessageBox.Show(Loc.T("Lütfen yeni şifreyi girin (şifre boş bırakılamaz).", "Please enter a new password (password cannot be empty)."), Loc.T("Uyarı", "Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targetName = SelectedUser.Username;
            if (UserDatabase.Instance.ResetPassword(targetName, ResetPasswordText.Trim()))
            {
                AddLog(Loc.F("[Şifre Sıfırlandı] '{0}' kullanıcısının şifresi güncellendi.", "[Password Reset] Updated password for '{0}'.", targetName));
                ResetPasswordText = string.Empty;
                if (IsServerRunning || IsServerConnected)
                {
                    _ = serverControl.ReloadUsersAsync();
                }
                ReloadUsers();
                MessageBox.Show(Loc.F("'{0}' kullanıcısının şifresi başarıyla güncellendi.", "Password for '{0}' updated successfully.", targetName), Loc.T("Başarılı", "Success"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ToggleBan()
        {
            if (SelectedUser == null)
            {
                MessageBox.Show(Loc.T("Lütfen önce listeden bir kullanıcı seçin.", "Please select a user from the list first."), Loc.T("Bilgi", "Info"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targetName = SelectedUser.Username;
            var newBanned = !SelectedUser.IsBanned;
            UserDatabase.Instance.SetBan(targetName, newBanned, newBanned ? Loc.T("Yönetici paneli tarafından banlandı", "Banned by admin panel") : string.Empty);
            AddLog(Loc.F("[Ban Durumu] '{0}' hesabı {1}.", "[Ban Status] Account '{0}' {1}.", targetName, newBanned ? Loc.T("BANLANDI", "BANNED") : Loc.T("BANI KALDIRILDI", "UNBANNED")));

            if (IsServerRunning || IsServerConnected)
            {
                _ = serverControl.ReloadUsersAsync();
                if (newBanned)
                {
                    _ = serverControl.KickPlayerAsync(targetName);
                }
            }

            ReloadUsers();
            _ = RefreshOnlinePlayersAsync();
        }

        private void DeleteUser()
        {
            if (SelectedUser == null)
            {
                MessageBox.Show(Loc.T("Lütfen önce listeden bir kullanıcı seçin.", "Please select a user from the list first."), Loc.T("Bilgi", "Info"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targetName = SelectedUser.Username;
            var confirm = MessageBox.Show(Loc.F("'{0}' hesabını silmek istediğinize emin misiniz?", "Are you sure you want to delete account '{0}'?", targetName), Loc.T("Hesap Sil", "Delete Account"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm == MessageBoxResult.Yes)
            {
                UserDatabase.Instance.DeleteUser(targetName);
                AddLog(Loc.F("[Kullanıcı Silindi] '{0}' hesabı silindi.", "[User Deleted] Account '{0}' deleted.", targetName));
                if (IsServerRunning || IsServerConnected)
                {
                    _ = serverControl.KickPlayerAsync(targetName);
                    _ = serverControl.ReloadUsersAsync();
                }
                ReloadUsers();
                _ = RefreshOnlinePlayersAsync();
            }
        }

        private void ReloadModules()
        {
            AllModules.Clear();
            if (string.IsNullOrWhiteSpace(BannerlordPath)) return;

            var modules = moduleScanner.ScanModules(BannerlordPath);
            foreach (var mod in modules)
            {
                AllModules.Add(mod);
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            FilteredModules.Clear();
            var q = (FilterText ?? string.Empty).Trim().ToLowerInvariant();
            foreach (var mod in AllModules)
            {
                if (string.IsNullOrEmpty(q) || mod.Name.ToLowerInvariant().Contains(q) || mod.Id.ToLowerInvariant().Contains(q))
                {
                    FilteredModules.Add(mod);
                }
            }
        }

        private void SelectAllCustom()
        {
            foreach (var m in FilteredModules)
            {
                if (!m.IsRequired && !m.IsIncompatible) m.IsSelected = true;
            }
            SaveConfig();
        }

        private void DeselectAllCustom()
        {
            foreach (var m in FilteredModules)
            {
                if (!m.IsRequired) m.IsSelected = false;
            }
            SaveConfig();
        }

        private static bool IsNewGameLabel(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string n = name.Trim();
            return n.Equals("Yeni Oyun", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Yeni_Oyun", StringComparison.OrdinalIgnoreCase)
                || n.Equals("NEW_GAME", StringComparison.OrdinalIgnoreCase)
                || n.Equals("New Game", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Nowa gra", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Neues Spiel", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Новая игра", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Nouvelle partie", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Nuova partita", StringComparison.OrdinalIgnoreCase)
                || n.Equals("新游戏", StringComparison.OrdinalIgnoreCase);
        }

        private void LoadSaves()
        {
            bool wasNewGame = IsNewGameLabel(SelectedSave);
            string newGameDisplay = Loc.T("Yeni Oyun", "New Game");
            SaveFiles.Clear();
            SaveFiles.Add(newGameDisplay);

            var saves = saveService.GetSaveFiles();
            foreach (var s in saves)
            {
                if (!IsNewGameLabel(s))
                {
                    SaveFiles.Add(s);
                }
            }

            if (wasNewGame || string.IsNullOrEmpty(SelectedSave) || !SaveFiles.Contains(SelectedSave))
            {
                selectedSave = newGameDisplay;
                OnPropertyChanged(nameof(SelectedSave));
            }
        }

        private void BrowseBannerlordFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Mount & Blade II: Bannerlord Ana Klasörünü Seçin"
            };
            if (dialog.ShowDialog() == true)
            {
                BannerlordPath = dialog.FolderName;
            }
        }

        private void OpenModulesFolder()
        {
            var path = Path.Combine(BannerlordPath, "Modules");
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
        }

        public async Task StartServerAsync()
        {
            if (IsServerRunning || IsBusy) return;

            IsBusy = true;
            StatusMessage = Loc.T("Mod paketleri hazırlanıyor...", "Preparing mod packages...");

            try
            {
                SaveConfig();
                var selectedMods = AllModules.Where(m => m.IsSelected).ToList();
                var customMods = selectedMods.Where(m => !m.IsOfficial).ToList();

                if (EnableFastDl)
                {
                    if (customMods.Count > 0)
                    {
                        AddLog($"[FastDL] {customMods.Count} özel mod istemcilerin indirmesi için hazırlanıyor...");
                    }
                    else
                    {
                        AddLog("[FastDL] Dağıtılacak harici mod yok. Yalnızca vanilla modlar aktif.");
                    }

                    // Resolve active save file path for FastDL distribution
                    string effectiveSave = SelectedSave ?? string.Empty;
                    if (IsNewGameLabel(effectiveSave) || effectiveSave.Contains("Bulunamadı"))
                    {
                        effectiveSave = SaveGameService.SanitizeSaveFileName(ServerName);
                    }

                    var saveFolder = saveService.GetPrimarySavesFolder();
                    var saveFileName = effectiveSave.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ? effectiveSave : (effectiveSave + ".sav");
                    fastDlServer.ActiveSavePath = Path.Combine(saveFolder, saveFileName);
                    fastDlServer.GamePort = ServerPort;
                    fastDlServer.MaxPlayers = MaxPlayers;
                    fastDlServer.HasServerPassword = !string.IsNullOrWhiteSpace(ServerPassword);
                    fastDlServer.ServerVisibility = ServerVisibility;
                    fastDlServer.GetCurrentPlayerCount = () => ConnectedPlayerCount;

                    var manifest = await packagerService.PrepareFastDlPackageAsync(ServerName, GetPrimaryLocalIp(), FastDlPort, customMods, msg => AddLog(msg));
                    manifest.EnableWarSails = IsWarSailsSelected();
                    fastDlServer.Start(FastDlPort, manifest, packagerService.CacheDirectory);
                }

                StatusMessage = Loc.T("Bannerlord sunucu işlemi arka planda başlatılıyor...", "Starting Bannerlord dedicated server process...");
                var cfg = CreateCurrentServerConfig();
                if (cfg.EnableWarSails && ModuleScannerService.IsWarSailsInstalled(BannerlordPath))
                {
                    AddLog("[Sunucu] War Sails (NavalDLC) modülü aktif olarak yükleniyor.");
                }
                var started = serverRunner.StartServer(BannerlordPath, selectedMods.Select(m => m.Id), SelectedSave, cfg);

                if (started)
                {
                    string targetWorldSaveName = SaveGameService.SanitizeSaveFileName(ServerName);
                    StatusMessage = Loc.F("Sunucu çalışıyor (Port: {0}). FastDL: {1}", "Server running (Port: {0}). FastDL: {1}", ServerPort, FastDlPort);
                    AddLog($"[Sunucu Başlatıldı] Port: {ServerPort} | FastDL: {FastDlPort} | Kayıt: {targetWorldSaveName}.sav");

                    // Send initial network performance configuration shortly after startup
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 15; i++)
                        {
                            await Task.Delay(2500);
                            if (!IsServerRunning) break;
                            if (i < 5)
                            {
                                var ok = await serverControl.SetNetworkSettingsAsync(ServerTickRateHz, DisconnectTimeoutSec, NetworkPollIntervalMs);
                                if (ok && i == 0)
                                {
                                    AddLog($"[Ağ Bağlantısı] LiteNetLib performansı sunucuya aktarıldı ({ServerTickRateHz} Hz, Timeout: {DisconnectTimeoutSec}s).");
                                }
                            }

                            var folder = saveService.GetPrimarySavesFolder();
                            var createdSavePath = Path.Combine(folder, targetWorldSaveName + ".sav");
                            if (File.Exists(createdSavePath))
                            {
                                Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    if (!SaveFiles.Contains(targetWorldSaveName))
                                    {
                                        SaveFiles.Insert(Math.Min(1, SaveFiles.Count), targetWorldSaveName);
                                    }
                                    SelectedSave = targetWorldSaveName;
                                });
                                break;
                            }
                        }
                    });
                }
                else
                {
                    StatusMessage = Loc.T("Sunucu başlatılamadı.", "Failed to start server.");
                    fastDlServer.Stop();
                }
            }
            catch (Exception ex)
            {
                AddLog(Loc.F("[Kritik Hata] Sunucu başlatılırken hata oluştu: {0}", "[Critical Error] Server start failed: {0}", ex.Message));
                StatusMessage = Loc.T("Başlatma hatası!", "Launch error!");
                fastDlServer.Stop();
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task StopServerAsync()
        {
            if (!IsServerRunning && !IsBusy) return;

            IsBusy = true;
            StatusMessage = Loc.T("Sunucu durduruluyor...", "Stopping server...");

            try
            {
                await serverRunner.StopServerAsync();
                fastDlServer.Stop();
                StatusMessage = Loc.T("Sunucu durduruldu.", "Server stopped.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task CheckServerModsAsync()
        {
            if (IsClientSyncing) return;

            IsClientSyncing = true;
            ClientSyncStatus = Loc.T("Sunucudan mod listesi sorgulanıyor...", "Querying server mod list...");
            ClientMissingModules.Clear();

            try
            {
                var manifest = await clientSync.FetchManifestAsync(ConnectServerIp, ConnectFastDlPort);
                lastFetchedManifest = manifest;

                AddLog(Loc.F("[İstemci] Sunucu mod manifesti alındı: '{0}'. Mod sayısı: {1}", "[Client] Received mod manifest: '{0}'. Count: {1}", manifest.ServerName, manifest.Modules.Count));

                var missing = clientSync.CheckMissingModules(BannerlordPath, manifest);
                foreach (var m in missing)
                {
                    ClientMissingModules.Add(m);
                }

                if (ClientMissingModules.Count == 0)
                {
                    ClientSyncStatus = Loc.T("Tüm sunucu modları yüklü ve güncel! Oyuna katılabilirsiniz.", "All server mods are installed and up to date! Ready to join.");
                    AddLog(Loc.T("[İstemci] Eksik veya uyumsuz mod bulunamadı. Oyuna doğrudan bağlanabilirsiniz.", "[Client] No missing or mismatched mods."));
                }
                else
                {
                    var totalSizeMb = ClientMissingModules.Sum(m => m.FileSizeBytes) / (1024.0 * 1024.0);
                    ClientSyncStatus = Loc.F("{0} adet eksik mod bulundu ({1:N1} MB). İndirmek için 'Eksik Modları İndir' butonuna basın.", "{0} missing mod(s) found ({1:N1} MB). Click 'Download Missing Mods'.", ClientMissingModules.Count, totalSizeMb);
                    AddLog(Loc.F("[İstemci] {0} adet eksik mod tespit edildi.", "[Client] {0} missing mod(s) detected.", ClientMissingModules.Count));
                }
            }
            catch (Exception ex)
            {
                ClientSyncStatus = Loc.F("Sunucuya bağlanılamadı: {0}", "Could not connect to server: {0}", ex.Message);
                AddLog(Loc.F("[İstemci Hata] Sunucu manifesti alınamadı: {0}", "[Client Error] Failed to fetch manifest: {0}", ex.Message));
            }
            finally
            {
                IsClientSyncing = false;
            }
        }

        public async Task DownloadMissingModsAsync()
        {
            if (IsClientSyncing || ClientMissingModules.Count == 0) return;

            IsClientSyncing = true;
            ClientSyncStatus = Loc.T("Eksik modlar indiriliyor ve yükleniyor...", "Downloading and installing missing mods...");
            ClientSyncProgress = 0;

            try
            {
                int count = 0;
                int total = ClientMissingModules.Count;
                var modulesToDownload = ClientMissingModules.ToList();

                foreach (var mod in modulesToDownload)
                {
                    count++;
                    await clientSync.DownloadAndExtractModuleAsync(
                        BannerlordPath,
                        mod,
                        (read, len) =>
                        {
                            if (len > 0)
                            {
                                var modProg = (double)read / len * 100.0;
                                ClientSyncProgress = ((count - 1) + (modProg / 100.0)) / total * 100.0;
                                ClientSyncStatus = Loc.F("İndiriliyor ({0}/{1}): {2} - %{3:F0}", "Downloading ({0}/{1}): {2} - {3:F0}%", count, total, mod.Name, modProg);
                            }
                        },
                        msg => AddLog(msg));
                }

                ClientSyncStatus = Loc.T("Tüm modlar başarıyla indirildi ve yüklendi! Artık oyuna bağlanabilirsiniz.", "All mods downloaded and installed! You can now launch and join.");
                ClientMissingModules.Clear();
                ReloadModules();
            }
            catch (Exception ex)
            {
                ClientSyncStatus = Loc.F("İndirme hatası: {0}", "Download error: {0}", ex.Message);
                AddLog(Loc.F("[İstemci İndirme Hatası] {0}", "[Client Download Error] {0}", ex.Message));
            }
            finally
            {
                IsClientSyncing = false;
            }
        }

        private bool IsWarSailsSelected()
        {
            var navalMod = AllModules.FirstOrDefault(m => string.Equals(m.Id, "NavalDLC", StringComparison.OrdinalIgnoreCase));
            return navalMod != null ? navalMod.IsSelected : ModuleScannerService.IsWarSailsInstalled(BannerlordPath);
        }

        public void PlayOnThisPc()
        {
            if (string.IsNullOrWhiteSpace(BannerlordPath))
            {
                AddLog("[Hata] Bannerlord dizini bulunamadı.");
                return;
            }

            var exePath = Path.Combine(BannerlordPath, "bin", "Win64_Shipping_Client", "Bannerlord.exe");
            if (!File.Exists(exePath))
            {
                AddLog($"[Hata] Bannerlord.exe bulunamadı: {exePath}");
                return;
            }

            if (!serverRunner.EnsureSteamIsRunning())
            {
                AddLog("[Hata] Oyun başlatılamadı: Steam açık değil veya başlatılamadı.");
                return;
            }

            var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            ServerRunnerService.EnsureSteamAppId(BannerlordPath, workingDir);

            bool enableWarSails = IsWarSailsSelected();
            var finalModules = ModuleScannerService.BuildLaunchModuleList(
                BannerlordPath,
                AllModules.Where(m => m.IsSelected).Select(m => m.Id),
                enableWarSails);

            serverRunner.ApplyConfigurationFiles(CreateCurrentServerConfig());

            var modArg = "_MODULES_*" + string.Join("*", finalModules) + "*_MODULES_";
            var startInfo = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false,
                Arguments = $"/singleplayer /client {modArg}"
            };
            startInfo.EnvironmentVariables["SteamAppId"] = "261550";
            startInfo.EnvironmentVariables["SteamGameId"] = "261550";
            var docFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var coopDir = Path.Combine(docFolder, "Mount and Blade II Bannerlord", "CalradiaConnectedData");
            startInfo.EnvironmentVariables["COOP_DATA_DIR"] = coopDir;

            try
            {
                var p = Process.Start(startInfo);
                string dlcTag = finalModules.Contains("NavalDLC", StringComparer.OrdinalIgnoreCase) ? " [War Sails Aktif]" : string.Empty;
                AddLog($"[İstemci] Bannerlord Oyunu başlatıldı{dlcTag} (PID: {p?.Id})! Ana menüden 'Kalradya''ya Katıl' seçerek 127.0.0.1:{ServerPort} ile bağlanabilirsiniz.");
            }
            catch (Exception ex)
            {
                AddLog($"[Hata] Oyun başlatılamadı: {ex.Message}");
            }
        }

        public void LaunchClientAndConnect()
        {
            if (string.IsNullOrWhiteSpace(BannerlordPath)) return;

            var exePath = Path.Combine(BannerlordPath, "bin", "Win64_Shipping_Client", "Bannerlord.exe");
            if (!File.Exists(exePath))
            {
                AddLog($"[Hata] Bannerlord.exe bulunamadı: {exePath}");
                return;
            }

            if (!serverRunner.EnsureSteamIsRunning())
            {
                AddLog("[Hata] Oyun başlatılamadı: Steam açık değil veya başlatılamadı.");
                return;
            }

            var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            ServerRunnerService.EnsureSteamAppId(BannerlordPath, workingDir);

            bool enableWarSails = lastFetchedManifest?.EnableWarSails ?? IsWarSailsSelected();
            var finalModules = ModuleScannerService.BuildLaunchModuleList(
                BannerlordPath,
                lastFetchedManifest?.Modules.Select(m => m.Id),
                enableWarSails);

            serverRunner.ApplyConfigurationFiles(CreateCurrentServerConfig());

            var modArg = "_MODULES_*" + string.Join("*", finalModules) + "*_MODULES_";
            var startInfo = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false,
                Arguments = $"/singleplayer /client {modArg}"
            };
            startInfo.EnvironmentVariables["SteamAppId"] = "261550";
            startInfo.EnvironmentVariables["SteamGameId"] = "261550";
            var docFolderClient = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var coopDirClient = Path.Combine(docFolderClient, "Mount and Blade II Bannerlord", "CalradiaConnectedData");
            startInfo.EnvironmentVariables["COOP_DATA_DIR"] = coopDirClient;
            Process.Start(startInfo);
            AddLog($"[İstemci] Oyun başlatıldı. Ana menüden 'Kalradya''ya Katıl' seçerek kullanıcı adı ve şifrenizle {ConnectServerIp}:{ConnectGamePort} sunucusuna bağlanabilirsiniz.");
        }

        private void FlushPendingLogs()
        {
            if (pendingLogs.IsEmpty) return;

            var count = 0;
            while (pendingLogs.TryDequeue(out var msg) && count < 80)
            {
                LogEntries.Add(msg);
                count++;
            }

            while (LogEntries.Count > 800)
            {
                LogEntries.RemoveAt(0);
            }

            ConsoleText = string.Join(Environment.NewLine, LogEntries);
        }

        private void AddLog(string msg)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            pendingLogs.Enqueue($"[{time}] {msg}");
        }

        private string ConfigFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private void SyncSelectedLanguageFromLoc()
        {
            var match = AvailableLanguages.FirstOrDefault(l =>
                string.Equals(l.Code, Loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                ?? AvailableLanguages.FirstOrDefault();
            if (match != null && !ReferenceEquals(selectedLanguage, match))
            {
                selectedLanguage = match;
                OnPropertyChanged(nameof(SelectedLanguage));
            }
        }

        private void RefreshAvailableTroops()
        {
            string? selectedId = SelectedTroop?.Id;
            AvailableTroops.Clear();

            string emp = Loc.T("İmparatorluk", "Empire");
            string vla = Loc.T("Vlandiya", "Vlandia");
            string bat = Loc.T("Battanya", "Battania");
            string khu = Loc.T("Kuzait", "Khuzait");
            string stu = Loc.T("Sturgiya", "Sturgia");
            string ase = Loc.T("Aserai", "Aserai");
            string t5 = Loc.T("Kademe 5", "Tier 5");
            string t6 = Loc.T("Kademe 6", "Tier 6");

            AvailableTroops.Add(new TroopPresetModel("imperial_elite_cataphract", Loc.T("İmparatorluk Seçkin Katafraktı", "Imperial Elite Cataphract"), emp, t6));
            AvailableTroops.Add(new TroopPresetModel("vlandian_banner_knight", Loc.T("Vlandiya Sancak Şövalyesi", "Vlandian Banner Knight"), vla, t6));
            AvailableTroops.Add(new TroopPresetModel("battanian_fian_champion", Loc.T("Battanya Fian Şampiyonu", "Battanian Fian Champion"), bat, t6));
            AvailableTroops.Add(new TroopPresetModel("khuzait_khans_guard", Loc.T("Kuzait Han Muhafızı", "Khuzait Khan's Guard"), khu, t6));
            AvailableTroops.Add(new TroopPresetModel("sturgian_veteran_warrior", Loc.T("Sturgiya Ağır Baltalı", "Sturgian Heavy Axeman"), stu, t5));
            AvailableTroops.Add(new TroopPresetModel("druzhinnik_champion", Loc.T("Sturgiya Druzhinnik Şampiyonu", "Sturgian Druzhinnik Champion"), stu, t6));
            AvailableTroops.Add(new TroopPresetModel("aserai_vanguard_faris", Loc.T("Aserai Öncü Farisi", "Aserai Vanguard Faris"), ase, t6));
            AvailableTroops.Add(new TroopPresetModel("imperial_legionary", Loc.T("İmparatorluk Lejyoneri", "Imperial Legionary"), emp, t5));
            AvailableTroops.Add(new TroopPresetModel("vlandian_sharpshooter", Loc.T("Vlandiya Keskin Nişancısı", "Vlandian Sharpshooter"), vla, t5));
            AvailableTroops.Add(new TroopPresetModel("aserai_mameluke_heavy_cavalry", Loc.T("Aserai Ağır Memlük", "Aserai Mameluke Heavy Cavalry"), ase, t5));

            SelectedTroop = (!string.IsNullOrEmpty(selectedId)
                ? AvailableTroops.FirstOrDefault(t => string.Equals(t.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                : null) ?? AvailableTroops.FirstOrDefault();
        }

        private void NotifyLanguageChanged()
        {
            RefreshAvailableTroops();
            LoadSaves();
            OnPropertyChanged(nameof(StatusBadgeText));
            OnPropertyChanged(nameof(UpdateButtonText));
            OnPropertyChanged(nameof(BandwidthDisplay));
            OnPropertyChanged(nameof(RamOptions));
            OnPropertyChanged(nameof(VramOptions));
            OnPropertyChanged(nameof(FpsOptions));
            OnPropertyChanged(nameof(CpuCoreOptions));
            OnPropertyChanged(nameof(PriorityOptions));
            OnPropertyChanged(nameof(BattleSizeOptions));
            OnPropertyChanged(nameof(DamageDifficultyOptions));
            OnPropertyChanged(nameof(CombatAiOptions));
            OnPropertyChanged(nameof(HeroDeathOptions));
            OnPropertyChanged(nameof(AutoSaveOptions));
            OnPropertyChanged(nameof(CorpseOptions));
            OnPropertyChanged(nameof(MaxPlayersOptions));
            OnPropertyChanged(nameof(VisibilityOptions));
            OnPropertyChanged(nameof(StartingLevelOptions));
            OnPropertyChanged(nameof(FocusPointsOptions));
            OnPropertyChanged(nameof(AttributePointsOptions));
            OnPropertyChanged(nameof(AllSkillsLevelOptions));
            OnPropertyChanged(nameof(CharacterXpOptions));
            OnPropertyChanged(nameof(SkillXpOptions));
            OnPropertyChanged(nameof(StartingGoldOptions));
            OnPropertyChanged(nameof(StartingRenownOptions));
            OnPropertyChanged(nameof(StartingInfluenceOptions));
            OnPropertyChanged(nameof(EquipmentPresetOptions));
            OnPropertyChanged(nameof(TroopsPresetOptions));
            OnPropertyChanged(nameof(MaxRamLimitGb));
            OnPropertyChanged(nameof(VramBudgetLevel));
            OnPropertyChanged(nameof(ServerMaxFps));
            OnPropertyChanged(nameof(CpuCoresAllocated));
            OnPropertyChanged(nameof(ProcessPriority));
            OnPropertyChanged(nameof(BattleSize));
            OnPropertyChanged(nameof(DamageDifficulty));
            OnPropertyChanged(nameof(CombatAiDifficulty));
            OnPropertyChanged(nameof(HeroDeathInBattle));
            OnPropertyChanged(nameof(AutoSaveIntervalMinutes));
            OnPropertyChanged(nameof(NumberOfCorpses));
            OnPropertyChanged(nameof(MaxPlayers));
            OnPropertyChanged(nameof(ServerVisibility));
            OnPropertyChanged(nameof(StartingLevel));
            OnPropertyChanged(nameof(StartingFocusPoints));
            OnPropertyChanged(nameof(StartingAttributePoints));
            OnPropertyChanged(nameof(StartingAllSkillsLevel));
            OnPropertyChanged(nameof(CharacterXpMultiplier));
            OnPropertyChanged(nameof(SkillXpMultiplier));
            OnPropertyChanged(nameof(StartingGold));
            OnPropertyChanged(nameof(StartingRenown));
            OnPropertyChanged(nameof(StartingInfluence));
            OnPropertyChanged(nameof(StartingEquipmentPreset));
            OnPropertyChanged(nameof(StartingTroopsPreset));
            if (!IsServerRunning)
            {
                ServerLiveRamDisplay = Loc.T("Sunucu Kapalı", "Server Offline");
                ServerLiveCpuDisplay = Loc.T("Sunucu Kapalı", "Server Offline");
                ServerTimeSpeed = Loc.T("Sunucu Kapalı", "Server Offline");
                StatusMessage = Loc.T("Sunucu hazır durumda.", "Server is ready.");
            }
            if (!IsClientSyncing && ClientMissingModules.Count == 0)
            {
                ClientSyncStatus = Loc.T("Sunucu IP adresi girip modları kontrol edin.", "Enter a server IP address and check server mods.");
            }
        }

        public ServerConfig CreateCurrentServerConfig()
        {
            return new ServerConfig
            {
                Language = languageManuallySet ? Loc.CurrentLanguage : string.Empty,
                LanguageManuallySet = languageManuallySet,
                BannerlordPath = BannerlordPath,
                ServerName = ServerName,
                ServerPort = ServerPort,
                FastDlPort = FastDlPort,
                EnableFastDl = EnableFastDl,
                SelectedSaveName = SelectedSave ?? string.Empty,
                SelectedModuleIds = AllModules.Where(m => m.IsSelected).Select(m => m.Id).ToList(),
                EnableWarSails = IsWarSailsSelected(),

                ServerPassword = ServerPassword,
                ServerVisibility = ServerVisibility,
                MaxPlayers = MaxPlayers,

                MaxRamLimitGb = MaxRamLimitGb,
                VramBudgetLevel = VramBudgetLevel,
                ServerMaxFps = ServerMaxFps,
                CpuCoresAllocated = CpuCoresAllocated,
                ProcessPriority = ProcessPriority,
                AutoRamCleanEnabled = AutoRamCleanEnabled,
                ServerDisableSound = ServerDisableSound,

                BattleSize = BattleSize,
                DamageDifficulty = DamageDifficulty,
                CombatAiDifficulty = CombatAiDifficulty,
                HeroDeathInBattle = HeroDeathInBattle,
                AutoSaveIntervalMinutes = AutoSaveIntervalMinutes,
                NumberOfCorpses = NumberOfCorpses,
                PvpEnabled = PvpEnabled,
                ArenaContinuousPracticeRespawn = ArenaContinuousPracticeRespawn,
                ArenaPvpDuelsEnabled = ArenaPvpDuelsEnabled,

                ServerTickRateHz = ServerTickRateHz,
                DisconnectTimeoutSec = DisconnectTimeoutSec,
                NetworkPollIntervalMs = NetworkPollIntervalMs,
                MaxBandwidthMbps = MaxBandwidthMbps,
                PersistentWorldTime = PersistentWorldTime,

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
                StartingFoodSupplies = StartingFoodSupplies
            };
        }

        private void SaveConfig()
        {
            try
            {
                var cfg = CreateCurrentServerConfig();
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, json);
                ServerRunnerService.SyncModConfigToDisk(cfg);
            }
            catch { }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFilePath)) return;

                var json = File.ReadAllText(ConfigFilePath);
                var cfg = JsonSerializer.Deserialize<ServerConfig>(json);
                if (cfg != null)
                {
                    if (cfg.LanguageManuallySet && !string.IsNullOrWhiteSpace(cfg.Language))
                    {
                        languageManuallySet = true;
                        ServerManagerLoc.Instance.SetLanguage(cfg.Language);
                    }
                    else
                    {
                        languageManuallySet = false;
                        Loc.SyncFromSystemCulture();
                        ServerManagerLoc.Instance.NotifyAll();
                    }
                    if (!string.IsNullOrWhiteSpace(cfg.BannerlordPath) && Directory.Exists(cfg.BannerlordPath))
                    {
                        bannerlordPath = cfg.BannerlordPath;
                    }
                    if (!string.IsNullOrWhiteSpace(cfg.ServerName)) serverName = cfg.ServerName;
                    if (cfg.ServerPort > 0) serverPort = cfg.ServerPort;
                    if (cfg.FastDlPort > 0) fastDlPort = cfg.FastDlPort;
                    enableFastDl = cfg.EnableFastDl;
                    if (!string.IsNullOrWhiteSpace(cfg.SelectedSaveName)) selectedSave = cfg.SelectedSaveName;
                    if (cfg.ServerTickRateHz > 0) serverTickRateHz = cfg.ServerTickRateHz;
                    if (cfg.DisconnectTimeoutSec > 0) disconnectTimeoutSec = cfg.DisconnectTimeoutSec;
                    if (cfg.NetworkPollIntervalMs > 0) networkPollIntervalMs = cfg.NetworkPollIntervalMs;
                    if (cfg.MaxBandwidthMbps >= 0) maxBandwidthMbps = cfg.MaxBandwidthMbps;
                    persistentWorldTime = cfg.PersistentWorldTime;

                    serverPassword = cfg.ServerPassword ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(cfg.ServerVisibility)) serverVisibility = cfg.ServerVisibility;
                    if (cfg.MaxPlayers > 0) maxPlayers = cfg.MaxPlayers;

                    maxRamLimitGb = cfg.MaxRamLimitGb;
                    vramBudgetLevel = cfg.VramBudgetLevel;
                    if (cfg.ServerMaxFps > 0) serverMaxFps = cfg.ServerMaxFps;
                    cpuCoresAllocated = cfg.CpuCoresAllocated;
                    if (!string.IsNullOrWhiteSpace(cfg.ProcessPriority)) processPriority = cfg.ProcessPriority;
                    autoRamCleanEnabled = cfg.AutoRamCleanEnabled;
                    serverDisableSound = cfg.ServerDisableSound;

                    if (cfg.BattleSize > 0) battleSize = cfg.BattleSize;
                    damageDifficulty = cfg.DamageDifficulty;
                    combatAiDifficulty = cfg.CombatAiDifficulty;
                    heroDeathInBattle = cfg.HeroDeathInBattle;
                    autoSaveIntervalMinutes = cfg.AutoSaveIntervalMinutes;
                    numberOfCorpses = cfg.NumberOfCorpses;
                    pvpEnabled = cfg.PvpEnabled;
                    arenaContinuousPracticeRespawn = cfg.ArenaContinuousPracticeRespawn;
                    arenaPvpDuelsEnabled = cfg.ArenaPvpDuelsEnabled;

                    if (cfg.StartingLevel > 0) startingLevel = cfg.StartingLevel;
                    startingFocusPoints = cfg.StartingFocusPoints;
                    startingAttributePoints = cfg.StartingAttributePoints;
                    startingAllSkillsLevel = cfg.StartingAllSkillsLevel;
                    if (cfg.CharacterXpMultiplier > 0.01) characterXpMultiplier = cfg.CharacterXpMultiplier;
                    if (cfg.SkillXpMultiplier > 0.01) skillXpMultiplier = cfg.SkillXpMultiplier;
                    if (cfg.StartingGold >= 0) startingGold = cfg.StartingGold;
                    startingRenown = cfg.StartingRenown;
                    startingInfluence = cfg.StartingInfluence;
                    startingEquipmentPreset = cfg.StartingEquipmentPreset;
                    startingTroopsPreset = cfg.StartingTroopsPreset;
                    startingFoodSupplies = cfg.StartingFoodSupplies;
                    NotifyLanguageChanged();
                }
            }
            catch { }
        }

        private async Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                var release = await githubUpdater.FetchLatestReleaseAsync().ConfigureAwait(false);
                if (!GitHubUpdateService.TryParseSemanticVersion(release.TagName, out var remoteVer))
                {
                    return;
                }

                var currentManagerVer = GitHubUpdateService.GetCurrentVersion();
                bool managerOutdated = remoteVer > currentManagerVer;
                bool modOutdated = GitHubUpdateService.TryGetInstalledModVersion(BannerlordPath, out var installedModVer)
                                   && remoteVer > installedModVer;

                if (!managerOutdated && !modOutdated)
                {
                    return;
                }

                string currentDisplay = managerOutdated
                    ? AppVersionDisplay
                    : $"v{installedModVer.Major}.{installedModVer.Minor}.{installedModVer.Build}";

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return;

                await dispatcher.InvokeAsync(async () =>
                {
                    AvailableUpdateTag = release.TagName;
                    var notice = Loc.F(
                        "Yeni sürüm mevcut: {0} (Mevcut: {1})",
                        "New version available: {0} (Current: {1})",
                        release.TagName,
                        currentDisplay);
                    AddLog($"[Güncelleyici] {notice}");
                    StatusMessage = notice;

                    var prompt = Loc.F(
                        "Calradia Connected için yeni bir sürüm mevcut!\n\nMevcut Sürüm: {0}\nYeni Sürüm: {1}\n\nGüncellemeyi şimdi indirip kurmak ister misiniz?",
                        "A new version of Calradia Connected is available!\n\nCurrent Version: {0}\nLatest Version: {1}\n\nWould you like to download and install the update now?",
                        currentDisplay,
                        release.TagName);

                    var confirm = MessageBox.Show(
                        prompt,
                        Loc.T("Yeni Güncelleme Mevcut", "New Update Available"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (confirm == MessageBoxResult.Yes)
                    {
                        await UpdateModAsync(release);
                    }
                });
            }
            catch
            {
                // Startup check stays silent when offline or when no release is published yet.
            }
        }

        private async Task UpdateModAsync(GitHubReleaseInfo? preloadedRelease = null)
        {
            if (IsUpdatingMod) return;

            if (IsServerRunning || GitHubUpdateService.IsBannerlordProcessRunning())
            {
                var warnMsg = Loc.T(
                    "Güncelleme öncesi aktif Bannerlord / Sunucu işlemini durdurmanız gerekir. Dosyalar kullanımda olduğundan güncellenemez.",
                    "Please stop the running Bannerlord / Dedicated Server process before updating so locked DLLs can be replaced.");
                AddLog($"[Güncelleyici] {warnMsg}");
                MessageBox.Show(warnMsg, Loc.T("Güncelleme Uyarısı", "Update Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsUpdatingMod = true;
            StatusMessage = Loc.T("GitHub üzerinden güncellemeler denetleniyor...", "Checking GitHub for updates...");
            AddLog(Loc.F("[Güncelleyici] '{0}' GitHub deposu kontrol ediliyor (Mevcut: {1})...", "[Updater] Checking '{0}' on GitHub (Current: {1})...", GitHubUpdateService.DefaultRepository, AppVersionDisplay));

            try
            {
                var release = preloadedRelease ?? await githubUpdater.FetchLatestReleaseAsync();
                var currentVer = GitHubUpdateService.GetCurrentVersion();
                var hasRemoteVer = GitHubUpdateService.TryParseSemanticVersion(release.TagName, out var remoteVer);
                bool modOutdated = hasRemoteVer
                                   && GitHubUpdateService.TryGetInstalledModVersion(BannerlordPath, out var installedModVer)
                                   && remoteVer > installedModVer;
                bool isNewer = (hasRemoteVer && remoteVer > currentVer) || modOutdated;
                AvailableUpdateTag = isNewer ? release.TagName : null;

                if (preloadedRelease == null)
                {
                    var notesPreview = string.IsNullOrWhiteSpace(release.Body)
                        ? Loc.T("Sürüm notu belirtilmemiş.", "No release notes provided.")
                        : (release.Body.Length > 400 ? release.Body.Substring(0, 400) + "..." : release.Body);

                    string prompt = isNewer
                        ? Loc.F(
                            "Yeni sürüm bulundu!\n\nMevcut Sürüm: {0}\nYeni Sürüm: {1}\n\nNotlar:\n{2}\n\nOyun içi mod ve Server Manager güncellensin mi? (Server Manager işlem sonunda otomatik yeniden başlatılacak.)",
                            "A new update is available!\n\nCurrent Version: {0}\nNew Version: {1}\n\nRelease Notes:\n{2}\n\nUpdate both the in-game mod and Server Manager now? (Server Manager will automatically restart.)",
                            AppVersionDisplay, release.TagName, notesPreview)
                        : Loc.F(
                            "Mevcut sürümünüz ({0}) GitHub'daki en son sürüm ({1}) ile güncel görünüyor.\n\nYine de son paketi yeniden indirip uygulamak istiyor musunuz?",
                            "Your current version ({0}) matches the latest GitHub release ({1}).\n\nDo you still want to re-download and reinstall it?",
                            AppVersionDisplay, release.TagName);

                    var confirm = MessageBox.Show(
                        prompt,
                        Loc.T("Calradia Connected Güncelleme", "Calradia Connected Update"),
                        MessageBoxButton.YesNo,
                        isNewer ? MessageBoxImage.Question : MessageBoxImage.Information);

                    if (confirm != MessageBoxResult.Yes)
                    {
                        StatusMessage = Loc.T("Güncelleme kullanıcı tarafından iptal edildi.", "Update canceled by user.");
                        return;
                    }
                }

                SaveConfig();
                StatusMessage = Loc.F("{0} indiriliyor ve uygulanıyor...", "Downloading and applying {0}...", release.TagName);

                var applyResult = await githubUpdater.ApplyReleaseAsync(release, BannerlordPath, msg => AddLog(msg));
                AvailableUpdateTag = null;
                if (applyResult.ModUpdated)
                {
                    ReloadModules();
                }

                if (applyResult.ServerManagerRestartPending)
                {
                    StatusMessage = Loc.T("Güncelleme uygulandı, Server Manager yeniden başlatılıyor...", "Update applied, restarting Server Manager...");
                    await Task.Delay(350);
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                else
                {
                    StatusMessage = Loc.F("Güncelleme tamamlandı ({0}).", "Update completed ({0}).", release.TagName);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[Güncelleyici Hata] {ex.Message}");
                StatusMessage = Loc.T("Güncelleme başarısız oldu.", "Update failed.");
                MessageBox.Show(
                    Loc.F("GitHub güncellemesi sırasında hata oluştu:\n{0}", "An error occurred during GitHub update:\n{0}", ex.Message),
                    Loc.T("Güncelleme Hatası", "Update Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsUpdatingMod = false;
            }
        }

        private static string GetPrimaryLocalIp()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                {
                    return endPoint.Address.ToString();
                }
            }
            catch
            {
            }
            return "127.0.0.1";
        }

        private static string GetLocalIpSummary()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ips = host.AddressList
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    .Select(ip => ip.ToString())
                    .ToList();

                return ips.Any() ? string.Join(", ", ips) : "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}


