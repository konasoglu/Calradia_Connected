using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CoopServerManager.Models;
using Microsoft.Win32;

namespace CoopServerManager.Services
{
    public class ModuleScannerService
    {
        private static readonly HashSet<string> CoreRequiredModuleIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "Native",
            "SandBoxCore",
            "CustomBattle",
            "SandBox",
            "CalradiaConnected"
        };

        private static readonly HashSet<string> IncompatibleModuleIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "BirthAndDeath",
            "FastMode",
            "Multiplayer",
            "Coop" // Legacy mod explicitly excluded
        };

        private static readonly HashSet<string> OfficialOptionalModuleIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "StoryMode",
            "NavalDLC"
        };

        private static readonly string[] StandardOfficialLoadOrder =
        {
            "Native",
            "SandBoxCore",
            "CustomBattle",
            "SandBox",
            "StoryMode",
            "NavalDLC",
            "CalradiaConnected"
        };

        public static bool IsWarSailsInstalled(string bannerlordDir)
        {
            if (string.IsNullOrWhiteSpace(bannerlordDir)) return false;
            return File.Exists(Path.Combine(bannerlordDir, "Modules", "NavalDLC", "SubModule.xml"))
                   && File.Exists(Path.Combine(bannerlordDir, "Modules", "StoryMode", "SubModule.xml"));
        }

        public static List<string> BuildLaunchModuleList(
            string bannerlordDir,
            IEnumerable<string>? requestedModules,
            bool enableWarSails = true)
        {
            var requested = (requestedModules ?? Enumerable.Empty<string>()).ToList();
            bool navalSelected = enableWarSails
                                 || requested.Contains("NavalDLC", StringComparer.OrdinalIgnoreCase);

            var finalModules = new List<string>
            {
                "Native",
                "SandBoxCore",
                "CustomBattle",
                "SandBox"
            };

            if (navalSelected && IsWarSailsInstalled(bannerlordDir))
            {
                finalModules.Add("StoryMode");
                finalModules.Add("NavalDLC");
            }

            finalModules.Add("CalradiaConnected");

            foreach (var mod in requested)
            {
                if (string.IsNullOrWhiteSpace(mod) || IncompatibleModuleIds.Contains(mod)) continue;
                if (string.Equals(mod, "StoryMode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mod, "NavalDLC", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!finalModules.Contains(mod, StringComparer.OrdinalIgnoreCase))
                {
                    finalModules.Add(mod);
                }
            }

            return finalModules;
        }

        public string DetectBannerlordDirectory()
        {
            var envDir = Environment.GetEnvironmentVariable("BANNERLORD_DIR", EnvironmentVariableTarget.Process)
                         ?? Environment.GetEnvironmentVariable("BANNERLORD_DIR", EnvironmentVariableTarget.User)
                         ?? Environment.GetEnvironmentVariable("BANNERLORD_DIR", EnvironmentVariableTarget.Machine);

            if (!string.IsNullOrWhiteSpace(envDir) && IsValidBannerlordDirectory(envDir))
            {
                return Path.GetFullPath(envDir);
            }

            var defaultSteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord";
            if (IsValidBannerlordDirectory(defaultSteamPath))
            {
                return defaultSteamPath;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 261550");
                if (key?.GetValue("InstallLocation") is string regPath && IsValidBannerlordDirectory(regPath))
                {
                    return regPath;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public bool IsValidBannerlordDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return false;

            var exePath = Path.Combine(path, "bin", "Win64_Shipping_Client", "Bannerlord.exe");
            var modulesPath = Path.Combine(path, "Modules");
            return File.Exists(exePath) && Directory.Exists(modulesPath);
        }

        public List<ModuleInfoModel> ScanModules(string bannerlordDir)
        {
            var result = new List<ModuleInfoModel>();
            var modulesRoot = Path.Combine(bannerlordDir, "Modules");

            if (!Directory.Exists(modulesRoot))
                return result;

            foreach (var moduleDir in Directory.GetDirectories(modulesRoot))
            {
                var subModuleXml = Path.Combine(moduleDir, "SubModule.xml");
                if (!File.Exists(subModuleXml))
                    continue;

                try
                {
                    var doc = XDocument.Load(subModuleXml);
                    var root = doc.Root;
                    if (root == null) continue;

                    var id = root.Element("Id")?.Attribute("value")?.Value
                             ?? Path.GetFileName(moduleDir);

                    // Skip legacy Coop module completely
                    if (string.Equals(id, "Coop", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = root.Element("Name")?.Attribute("value")?.Value
                               ?? id;

                    var version = root.Element("Version")?.Attribute("value")?.Value
                                  ?? "1.0.0";

                    var deps = root.Element("DependedModules")?
                        .Elements("DependedModule")
                        .Select(e => e.Attribute("Id")?.Value ?? string.Empty)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList() ?? new List<string>();

                    bool isCore = CoreRequiredModuleIds.Contains(id);
                    bool isOfficialOptional = OfficialOptionalModuleIds.Contains(id);
                    bool isIncompatible = IncompatibleModuleIds.Contains(id);
                    bool isConnectedCore = string.Equals(id, "CalradiaConnected", StringComparison.OrdinalIgnoreCase);

                    var model = new ModuleInfoModel
                    {
                        Id = id,
                        Name = string.Equals(id, "NavalDLC", StringComparison.OrdinalIgnoreCase) ? "War Sails (NavalDLC)" : name,
                        Version = version,
                        DirectoryPath = moduleDir,
                        IsOfficial = isCore || isOfficialOptional || isIncompatible,
                        IsCoopCore = isConnectedCore,
                        IsCoreRequired = isCore,
                        IsIncompatible = isIncompatible,
                        Dependencies = deps,
                        IsSelected = isCore || isOfficialOptional // Core & supported official expansions (War Sails) selected by default
                    };

                    result.Add(model);
                }
                catch
                {
                }
            }

            return result
                .OrderBy(m => GetSortOrder(m.Id))
                .ThenBy(m => m.Name)
                .ToList();
        }

        private int GetSortOrder(string id)
        {
            for (int i = 0; i < StandardOfficialLoadOrder.Length; i++)
            {
                if (string.Equals(StandardOfficialLoadOrder[i], id, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 1000;
        }
    }
}
