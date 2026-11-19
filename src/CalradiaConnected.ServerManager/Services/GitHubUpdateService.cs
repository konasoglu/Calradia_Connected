using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CoopServerManager.Services
{
    public sealed class GitHubReleaseInfo
    {
        public string TagName { get; set; } = string.Empty;
        public string VersionString { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string? ModAssetName { get; set; }
        public string? ModAssetDownloadUrl { get; set; }
        public long ModAssetSize { get; set; }
        public string? ManagerAssetName { get; set; }
        public string? ManagerAssetDownloadUrl { get; set; }
        public long ManagerAssetSize { get; set; }
    }

    public sealed class UpdateApplyResult
    {
        public bool ModUpdated { get; set; }
        public bool ServerManagerRestartPending { get; set; }
        public string TargetVersion { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks public GitHub Releases for konasoglu/Calradia_Connected, downloads updated
    /// in-game Mod and ServerManager packages, and performs pure C# file replacement/restart.
    /// </summary>
    public sealed class GitHubUpdateService
    {
        public const string DefaultRepository = "konasoglu/Calradia_Connected";
        public const string ModAssetPrefix = "CalradiaConnected-Mod";
        public const string ManagerAssetPrefix = "CalradiaConnected-ServerManager";
        private const string BackupFileSuffix = ".cc_old";

        private readonly HttpClient _httpClient;
        private readonly string _repository;

        public GitHubUpdateService(string repository = DefaultRepository)
        {
            _repository = repository;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CalradiaConnected-Updater", "1.1"));
        }

        public static void CleanupPreviousUpdateBackups()
        {
            try
            {
                var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(installDir)) return;

                foreach (var oldFile in Directory.GetFiles(installDir, $"*{BackupFileSuffix}", SearchOption.AllDirectories))
                {
                    try { File.Delete(oldFile); } catch { }
                }
            }
            catch
            {
            }
        }

        public static Version GetCurrentVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plusIdx = info.IndexOf('+');
                if (plusIdx > 0) info = info.Substring(0, plusIdx);
                if (TryParseSemanticVersion(info, out var parsedInfo))
                {
                    return parsedInfo;
                }
            }

            var v = asm.GetName().Version;
            return v != null ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
        }

        public static string GetCurrentVersionDisplay()
        {
            var v = GetCurrentVersion();
            return $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        public static bool TryParseSemanticVersion(string? raw, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var cleaned = raw.Trim().TrimStart('v', 'V');
            var dashIdx = cleaned.IndexOf('-');
            if (dashIdx > 0) cleaned = cleaned.Substring(0, dashIdx);

            var parts = cleaned.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;

            if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            {
                return false;
            }

            int build = 0;
            if (parts.Length >= 3 && !int.TryParse(parts[2], out build))
            {
                return false;
            }

            version = new Version(major, minor, build);
            return true;
        }

        public static bool TryGetInstalledModVersion(string? bannerlordDir, out Version modVersion)
        {
            modVersion = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(bannerlordDir)) return false;

            try
            {
                var subModulePath = Path.Combine(bannerlordDir, "Modules", "CalradiaConnected", "SubModule.xml");
                if (!File.Exists(subModulePath)) return false;

                var xml = File.ReadAllText(subModulePath);
                var match = Regex.Match(xml, @"<Version\s+value\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (match.Success && TryParseSemanticVersion(match.Groups[1].Value, out var parsed))
                {
                    modVersion = parsed;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        public static bool IsBannerlordProcessRunning()
        {
            var names = new[] { "Bannerlord", "Bannerlord.Native", "Bannerlord_BE" };
            foreach (var name in names)
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }
            }
            return false;
        }

        public async Task<GitHubReleaseInfo> FetchLatestReleaseAsync()
        {
            var latestUrl = $"https://api.github.com/repos/{_repository}/releases/latest";
            var response = await SendGitHubRequestAsync(latestUrl);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var listUrl = $"https://api.github.com/repos/{_repository}/releases?per_page=1";
                response.Dispose();
                response = await SendGitHubRequestAsync(listUrl);
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    var code = (int)response.StatusCode;
                    response.Dispose();
                    throw new InvalidOperationException($"GitHub Release bulunamadı ({code}): {errBody}");
                }

                var arrJson = await response.Content.ReadAsStringAsync();
                response.Dispose();
                using var doc = JsonDocument.Parse(arrJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("GitHub deposunda henüz yayınlanmış bir Release bulunamadı.");
                }
                return ParseReleaseElement(doc.RootElement[0]);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidOperationException($"GitHub API hatası ({code}): {errBody}");
            }

            var json = await response.Content.ReadAsStringAsync();
            response.Dispose();
            using var rootDoc = JsonDocument.Parse(json);
            return ParseReleaseElement(rootDoc.RootElement);
        }

        public async Task<UpdateApplyResult> ApplyReleaseAsync(
            GitHubReleaseInfo release,
            string? bannerlordDir,
            Action<string>? log = null)
        {
            var result = new UpdateApplyResult
            {
                TargetVersion = string.IsNullOrWhiteSpace(release.VersionString) ? release.TagName : release.VersionString
            };

            var stagingRoot = Path.Combine(Path.GetTempPath(), $"CalradiaConnected_Update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingRoot);

            try
            {
                // 1. Update in-game Bannerlord mod (Modules\CalradiaConnected)
                if (!string.IsNullOrWhiteSpace(bannerlordDir) &&
                    Directory.Exists(bannerlordDir) &&
                    !string.IsNullOrWhiteSpace(release.ModAssetDownloadUrl))
                {
                    var modZipPath = Path.Combine(stagingRoot, release.ModAssetName ?? "CalradiaConnected-Mod.zip");
                    log?.Invoke($"[Güncelleyici] Oyun içi mod paketi indiriliyor ({release.ModAssetSize / (1024.0 * 1024.0):N2} MB)...");
                    await DownloadAssetAsync(release.ModAssetDownloadUrl, modZipPath);

                    var modExtractDir = Path.Combine(stagingRoot, "mod_extract");
                    await Task.Run(() => ExtractZipSafely(modZipPath, modExtractDir));

                    var sourceModDir = ResolveExtractedModRoot(modExtractDir);
                    var targetModDir = Path.Combine(bannerlordDir, "Modules", "CalradiaConnected");

                    log?.Invoke($"[Güncelleyici] Mod dosyaları uygulanıyor -> {targetModDir}");
                    await Task.Run(() => ReplaceDirectoryContents(sourceModDir, targetModDir));

                    result.ModUpdated = true;
                    log?.Invoke("[Güncelleyici] Oyun içi mod güncellemesi tamamlandı.");
                }
                else
                {
                    log?.Invoke("[Güncelleyici] Uyarı: Mod paketi bulunamadı veya Bannerlord klasörü geçersiz; mod adımı atlandı.");
                }

                // 2. Apply ServerManager self-update in-place using pure C# file swap + restart
                if (!string.IsNullOrWhiteSpace(release.ManagerAssetDownloadUrl))
                {
                    var mgrZipPath = Path.Combine(stagingRoot, release.ManagerAssetName ?? "CalradiaConnected-ServerManager.zip");
                    log?.Invoke($"[Güncelleyici] Server Manager paketi indiriliyor ({release.ManagerAssetSize / (1024.0 * 1024.0):N2} MB)...");
                    await DownloadAssetAsync(release.ManagerAssetDownloadUrl, mgrZipPath);

                    var mgrExtractDir = Path.Combine(stagingRoot, "manager_extract");
                    await Task.Run(() => ExtractZipSafely(mgrZipPath, mgrExtractDir));

                    var sourceMgrDir = ResolveExtractedManagerRoot(mgrExtractDir);

                    var stagedConfig = Path.Combine(sourceMgrDir, "config.json");
                    if (File.Exists(stagedConfig))
                    {
                        File.Delete(stagedConfig);
                    }

                    var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var currentExePath = Environment.ProcessPath ?? Path.Combine(installDir, "CalradiaConnected.ServerManager.exe");

                    log?.Invoke("[Güncelleyici] Server Manager güncelleniyor ve yeniden başlatılıyor...");
                    await Task.Run(() => ApplyServerManagerSelfUpdate(sourceMgrDir, installDir, currentExePath));
                    result.ServerManagerRestartPending = true;
                }

                return result;
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    try { Directory.Delete(stagingRoot, true); } catch { }
                }
            }
        }

        private async Task<HttpResponseMessage> SendGitHubRequestAsync(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return await _httpClient.SendAsync(req);
        }

        private async Task DownloadAssetAsync(string browserUrl, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(browserUrl))
            {
                throw new InvalidOperationException("İndirme bağlantısı bulunamadı.");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, browserUrl);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            using var src = await resp.Content.ReadAsStreamAsync();
            using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await src.CopyToAsync(dst);
        }

        private static GitHubReleaseInfo ParseReleaseElement(JsonElement el)
        {
            var tag = el.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            var info = new GitHubReleaseInfo
            {
                TagName = tag,
                VersionString = tag.TrimStart('v', 'V'),
                Name = el.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tag : tag,
                Body = el.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty,
                HtmlUrl = el.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty
            };

            if (el.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var dlUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;

                    if (assetName.StartsWith(ModAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                        assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ModAssetName = assetName;
                        info.ModAssetDownloadUrl = dlUrl;
                        info.ModAssetSize = size;
                    }
                    else if (assetName.StartsWith(ManagerAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                             assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ManagerAssetName = assetName;
                        info.ManagerAssetDownloadUrl = dlUrl;
                        info.ManagerAssetSize = size;
                    }
                }
            }

            return info;
        }

        private static void ExtractZipSafely(string zipPath, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            var fullDest = Path.GetFullPath(destinationDir);
            if (!fullDest.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                fullDest += Path.DirectorySeparatorChar;
            }

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var destPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
                if (!destPath.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Güvenlik ihlali: arşiv yolu geçersiz ({entry.FullName}).");
                }

                var parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        private static string ResolveExtractedModRoot(string extractDir)
        {
            var nested = Path.Combine(extractDir, "CalradiaConnected");
            if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, "SubModule.xml")))
            {
                return nested;
            }
            if (File.Exists(Path.Combine(extractDir, "SubModule.xml")))
            {
                return extractDir;
            }

            var subModule = Directory.GetFiles(extractDir, "SubModule.xml", SearchOption.AllDirectories).FirstOrDefault();
            if (subModule != null)
            {
                return Path.GetDirectoryName(subModule)!;
            }

            throw new InvalidOperationException("İndirilen mod arşivinde SubModule.xml bulunamadı.");
        }

        private static string ResolveExtractedManagerRoot(string extractDir)
        {
            var exeFile = Directory.GetFiles(extractDir, "CalradiaConnected.ServerManager.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exeFile != null)
            {
                return Path.GetDirectoryName(exeFile)!;
            }
            throw new InvalidOperationException("İndirilen Server Manager arşivinde CalradiaConnected.ServerManager.exe bulunamadı.");
        }

        private static void ReplaceDirectoryContents(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, dir);
                Directory.CreateDirectory(Path.Combine(targetDir, rel));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file);
                var dst = Path.Combine(targetDir, rel);
                var parent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.Copy(file, dst, overwrite: true);
            }
        }

        private static void ApplyServerManagerSelfUpdate(
            string stagedSourceDir,
            string installDir,
            string exeToRelaunch)
        {
            Directory.CreateDirectory(installDir);

            foreach (var srcFile in Directory.GetFiles(stagedSourceDir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(srcFile);
                if (string.Equals(fileName, "config.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(stagedSourceDir, srcFile);
                var dst = Path.Combine(installDir, rel);
                var parent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                try
                {
                    File.Copy(srcFile, dst, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // On Windows, locked running executables/DLLs can be renamed in place before copying the new file.
                    var backupPath = dst + BackupFileSuffix;
                    if (File.Exists(backupPath))
                    {
                        try { File.Delete(backupPath); }
                        catch { backupPath = $"{dst}.{DateTime.UtcNow.Ticks}{BackupFileSuffix}"; }
                    }

                    File.Move(dst, backupPath);
                    File.Copy(srcFile, dst, overwrite: true);
                }
            }

            Process.Start(new ProcessStartInfo(exeToRelaunch)
            {
                WorkingDirectory = installDir,
                UseShellExecute = true
            });
        }
    }
}
