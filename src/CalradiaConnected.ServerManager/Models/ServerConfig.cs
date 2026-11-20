using System.Collections.Generic;

namespace CoopServerManager.Models
{
    public class ServerConfig
    {
        // Arayüz Dili (Boşsa veya LanguageManuallySet=false ise sistem diline göre otomatik açılır)
        public string Language { get; set; } = string.Empty;
        public bool LanguageManuallySet { get; set; } = false;

        // Temel Sunucu & Dizin Bilgileri
        public string BannerlordPath { get; set; } = string.Empty;
        public string ServerName { get; set; } = "Calradia Connected";
        public int ServerPort { get; set; } = 4200;
        public int FastDlPort { get; set; } = 4201;
        public bool EnableFastDl { get; set; } = true;
        public string SelectedSaveName { get; set; } = string.Empty;
        public List<string> SelectedModuleIds { get; set; } = new();
        public bool EnableWarSails { get; set; } = true;

        // Lobi & Güvenlik
        public string ServerPassword { get; set; } = string.Empty;
        public string ServerVisibility { get; set; } = "Public"; // Public, LAN, None
        public int MaxPlayers { get; set; } = 16;

        // Donanım, RAM, VRAM & İşlemci (CPU) Tahsisi
        public int MaxRamLimitGb { get; set; } = 0; // 0 = Sınırsız / Auto, 4, 6, 8, 12, 16, 24, 32
        public int VramBudgetLevel { get; set; } = 0; // 0=Ultra Düşük (Önerilen Sunucu), 1=Düşük, 2=Orta, 3=Yüksek, 4=Maksimum
        public int ServerMaxFps { get; set; } = 20; // 20 (Ultra Hafif), 30, 60, 120, 200
        public int CpuCoresAllocated { get; set; } = 0; // 0 = Tüm Çekirdekler, 2, 4, 6, 8
        public string ProcessPriority { get; set; } = "Normal"; // Normal, AboveNormal, High, BelowNormal
        public bool AutoRamCleanEnabled { get; set; } = true; // Periyodik WorkingSet temizliği
        public bool ServerDisableSound { get; set; } = true; // Arka plan sunucusunda sesleri kapat (CPU & RAM tasarrufu)
        public bool MinimizeServerWindow { get; set; } = true; // Sunucu yüklenince pencereyi simge durumuna küçült (GPU tasarrufu)

        // Kalradya Oyun & Zorluk Ayarları
        public int BattleSize { get; set; } = 400; // 200, 300, 400, 500, 600, 800, 1000 asker
        public int DamageDifficulty { get; set; } = 1; // 0 = Kolay (%33 hasar), 1 = Normal (%66 hasar), 2 = Gerçekçi (%100 hasar)
        public int CombatAiDifficulty { get; set; } = 1; // 0 = Çaylak, 1 = Normal, 2 = Usta / Kıdemli
        public int HeroDeathInBattle { get; set; } = 0; // 0 = Kapalı, 1 = Sadece Yoldaşlar, 2 = Herkes İçin Açık
        public int AutoSaveIntervalMinutes { get; set; } = 15; // 5, 10, 15, 30, 60 dk, 0 = Kapalı
        public int NumberOfCorpses { get; set; } = 20; // 0, 5, 20, 50, 100, 200 ceset
        public bool PvpEnabled { get; set; } = true; // Oyuncular arası savaş / dost ateşi
        public bool ArenaContinuousPracticeRespawn { get; set; } = true; // Arena idmanında sürekli yeniden doğma
        public bool ArenaPvpDuelsEnabled { get; set; } = true; // Arenada 1v1 düellolar ve PvP hasarı

        // Performans & Ağ Ayarları
        public int ServerTickRateHz { get; set; } = 60;
        public int DisconnectTimeoutSec { get; set; } = 60;
        public int NetworkPollIntervalMs { get; set; } = 20;
        public int MaxBandwidthMbps { get; set; } = 50;
        public bool PersistentWorldTime { get; set; } = true;

        // Karakter Başlangıç & İlerleme (XP, Statü, Ekipman & Birlik) Ayarları
        public int StartingLevel { get; set; } = 1;
        public int StartingFocusPoints { get; set; } = 5;
        public int StartingAttributePoints { get; set; } = 2;
        public int StartingAllSkillsLevel { get; set; } = 0;
        public double CharacterXpMultiplier { get; set; } = 1.0;
        public double SkillXpMultiplier { get; set; } = 1.0;
        public int StartingGold { get; set; } = 1000;
        public int StartingRenown { get; set; } = 0;
        public int StartingInfluence { get; set; } = 0;
        public int StartingEquipmentPreset { get; set; } = 0;
        public int StartingTroopsPreset { get; set; } = 0;
        public bool StartingFoodSupplies { get; set; } = true;
    }
}
