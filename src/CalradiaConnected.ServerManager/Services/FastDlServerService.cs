using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CoopServerManager.Models;
using Common.Users;

namespace CoopServerManager.Services
{
    public class AuthRequestDto
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public class FastDlServerService : IDisposable
    {
        private TcpListener? listener;
        private CancellationTokenSource? cts;
        private Task? listenTask;
        private FastDlManifest? currentManifest;
        private string cacheDirectory = string.Empty;

        public bool IsRunning => listener != null;
        public int Port { get; private set; }
        public int GamePort { get; set; } = 4200;
        public int MaxPlayers { get; set; } = 16;
        public bool HasServerPassword { get; set; }
        public string ServerVisibility { get; set; } = "Public";
        public Func<int>? GetCurrentPlayerCount { get; set; }
        public string? ActiveSavePath { get; set; }

        public event Action<string>? OnLog;

        public void Start(int port, FastDlManifest manifest, string cacheDir)
        {
            if (IsRunning)
                Stop();

            Port = port;
            currentManifest = manifest;
            cacheDirectory = cacheDir;

            cts = new CancellationTokenSource();
            // Bind to 0.0.0.0 so LAN, Radmin VPN, and local loopback clients connect without requiring Windows Admin http.sys urlacl
            listener = new TcpListener(IPAddress.Any, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();

            OnLog?.Invoke($"[FastDL & Auth] HTTP Sunucusu 0.0.0.0:{port} adresinde başlatıldı (Yerel / LAN / Radmin VPN erişilebilir).");
            listenTask = Task.Run(() => ProcessRequestsAsync(cts.Token));
        }

        public void UpdateManifest(FastDlManifest manifest)
        {
            currentManifest = manifest;
        }

        public void Stop()
        {
            try
            {
                cts?.Cancel();
                if (listener != null)
                {
                    listener.Stop();
                }
                OnLog?.Invoke("[FastDL & Auth] HTTP Sunucusu durduruldu.");
            }
            catch
            {
            }
            finally
            {
                listener = null;
                cts = null;
            }
        }

        private async Task ProcessRequestsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener != null)
            {
                try
                {
                    var tcpClient = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleTcpClientAsync(tcpClient, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (token.IsCancellationRequested) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[FastDL Hata] {ex.Message}");
                }
            }
        }

        private async Task HandleTcpClientAsync(TcpClient tcpClient, CancellationToken token)
        {
            string clientIp = "unknown";
            try
            {
                using (tcpClient)
                {
                    tcpClient.ReceiveTimeout = 10000;
                    tcpClient.SendTimeout = 120000;

                    if (tcpClient.Client.RemoteEndPoint is IPEndPoint ep)
                    {
                        clientIp = ep.Address.ToString();
                    }

                    using var stream = tcpClient.GetStream();
                    var (method, rawPath, bodyBytes) = await ReadHttpRequestAsync(stream, token);
                    if (string.IsNullOrEmpty(method)) return;

                    if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteTextResponseAsync(stream, 200, "OK", "text/plain", string.Empty, token);
                        return;
                    }

                    if (rawPath.Equals("manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        OnLog?.Invoke($"[FastDL] {clientIp} -> Manifest talep etti.");
                        var json = JsonSerializer.Serialize(currentManifest, new JsonSerializerOptions { WriteIndented = true });
                        await WriteTextResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, token);
                    }
                    else if (rawPath.Equals("api/save", StringComparison.OrdinalIgnoreCase) ||
                             rawPath.Equals("save", StringComparison.OrdinalIgnoreCase) ||
                             rawPath.EndsWith("/save", StringComparison.OrdinalIgnoreCase))
                    {
                        string? saveFileToSend = await ResolveSaveFileForDownloadAsync(token);
                        if (!string.IsNullOrEmpty(saveFileToSend) && File.Exists(saveFileToSend))
                        {
                            var fi = new FileInfo(saveFileToSend);
                            OnLog?.Invoke($"[FastDL] {clientIp} -> Dünya kayıt dosyasını talep etti: '{fi.Name}' ({fi.Length / (1024.0 * 1024.0):N1} MB). Gönderiliyor...");

                            await WriteHeaderAsync(stream, 200, "OK", "application/octet-stream", fi.Length,
                                $"Content-Disposition: attachment; filename=\"{fi.Name}\"\r\n", token);

                            using (var fs = new FileStream(saveFileToSend, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                await fs.CopyToAsync(stream, 81920, token);
                            }
                            await stream.FlushAsync(token);
                            OnLog?.Invoke($"[FastDL] {clientIp} -> Dünya kayıt dosyası ('{fi.Name}') başarıyla aktarıldı.");
                        }
                        else
                        {
                            OnLog?.Invoke($"[FastDL] {clientIp} -> HATA: Sunucu dünya kayıt dosyası bulunamadı! (Yol: {saveFileToSend})");
                            await WriteTextResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "World save not found.", token);
                        }
                    }
                    else if (rawPath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                    {
                        var filePart = rawPath.Substring("mods/".Length);
                        if (!filePart.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            filePart += ".zip";
                        }

                        var fullCacheDir = Path.GetFullPath(cacheDirectory);
                        var filePath = Path.GetFullPath(Path.Combine(cacheDirectory, filePart));
                        if (filePath.StartsWith(fullCacheDir, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
                        {
                            var fileInfo = new FileInfo(filePath);
                            OnLog?.Invoke($"[FastDL] {clientIp} -> '{filePart}' indiriyor ({fileInfo.Length / (1024 * 1024):N1} MB)...");

                            await WriteHeaderAsync(stream, 200, "OK", "application/zip", fileInfo.Length,
                                $"Content-Disposition: attachment; filename=\"{filePart}\"\r\n", token);

                            using (var fs = File.OpenRead(filePath))
                            {
                                await fs.CopyToAsync(stream, 81920, token);
                            }
                            await stream.FlushAsync(token);
                            OnLog?.Invoke($"[FastDL] {clientIp} -> '{filePart}' indirmesi tamamlandı.");
                        }
                        else
                        {
                            OnLog?.Invoke($"[FastDL] {clientIp} -> İstenen mod dosyası bulunamadı: {filePart}");
                            await WriteTextResponseAsync(stream, 404, "Not Found", "text/plain", "Not Found", token);
                        }
                    }
                    else if (rawPath.Equals("api/auth/login", StringComparison.OrdinalIgnoreCase) && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = Encoding.UTF8.GetString(bodyBytes);
                        var authData = JsonSerializer.Deserialize<AuthRequestDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        var ok = UserDatabase.Instance.ValidateLogin(authData?.Username ?? "", authData?.Password ?? "", out var error);
                        OnLog?.Invoke($"[Auth] {clientIp} -> Giriş denemesi ({authData?.Username}): {(ok ? "BAŞARILI" : "REDDEDİLDİ: " + error)}");

                        var result = new
                        {
                            success = ok,
                            message = ok ? "Giriş başarılı" : error,
                            username = authData?.Username
                        };

                        var json = JsonSerializer.Serialize(result);
                        await WriteTextResponseAsync(stream, ok ? 200 : 401, ok ? "OK" : "Unauthorized", "application/json; charset=utf-8", json, token);
                    }
                    else if (rawPath.Equals("api/auth/register", StringComparison.OrdinalIgnoreCase) && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = Encoding.UTF8.GetString(bodyBytes);
                        var authData = JsonSerializer.Deserialize<AuthRequestDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        var ok = UserDatabase.Instance.Register(authData?.Username ?? "", authData?.Password ?? "", false, out var error);
                        OnLog?.Invoke($"[Auth] {clientIp} -> Kayıt denemesi ({authData?.Username}): {(ok ? "BAŞARILI" : "REDDEDİLDİ: " + error)}");

                        var result = new
                        {
                            success = ok,
                            message = ok ? "Kayıt başarılı" : error
                        };

                        var json = JsonSerializer.Serialize(result);
                        await WriteTextResponseAsync(stream, ok ? 200 : 400, ok ? "OK" : "Bad Request", "application/json; charset=utf-8", json, token);
                    }
                    else
                    {
                        var status = new
                        {
                            status = "Online",
                            service = "Calradia Connected FastDL & Lobby API",
                            serverName = currentManifest?.ServerName ?? "Calradia Connected Sunucusu",
                            gamePort = GamePort,
                            fastDlPort = Port,
                            currentPlayers = GetCurrentPlayerCount?.Invoke() ?? 0,
                            maxPlayers = MaxPlayers,
                            hasPassword = HasServerPassword,
                            visibility = ServerVisibility,
                            modulesCount = currentManifest?.Modules.Count ?? 0,
                            port = Port,
                            usersCount = UserDatabase.Instance.GetAllUsers().Count
                        };
                        var json = JsonSerializer.Serialize(status);
                        await WriteTextResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, token);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FastDL Aktarım Hatası] ({clientIp}): {ex.Message}");
            }
        }

        private async Task<string?> ResolveSaveFileForDownloadAsync(CancellationToken token)
        {
            string? saveFileToSend = ActiveSavePath;
            for (int retry = 0; retry < 30 && !token.IsCancellationRequested; retry++)
            {
                if (!string.IsNullOrEmpty(saveFileToSend) && File.Exists(saveFileToSend) && new FileInfo(saveFileToSend).Length > 100000)
                {
                    return saveFileToSend;
                }

                saveFileToSend = FindLatestActiveSaveCandidate();
                if (!string.IsNullOrEmpty(saveFileToSend) && File.Exists(saveFileToSend) && new FileInfo(saveFileToSend).Length > 100000)
                {
                    return saveFileToSend;
                }

                await Task.Delay(1000, token);
            }
            return saveFileToSend;
        }

        private string? FindLatestActiveSaveCandidate()
        {
            var saveSvc = new SaveGameService();
            var primaryFolder = saveSvc.GetPrimarySavesFolder();
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var candidateDirs = new[]
            {
                primaryFolder,
                Path.Combine(myDocs, "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "Game Saves")
            }
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            var sanitizedTitle = SaveGameService.SanitizeSaveFileName(currentManifest?.ServerName) + ".sav";
            foreach (var dir in candidateDirs)
            {
                var namedCandidate = Path.Combine(dir, sanitizedTitle);
                if (File.Exists(namedCandidate) && new FileInfo(namedCandidate).Length > 100000)
                    return namedCandidate;

                var yeniCandidate = Path.Combine(dir, "Yeni_Oyun.sav");
                if (File.Exists(yeniCandidate) && new FileInfo(yeniCandidate).Length > 100000)
                    return yeniCandidate;

                var latest = new DirectoryInfo(dir)
                    .GetFiles("*.sav", SearchOption.AllDirectories)
                    .Where(f => !f.Name.Equals("CalradiaConnected_World.sav", StringComparison.OrdinalIgnoreCase) && f.Length > 100000)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest != null)
                    return latest.FullName;
            }

            return null;
        }

        private static async Task<(string Method, string RawPath, byte[] Body)> ReadHttpRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var headerBytes = new List<byte>(1024);
            var singleByte = new byte[1];
            int matchState = 0;

            while (headerBytes.Count < 16384 && !token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(singleByte, 0, 1, token);
                if (read <= 0) break;

                byte b = singleByte[0];
                headerBytes.Add(b);

                if (b == '\r' && (matchState == 0 || matchState == 2)) matchState++;
                else if (b == '\n' && matchState == 1) matchState = 2;
                else if (b == '\n' && matchState == 3) break;
                else if (b == '\n' && matchState == 0) matchState = 2;
                else matchState = 0;
            }

            if (headerBytes.Count == 0) return (string.Empty, string.Empty, Array.Empty<byte>());

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return (string.Empty, string.Empty, Array.Empty<byte>());

            var reqParts = lines[0].Split(' ');
            string method = reqParts.Length > 0 ? reqParts[0].Trim().ToUpperInvariant() : "GET";
            string rawTarget = reqParts.Length > 1 ? reqParts[1].Trim() : "/";

            int qIdx = rawTarget.IndexOf('?');
            if (qIdx >= 0) rawTarget = rawTarget.Substring(0, qIdx);
            if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var absUri))
            {
                rawTarget = absUri.AbsolutePath;
            }
            string rawPath = rawTarget.Trim('/');

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = lines[i].Substring("Content-Length:".Length).Trim();
                    int.TryParse(val, out contentLength);
                }
            }

            byte[] body = Array.Empty<byte>();
            if (contentLength > 0 && contentLength <= 1024 * 1024)
            {
                body = new byte[contentLength];
                int offset = 0;
                while (offset < contentLength)
                {
                    int r = await stream.ReadAsync(body, offset, contentLength - offset, token);
                    if (r <= 0) break;
                    offset += r;
                }
            }

            return (method, rawPath, body);
        }

        private static Task WriteTextResponseAsync(NetworkStream stream, int code, string reason, string contentType, string body, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            return WriteBytesResponseAsync(stream, code, reason, contentType, bytes, token);
        }

        private static async Task WriteBytesResponseAsync(NetworkStream stream, int code, string reason, string contentType, byte[] body, CancellationToken token)
        {
            await WriteHeaderAsync(stream, code, reason, contentType, body.Length, string.Empty, token);
            if (body.Length > 0)
            {
                await stream.WriteAsync(body, 0, body.Length, token);
            }
            await stream.FlushAsync(token);
        }

        private static async Task WriteHeaderAsync(NetworkStream stream, int code, string reason, string contentType, long contentLength, string extraHeaders, CancellationToken token)
        {
            var hdr = $"HTTP/1.1 {code} {reason}\r\n" +
                      $"Content-Type: {contentType}\r\n" +
                      $"Content-Length: {contentLength}\r\n" +
                      $"Access-Control-Allow-Origin: *\r\n" +
                      $"Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                      $"Access-Control-Allow-Headers: Content-Type\r\n" +
                      $"Connection: close\r\n" +
                      extraHeaders +
                      "\r\n";
            var hdrBytes = Encoding.ASCII.GetBytes(hdr);
            await stream.WriteAsync(hdrBytes, 0, hdrBytes.Length, token);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
