using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoopServerManager.Models;
using Newtonsoft.Json.Linq;

namespace CoopServerManager.Services
{
    public class ServerRunnerService
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
        private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const int SW_HIDE = 0;

        private Process? serverProcess;
        private IntPtr? jobHandle;
        private CancellationTokenSource? logCts;
        private Task? logTailTask;
        private CancellationTokenSource? ramCleanCts;

        public bool IsRunning => serverProcess != null && !serverProcess.HasExited;
        public int? ProcessId => IsRunning ? serverProcess?.Id : null;
        public Process? ServerProcess => serverProcess;

        public event Action<string>? OnLog;
        public event Action<bool>? OnStateChanged;

        public bool StartServer(string bannerlordDir, IEnumerable<string> moduleIds, string? saveName, ServerConfig config)
        {
            if (IsRunning)
            {
                OnLog?.Invoke("[Uyarı] Sunucu zaten çalışıyor.");
                return false;
            }

            // Pre-flight check: Steam client MUST be running for Bannerlord (Steamworks)
            if (!EnsureSteamIsRunning())
            {
                return false;
            }

            var exePath = Path.Combine(bannerlordDir, "bin", "Win64_Shipping_Client", "Bannerlord.exe");
            if (!File.Exists(exePath))
            {
                OnLog?.Invoke($"[Hata] Bannerlord.exe bulunamadı: {exePath}");
                return false;
            }

            var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;

            // Ensure steam_appid.txt exists in both workingDir and bannerlordDir
            EnsureSteamAppId(bannerlordDir, workingDir);

            // Ensure proper module load order: Native -> SandBoxCore -> CustomBattle -> SandBox -> (StoryMode -> NavalDLC) -> CalradiaConnected -> Custom mods
            var finalModules = ModuleScannerService.BuildLaunchModuleList(bannerlordDir, moduleIds, config.EnableWarSails);
            var moduleString = "_MODULES_*" + string.Join("*", finalModules) + "*_MODULES_";

            var arguments = new List<string>
            {
                "/singleplayer",
                "/server",
                "/autoconnect",
                moduleString
            };

            string? effectiveSave = saveName;
            var saveSvc = new SaveGameService();
            string serverTitleSaveName = SaveGameService.SanitizeSaveFileName(config.ServerName);

            if (string.IsNullOrWhiteSpace(effectiveSave) ||
                effectiveSave.StartsWith("Yeni Oyun", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("Yeni_Oyun", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("NEW_GAME", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("New Game", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("Nowa gra", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("Neues Spiel", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("Новая игра", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("Nouvelle partie", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("Nuova partita", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Equals("新游戏", StringComparison.OrdinalIgnoreCase) ||
                effectiveSave.Contains("Bulunamadı"))
            {
                effectiveSave = "NEW_GAME";
                ClearAppliedStarterRecords();

                // Clear previous saves and hero records so server generates a fresh world and FastDL doesn't serve stale data
                try
                {
                    var primaryFolder = saveSvc.GetPrimarySavesFolder();
                    foreach (var staleFile in new[] { $"{serverTitleSaveName}.sav", "Yeni_Oyun.sav", "CalradiaConnected_World.sav" })
                    {
                        var stalePath = Path.Combine(primaryFolder, staleFile);
                        if (File.Exists(stalePath))
                        {
                            File.Delete(stalePath);
                        }
                    }

                    Common.Users.UserDatabase.Instance.ResetAllHeroInfo();
                }
                catch { }

                OnLog?.Invoke($"[Sunucu] 'Yeni Oyun' seçildi. Sunucu başlığına göre '{serverTitleSaveName}.sav' adıyla yeni kayıt oluşturuluyor...");
            }
            else
            {
                serverTitleSaveName = Path.GetFileNameWithoutExtension(effectiveSave);
                OnLog?.Invoke($"[Sunucu] Seçilen kayıt dosyası: '{effectiveSave}'");
            }

            arguments.Add("/coopsave");
            arguments.Add(effectiveSave);
            arguments.Add("/newgamesave");
            arguments.Add(serverTitleSaveName);
            arguments.Add("/port");
            arguments.Add((config.ServerPort > 0 ? config.ServerPort : 4200).ToString());
            arguments.Add("/servername");
            arguments.Add(string.IsNullOrWhiteSpace(config.ServerName) ? "Calradia Connected" : config.ServerName.Trim());
            arguments.Add("/maxplayers");
            arguments.Add((config.MaxPlayers > 0 ? config.MaxPlayers : 16).ToString());

            if (!string.IsNullOrWhiteSpace(config.ServerPassword))
            {
                arguments.Add("/cooppassword");
                arguments.Add(config.ServerPassword.Trim());
            }

            if (!string.IsNullOrWhiteSpace(config.ServerVisibility))
            {
                arguments.Add("/coopvisibility");
                arguments.Add(config.ServerVisibility.Trim());
            }

            // Apply Hardware & Game Configurations to Bannerlord config files prior to start
            ApplyConfigurationFiles(config);

            var startInfo = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
            };

            startInfo.EnvironmentVariables["SteamAppId"] = "261550";
            startInfo.EnvironmentVariables["SteamGameId"] = "261550";

            var docFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var preferredCoopDir = Path.Combine(docFolder, "Mount and Blade II Bannerlord", "CalradiaConnectedData");
            startInfo.EnvironmentVariables["COOP_DATA_DIR"] = preferredCoopDir;
            startInfo.EnvironmentVariables["CALRADIA_CONNECTED_DATA_DIR"] = preferredCoopDir;
            startInfo.EnvironmentVariables["COOP_USER_DB"] = Common.Users.UserDatabase.Instance.DbFilePath;

            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            try
            {
                OnLog?.Invoke("[Başlatıcı] Bannerlord Co-op arka plan motoru yapılandırılıyor...");
                OnLog?.Invoke($"[Yüklenen Modlar] {string.Join(", ", finalModules)}");

                serverProcess = Process.Start(startInfo);

                if (serverProcess != null)
                {
                    serverProcess.EnableRaisingEvents = true;
                    serverProcess.Exited += (s, e) =>
                    {
                        OnLog?.Invoke($"[Sunucu Durumu] Sunucu işlemi kapandı (PID: {serverProcess?.Id ?? 0}).");
                        StopLogTail();
                        StopRamCleaner();
                        CloseJobHandle();
                        serverProcess = null;
                        OnStateChanged?.Invoke(false);
                    };

                    // 1. Apply Process Priority
                    try
                    {
                        serverProcess.PriorityClass = config.ProcessPriority switch
                        {
                            "AboveNormal" => ProcessPriorityClass.AboveNormal,
                            "High" => ProcessPriorityClass.High,
                            "BelowNormal" => ProcessPriorityClass.BelowNormal,
                            _ => ProcessPriorityClass.Normal
                        };
                        OnLog?.Invoke($"[İşlemci Önceliği] Öncelik seviyesi: '{serverProcess.PriorityClass}' olarak ayarlandı.");
                    }
                    catch { }

                    // 2. Apply CPU Core Affinity
                    try
                    {
                        if (config.CpuCoresAllocated > 0 && config.CpuCoresAllocated <= Environment.ProcessorCount)
                        {
                            long mask = 0;
                            for (int i = 0; i < config.CpuCoresAllocated; i++)
                            {
                                mask |= (1L << i);
                            }
                            serverProcess.ProcessorAffinity = (IntPtr)mask;
                            OnLog?.Invoke($"[CPU Tahsisi] Sunucuya {config.CpuCoresAllocated} adet mantıksal çekirdek tahsis edildi (Mask: 0x{mask:X}).");
                        }
                        else
                        {
                            OnLog?.Invoke($"[CPU Tahsisi] Tüm işlemci çekirdekleri ({Environment.ProcessorCount} çekirdek) devrede.");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"[CPU Tahsis Uyarısı] {ex.Message}");
                    }

                    // 3. Apply RAM Memory Limit via Windows WorkingSet & Job Object
                    try
                    {
                        bool hasWarSails = config.EnableWarSails ||
                            finalModules.Any(m => string.Equals(m, "NavalDLC", StringComparison.OrdinalIgnoreCase));

                        if (ShouldApplyHardRamCap(config.MaxRamLimitGb, hasWarSails))
                        {
                            long maxBytes = (long)config.MaxRamLimitGb * 1024L * 1024L * 1024L;
                            try
                            {
                                SetProcessWorkingSetSize(serverProcess.Handle, (IntPtr)(-1), (IntPtr)maxBytes);
                            }
                            catch { }

                            // Delay Job Object assignment slightly so Steamworks and engine hooks initialize cleanly
                            var proc = serverProcess;
                            Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(10000).ConfigureAwait(false);
                                    if (IsRunning && proc != null && !proc.HasExited)
                                    {
                                        ApplyJobMemoryLimit(proc.Handle, maxBytes);
                                    }
                                }
                                catch { }
                            });

                            OnLog?.Invoke($"[RAM Limiti] Maksimum RAM sınırı {config.MaxRamLimitGb} GB ({maxBytes / (1024 * 1024)} MB) olarak kilitlendi.");
                        }
                        else if (config.MaxRamLimitGb > 0 && hasWarSails)
                        {
                            OnLog?.Invoke($"[RAM Limiti] War Sails (NavalDLC) dünya kaydı sırasında bellek yetersizliği (OOM) çökmesini önlemek için {config.MaxRamLimitGb} GB katı RAM sınırı atlandı (Sınırsız / Otomatik mod).");
                        }
                        else
                        {
                            OnLog?.Invoke("[RAM Limiti] Sınırsız / Sistem Otomatik modu devrede.");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"[RAM Limit Uyarısı] {ex.Message}");
                    }

                    // 4. Start Background RAM Cleaner if enabled
                    if (config.AutoRamCleanEnabled)
                    {
                        StartRamCleaner();
                        OnLog?.Invoke("[Bellek Optimizasyonu] Otomatik periyodik RAM temizleyici (Working Set Trimmer) devrede.");
                    }

                    OnLog?.Invoke($"[Sunucu Başarılı] PID: {serverProcess.Id} ile arka plan sunucusu hazır.");
                    OnStateChanged?.Invoke(true);

                    // Start background log tailing of Coop_server.log
                    var logFilePath = Path.Combine(workingDir, "Coop_server.log");
                    StartLogTail(logFilePath);

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Sunucu Başlatılamadı] {ex.Message}");
                serverProcess = null;
                OnStateChanged?.Invoke(false);
                return false;
            }
        }

        private void ApplyJobMemoryLimit(IntPtr processHandle, long maxBytes)
        {
            CloseJobHandle();
            try
            {
                var hJob = CreateJobObject(IntPtr.Zero, null);
                if (hJob == IntPtr.Zero) return;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_BREAKAWAY_OK | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK
                    },
                    ProcessMemoryLimit = new UIntPtr((ulong)maxBytes)
                };

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                    {
                        if (AssignProcessToJobObject(hJob, processHandle))
                        {
                            jobHandle = hJob;
                            return;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }

                CloseHandle(hJob);
            }
            catch
            {
            }
        }

        private void CloseJobHandle()
        {
            if (jobHandle.HasValue && jobHandle.Value != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(jobHandle.Value);
                }
                catch { }
                finally
                {
                    jobHandle = null;
                }
            }
        }

        public static bool ShouldApplyHardRamCap(int maxRamLimitGb, bool hasWarSails)
        {
            if (maxRamLimitGb <= 0) return false;
            return !hasWarSails || maxRamLimitGb >= 14;
        }

        private void StartRamCleaner()
        {
            StopRamCleaner();
            ramCleanCts = new CancellationTokenSource();
            var token = ramCleanCts.Token;

            Task.Run(async () =>
            {
                // Allow initial world load and startup SaveAs to finish without trimming the working set.
                await Task.Delay(90000, token).ConfigureAwait(false);

                while (!token.IsCancellationRequested && IsRunning)
                {
                    if (token.IsCancellationRequested || !IsRunning || serverProcess == null) break;

                    try
                    {
                        EmptyWorkingSet(serverProcess.Handle);
                    }
                    catch { }

                    await Task.Delay(30000, token).ConfigureAwait(false);
                }
            }, token);
        }

        public void ApplyLiveProcessSettings(ServerConfig config)
        {
            if (config == null || !IsRunning || serverProcess == null) return;
            try
            {
                if (serverProcess.HasExited) return;

                serverProcess.PriorityClass = config.ProcessPriority switch
                {
                    "AboveNormal" => ProcessPriorityClass.AboveNormal,
                    "High" => ProcessPriorityClass.High,
                    "BelowNormal" => ProcessPriorityClass.BelowNormal,
                    _ => ProcessPriorityClass.Normal
                };

                if (config.CpuCoresAllocated > 0 && config.CpuCoresAllocated <= Environment.ProcessorCount)
                {
                    long mask = 0;
                    for (int i = 0; i < config.CpuCoresAllocated; i++)
                    {
                        mask |= (1L << i);
                    }
                    serverProcess.ProcessorAffinity = (IntPtr)mask;
                }
                else
                {
                    long allMask = Environment.ProcessorCount >= 63 ? long.MaxValue : ((1L << Environment.ProcessorCount) - 1);
                    serverProcess.ProcessorAffinity = (IntPtr)allMask;
                }

                if (config.AutoRamCleanEnabled)
                {
                    if (ramCleanCts == null) StartRamCleaner();
                }
                else
                {
                    StopRamCleaner();
                }

                OnLog?.Invoke($"[Donanım] Çalışan sunucu işlem ayarları güncellendi (Öncelik: {config.ProcessPriority}, Çekirdek: {(config.CpuCoresAllocated > 0 ? config.CpuCoresAllocated.ToString() : "Tümü")}, RAM Temizleyici: {(config.AutoRamCleanEnabled ? "Açık" : "Kapalı")}).");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Donanım Uyarısı] Canlı işlem ayarları güncellenemedi: {ex.Message}");
            }
        }

        private void StopRamCleaner()
        {
            try
            {
                ramCleanCts?.Cancel();
            }
            catch { }
            finally
            {
                ramCleanCts = null;
            }
        }

        public void ApplyConfigurationFiles(ServerConfig config)
        {
            try
            {
                var docFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var configDir = Path.Combine(docFolder, "Mount and Blade II Bannerlord", "Configs");
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                // 1. Keep GPU/FPS out of engine_config.txt for shared client launches (server GPU/FPS throttling runs in-memory via mod-config.json).
                // Write disable_sound from config.ServerDisableSound (FMOD audio init crashes right after banner_icons.xml when disable_sound = 0 in this environment).
                var engineConfigPath = Path.Combine(configDir, "engine_config.txt");
                var engineDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["safely_exited"] = "1",
                    ["disable_sound"] = config.ServerDisableSound ? "1" : "0"
                };

                UpdateConfigFileWithFormat(engineConfigPath, engineDict, " = ", setReadOnly: false);

                // 2. Configure BannerlordConfig.txt (BattleSize, Damage, AutoSave, Corpses)
                var gameConfigPath = Path.Combine(configDir, "BannerlordConfig.txt");
                var gameDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BattleSize"] = MapBattleSizeToIndex(config.BattleSize).ToString(),
                    ["PlayerReceivedDamageDifficulty"] = Math.Clamp(config.DamageDifficulty, 0, 2).ToString(),
                    ["AutoSaveInterval"] = Math.Max(0, config.AutoSaveIntervalMinutes).ToString(),
                    ["NumberOfCorpses"] = MapCorpseCountToIndex(config.NumberOfCorpses).ToString(),
                    ["StopGameOnFocusLost"] = "False"
                };

                UpdateConfigFileWithFormat(gameConfigPath, gameDict, "=", setReadOnly: false);

                // 3. Configure mod-config.json (Starter Progression, Level, Focus, Gear & Multipliers) across all CoopData dirs
                SyncModConfigToDisk(config);

                OnLog?.Invoke($"[Yapılandırma] Oyun ve motor ayarları diske uygulandı (Savaş Boyutu: {config.BattleSize}, VRAM Seviyesi: {config.VramBudgetLevel}, FPS Sınırı: {config.ServerMaxFps}).");
                OnLog?.Invoke($"[Karakter & İlerleme] Başlangıç Seviyesi: {config.StartingLevel}, Odak: {config.StartingFocusPoints}, Nitelik: {config.StartingAttributePoints}, XP Çarpanı: {config.CharacterXpMultiplier}x, Yetenek XP: {config.SkillXpMultiplier}x, Dinar: {config.StartingGold:N0}.");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Yapılandırma Hatası] Dosyalar güncellenirken hata: {ex.Message}");
            }
        }

        private static int MapBattleSizeToIndex(int battleSize)
        {
            if (battleSize <= 200) return 0;
            if (battleSize <= 300) return 1;
            if (battleSize <= 400) return 2;
            if (battleSize <= 500) return 3;
            if (battleSize <= 600) return 4;
            if (battleSize <= 800) return 5;
            return 6; // 1000 asker
        }

        private static int MapCorpseCountToIndex(int corpseCount)
        {
            if (corpseCount <= 0) return 0;
            if (corpseCount <= 5) return 1;
            if (corpseCount <= 20) return 2;
            if (corpseCount <= 50) return 3;
            if (corpseCount <= 100) return 4;
            return 5; // 200 ceset
        }

        private static void UpdateConfigFileWithFormat(string filePath, Dictionary<string, string> dict, string separator, bool setReadOnly)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
                var lines = File.ReadAllLines(filePath).ToList();
                var remaining = new HashSet<string>(dict.Keys, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//")) continue;

                    var eqIdx = line.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = line.Substring(0, eqIdx).Trim();
                        if (dict.TryGetValue(key, out var val))
                        {
                            lines[i] = $"{key}{separator}{val}";
                            remaining.Remove(key);
                        }
                    }
                }

                foreach (var rem in remaining)
                {
                    lines.Add($"{rem}{separator}{dict[rem]}");
                }

                File.WriteAllLines(filePath, lines);

                if (setReadOnly)
                {
                    File.SetAttributes(filePath, FileAttributes.ReadOnly);
                }
            }
            catch { }
        }

        public static IEnumerable<string> GetAllPossibleCoopDataDirs()
        {
            var envConnected = Environment.GetEnvironmentVariable("CALRADIA_CONNECTED_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envConnected)) yield return envConnected;

            var envCoop = Environment.GetEnvironmentVariable("COOP_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envCoop)) yield return envCoop;

            var envUser = Environment.GetEnvironmentVariable("BANNERLORD_USER_DIR");
            if (!string.IsNullOrWhiteSpace(envUser))
            {
                yield return Path.Combine(envUser, "CalradiaConnectedData");
                yield return Path.Combine(envUser, "CoopData");
            }

            var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(myDocs))
            {
                yield return Path.Combine(myDocs, "Mount and Blade II Bannerlord", "CalradiaConnectedData");
                yield return Path.Combine(myDocs, "Mount and Blade II Bannerlord", "CoopData");
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                yield return Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "CalradiaConnectedData");
                yield return Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "CalradiaConnectedData");
                yield return Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "CalradiaConnectedData");
                yield return Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "CoopData");
                yield return Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "CoopData");
                yield return Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "CoopData");
            }
        }

        public static void ClearAppliedStarterRecords()
        {
            foreach (var coopDir in GetAllPossibleCoopDataDirs().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var appliedPath = Path.Combine(coopDir, "starter_applied.json");
                    if (File.Exists(appliedPath))
                    {
                        File.Delete(appliedPath);
                    }
                }
                catch { }
            }
        }

        public static void SyncModConfigToDisk(ServerConfig config)
        {
            if (config == null) return;

            foreach (var coopDir in GetAllPossibleCoopDataDirs().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(coopDir)) Directory.CreateDirectory(coopDir);
                    var modConfigPath = Path.Combine(coopDir, "mod-config.json");
                    UpdateModConfigJson(modConfigPath, config);
                }
                catch { }
            }
        }

        private static void UpdateModConfigJson(string filePath, ServerConfig config)
        {
            try
            {
                JObject root;
                if (File.Exists(filePath))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    var text = File.ReadAllText(filePath);
                    root = JObject.Parse(text);
                }
                else
                {
                    root = new JObject();
                }

                var starter = new JObject
                {
                    ["startingLevel"] = config.StartingLevel,
                    ["startingFocusPoints"] = config.StartingFocusPoints,
                    ["startingAttributePoints"] = config.StartingAttributePoints,
                    ["startingAllSkillsLevel"] = config.StartingAllSkillsLevel,
                    ["characterXpMultiplier"] = config.CharacterXpMultiplier,
                    ["skillXpMultiplier"] = config.SkillXpMultiplier,
                    ["startingGold"] = config.StartingGold,
                    ["startingRenown"] = config.StartingRenown,
                    ["startingInfluence"] = config.StartingInfluence,
                    ["startingEquipmentPreset"] = config.StartingEquipmentPreset,
                    ["startingTroopsPreset"] = config.StartingTroopsPreset,
                    ["startingFoodSupplies"] = config.StartingFoodSupplies
                };

                root["starterProgression"] = starter;

                root["serverPerformance"] = new JObject
                {
                    ["serverMaxFps"] = config.ServerMaxFps,
                    ["serverDisableSound"] = config.ServerDisableSound,
                    ["vramBudgetLevel"] = config.VramBudgetLevel,
                    ["minimizeServerWindow"] = config.MinimizeServerWindow
                };

                root["gameRules"] = new JObject
                {
                    ["battleSize"] = config.BattleSize,
                    ["damageDifficulty"] = config.DamageDifficulty,
                    ["combatAiDifficulty"] = config.CombatAiDifficulty,
                    ["heroDeathInBattle"] = config.HeroDeathInBattle,
                    ["autoSaveIntervalMinutes"] = config.AutoSaveIntervalMinutes,
                    ["numberOfCorpses"] = config.NumberOfCorpses,
                    ["pvpEnabled"] = config.PvpEnabled,
                    ["persistentWorldTime"] = config.PersistentWorldTime,
                    ["arenaContinuousPracticeRespawn"] = config.ArenaContinuousPracticeRespawn,
                    ["arenaPvpDuelsEnabled"] = config.ArenaPvpDuelsEnabled
                };

                root["lobbySettings"] = new JObject
                {
                    ["serverName"] = config.ServerName ?? "Calradia Connected",
                    ["serverPassword"] = config.ServerPassword ?? string.Empty,
                    ["serverVisibility"] = config.ServerVisibility ?? "Public",
                    ["maxPlayers"] = config.MaxPlayers,
                    ["serverTickRateHz"] = config.ServerTickRateHz,
                    ["disconnectTimeoutSec"] = config.DisconnectTimeoutSec,
                    ["networkPollIntervalMs"] = config.NetworkPollIntervalMs,
                    ["maxBandwidthMbps"] = config.MaxBandwidthMbps
                };

                var modOpts = root["modOptions"] as JObject ?? new JObject();
                modOpts["battleSize"] = config.BattleSize;
                root["modOptions"] = modOpts;

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(filePath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateModConfigJson Error] {ex}");
            }
        }

        private void StartLogTail(string logFilePath)
        {
            StopLogTail();
            logCts = new CancellationTokenSource();
            var token = logCts.Token;

            logTailTask = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    if (File.Exists(logFilePath) || token.IsCancellationRequested) break;
                    await Task.Delay(500, token);
                }

                if (!File.Exists(logFilePath) || token.IsCancellationRequested) return;

                try
                {
                    using var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    try
                    {
                        stream.Seek(0, SeekOrigin.End);
                    }
                    catch { }

                    while (!token.IsCancellationRequested && IsRunning)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line != null)
                        {
                            if (line.Contains("[ObjectManager] Failed to get id for object") ||
                                line.Contains("Common.FpsLogger") ||
                                line.Contains("[Tournament] Broadcasting native tournament state"))
                            {
                                continue;
                            }

                            OnLog?.Invoke($"[GameLog] {line}");
                        }
                        else
                        {
                            await Task.Delay(250, token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Log İzleyici Bilgi] {ex.Message}");
                }
            }, token);
        }

        private void StopLogTail()
        {
            try
            {
                logCts?.Cancel();
            }
            catch { }
            finally
            {
                logCts = null;
            }
        }

        public static void EnsureSteamAppId(string bannerlordDir, string workingDir)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
                {
                    var p1 = Path.Combine(workingDir, "steam_appid.txt");
                    File.WriteAllText(p1, "261550");
                }
                if (!string.IsNullOrWhiteSpace(bannerlordDir) && Directory.Exists(bannerlordDir))
                {
                    var p2 = Path.Combine(bannerlordDir, "steam_appid.txt");
                    File.WriteAllText(p2, "261550");
                }
            }
            catch { }
        }

        private void CheckPrivilegeMismatch()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    OnLog?.Invoke("[Yetki Uyarısı] Sunucu Yöneticisi Yönetici (Admin) olarak çalışıyor. Eğer Steam normal kullanıcı olarak açıksa, Windows güvenlik duvarı (UIPI) nedeniyle 'Unable to initialize Steam API' hatası alabilirsiniz. Çözüm: Steam'i de Yönetici olarak başlatın veya Sunucu Yöneticisini normal (çift tıklayarak) başlatın.");
                }
            }
            catch { }
        }

        public bool EnsureSteamIsRunning()
        {
            var steamProcesses = Process.GetProcessesByName("steam");
            if (steamProcesses.Length > 0)
            {
                CheckPrivilegeMismatch();
                return true;
            }

            OnLog?.Invoke("[Steam Kontrolü] Steam istemcisi arka planda açık değil. Bannerlord bir Steam oyunu olduğu için Steam gereklidir.");

            // Try to find Steam path from Registry or default directory
            string? steamExe = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steamExe = key?.GetValue("SteamExe") as string;
                if (string.IsNullOrWhiteSpace(steamExe))
                {
                    var steamPath = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrWhiteSpace(steamPath))
                    {
                        var candidate = Path.Combine(steamPath, "steam.exe");
                        if (File.Exists(candidate)) steamExe = candidate;
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
            {
                var defaultPath = @"C:\Program Files (x86)\Steam\steam.exe";
                if (File.Exists(defaultPath))
                {
                    steamExe = defaultPath;
                }
            }

            if (!string.IsNullOrWhiteSpace(steamExe) && File.Exists(steamExe))
            {
                OnLog?.Invoke($"[Steam Başlatılıyor] Steam otomatik olarak açılıyor ({steamExe})...");
                try
                {
                    Process.Start(new ProcessStartInfo(steamExe, "-silent") { UseShellExecute = true });

                    // Wait up to 12 seconds for Steam process to appear
                    for (int i = 0; i < 24; i++)
                    {
                        Thread.Sleep(500);
                        if (Process.GetProcessesByName("steam").Length > 0)
                        {
                            OnLog?.Invoke("[Steam Başarılı] Steam istemcisi başarıyla başlatıldı ve arka planda devrede.");
                            Thread.Sleep(2500); // Give Steam IPC time to listen
                            CheckPrivilegeMismatch();
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Steam Başlatma Uyarısı] {ex.Message}");
                }
            }

            OnLog?.Invoke("[Kritik Steam Hatası] Steam uygulaması (Steam.exe) bilgisayarınızda açık değil!");
            OnLog?.Invoke("[Kritik Steam Hatası] Bannerlord Steam sürümü olduğu için Steam açık olmadan açılamaz ('Unable to initialize Steam API' hatası).");
            OnLog?.Invoke("[Kritik Steam Hatası] Lütfen masaüstünüzden Steam'i açıp hesabınıza giriş yapın, ardından 'Sunucuyu Başlat'a basınız.");
            return false;
        }

        public async Task StopServerAsync()
        {
            StopLogTail();
            StopRamCleaner();
            CloseJobHandle();

            if (!IsRunning || serverProcess == null)
                return;

            try
            {
                OnLog?.Invoke("[Durdurucu] Sunucu işlemi sonlandırılıyor...");

                serverProcess.Kill(entireProcessTree: true);
                await serverProcess.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Durdurma Hatası] {ex.Message}");
                try { serverProcess?.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                serverProcess = null;
                OnStateChanged?.Invoke(false);
            }
        }
    }
}

