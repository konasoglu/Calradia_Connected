#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Options;
using IOPath = System.IO.Path;
using TWDebug = TaleWorlds.Library.Debug;

namespace CalradiaConnected.Mod.Services
{
    /// <summary>
    /// Dedicated-server-only runtime optimizer. Delays native options and window minimization
    /// until MapState is stable (never calling native graphics/engine interfaces during OnSubModuleLoad),
    /// and enforces deterministic managed frame pacing without touching client engine_config.txt.
    /// </summary>
    public sealed class ServerPerformanceOptimizer
    {
        public static ServerPerformanceOptimizer Instance { get; } = new ServerPerformanceOptimizer();

        private const int SW_SHOWMINNOACTIVE = 7;
        private const int MapStabilizeDelayTicks = 120;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private bool _mapThrottlesApplied = false;
        private bool _windowMinimized = false;
        private int _minimizeAttempts = 0;
        private int _mapTicks = 0;
        private long _lastFrameTimestamp = 0;

        public int ServerMaxFps { get; private set; } = 20;
        public bool ServerDisableSound { get; private set; } = true;
        public bool MinimizeServerWindow { get; private set; } = true;
        public int VramBudgetLevel { get; private set; } = 0;

        private ServerPerformanceOptimizer() { }

        public static int ClampServerFps(int configuredFps)
        {
            if (configuredFps <= 0) return 20;
            return Math.Max(15, Math.Min(30, configuredFps));
        }

        public void LoadServerPerformanceConfig()
        {
            try
            {
                var candidateDirs = new[]
                {
                    Environment.GetEnvironmentVariable("CALRADIA_CONNECTED_DATA_DIR"),
                    Environment.GetEnvironmentVariable("COOP_DATA_DIR"),
                    IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                    IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                    IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                    IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Mount and Blade II Bannerlord", "CalradiaConnectedData"),
                    IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "CoopData")
                };

                foreach (var dir in candidateDirs)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string path = IOPath.Combine(dir, "mod-config.json");
                    if (!File.Exists(path)) continue;

                    var root = JObject.Parse(File.ReadAllText(path));
                    if (root["serverPerformance"] is JObject perf)
                    {
                        if (perf["serverMaxFps"] != null)
                        {
                            ServerMaxFps = ClampServerFps(perf["serverMaxFps"]!.Value<int>());
                        }
                        if (perf["serverDisableSound"] != null)
                        {
                            ServerDisableSound = perf["serverDisableSound"]!.Value<bool>();
                        }
                        if (perf["minimizeServerWindow"] != null)
                        {
                            MinimizeServerWindow = perf["minimizeServerWindow"]!.Value<bool>();
                        }
                        if (perf["vramBudgetLevel"] != null)
                        {
                            VramBudgetLevel = Math.Max(0, Math.Min(4, perf["vramBudgetLevel"]!.Value<int>()));
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        public void ReloadAndApplyLive()
        {
            if (!CalradiaConnectedSubModule.IsServerProcess) return;
            LoadServerPerformanceConfig();
            if (_mapThrottlesApplied)
            {
                ApplySafeRuntimeThrottles();
            }
        }

        /// <summary>
        /// Called from OnSubModuleLoad: pure managed config read only!
        /// Native engine interfaces (Utilities / NativeOptions) are not initialized yet during OnSubModuleLoad.
        /// </summary>
        public void ApplyStartupThrottles()
        {
            if (!CalradiaConnectedSubModule.IsServerProcess) return;
            LoadServerPerformanceConfig();
        }

        public void OnServerMapTick()
        {
            if (!CalradiaConnectedSubModule.IsServerProcess) return;

            _mapTicks++;
            if (_mapTicks < MapStabilizeDelayTicks)
            {
                return;
            }

            if (!_mapThrottlesApplied)
            {
                _mapThrottlesApplied = true;
                ApplySafeRuntimeThrottles();
            }

            if (MinimizeServerWindow && !_windowMinimized && _minimizeAttempts < 20)
            {
                _minimizeAttempts++;
                try
                {
                    IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
                        _windowMinimized = true;
                    }
                }
                catch { }
            }

            PaceServerFrame();
        }

        private void ApplySafeRuntimeThrottles()
        {
            try
            {
                Utilities.SetFrameLimiterWithSleep(true);
                NativeOptions.SetConfig(NativeOptions.NativeOptionsType.FrameLimiter, ClampServerFps(ServerMaxFps));
                NativeOptions.SetConfig(NativeOptions.NativeOptionsType.TextureBudget, Math.Max(0, Math.Min(4, VramBudgetLevel)));

                if (ServerDisableSound)
                {
                    NativeOptions.SetConfig(NativeOptions.NativeOptionsType.MasterVolume, 0f);
                    NativeOptions.SetConfig(NativeOptions.NativeOptionsType.SoundVolume, 0f);
                    NativeOptions.SetConfig(NativeOptions.NativeOptionsType.MusicVolume, 0f);
                    NativeOptions.SetConfig(NativeOptions.NativeOptionsType.VoiceChatVolume, 0f);
                    NativeOptions.SetConfig(NativeOptions.NativeOptionsType.VoiceOverVolume, 0f);
                }
                else
                {
                    NativeOptions.SetConfig(NativeOptions.NativeOptionsType.MasterVolume, 0.5f);
                    NativeOptions.SetConfig(NativeOptions.NativeOptionsType.SoundVolume, 0.5f);
                }
            }
            catch (Exception ex)
            {
                TWDebug.PrintError($"[Calradia-Connected] Server runtime throttle warning: {ex.Message}");
            }
        }

        private void PaceServerFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastFrameTimestamp == 0)
            {
                _lastFrameTimestamp = now;
                return;
            }

            double targetMs = 1000.0 / ClampServerFps(ServerMaxFps);
            double elapsedMs = (now - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            int remainingMs = (int)Math.Floor(targetMs - elapsedMs);
            if (remainingMs >= 2 && remainingMs <= 60)
            {
                Thread.Sleep(remainingMs);
            }

            _lastFrameTimestamp = Stopwatch.GetTimestamp();
        }
    }
}
