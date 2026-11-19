using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using CoopServerManager.Models;

namespace CoopServerManager.Services
{
    public class ModPackagerService
    {
        private static readonly HashSet<string> ExcludedModuleIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "Native",
            "SandBoxCore",
            "CustomBattle",
            "SandBox",
            "StoryMode",
            "BirthAndDeath",
            "NavalDLC",
            "Multiplayer",
            "FastMode"
        };

        private readonly string cacheDirectory;

        public ModPackagerService()
        {
            cacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerCache", "Mods");
            Directory.CreateDirectory(cacheDirectory);
        }

        public string CacheDirectory => cacheDirectory;

        public async Task<FastDlManifest> PrepareFastDlPackageAsync(
            string serverName,
            string hostIpOrDomain,
            int fastDlPort,
            IEnumerable<ModuleInfoModel> modulesToDistribute,
            Action<string>? statusCallback = null)
        {
            var manifest = new FastDlManifest
            {
                ServerName = serverName,
                GameVersion = "v1.4.8",
                TimestampUtc = DateTime.UtcNow
            };

            // Filter out base game / official modules — only package custom community mods
            var targetModules = modulesToDistribute
                .Where(m => !m.IsOfficial && !ExcludedModuleIds.Contains(m.Id))
                .ToList();

            if (targetModules.Count == 0)
            {
                statusCallback?.Invoke("[FastDL] Dağıtılacak özel mod bulunmuyor (Vanilla oyun modları kullanılıyor).");
                return manifest;
            }

            foreach (var module in targetModules)
            {
                statusCallback?.Invoke($"Paketleniyor: {module.Name} ({module.Id})...");

                var zipPath = Path.Combine(cacheDirectory, $"{module.Id}.zip");

                // Determine if we need to rebuild the zip
                bool needRebuild = !File.Exists(zipPath) || IsDirectoryNewerThanFile(module.DirectoryPath, zipPath);

                if (needRebuild)
                {
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }

                    await Task.Run(() => CreateModuleArchive(module.DirectoryPath, module.Id, zipPath));
                }

                var fileInfo = new FileInfo(zipPath);
                var sha256 = await Task.Run(() => ComputeSha256(zipPath));

                var downloadUrl = $"http://{hostIpOrDomain}:{fastDlPort}/mods/{module.Id}.zip";

                manifest.Modules.Add(new FastDlModuleEntry
                {
                    Id = module.Id,
                    Name = module.Name,
                    Version = module.Version,
                    Sha256 = sha256,
                    FileSizeBytes = fileInfo.Length,
                    DownloadUrl = downloadUrl,
                    IsRequired = true
                });
            }

            statusCallback?.Invoke("Tüm özel modlar FastDL için hazırlandı.");
            return manifest;
        }

        private static void CreateModuleArchive(string sourceDirectory, string rootFolderName, string destinationZip)
        {
            // Create zip containing <rootFolderName>/... so when extracted into Modules/, it places properly
            using var zipArchive = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
            var dirInfo = new DirectoryInfo(sourceDirectory);

            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                // Skip temporary visual studio or git artifacts if present
                if (file.FullName.Contains(@"\.git\") || file.FullName.Contains(@"\.vs\"))
                    continue;

                var relativePath = Path.GetRelativePath(sourceDirectory, file.FullName);
                var entryName = Path.Combine(rootFolderName, relativePath).Replace('\\', '/');

                zipArchive.CreateEntryFromFile(file.FullName, entryName, CompressionLevel.Fastest);
            }
        }

        private static bool IsDirectoryNewerThanFile(string dirPath, string filePath)
        {
            var fileTime = File.GetLastWriteTimeUtc(filePath);
            var dirInfo = new DirectoryInfo(dirPath);

            try
            {
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (file.LastWriteTimeUtc > fileTime)
                        return true;
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
