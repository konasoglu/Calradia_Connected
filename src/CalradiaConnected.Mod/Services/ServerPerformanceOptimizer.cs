#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using IOPath = System.IO.Path;
using TWDebug = TaleWorlds.Library.Debug;

namespace CalradiaConnected.Mod.Services
{
    /// <summary>
    /// Dedicated-server-only runtime optimizer.
    /// IMPORTANT: Never mutates Bannerlord's shared NativeOptions / engine_config.txt
    /// (FrameLimiter, TextureBudget, MasterVolume, SoundVolume, disable_sound), because
    /// Bannerlord persists those values to Documents\Mount and Blade II Bannerlord\Configs\engine_config.txt
    /// on exit and would otherwise permanently degrade graphics/FPS and mute audio in singleplayer!
    /// Instead, FPS pacing runs 100% in-memory via Stopwatch + Thread.Sleep, window minimization
    /// lowers GPU swapchain overhead, and server audio muting uses per-process Windows WASAPI mute.
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
        private int _audioMuteAttempts = 0;
        private bool _audioMuteApplied = false;
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
            _audioMuteApplied = false;
            _audioMuteAttempts = 0;
            if (_mapThrottlesApplied)
            {
                ApplySafeRuntimeThrottles();
            }
        }

        /// <summary>
        /// Called from OnSubModuleLoad: pure managed config read only!
        /// </summary>
        public void ApplyStartupThrottles()
        {
            if (!CalradiaConnectedSubModule.IsServerProcess) return;
            LoadServerPerformanceConfig();
            TrySetCurrentProcessAudioMute(ServerDisableSound);
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

            if (!_audioMuteApplied && _audioMuteAttempts < 10)
            {
                _audioMuteAttempts++;
                if (TrySetCurrentProcessAudioMute(ServerDisableSound))
                {
                    _audioMuteApplied = true;
                }
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
                // Apply per-process Windows WASAPI mute ONLY to this /server process (never touches engine_config.txt!)
                if (TrySetCurrentProcessAudioMute(ServerDisableSound))
                {
                    _audioMuteApplied = true;
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

        /// <summary>
        /// One-time automatic self-heal for players whose engine_config.txt was previously corrupted
        /// by older mod versions (disable_sound = 1, max_framerate = 15..30, texture_budget = 0, or all volumes = 0).
        /// Runs strictly once per machine (tracked via engine_config_healed_v1.flag) so user's own
        /// manual graphics/audio settings are never overridden afterwards.
        /// </summary>
        public static bool RepairLeakedEngineConfigOnce()
        {
            try
            {
                string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string baseDir = IOPath.Combine(myDocs, "Mount and Blade II Bannerlord");
                string dataDir = IOPath.Combine(baseDir, "CalradiaConnectedData");
                string flagPath = IOPath.Combine(dataDir, "engine_config_healed_v1.flag");
                if (File.Exists(flagPath)) return false;

                string engineConfigPath = IOPath.Combine(baseDir, "Configs", "engine_config.txt");
                bool modified = false;

                if (File.Exists(engineConfigPath))
                {
                    File.SetAttributes(engineConfigPath, FileAttributes.Normal);
                    var lines = File.ReadAllLines(engineConfigPath).ToList();
                    var map = new Dictionary<string, (int Index, string Value)>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < lines.Count; i++)
                    {
                        string trimmed = lines[i].Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//")) continue;
                        int eq = trimmed.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = trimmed.Substring(0, eq).Trim();
                        string val = trimmed.Substring(eq + 1).Trim();
                        map[key] = (i, val);
                    }

                    bool hadDisabledSound = map.TryGetValue("disable_sound", out var ds) && ds.Value == "1";
                    if (hadDisabledSound)
                    {
                        lines[ds.Index] = "disable_sound = 0";
                        modified = true;
                    }

                    if (map.TryGetValue("max_framerate", out var mf) &&
                        int.TryParse(mf.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) &&
                        fps >= 10 && fps <= 30)
                    {
                        lines[mf.Index] = "max_framerate = 360";
                        modified = true;
                    }

                    bool allAudioZeroed =
                        IsZeroFloat(map, "master_volume") &&
                        IsZeroFloat(map, "sound_volume") &&
                        IsZeroFloat(map, "music_volume") &&
                        IsZeroFloat(map, "voice_over_volume");

                    if (allAudioZeroed)
                    {
                        SetKey(lines, map, "master_volume", "1.0000");
                        SetKey(lines, map, "sound_volume", "1.0000");
                        SetKey(lines, map, "music_volume", "1.0000");
                        SetKey(lines, map, "voice_over_volume", "1.0000");
                        SetKey(lines, map, "voice_chat_volume", "1.0000");
                        modified = true;
                    }
                    else if (hadDisabledSound && IsZeroFloat(map, "voice_chat_volume"))
                    {
                        SetKey(lines, map, "voice_chat_volume", "1.0000");
                        modified = true;
                    }

                    if (map.TryGetValue("texture_budget", out var tb) && tb.Value == "0" && (hadDisabledSound || allAudioZeroed || modified))
                    {
                        lines[tb.Index] = "texture_budget = 2";
                        modified = true;
                    }

                    if (modified)
                    {
                        File.WriteAllLines(engineConfigPath, lines);
                    }
                }

                Directory.CreateDirectory(dataDir);
                File.WriteAllText(flagPath, $"Healed={modified};Utc={DateTime.UtcNow:O}");
                return modified;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsZeroFloat(Dictionary<string, (int Index, string Value)> map, string key)
        {
            return map.TryGetValue(key, out var entry) &&
                   float.TryParse(entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) &&
                   v <= 0.001f;
        }

        private static void SetKey(List<string> lines, Dictionary<string, (int Index, string Value)> map, string key, string value)
        {
            if (map.TryGetValue(key, out var entry))
            {
                lines[entry.Index] = $"{key} = {value}";
            }
        }

        #region Per-Process Windows WASAPI Audio Mute (Zero Config File Side-Effects)

        private static bool TrySetCurrentProcessAudioMute(bool mute)
        {
            try
            {
                uint myPid = (uint)Process.GetCurrentProcess().Id;
                var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                try
                {
                    // eRender = 0, eMultimedia = 1
                    enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
                    if (device == null) return false;
                    try
                    {
                        Guid iidManager2 = typeof(IAudioSessionManager2).GUID;
                        device.Activate(ref iidManager2, 0, IntPtr.Zero, out object mgrObj);
                        if (mgrObj is not IAudioSessionManager2 manager) return false;
                        try
                        {
                            manager.GetSessionEnumerator(out var sessionEnumerator);
                            if (sessionEnumerator == null) return false;
                            try
                            {
                                sessionEnumerator.GetCount(out int count);
                                bool found = false;
                                for (int i = 0; i < count; i++)
                                {
                                    sessionEnumerator.GetSession(i, out var ctl);
                                    if (ctl == null) continue;
                                    try
                                    {
                                        if (ctl is IAudioSessionControl2 ctl2 &&
                                            ctl2.GetProcessId(out uint pid) == 0 &&
                                            pid == myPid &&
                                            ctl is ISimpleAudioVolume vol)
                                        {
                                            Guid ctx = Guid.Empty;
                                            vol.SetMute(mute, ref ctx);
                                            found = true;
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.ReleaseComObject(ctl);
                                    }
                                }
                                return found;
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(sessionEnumerator);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(manager);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumerator);
                }
            }
            catch
            {
                return false;
            }
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int NotImpl1();
            int NotImpl2();
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int SessionCount);
            [PreserveSig]
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl { }

        [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            int NotImpl0();
            int NotImpl1();
            int NotImpl2();
            int NotImpl3();
            int NotImpl4();
            int NotImpl5();
            int NotImpl6();
            int NotImpl7();
            int NotImpl8();
            int NotImpl9();
            int NotImpl10();
            [PreserveSig]
            int GetProcessId(out uint pRetVal);
        }

        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig]
            int SetMasterVolume(float fLevel, ref Guid EventContext);
            [PreserveSig]
            int GetMasterVolume(out float pfLevel);
            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
            [PreserveSig]
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        #endregion
    }
}
