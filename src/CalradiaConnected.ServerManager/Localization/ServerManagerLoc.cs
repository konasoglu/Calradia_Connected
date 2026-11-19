using System;
using System.Collections.Generic;
using System.ComponentModel;
using CalradiaConnected.Common.Localization;

namespace CoopServerManager.Localization
{
    /// <summary>
    /// Live 8-language (TR, EN, PL, DE, RU, FR, IT, ZH) humanized WPF binding dictionary
    /// for Calradia-Connected Server Manager, synchronized with Loc.
    /// </summary>
    public sealed class ServerManagerLoc : INotifyPropertyChanged
    {
        public static ServerManagerLoc Instance { get; } = new ServerManagerLoc();

        public event PropertyChangedEventHandler? PropertyChanged;

        // Order: [0]=TR, [1]=EN, [2]=PL, [3]=DE, [4]=RU, [5]=FR, [6]=IT, [7]=ZH
        private static readonly Dictionary<string, string[]> Strings =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["WindowTitle"] = new[] {
                "Calradia Connected — Sunucu Yöneticisi",
                "Calradia Connected — Server Manager",
                "Calradia Connected — Menedżer Serwera",
                "Calradia Connected — Server-Manager",
                "Calradia Connected — Менеджер сервера",
                "Calradia Connected — Gestionnaire de serveur",
                "Calradia Connected — Gestore Server",
                "Calradia Connected — 服务器管理器"
            },
            ["BrandBadge"] = new[] { "⚔ KALRADYA", "⚔ CALRADIA", "⚔ CALRADIA", "⚔ CALRADIA", "⚔ КАЛЬРАДИЯ", "⚔ CALRADIA", "⚔ CALRADIA", "⚔ 卡拉迪亚" },
            ["BrandTitle"] = new[] { "Sunucu Yönetimi", "Server Control", "Panel Serwera", "Server-Steuerung", "Управление сервером", "Contrôle du serveur", "Controllo Server", "服务器控制台" },
            ["LangToggleText"] = new[] { "Dil", "Language", "Język", "Sprache", "Язык", "Langue", "Lingua", "语言" },
            ["UpdateModBtn"] = new[] { "Güncelle", "Update", "Aktualizuj", "Aktualisieren", "Обновить", "Mettre à jour", "Aggiorna", "检查更新" },
            ["UpdatingModBtn"] = new[] { "Güncelleniyor...", "Updating...", "Aktualizowanie...", "Aktualisiere...", "Обновление...", "Mise à jour...", "Aggiornamento...", "正在更新..." },
            ["DirectIpLabel"] = new[] { "Bağlantı Adresi:", "Connect:", "Adres:", "Adresse:", "Адрес:", "Adresse :", "Indirizzo:", "连接地址：" },
            ["CopyBtn"] = new[] { "Kopyala", "Copy", "Kopiuj", "Kopieren", "Копировать", "Copier", "Copia", "复制" },

            // Metric Cards
            ["CardPlayers"] = new[] { "OYUNCULAR", "PLAYERS", "GRACZE", "SPIELER", "ИГРОКИ", "JOUEURS", "GIOCATORI", "在线玩家" },
            ["CardPlayersUnit"] = new[] { " Çevrimiçi", " Online", " Online", " Online", " В сети", " En ligne", " Online", " 在线" },
            ["CardWorldTime"] = new[] { "DÜNYA ZAMANI", "WORLD TIME", "CZAS ŚWIATA", "WELTZEIT", "ВРЕМЯ МИРА", "TEMPS DU MONDE", "TEMPO DEL MONDO", "世界时间" },
            ["CardTickRate"] = new[] { "SUNUCU HIZI", "UPDATE RATE", "ODŚWIEŻANIE", "TAKTRATE", "ЧАСТОТА", "FRÉQUENCE", "FREQUENZA", "刷新率" },
            ["CardEngine"] = new[] { "Ağ Motoru", "Network Engine", "Silnik sieciowy", "Netzwerk-Engine", "Сетевой движок", "Moteur réseau", "Motore di rete", "网络引擎" },
            ["CardBandwidth"] = new[] { "BAĞLANTI HIZI", "BANDWIDTH", "PRZEPUSTOWOŚĆ", "BANDBREITE", "КАНАЛ СЕТИ", "BANDE PASSANTE", "BANDA DI RETE", "网络带宽" },
            ["CardRamCpu"] = new[] { "SİSTEM KULLANIMI", "SYSTEM USAGE", "ZUŻYCIE ZASOBÓW", "SYSTEMAUSLASTUNG", "НАГРУЗКА", "RESSOURCES", "RISORSE SISTEMA", "系统占用" },

            // Tab Headers (clean, concise, human)
            ["TabControl"] = new[] { "Sunucu", "Server", "Serwer", "Server", "Сервер", "Serveur", "Server", "服务器" },
            ["TabSettings"] = new[] { "Oyun ve Donanım", "Game & Hardware", "Gra i sprzęt", "Spiel & Hardware", "Игра и система", "Jeu et matériel", "Gioco e hardware", "游戏与硬件" },
            ["TabProgression"] = new[] { "Başlangıç ve Gelişim", "Starter & Growth", "Start i rozwój", "Start & Fortschritt", "Старт и прокачка", "Départ et progrès", "Inizio e crescita", "开局与成长" },
            ["TabAdmin"] = new[] { "Yönetici Araçları", "Admin Tools", "Narzędzia admina", "Admin-Werkzeuge", "Инструменты админа", "Outils d'admin", "Strumenti Admin", "管理工具" },
            ["TabUsers"] = new[] { "Hesaplar", "Accounts", "Konta", "Konten", "Аккаунты", "Comptes", "Account", "账号管理" },
            ["TabClientSync"] = new[] { "Sunucuya Katıl", "Join Server", "Dołącz do serwera", "Server beitreten", "Подключиться", "Rejoindre", "Unisciti", "加入服务器" },

            // Tab 1: Modules & Controls
            ["GameFolderLabel"] = new[] { "Bannerlord Oyun Klasörü:", "Bannerlord Game Folder:", "Folder gry Bannerlord:", "Bannerlord-Spielordner:", "Папка игры Bannerlord:", "Dossier du jeu Bannerlord :", "Cartella di Bannerlord:", "骑砍2 游戏目录：" },
            ["BrowseBtn"] = new[] { "Seç...", "Browse...", "Przeglądaj...", "Durchsuchen...", "Обзор...", "Parcourir...", "Sfoglia...", "浏览..." },
            ["RefreshBtn"] = new[] { "Yenile", "Refresh", "Odśwież", "Aktualisieren", "Обновить", "Actualiser", "Aggiorna", "刷新" },
            ["SelectAllBtn"] = new[] { "Tümünü Seç", "Select All", "Zaznacz wszystkie", "Alle auswählen", "Выбрать все", "Tout sélectionner", "Seleziona tutto", "全选" },
            ["DeselectAllBtn"] = new[] { "Seçimi Temizle", "Clear Selection", "Odznacz wszystkie", "Auswahl aufheben", "Снять выбор", "Tout désélectionner", "Deseleziona", "取消全选" },
            ["ModulesFolderBtn"] = new[] { "Modlar Klasörü", "Modules Folder", "Folder modułów", "Modules-Ordner", "Папка Modules", "Dossier Modules", "Cartella Modules", "打开 Modules" },
            ["ServerTitleLabel"] = new[] { "Sunucu Adı:", "Server Name:", "Nazwa serwera:", "Servername:", "Название сервера:", "Nom du serveur :", "Nome del server:", "服务器名称：" },
            ["ServerTitleTip"] = new[] { "Yeni bir dünya açıldığında kayıt dosyası bu adla kaydedilir.", "New campaigns are saved using this server name.", "Nowa kampania zostanie zapisana pod tą nazwą.", "Neue Kampagnen werden unter diesem Namen gespeichert.", "Новая кампания сохраняется под этим названием.", "Les nouvelles campagnes portent ce nom.", "Le nuove campagne vengono salvate con questo nome.", "新世界存档将使用此名称。" },
            ["ServerPassLabel"] = new[] { "Sunucu Şifresi (İsteğe Bağlı):", "Server Password (Optional):", "Hasło serwera (opcjonalne):", "Serverpasswort (optional):", "Пароль сервера (необязательно):", "Mot de passe (optionnel) :", "Password server (opzionale):", "服务器密码（可选）：" },
            ["ServerPassTip"] = new[] { "Boş bırakırsanız herkes şifresiz katılabilir.", "Leave blank to allow anyone to join without a password.", "Pozostaw puste, aby serwer był otwarty.", "Leer lassen für einen offenen Server ohne Passwort.", "Оставьте пустым для свободного входа.", "Laissez vide pour un serveur ouvert.", "Lascia vuoto per un server aperto.", "留空则无需密码即可加入。" },
            ["SaveFileLabel"] = new[] { "Dünya Kaydı:", "World Save:", "Zapis świata:", "Spielstand:", "Сохранение мира:", "Sauvegarde du monde :", "Salvataggio mondo:", "世界存档：" },
            ["PlayOnPcBtn"] = new[] { "Bu Bilgisayarda Oyuna Katıl", "Play on This PC", "Zagraj na tym komputerze", "Auf diesem PC spielen", "Зайти в игру на этом ПК", "Jouer sur ce PC", "Gioca su questo PC", "在本机进入游戏" },
            ["StartServerBtn"] = new[] { "Sunucuyu Başlat", "Start Server", "Uruchom serwer", "Server starten", "Запустить сервер", "Démarrer le serveur", "Avvia Server", "启动服务器" },
            ["StopServerBtn"] = new[] { "Sunucuyu Durdur", "Stop Server", "Zatrzymaj serwer", "Server stoppen", "Остановить сервер", "Arrêter le serveur", "Ferma Server", "停止服务器" },
            ["WorldTimeLabel"] = new[] { "Dünya Zamanı:", "World Time:", "Czas świata:", "Weltzeit:", "Время мира:", "Temps du monde :", "Tempo:", "世界时间：" },
            ["TimePauseBtn"] = new[] { "Duraklat", "Pause", "Pauza", "Pause", "Пауза", "Pause", "Pausa", "暂停" },
            ["Time1xBtn"] = new[] { "Normal (1x)", "Normal (1x)", "Normalnie (1x)", "Normal (1x)", "Обычно (1x)", "Normal (1x)", "Normale (1x)", "正常 (1x)" },
            ["Time2xBtn"] = new[] { "Hızlı (2x)", "Fast (2x)", "Szybko (2x)", "Schnell (2x)", "Быстро (2x)", "Rapide (2x)", "Veloce (2x)", "快速 (2x)" },
            ["Time3xBtn"] = new[] { "Çok Hızlı (3x)", "Very Fast (3x)", "Bardzo szybko (3x)", "Sehr schnell (3x)", "Очень быстро (3x)", "Très rapide (3x)", "Molto veloce (3x)", "极速 (3x)" },
            ["SaveWorldBtn"] = new[] { "Dünyayı Kaydet", "Save World", "Zapisz świat", "Welt speichern", "Сохранить мир", "Sauvegarder", "Salva mondo", "保存世界" },
            ["PersistentTimeChk"] = new[] { "Zaman sürekli aksın", "Keep world time flowing", "Ciągły upływ czasu", "Weltzeit fortlaufend", "Непрерывное время", "Temps continu", "Tempo continuo", "持续流逝时间" },
            ["TickRateLabel"] = new[] { "Güncelleme Sıklığı:", "Update Rate:", "Częstotliwość:", "Aktualisierungsrate:", "Частота обновлений:", "Fréquence :", "Frequenza:", "更新频率：" },
            ["TimeoutLabel"] = new[] { "Bağlantı Süresi:", "Timeout:", "Limit czasu:", "Zeitlimit:", "Тайм-аут:", "Délai :", "Timeout:", "断线超时：" },
            ["BandwidthLimitLabel"] = new[] { "Bant Genişliği:", "Bandwidth Limit:", "Limit łącza:", "Bandbreite:", "Ограничение канала:", "Bande passante :", "Limite banda:", "带宽上限：" },
            ["ApplyNetBtn"] = new[] { "Uygula", "Apply", "Zastosuj", "Anwenden", "Применить", "Appliquer", "Applica", "应用" },
            ["ConsoleHeader"] = new[] { "SUNUCU GÜNLÜĞÜ", "SERVER LOG", "DZIENNIK SERWERA", "SERVER-PROTOKOLL", "ЖУРНАЛ СЕРВЕРА", "JOURNAL DU SERVEUR", "REGISTRO SERVER", "服务器日志" },
            ["CopyAllLogsBtn"] = new[] { "Tümünü Kopyala", "Copy All", "Kopiuj wszystko", "Alles kopieren", "Копировать всё", "Tout copier", "Copia tutto", "全部复制" },
            ["ClearBtn"] = new[] { "Temizle", "Clear", "Wyczyść", "Leeren", "Очистить", "Effacer", "Pulisci", "清空" },
            ["CtxCopySel"] = new[] { "Seçili Metni Kopyala", "Copy Selection", "Kopiuj zaznaczenie", "Auswahl kopieren", "Копировать выделенное", "Copier la sélection", "Copia selezione", "复制选中" },
            ["CtxSelectAll"] = new[] { "Tümünü Seç", "Select All", "Zaznacz wszystko", "Alles auswählen", "Выбрать всё", "Tout sélectionner", "Seleziona tutto", "全选" },
            ["CtxCopyAll"] = new[] { "Tüm Günlüğü Kopyala", "Copy Full Log", "Kopiuj cały dziennik", "Gesamtes Protokoll kopieren", "Копировать весь журнал", "Copier tout le journal", "Copia tutto il registro", "复制完整日志" },
            ["CtxClear"] = new[] { "Günlüğü Temizle", "Clear Log", "Wyczyść dziennik", "Protokoll leeren", "Очистить журнал", "Effacer le journal", "Pulisci registro", "清空日志" },

            // Tab 2: Server & Game Settings
            ["CfgBannerTitle"] = new[] { "Sunucu, Donanım ve Dünya Kuralları", "Server, Hardware & World Rules", "Serwer, sprzęt i zasady świata", "Server-, Hardware- & Weltregeln", "Настройки сервера, системы и мира", "Serveur, matériel et règles du monde", "Server, Hardware e Regole del Mondo", "服务器、硬件与世界规则" },
            ["CfgBannerDesc"] = new[] { "Sunucuyu başlatmadan önce kaynak sınırlarını, savaş kurallarını ve erişim tercihlerini kolayca ayarlayın.", "Adjust performance limits, battle rules, and server access before launching your world.", "Dostosuj limity zasobów, zasady bitew i dostęp przed uruchomieniem świata.", "Passe Leistungslimits, Kampfregeln und Serverzugang vor dem Start an.", "Настройте лимиты ресурсов, правила боёв и доступ перед запуском мира.", "Réglez les ressources, les règles de combat et l'accès avant de lancer le monde.", "Configura risorse, regole di battaglia e accesso prima di avviare il mondo.", "在启动世界之前轻松调整性能上限、战斗规则和访问权限。" },
            ["SaveApplyBtn"] = new[] { "Ayarları Kaydet", "Save Settings", "Zapisz ustawienia", "Einstellungen speichern", "Сохранить настройки", "Enregistrer", "Salva Impostazioni", "保存设置" },
            ["ResetProgBtn"] = new[] { "Başlangıç Geçmişini Sıfırla", "Reset Starter History", "Resetuj historię startową", "Startverlauf zurücksetzen", "Сбросить историю старта", "Réinitialiser le départ", "Ripristina cronologia", "重置开局记录" },
            ["ResetProgTip"] = new[] { "Oyuncuların başlangıç hediyelerini tekrar alabilmesi için geçmişi temizler.", "Clears tracked starter grants so players receive starter bonuses fresh on join.", "Czyści zapisane pakiety startowe graczy.", "Setzt bereits vergebene Startpakete zurück.", "Сбрасывает учёт стартовых наборов для игроков.", "Réinitialise le suivi des bonus de départ.", "Azzera il registro dei bonus iniziali.", "清除开局礼包领取记录，以便玩家重新获得。" },
            ["DefaultsBtn"] = new[] { "Varsayılanlara Dön", "Restore Defaults", "Domyślne", "Standardwerte", "По умолчанию", "Par défaut", "Predefiniti", "恢复默认" },
            ["HwHeader"] = new[] { "DONANIM VE PERFORMANS", "HARDWARE & PERFORMANCE", "SPRZĘT I WYDAJNOŚĆ", "HARDWARE & LEISTUNG", "СИСТЕМА И ПРОИЗВОДИТЕЛЬНОСТЬ", "MATÉRIEL ET PERFORMANCE", "HARDWARE E PRESTAZIONI", "硬件与性能" },
            ["MaxRamLabel"] = new[] { "Bellek Sınırı (RAM):", "Memory Limit (RAM):", "Limit pamięci (RAM):", "Speicherlimit (RAM):", "Лимит памяти (ОЗУ):", "Limite mémoire (RAM) :", "Limite memoria (RAM):", "内存上限 (RAM)：" },
            ["MaxRamDesc"] = new[] { "Sunucunun kullanabileceği en yüksek bellek miktarını belirler.", "Sets the upper memory ceiling for the background server.", "Określa maksymalne użycie pamięci RAM przez serwer.", "Legt das maximale RAM-Limit für den Server fest.", "Задаёт максимальный объём памяти для сервера.", "Définit la mémoire maximale du serveur.", "Imposta il limite massimo di RAM del server.", "限制后台服务器可使用的最大内存。" },
            ["VramLabel"] = new[] { "Grafik Bellek Bütçesi:", "Graphics Memory Budget:", "Budżet pamięci graficznej:", "Grafikspeicher-Budget:", "Лимит видеопамяти:", "Budget mémoire vidéo :", "Budget memoria grafica:", "显存预算：" },
            ["VramDesc"] = new[] { "Arka plandaki sunucunun ekran kartı kullanımını düşük tutar.", "Keeps GPU memory usage low for the background server.", "Ogranicza zużycie karty graficznej przez serwer w tle.", "Hält die GPU-Auslastung des Servers im Hintergrund gering.", "Снижает нагрузку на видеокарту в фоновом режиме.", "Réduit l'utilisation GPU du serveur en arrière-plan.", "Mantiene basso l'uso della GPU per il server.", "降低后台服务器对显存的占用。" },
            ["ServerFpsLabel"] = new[] { "Sunucu Kare Hızı (FPS):", "Server Frame Rate Cap:", "Limit klatek serwera (FPS):", "Server-FPS-Limit:", "Лимит кадров сервера (FPS):", "Limite FPS du serveur :", "Limite FPS server:", "服务器帧率上限：" },
            ["ServerFpsDesc"] = new[] { "Gereksiz çizimi azaltarak işlemci ve ekran kartı sıcaklığını düşürür.", "Prevents unnecessary rendering to keep temperatures and load low.", "Zmniejsza zbędne renderowanie oraz temperatury podzespołów.", "Verhindert unnötige Bildberechnung und senkt die Last.", "Снижает лишнюю отрисовку, уменьшая нагрев ПК.", "Évite le rendu inutile pour alléger le PC.", "Riduce il carico inutile su CPU e GPU.", "减少不必要的渲染，降低 CPU 与显卡负载。" },
            ["CpuCoreLabel"] = new[] { "İşlemci Çekirdeği:", "CPU Cores:", "Rdzenie procesora:", "CPU-Kerne:", "Ядра процессора:", "Cœurs CPU :", "Core CPU:", "分配的 CPU 核心：" },
            ["CpuCoreDesc"] = new[] { "Sunucuya ayrılacak çekirdek sayısını seçin; kalanlar oyununuza kalır.", "Reserve cores for the server while leaving the rest for your game.", "Przydziel rdzenie dla serwera, zostawiając resztę dla gry.", "Weise dem Server Kerne zu und halte den Rest für dein Spiel frei.", "Выделите ядра для сервера, оставив остальные игре.", "Allouez des cœurs au serveur tout en préservant votre jeu.", "Riserva i core al server lasciando il resto al gioco.", "为服务器分配指定核心，其余留给游戏客户端。" },
            ["PriorityLabel"] = new[] { "İşlem Önceliği:", "Process Priority:", "Priorytet procesu:", "Prozesspriorität:", "Приоритет процесса:", "Priorité du processus :", "Priorità processo:", "进程优先级：" },
            ["PriorityDesc"] = new[] { "Windows üzerinde sunucunun çalışma önceliğini belirler.", "Controls how Windows prioritizes the server process.", "Określa priorytet procesu serwera w systemie Windows.", "Legt die Windows-Priorität des Serverprozesses fest.", "Управляет приоритетом сервера в системе Windows.", "Définit la priorité Windows du processus serveur.", "Imposta la priorità del processo in Windows.", "设置服务器进程在 Windows 中的调度优先级。" },
            ["AutoRamChk"] = new[] { "Otomatik bellek optimizasyonu", "Automatic memory cleanup", "Automatyczne czyszczenie pamięci", "Automatische Speicherbereinigung", "Автоочистка памяти", "Nettoyage mémoire automatique", "Ottimizzazione automatica RAM", "自动内存清理" },
            ["AutoRamDesc"] = new[] { "Kullanılmayan bellek alanlarını düzenli olarak serbest bırakır.", "Periodically frees unused memory in the background.", "Okresowo zwalnia nieużywaną pamięć w tle.", "Gibt ungenutzten Arbeitsspeicher regelmäßig frei.", "Периодически освобождает неиспользуемую память.", "Libère régulièrement la mémoire inutilisée.", "Libera periodicamente la memoria inutilizzata.", "定期在后台释放未使用的内存。" },
            ["DisableSoundChk"] = new[] { "Sunucu seslerini kapat", "Mute server audio", "Wycisz dźwięk serwera", "Server-Audio stummschalten", "Отключить звук сервера", "Couper le son du serveur", "Disattiva audio server", "关闭服务器音频" },
            ["DisableSoundDesc"] = new[] { "Arka plan sunucusunda ses işlenmesini kapatarak kaynak tasarrufu sağlar.", "Saves CPU and memory by disabling audio on the server process.", "Oszczędza zasoby poprzez wyłączenie dźwięku na serwerze.", "Spart CPU und RAM durch Deaktivieren des Server-Audios.", "Экономит ресурсы ЦП и памяти, отключая звук на сервере.", "Économise du CPU et de la RAM en coupant l'audio du serveur.", "Risparmia CPU e RAM disattivando l'audio del server.", "关闭后台服务器音频以节省 CPU 和内存。" },

            ["GameRulesHeader"] = new[] { "KALRADYA OYUN KURALLARI", "CALRADIA GAME RULES", "ZASADY ROZGRYWKI", "SPIELREGELN", "ПРАВИЛА ИГРЫ", "RÈGLES DU JEU", "REGOLE DI GIOCO", "游戏规则" },
            ["BattleSizeLabel"] = new[] { "Savaş Alanı Birlik Limiti:", "Battle Size Limit:", "Limit armii w bitwie:", "Schlachtgröße:", "Размер битвы:", "Taille des batailles :", "Dimensione battaglia:", "战场人数上限：" },
            ["BattleSizeDesc"] = new[] { "Savaş meydanında aynı anda bulunabilecek en fazla asker sayısı.", "Maximum number of soldiers fighting simultaneously on the field.", "Maksymalna liczba żołnierzy jednocześnie na polu bitwy.", "Maximale Anzahl gleichzeitig kämpfender Soldaten.", "Максимум воинов одновременно на поле боя.", "Nombre maximal de soldats simultanés sur le champ de bataille.", "Numero massimo di soldati contemporaneamente in campo.", "战场上同时作战的最大士兵数量。" },
            ["DamageDiffLabel"] = new[] { "Alınan Hasar Zorluğu:", "Damage Difficulty:", "Otrzymywane obrażenia:", "Erlittener Schaden:", "Получаемый урон:", "Dégâts subis :", "Difficoltà danni:", "承受伤害难度：" },
            ["DamageDiffDesc"] = new[] { "Kolay (%33), Dengeli (%66) veya Gerçekçi (%100) hasar oranı.", "Easy (33%), Balanced (66%), or Realistic (100%) damage taken.", "Łatwy (33%), Zbalansowany (66%) lub Realistyczny (100%).", "Einfach (33 %), Ausgewogen (66 %) oder Realistisch (100 %).", "Лёгкий (33%), Сбалансированный (66%) или Реалистичный (100%).", "Facile (33 %), Équilibré (66 %) ou Réaliste (100 %).", "Facile (33%), Bilanciato (66%) o Realistico (100%).", "简单 (33%)、平衡 (66%) 或真实 (100%) 伤害。" },
            ["CombatAiLabel"] = new[] { "Yapay Zeka Savaş Seviyesi:", "Combat AI Level:", "Poziom SI w walce:", "Kampf-KI-Stufe:", "Уровень ИИ в бою:", "Niveau de l'IA au combat :", "Livello IA in battaglia:", "战斗 AI 难度：" },
            ["CombatAiDesc"] = new[] { "Yapay zeka askerlerinin bloklama ve dövüş yeteneğini belirler.", "Controls how smartly AI soldiers block, aim, and maneuver.", "Określa umiejętności walki i blokowania oddziałów SI.", "Bestimmt das Block- und Kampfverhalten der KI-Truppen.", "Определяет тактику и умение ИИ в бою.", "Détermine l'habileté des soldats contrôlés par l'IA.", "Determina l'abilità di combattimento dei soldati IA.", "决定 AI 士兵的格挡、瞄准与战术水平。" },
            ["HeroDeathLabel"] = new[] { "Savaşta Kahraman Ölümü:", "Hero Death in Battle:", "Śmierć bohaterów w bitwie:", "Heldentod im Kampf:", "Смерть героев в бою:", "Mort des héros au combat :", "Morte eroi in battaglia:", "英雄战死规则：" },
            ["HeroDeathDesc"] = new[] { "Lord ve yoldaşların savaş alanında kalıcı olarak ölüp ölemeyeceğini seçin.", "Choose whether lords and companions can die permanently in battle.", "Wybierz, czy lordowie i towarzysze mogą zginąć w bitwie.", "Lege fest, ob Fürsten und Gefährten dauerhaft im Kampf fallen können.", "Решите, могут ли лорды и спутники погибнуть навсегда в бою.", "Choisissez si les seigneurs et compagnons peuvent mourir au combat.", "Scegli se lord e compagni possono morire in battaglia.", "决定领主与同伴是否会在战斗中永久阵亡。" },
            ["AutoSaveLabel"] = new[] { "Otomatik Kayıt Aralığı:", "Auto-Save Interval:", "Automatyczny zapis:", "Automatisches Speichern:", "Интервал автосохранения:", "Sauvegarde automatique :", "Salvataggio automatico:", "自动存档间隔：" },
            ["AutoSaveDesc"] = new[] { "Dünya ilerlemesinin kaç dakikada bir otomatik kaydedileceğini belirler.", "How often your world automatically saves progress.", "Jak często świat zapisuje postępy automatycznie.", "Wie oft der Spielstand automatisch gesichert wird.", "Как часто мир автоматически сохраняет прогресс.", "Fréquence de sauvegarde automatique de votre monde.", "Ogni quanto tempo il mondo viene salvato in automatico.", "设置世界进度自动保存的时间间隔。" },
            ["CorpseLabel"] = new[] { "Savaş Alanı Ceset Sayısı:", "Battlefield Corpse Limit:", "Limit ciał na polu bitwy:", "Leichenlimit im Kampf:", "Лимит тел на поле боя:", "Limite de corps au sol :", "Limite corpi sul campo:", "战场尸体数量上限：" },
            ["CorpseDesc"] = new[] { "Savaş meydanında görünür kalacak en fazla düşmüş asker sayısı.", "Maximum fallen bodies visible on the battlefield.", "Maksymalna liczba poległych widocznych na polu bitwy.", "Maximale Anzahl sichtbarer gefallener Soldaten.", "Максимум павших воинов, остающихся на поле боя.", "Nombre maximal de corps visibles sur le champ de bataille.", "Numero massimo di caduti visibili sul campo.", "战场上保留显示的最大倒地士兵数。" },
            ["PvpChk"] = new[] { "Oyuncular arası savaşlara izin ver", "Allow battles between players", "Zezwalaj na bitwy między graczami", "Kämpfe zwischen Spielern erlauben", "Разрешить сражения между игроками", "Autoriser les batailles entre joueurs", "Consenti battaglie tra giocatori", "允许玩家之间开战" },
            ["PvpDesc"] = new[] { "Farklı klanlardaki lordların birbirine savaş açabilmesini sağlar.", "Allows opposing lords to declare war and clash on the battlefield.", "Pozwala lordom wrogich klanów toczyć ze sobą bitwy.", "Ermöglicht es rivalisierenden Fürsten, gegeneinander zu kämpfen.", "Позволяет враждующим лордам объявлять войну и сражаться.", "Permet aux seigneurs rivaux de se déclarer la guerre.", "Permette ai lord rivali di dichiararsi guerra e combattere.", "允许不同家族的领主相互宣战并在战场交锋。" },
            ["PersistentTimeDesc"] = new[] { "Kimse bağlı olmasa bile Kalradya takvimi akmaya devam eder.", "Calradia's calendar continues advancing even when no one is online.", "Kalendarz Calradii płynie dalej, nawet gdy serwer jest pusty.", "Der Kalender von Calradia läuft weiter, auch wenn niemand online ist.", "Время в Кальрадии идёт дальше, даже когда игроков нет в сети.", "Le calendrier de Calradia continue d'avancer même sans joueur connecté.", "Il calendario di Calradia avanza anche senza giocatori online.", "即使没有玩家在线，卡拉迪亚的时间也会持续流逝。" },
            ["ArenaRespawnChk"] = new[] { "Arenada kesintisiz idman", "Continuous arena practice", "Ciągły trening na arenie", "Durchgehendes Arenatraining", "Непрерывная тренировка на арене", "Entraînement continu en arène", "Allenamento continuo in arena", "竞技场连续练习" },
            ["ArenaRespawnDesc"] = new[] { "Arena idmanında düştüğünüzde kısa süre sonra yeniden başlarsınız.", "Quickly puts you back into the fight during arena practice.", "Szybko przywraca cię do walki podczas treningu na arenie.", "Setzt dich beim Arenatraining nach einem Treffer schnell wieder ein.", "Быстро возвращает в бой во время тренировки на арене.", "Vous remet rapidement en combat lors de l'entraînement.", "Ti riporta subito in combattimento durante l'allenamento.", "在竞技场练习倒下后快速重新投入战斗。" },
            ["ArenaDuelChk"] = new[] { "Şehir arenalarında düellolar", "Town arena duels", "Pojedynki na arenach miast", "Duelle in Stadtarenen", "Дуэли на городских аренах", "Duels dans les arènes", "Duelli nelle arene cittadine", "城镇竞技场决斗" },
            ["ArenaDuelDesc"] = new[] { "Aynı şehirdeki lordlar arenada teke tek düello yapabilir.", "Lords in the same town can challenge each other to an arena duel.", "Lordowie w tym samym mieście mogą stoczyć pojedynek na arenie.", "Fürsten in derselben Stadt können sich in der Arena duellieren.", "Лорды в одном городе могут сразиться один на один на арене.", "Les seigneurs d'une même ville peuvent s'affronter en duel.", "I lord nella stessa città possono sfidarsi a duello in arena.", "同一城镇中的领主可以在竞技场进行一对一决斗。" },

            ["LobbyHeader"] = new[] { "ERİŞİM VE GÜVENLİK", "ACCESS & SECURITY", "DOSTĘP I BEZPIECZEŃSTWO", "ZUGANG & SICHERHEIT", "ДОСТУП И БЕЗОПАСНОСТЬ", "ACCÈS ET SÉCURITÉ", "ACCESSO E SICUREZZA", "访问与安全" },
            ["LobbyNameLabel"] = new[] { "Sunucu Adı:", "Server Name:", "Nazwa serwera:", "Servername:", "Название сервера:", "Nom du serveur :", "Nome del server:", "服务器名称：" },
            ["LobbyPassLabel"] = new[] { "Giriş Şifresi:", "Join Password:", "Hasło wejścia:", "Zugangspasswort:", "Пароль входа:", "Mot de passe :", "Password di accesso:", "加入密码：" },
            ["LobbyPassDesc"] = new[] { "Boş bırakılırsa sunucuya şifresiz girilebilir.", "Leave blank for an open server without a password.", "Pozostaw puste dla serwera bez hasła.", "Leer lassen für freien Zugang ohne Passwort.", "Оставьте пустым для входа без пароля.", "Laissez vide pour un accès sans mot de passe.", "Lascia vuoto per accedere senza password.", "留空则允许无密码加入。" },
            ["MaxPlayersLabel"] = new[] { "Oyuncu Kapasitesi:", "Player Capacity:", "Limit graczy:", "Spielerlimit:", "Макс. игроков:", "Capacité de joueurs :", "Capacità giocatori:", "最大玩家数：" },
            ["VisibilityLabel"] = new[] { "Görünürlük:", "Visibility:", "Widoczność:", "Sichtbarkeit:", "Видимость:", "Visibilité :", "Visibilità:", "可见性：" },
            ["GamePortLabel"] = new[] { "Oyun Portu:", "Game Port:", "Port gry:", "Spielport:", "Игровой порт:", "Port de jeu :", "Porta di gioco:", "游戏端口：" },
            ["FastDlPortLabel"] = new[] { "Mod Paylaşım Portu:", "Mod Transfer Port:", "Port transferu modów:", "Mod-Transfer-Port:", "Порт передачи модов:", "Port de transfert :", "Porta trasferimento:", "模组传输端口：" },
            ["FastDlChk"] = new[] { "Eksik modları otomatik paylaş", "Share missing mods automatically", "Udostępniaj brakujące mody automatycznie", "Fehlende Mods automatisch teilen", "Автоматически раздавать моды", "Partager automatiquement les mods", "Condividi automaticamente le mod", "自动分发缺失模组" },
            ["FastDlDesc"] = new[] { "Katılan oyuncuların eksik modları doğrudan sunucunuzdan indirmesini sağlar.", "Lets joining friends download any missing mods directly from your server.", "Pozwala graczom pobierać brakujące mody bezpośrednio z serwera.", "Lässt Mitspieler fehlende Mods direkt von deinem Server laden.", "Позволяет игрокам скачивать недостающие моды прямо с вашего сервера.", "Permet aux joueurs de télécharger les mods manquants depuis votre serveur.", "Permette ai giocatori di scaricare le mod mancanti dal tuo server.", "让加入的玩家直接从你的服务器下载缺失的模组。" },
            ["ApplyDiskBtn"] = new[] { "Tüm Ayarları Kaydet", "Save All Settings", "Zapisz wszystkie ustawienia", "Alle Einstellungen speichern", "Сохранить все настройки", "Tout enregistrer", "Salva tutto", "保存所有设置" },
            ["BalancedResetBtn"] = new[] { "Önerilen Ayarlara Sıfırla", "Reset to Recommended", "Przywróć zalecane", "Empfohlene Werte laden", "Сбросить к рекомендуемым", "Valeurs recommandées", "Valori consigliati", "恢复推荐设置" },

            // Tab 3: Starter & Progression
            ["ProgTitle"] = new[] { "Başlangıç ve Karakter Gelişimi", "Starter Bonuses & Character Growth", "Start i rozwój postaci", "Startboni & Charakterentwicklung", "Старт и развитие персонажа", "Départ et progression", "Inizio e Crescita Personaggio", "开局奖励与角色成长" },
            ["ProgDesc"] = new[] { "Yeni katılan lordların başlangıç seviyesini, ekipmanlarını, askerlerini ve deneyim hızını buradan belirleyin.", "Customize starting level, gear, troops, and experience rates for every joining lord.", "Ustaw poziom startowy, ekwipunek, oddziały oraz mnożniki doświadczenia.", "Passe Startstufe, Ausrüstung, Truppen und Erfahrungsmultiplikatoren an.", "Настройте стартовый уровень, снаряжение, воинов и скорость прокачки.", "Personnalisez le niveau de départ, l'équipement, les troupes et l'expérience.", "Personalizza livello iniziale, equipaggiamento, truppe e velocità di esperienza.", "为每位新加入的领主自定义开局等级、装备、士兵与经验倍率。" },
            ["ProgCard1"] = new[] { "SEVİYE VE YETENEKLER", "LEVEL & SKILLS", "POZIOM I UMIEJĘTNOŚCI", "STUFE & FERTIGKEITEN", "УРОВЕНЬ И НАВЫКИ", "NIVEAU ET COMPÉTENCES", "LIVELLO E ABILITÀ", "等级与技能" },
            ["StartLvlLabel"] = new[] { "Başlangıç Seviyesi:", "Starting Level:", "Poziom początkowy:", "Startstufe:", "Начальный уровень:", "Niveau de départ :", "Livello iniziale:", "初始等级：" },
            ["StartLvlDesc"] = new[] { "Sefere başlarken verilecek karakter seviyesi.", "Character level granted at the start of the campaign.", "Poziom postaci na początku kampanii.", "Charakterstufe zu Beginn der Kampagne.", "Уровень персонажа в начале кампании.", "Niveau du personnage au début de la campagne.", "Livello del personaggio all'inizio della campagna.", "战役开始时赋予的角色等级。" },
            ["FocusPtsLabel"] = new[] { "Odak Puanları:", "Focus Points:", "Punkty skupienia:", "Fokuspunkte:", "Очки фокуса:", "Points de concentration :", "Punti focus:", "专精点数：" },
            ["FocusPtsDesc"] = new[] { "Yeteneklere dağıtılabilecek serbest odak puanı.", "Unspent focus points to invest in skills.", "Wolne punkty skupienia do rozdania.", "Freie Fokuspunkte für Fertigkeiten.", "Свободные очки фокуса для распределения.", "Points de concentration à répartir.", "Punti focus disponibili per le abilità.", "可自由分配到各项技能的专精点数。" },
            ["AttrPtsLabel"] = new[] { "Nitelik Puanları:", "Attribute Points:", "Punkty atrybutów:", "Attributpunkte:", "Очки характеристик:", "Points d'attribut :", "Punti attributo:", "属性点数：" },
            ["AttrPtsDesc"] = new[] { "Ana niteliklere eklenebilecek serbest puanlar.", "Extra attribute points available at campaign start.", "Dodatkowe punkty atrybutów na start.", "Zusätzliche Attributpunkte zum Start.", "Дополнительные очки характеристик на старте.", "Points d'attribut supplémentaires au départ.", "Punti attributo aggiuntivi all'inizio.", "开局额外赠送的属性点数。" },
            ["MinSkillsLabel"] = new[] { "Taban Yetenek Seviyesi:", "Minimum Skill Level:", "Minimalny poziom umiejętności:", "Mindest-Fertigkeitsstufe:", "Мин. уровень навыков:", "Niveau minimal des compétences :", "Livello minimo abilità:", "最低技能等级：" },
            ["MinSkillsDesc"] = new[] { "Tüm temel yeteneklerin başlayacağı en düşük seviye.", "Minimum starting level across all 18 skills.", "Minimalny poziom startowy dla wszystkich 18 umiejętności.", "Mindeststufe für alle 18 Fertigkeiten.", "Минимальный стартовый уровень всех 18 навыков.", "Niveau minimal appliqué aux 18 compétences.", "Livello minimo applicato a tutte le 18 abilità.", "所有 18 项基础技能的初始保底等级。" },
            ["ProgCard2"] = new[] { "DENEYİM HIZI", "EXPERIENCE RATES", "TEMPO DOŚWIADCZENIA", "ERFAHRUNGSRATEN", "СКОРОСТЬ ПРОКАЧКИ", "VITESSE D'EXPÉRIENCE", "VELOCITÀ ESPERIENZA", "经验获取倍率" },
            ["CharXpLabel"] = new[] { "Karakter Deneyim Çarpanı:", "Character XP Multiplier:", "Mnożnik PD postaci:", "Charakter-EP-Multiplikator:", "Множитель опыта героя:", "Multiplicateur XP héros :", "Moltiplicatore XP eroe:", "角色经验倍率：" },
            ["CharXpDesc"] = new[] { "Karakterin seviye atlama hızını belirler.", "Scales how quickly characters level up.", "Określa tempo zdobywania poziomów postaci.", "Bestimmt, wie schnell Charaktere aufsteigen.", "Определяет скорость повышения уровня персонажа.", "Définit la vitesse de montée de niveau.", "Determina la velocità di salita di livello.", "调整角色升级的整体速度。" },
            ["SkillXpLabel"] = new[] { "Yetenek Deneyim Çarpanı:", "Skill XP Multiplier:", "Mnożnik PD umiejętności:", "Fertigkeits-EP-Multiplikator:", "Множитель опыта навыков:", "Multiplicateur XP compétences :", "Moltiplicatore XP abilità:", "技能经验倍率：" },
            ["SkillXpDesc"] = new[] { "Savaş, binicilik, ticaret ve zanaat gelişimini hızlandırır.", "Scales skill gains from combat, riding, trade, and smithing.", "Przyspiesza rozwój umiejętności walki, jazdy, handlu i kowalstwa.", "Beschleunigt Fortschritte in Kampf, Reiten, Handel und Schmieden.", "Ускоряет прокачку боя, верховой езды, торговли и кузнечного дела.", "Accélère les progrès au combat, à cheval, au commerce et à la forge.", "Accelera i progressi in combattimento, equitazione, commercio e forgiatura.", "加快战斗、骑术、交易与锻造等技能的提升速度。" },
            ["XpInfoTitle"] = new[] { "Ortak Gelişim Dengesi", "Shared Progression", "Wspólny postęp", "Gemeinsamer Fortschritt", "Единый баланс прокачки", "Progression synchronisée", "Crescita sincronizzata", "全局同步成长" },
            ["XpInfoDesc"] = new[] { "Bu oranlar sunucudaki tüm oyuncular için otomatik olarak geçerli olur.", "These multipliers apply automatically to every player on the server.", "Te mnożniki działają automatycznie dla wszystkich graczy na serwerze.", "Diese Raten gelten automatisch für alle Spieler auf dem Server.", "Эти множители автоматически применяются ко всем игрокам.", "Ces taux s'appliquent automatiquement à tous les joueurs.", "Questi tassi si applicano automaticamente a tutti i giocatori.", "这些倍率会自动同步并生效于服务器上的所有玩家。" },
            ["ProgCard3"] = new[] { "EKONOMİ VE KLAN", "ECONOMY & CLAN", "EKONOMIA I KLAN", "WIRTSCHAFT & KLAN", "ЭКОНОМИКА И КЛАН", "ÉCONOMIE ET CLAN", "ECONOMIA E CLAN", "经济与家族" },
            ["StartGoldLabel"] = new[] { "Başlangıç Altını:", "Starting Gold:", "Złoto na start:", "Startgold:", "Начальное золото:", "Or de départ :", "Oro iniziale:", "初始第纳尔：" },
            ["StartGoldDesc"] = new[] { "Her oyuncunun sefere başlarken sahip olacağı altın.", "Denars given to each player upon starting.", "Liczba denarów na rozpoczęcie gry.", "Denare für jeden Spieler zu Beginn.", "Сумма динаров, выдаваемая на старте.", "Deniers accordés à chaque joueur au départ.", "Denari assegnati a ogni giocatore all'inizio.", "每位玩家开局拥有的初始第纳尔。" },
            ["StartRenownLabel"] = new[] { "Başlangıç Klan Namı:", "Starting Clan Renown:", "Początkowa sława klanu:", "Anfänglicher Klanruhm:", "Начальная известность:", "Renommée initiale :", "Fama iniziale del clan:", "初始家族声望：" },
            ["StartRenownDesc"] = new[] { "Klanın başlangıç kademesini belirler.", "Determines the clan's starting tier.", "Określa początkowy poziom klanu.", "Bestimmt den anfänglichen Klanrang.", "Определяет начальный уровень клана.", "Détermine le rang initial du clan.", "Determina il grado iniziale del clan.", "决定家族的初始等级。" },
            ["StartInflLabel"] = new[] { "Başlangıç Nüfuzu:", "Starting Influence:", "Początkowe wpływy:", "Anfänglicher Einfluss:", "Начальное влияние:", "Influence initiale :", "Influenza iniziale:", "初始影响力：" },
            ["StartInflDesc"] = new[] { "Krallık kararları ve ordu çağrıları için başlangıç nüfuzu.", "Political influence usable in kingdom votes and armies.", "Wpływy polityczne do decyzji królestwa i armii.", "Politischer Einfluss für Abstimmungen und Armeen.", "Влияние для решений королевства и созыва армий.", "Influence utilisable pour les décisions et armées.", "Influenza politica per decisioni del regno ed eserciti.", "用于王国决策与召集军团的初始影响力。" },
            ["ProgCard4"] = new[] { "EKİPMAN, ERZAK VE ASKER", "GEAR, RATIONS & TROOPS", "EKWIPUNEK, PROWIANT I WOJSKO", "AUSRÜSTUNG, PROVIANT & TRUPPEN", "СНАРЯЖЕНИЕ, ЕДА И ВОИНЫ", "ÉQUIPEMENT, VIVRES ET TROUPES", "EQUIPAGGIAMENTO E TRUPPE", "装备、粮草与士兵" },
            ["StartEquipLabel"] = new[] { "Başlangıç Ekipmanı:", "Starter Gear:", "Ekwipunek startowy:", "Startausrüstung:", "Стартовое снаряжение:", "Équipement de départ :", "Equipaggiamento iniziale:", "开局装备：" },
            ["StartEquipDesc"] = new[] { "Seçilen kademedeki silah, zırh ve at envantere eklenir.", "Adds the chosen tier of weapons, armor, and mount.", "Dodaje broń, pancerz i wierzchowca wybranego poziomu.", "Fügt Waffen, Rüstung und Reittier der gewählten Stufe hinzu.", "Добавляет оружие, броню и коня выбранного уровня.", "Ajoute les armes, armures et monture du palier choisi.", "Aggiunge armi, armatura e cavalcatura del livello scelto.", "将所选档次的武器、护甲和坐骑添加到背包。" },
            ["StartTroopsLabel"] = new[] { "Başlangıç Birlikleri:", "Starter Troops:", "Oddziały startowe:", "Starttruppen:", "Стартовый отряд:", "Troupes de départ :", "Truppe iniziali:", "开局士兵：" },
            ["StartTroopsDesc"] = new[] { "Karakterin kültürüne uygun başlangıç askerleri birliğe eklenir.", "Recruits culture-matched soldiers into the starting party.", "Dodaje żołnierzy pasujących do kultury bohatera.", "Fügt kulturgerechte Soldaten zur Starttruppe hinzu.", "Добавляет воинов выбранной культуры в отряд.", "Ajoute des soldats adaptés à la culture du personnage.", "Aggiunge soldati della cultura scelta al gruppo iniziale.", "按角色文化将初始士兵加入部队。" },
            ["FoodChk"] = new[] { "Başlangıç erzak paketi ekle", "Include starter food rations", "Dołącz prowiant startowy", "Startproviant hinzufügen", "Выдать стартовый провиант", "Inclure des vivres de départ", "Includi provviste iniziali", "附带开局粮草" },
            ["FoodDesc"] = new[] { "İlk günler için tahıl, et, tereyağı ve peynir ekler.", "Adds grain, meat, butter, and cheese for the first days.", "Dodaje zboże, mięso, masło i ser na pierwsze dni.", "Fügt Getreide, Fleisch, Butter und Käse für die ersten Tage hinzu.", "Добавляет зерно, мясо, масло и сыр на первые дни.", "Ajoute céréales, viande, beurre et fromage pour bien démarrer.", "Aggiunge grano, carne, burro e formaggio per i primi giorni.", "为前几天提供谷物、肉类、黄油和奶酪。" },

            // Tab 4: Admin Tools
            ["SelectPlayerLabel"] = new[] { "Oyuncu Seç:", "Select Player:", "Wybierz gracza:", "Spieler wählen:", "Выберите игрока:", "Choisir un joueur :", "Seleziona giocatore:", "选择玩家：" },
            ["ManualTargetLabel"] = new[] { "veya İsim / Tümü:", "or Name / 'all':", "lub nazwa / 'all':", "oder Name / 'all':", "или имя / 'all':", "ou nom / 'all' :", "o nome / 'all':", "或输入名称 / 'all'：" },
            ["ManualTargetTip"] = new[] { "Örn: 'all' veya oyuncu adı", "e.g. 'all' or player name", "np. 'all' lub nazwa gracza", "z. B. 'all' oder Spielername", "напр.: 'all' или имя игрока", "ex. 'all' ou nom du joueur", "es. 'all' o nome giocatore", "例如 'all' 或玩家名" },
            ["RefreshPlayersBtn"] = new[] { "Listeyi Yenile", "Refresh List", "Odśwież listę", "Liste aktualisieren", "Обновить список", "Actualiser", "Aggiorna lista", "刷新列表" },
            ["TargetBadgePrefix"] = new[] { "Hedef: ", "Target: ", "Cel: ", "Ziel: ", "Цель: ", "Cible : ", "Obiettivo: ", "目标：" },
            ["GoldCardTitle"] = new[] { "ALTIN GÖNDER", "SEND GOLD", "WYŚLIJ ZŁOTO", "GOLD SENDEN", "ВЫДАТЬ ЗОЛОТО", "ENVOYER DE L'OR", "INVIA ORO", "发放金币" },
            ["GoldCardDesc"] = new[] { "Seçili oyuncuya anında altın ekler:", "Instantly grants denars to the selected player:", "Natychmiast przekazuje złoto wybranemu graczowi:", "Überweist dem gewählten Spieler sofort Denare:", "Мгновенно начисляет динары выбранному игроку:", "Accorde immédiatement des deniers au joueur choisi :", "Assegna subito denari al giocatore selezionato:", "立即向所选玩家发放第纳尔：" },
            ["SendGoldBtn"] = new[] { "Altın Gönder", "Send Gold", "Wyślij złoto", "Gold senden", "Выдать", "Envoyer", "Invia oro", "发放金币" },
            ["RenownInflTitle"] = new[] { "NAM VE NÜFUZ", "RENOWN & INFLUENCE", "SŁAWA I WPŁYWY", "RUHM & EINFLUSS", "ИЗВЕСТНОСТЬ И ВЛИЯНИЕ", "RENOMMÉE ET INFLUENCE", "FAMA E INFLUENZA", "声望与影响力" },
            ["RenownLabel"] = new[] { "Klan Namı:", "Clan Renown:", "Sława klanu:", "Klanruhm:", "Известность клана:", "Renommée du clan :", "Fama del clan:", "家族声望：" },
            ["InflLabel"] = new[] { "Krallık Nüfuzu:", "Kingdom Influence:", "Wpływy królestwa:", "Königreicheinfluss:", "Влияние в королевстве:", "Influence :", "Influenza:", "王国影响力：" },
            ["TroopsTitle"] = new[] { "ASKER TAKVİYESİ", "TROOP REINFORCEMENTS", "POSILKI WOJSKOWE", "TRUPPENVERSTÄRKUNG", "ПОДКРЕПЛЕНИЕ", "RENFORTS DE TROUPES", "RINFORZI TRUPPE", "增援士兵" },
            ["EliteTroopsLabel"] = new[] { "Seçkin Birlikler:", "Elite Troops:", "Elitarne oddziały:", "Elitetruppen:", "Элитные воины:", "Troupes d'élite :", "Truppe d'élite:", "精锐兵种：" },
            ["CustomTroopTip"] = new[] { "İsteğe bağlı özel birlik kimliği (boş bırakırsanız yukarıdaki seçilir)", "Optional custom troop ID (blank uses the dropdown above)", "Opcjonalne ID jednostki (puste używa listy powyżej)", "Optionale Truppen-ID (leer nutzt die Auswahl oben)", "Необязательный ID отряда (пусто — выбор выше)", "ID d'unité optionnel (vide = liste ci-dessus)", "ID truppa opzionale (vuoto = scelta sopra)", "可选自定义兵种 ID（留空则使用上方选择）" },
            ["AmountTip"] = new[] { "Adet", "Amount", "Ilość", "Menge", "Кол-во", "Quantité", "Quantità", "数量" },
            ["GiveTroopsBtn"] = new[] { "Asker Gönder", "Send Troops", "Wyślij wojsko", "Truppen senden", "Выдать воинов", "Envoyer troupes", "Invia truppe", "派遣士兵" },
            ["DupTroopsBtn"] = new[] { "Birliği Çoğalt (+10)", "Reinforce Party (+10)", "Wzmocnij oddział (+10)", "Trupp verstärken (+10)", "Усилить отряд (+10)", "Renforcer (+10)", "Rinforza (+10)", "扩充部队 (+10)" },
            ["HealAdminTitle"] = new[] { "İYİLEŞTİRME VE YETKİ", "HEALING & PERMISSIONS", "LECZENIE I UPRAWNIENIA", "HEILUNG & RECHTE", "ЛЕЧЕНИЕ И ПРАВА", "SOINS ET DROITS", "CURE E PERMESSI", "治疗与权限" },
            ["HealHeroBtn"] = new[] { "Kahramanı İyileştir (%100)", "Heal Hero (100%)", "Ulecz bohatera (100%)", "Held vollständig heilen", "Исцелить героя (100%)", "Soigner le héros (100 %)", "Cura eroe (100%)", "完全治疗英雄 (100%)" },
            ["HealPartyBtn"] = new[] { "Yaralı Askerleri İyileştir", "Heal Wounded Troops", "Ulecz rannych żołnierzy", "Verwundete Truppen heilen", "Вылечить раненых воинов", "Soigner les blessés", "Cura truppe ferite", "治疗受伤士兵" },
            ["ToggleAdminBtn"] = new[] { "Yönetici Yetkisi Ver / Al (F10)", "Grant / Revoke Admin (F10)", "Nadaj / odbierz admina (F10)", "Admin-Rechte erteilen / entziehen", "Выдать / снять права админа (F10)", "Accorder / retirer admin (F10)", "Assegna / revoca Admin (F10)", "授予 / 撤销管理员权限 (F10)" },
            ["AdminHint"] = new[] { "Yetkili oyuncular oyun içinde F10 tuşu ile yönetici panelini açabilir.", "Authorized admins can press F10 in-game to open the admin panel.", "Uprawnieni administratorzy mogą otworzyć panel klawiszem F10 w grze.", "Berechtigte Admins öffnen das Admin-Menü im Spiel mit F10.", "Администраторы могут открыть панель в игре клавишей F10.", "Les administrateurs peuvent ouvrir le panneau en jeu avec F10.", "Gli amministratori possono aprire il pannello in gioco con F10.", "管理员可在游戏内按 F10 打开管理面板。" },
            ["OnlinePlayersTitle"] = new[] { "ÇEVRİMİÇİ VE KAYITLI OYUNCULAR", "ONLINE & REGISTERED PLAYERS", "GRACZE ONLINE I ZAREJESTROWANI", "ONLINE- & REGISTRIERTE SPIELER", "ИГРОКИ В СЕТИ И В БАЗЕ", "JOUEURS EN LIGNE ET INSCRITS", "GIOCATORI ONLINE E REGISTRATI", "在线与已注册玩家" },
            ["ColUsername"] = new[] { "Hesap", "Account", "Konto", "Konto", "Аккаунт", "Compte", "Account", "账号" },
            ["ColHeroName"] = new[] { "Kahraman", "Hero", "Bohater", "Held", "Герой", "Héros", "Eroe", "英雄" },
            ["ColClan"] = new[] { "Klan", "Clan", "Klan", "Klan", "Клан", "Clan", "Clan", "家族" },
            ["ColGold"] = new[] { "Altın", "Gold", "Złoto", "Gold", "Золото", "Or", "Oro", "金币" },
            ["ColRenown"] = new[] { "Nam", "Renown", "Sława", "Ruhm", "Слава", "Renommée", "Fama", "声望" },
            ["ColInfluence"] = new[] { "Nüfuz", "Influence", "Wpływy", "Einfluss", "Влияние", "Influence", "Influenza", "影响力" },
            ["ColArmy"] = new[] { "Birlik", "Party", "Oddział", "Trupp", "Отряд", "Troupe", "Gruppo", "部队" },
            ["ColHealth"] = new[] { "Can", "Health", "Zdrowie", "Leben", "Здоровье", "Santé", "Salute", "生命" },
            ["ColAdmin"] = new[] { "Yönetici", "Admin", "Admin", "Admin", "Админ", "Admin", "Admin", "管理员" },
            ["ColConnected"] = new[] { "Durum", "Online", "Online", "Online", "В сети", "En ligne", "Online", "在线" },

            // Tab 5: Users
            ["SearchUserLabel"] = new[] { "Hesap Ara:", "Search Account:", "Szukaj konta:", "Konto suchen:", "Поиск аккаунта:", "Rechercher :", "Cerca account:", "搜索账号：" },
            ["AutoRegChk"] = new[] { "Yeni oyuncular ilk girişte otomatik kayıt olabilsin", "Allow new players to register automatically on first join", "Zezwalaj na automatyczną rejestrację nowych graczy", "Automatische Registrierung neuer Spieler erlauben", "Разрешить авторегистрацию при первом входе", "Autoriser l'inscription automatique à la première connexion", "Consenti registrazione automatica al primo accesso", "允许新玩家首次加入时自动注册" },
            ["UsersTableTitle"] = new[] { "KAYITLI HESAPLAR", "REGISTERED ACCOUNTS", "ZAREJESTROWANE KONTA", "REGISTRIERTE KONTEN", "ЗАРЕГИСТРИРОВАННЫЕ АККАУНТЫ", "COMPTES ENREGISTRÉS", "ACCOUNT REGISTRATI", "已注册账号" },
            ["ColUserUsername"] = new[] { "Kullanıcı Adı", "Username", "Nazwa użytkownika", "Benutzername", "Имя пользователя", "Nom d'utilisateur", "Nome utente", "用户名" },
            ["ColUserClan"] = new[] { "Klan", "Clan", "Klan", "Klan", "Клан", "Clan", "Clan", "家族" },
            ["ColUserAdmin"] = new[] { "Yönetici", "Admin", "Admin", "Admin", "Админ", "Admin", "Admin", "管理员" },
            ["ColUserBanned"] = new[] { "Yasaklı", "Banned", "Zbanowany", "Gesperrt", "Заблокирован", "Banni", "Bannato", "封禁" },
            ["ColUserCreated"] = new[] { "Kayıt Tarihi", "Joined", "Data utworzenia", "Erstellt am", "Дата регистрации", "Date de création", "Data creazione", "注册时间" },
            ["NewUserHeader"] = new[] { "YENİ HESAP OLUŞTUR", "CREATE NEW ACCOUNT", "UTWÓRZ NOWE KONTO", "NEUES KONTO ERSTELLEN", "СОЗДАТЬ АККАУНТ", "CRÉER UN COMPTE", "CREA NUOVO ACCOUNT", "创建新账号" },
            ["NewUserNameLabel"] = new[] { "Kullanıcı Adı:", "Username:", "Nazwa użytkownika:", "Benutzername:", "Имя пользователя:", "Nom d'utilisateur :", "Nome utente:", "用户名：" },
            ["NewUserPassLabel"] = new[] { "Şifre:", "Password:", "Hasło:", "Passwort:", "Пароль:", "Mot de passe :", "Password:", "密码：" },
            ["NewUserAdminChk"] = new[] { "Yönetici yetkisi ver", "Grant admin privileges", "Nadaj uprawnienia administratora", "Admin-Rechte vergeben", "Выдать права администратора", "Accorder les droits d'administrateur", "Assegna privilegi di amministratore", "授予管理员权限" },
            ["CreateAccBtn"] = new[] { "Hesap Oluştur", "Create Account", "Utwórz konto", "Konto erstellen", "Создать аккаунт", "Créer le compte", "Crea account", "创建账号" },
            ["ManageUserHeader"] = new[] { "SEÇİLİ HESABI YÖNET", "MANAGE SELECTED ACCOUNT", "ZARZĄDZAJ KONTEM", "KONTO VERWALTEN", "УПРАВЛЕНИЕ АККАУНТОМ", "GÉRER LE COMPTE", "GESTISCI ACCOUNT", "管理选中账号" },
            ["NewPassResetLabel"] = new[] { "Yeni Şifre:", "New Password:", "Nowe hasło:", "Neues Passwort:", "Новый пароль:", "Nouveau mot de passe :", "Nuova password:", "新密码：" },
            ["ResetPassBtn"] = new[] { "Şifreyi Güncelle", "Update Password", "Zmień hasło", "Passwort ändern", "Сменить пароль", "Mettre à jour", "Aggiorna password", "修改密码" },
            ["ToggleBanBtn"] = new[] { "Yasakla / Yasağı Kaldır", "Ban / Unban", "Zbanuj / Odbanuj", "Sperren / Entsperren", "Забанить / Разбанить", "Bannir / Débannir", "Banna / Sbanna", "封禁 / 解封" },
            ["DeleteAccBtn"] = new[] { "Hesabı Sil", "Delete Account", "Usuń konto", "Konto löschen", "Удалить аккаунт", "Supprimer le compte", "Elimina account", "删除账号" },

            // Tab 6: Join Server
            ["ServerIpLabel"] = new[] { "Sunucu IP:", "Server IP:", "IP serwera:", "Server-IP:", "IP сервера:", "IP du serveur :", "IP server:", "服务器 IP：" },
            ["FastDlLabel"] = new[] { "Mod Portu:", "Mod Port:", "Port modów:", "Mod-Port:", "Порт модов:", "Port mods :", "Porta mod:", "模组端口：" },
            ["GamePortLabelShort"] = new[] { "Oyun Portu:", "Game Port:", "Port gry:", "Spielport:", "Порт игры:", "Port jeu :", "Porta gioco:", "游戏端口：" },
            ["CheckModsBtn"] = new[] { "Modları Kontrol Et", "Check Server Mods", "Sprawdź mody serwera", "Server-Mods prüfen", "Проверить моды", "Vérifier les mods", "Controlla mod", "检查服务器模组" },
            ["ColModName"] = new[] { "Mod Adı", "Mod Name", "Nazwa moda", "Mod-Name", "Название мода", "Nom du mod", "Nome mod", "模组名称" },
            ["ColModId"] = new[] { "Mod Kimliği", "Mod ID", "ID moda", "Mod-ID", "ID мода", "ID du mod", "ID mod", "模组 ID" },
            ["ColModVersion"] = new[] { "Sürüm", "Version", "Wersja", "Version", "Версия", "Version", "Versione", "版本" },
            ["ColModSize"] = new[] { "Boyut", "Size", "Rozmiar", "Größe", "Размер", "Taille", "Dimensione", "大小" },
            ["DownloadModsBtn"] = new[] { "Eksik Modları İndir ve Kur", "Download & Install Missing Mods", "Pobierz brakujące mody", "Fehlende Mods installieren", "Скачать недостающие моды", "Télécharger les mods manquants", "Scarica mod mancanti", "下载并安装缺失模组" },
            ["LaunchJoinBtn"] = new[] { "Oyunu Başlat ve Bağlan", "Launch Game & Connect", "Uruchom grę i połącz", "Spiel starten & verbinden", "Запустить игру и войти", "Lancer le jeu et se connecter", "Avvia gioco e connetti", "启动游戏并连接" }
        };

        private static int GetLangIndex(string code) => code.ToUpperInvariant() switch
        {
            "TR" => 0,
            "EN" => 1,
            "PL" => 2,
            "DE" => 3,
            "RU" => 4,
            "FR" => 5,
            "IT" => 6,
            "ZH" => 7,
            _ => 1
        };

        public string this[string key]
        {
            get
            {
                if (Strings.TryGetValue(key, out var arr))
                {
                    int idx = GetLangIndex(Loc.CurrentLanguage);
                    if (idx >= 0 && idx < arr.Length && !string.IsNullOrEmpty(arr[idx]))
                    {
                        return arr[idx];
                    }
                    return arr[1]; // EN fallback
                }
                return key;
            }
        }

        public void SetLanguage(string? lang)
        {
            Loc.SetLanguage(lang);
            NotifyAll();
        }

        public string ToggleLanguage()
        {
            Loc.ToggleLanguage();
            NotifyAll();
            return Loc.CurrentLanguage;
        }

        public void NotifyAll()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }
}
