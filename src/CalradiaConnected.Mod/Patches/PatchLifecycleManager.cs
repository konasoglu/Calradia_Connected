using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Manages the installation, verification, and lifecycle of all Harmony patches in the mod.
    /// Isolates each patch class so a single target mismatch never aborts other patches.
    /// </summary>
    public static class PatchLifecycleManager
    {
        public static int AppliedCount { get; private set; }
        public static int FailedCount { get; private set; }
        public static IReadOnlyList<string> FailedPatches => _failedPatches;
        private static readonly List<string> _failedPatches = new List<string>();

        public static void InstallAll(Harmony harmony, Assembly? targetAssembly = null)
        {
            AppliedCount = 0;
            FailedCount = 0;
            _failedPatches.Clear();

            var asm = targetAssembly ?? typeof(PatchLifecycleManager).Assembly;

            foreach (var type in AccessTools.GetTypesFromAssembly(asm))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    AppliedCount++;
                }
                catch (Exception ex)
                {
                    FailedCount++;
                    string reason = (ex.InnerException ?? ex).Message;
                    string entry = $"{type.FullName}: {reason}";
                    _failedPatches.Add(entry);

                    string msg = $"[Calradia-Connected] Harmony yaması uygulanamadı: {entry}";
                    TaleWorlds.Library.Debug.PrintError(msg);
                    TaleWorlds.Library.Debug.Print(msg);
                }
            }

            StoryModeNeutralizer.Apply(harmony);
            PvpOrderOfBattlePatch.Apply(harmony);
            TaleWorlds.Library.Debug.Print($"[Calradia-Connected] PatchLifecycleManager: {AppliedCount} yama uygulandı, {FailedCount} başarısız.");
        }

        public static void UninstallAll(Harmony? harmony, string harmonyId = "com.calradiaconnected.mod")
        {
            try
            {
                harmony?.UnpatchAll(harmonyId);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] PatchLifecycleManager Uninstall hatası: {ex.Message}");
            }
        }
    }
}
