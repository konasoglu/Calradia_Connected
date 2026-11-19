using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using CoopServerManager.Models;

namespace CoopServerManager.Services
{
    public class ClientSyncService
    {
        private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

        public async Task<FastDlManifest> FetchManifestAsync(string serverIp, int fastDlPort)
        {
            var url = $"http://{serverIp}:{fastDlPort}/manifest";
            var response = await httpClient.GetStringAsync(url);
            var manifest = JsonSerializer.Deserialize<FastDlManifest>(response);
            if (manifest == null)
            {
                throw new InvalidOperationException("Sunucu manifestosu boş veya geçersiz.");
            }
            return manifest;
        }

        public List<FastDlModuleEntry> CheckMissingModules(string bannerlordDir, FastDlManifest manifest)
        {
            var missing = new List<FastDlModuleEntry>();
            var modulesRoot = Path.Combine(bannerlordDir, "Modules");

            foreach (var mod in manifest.Modules)
            {
                var modDir = Path.Combine(modulesRoot, mod.Id);
                var subModuleXml = Path.Combine(modDir, "SubModule.xml");

                if (!Directory.Exists(modDir) || !File.Exists(subModuleXml))
                {
                    missing.Add(mod);
                }
            }

            return missing;
        }

        public async Task DownloadAndExtractModuleAsync(
            string bannerlordDir,
            FastDlModuleEntry module,
            Action<long, long>? progressCallback = null,
            Action<string>? statusCallback = null)
        {
            var modulesRoot = Path.Combine(bannerlordDir, "Modules");
            Directory.CreateDirectory(modulesRoot);

            var tempZip = Path.Combine(Path.GetTempPath(), $"{module.Id}_{Guid.NewGuid():N}.zip");

            try
            {
                statusCallback?.Invoke($"İndiriliyor: {module.Name} ({module.FileSizeBytes / (1024 * 1024):N1} MB)...");

                using (var response = await httpClient.GetAsync(module.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? module.FileSizeBytes;

                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;

                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        progressCallback?.Invoke(totalRead, totalBytes);
                    }
                }

                statusCallback?.Invoke($"Arşivden çıkartılıyor: {module.Name}...");

                // Extract archive into Modules/
                await Task.Run(() =>
                {
                    ZipFile.ExtractToDirectory(tempZip, modulesRoot, overwriteFiles: true);
                });

                statusCallback?.Invoke($"Yüklendi: {module.Name}");
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { }
                }
            }
        }
    }
}
