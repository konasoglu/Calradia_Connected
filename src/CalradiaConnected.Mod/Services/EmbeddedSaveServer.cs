using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CalradiaConnected.Mod.Services
{
    public class EmbeddedSaveServer : IDisposable
    {
        public const int UdpChunkSize = 16 * 1024; // 16 KB chunks (safe for LiteNetLib reliable window)

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly object _udpCacheLock = new object();
        private byte[]? _cachedCompressedSave;
        private DateTime _cachedCompressedUtc = DateTime.MinValue;
        private string _cachedSourcePath = string.Empty;

        public bool IsRunning => _listener != null;
        public int Port { get; private set; }

        public bool Start(int port)
        {
            if (IsRunning) return true;

            Port = port;

            // Probe if ServerManager or an external FastDL service is already running on this port
            if (IsPortAlreadyInUse(port))
            {
                TaleWorlds.Library.Debug.Print($"[Calradia-Connected] FastDL portu ({port}) zaten kullanımda (ServerManager FastDL devrede). Dahili HTTP servisi beklemede.");
                return true;
            }

            _cts = new CancellationTokenSource();

            try
            {
                // Bind to IPAddress.Any (0.0.0.0) so LAN, Wi-Fi and Radmin VPN clients can connect without needing Windows Admin http.sys urlacl
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();

                Task.Run(() => ListenLoopAsync(_cts.Token));
                TaleWorlds.Library.Debug.Print($"[Calradia-Connected] Embedded FastDL HTTP Server started on 0.0.0.0:{port} (LAN/Radmin/Local).");
                return true;
            }
            catch (SocketException ex)
            {
                TaleWorlds.Library.Debug.Print($"[Calradia-Connected] FastDL portu ({port}) bağlanamadı ({ex.SocketErrorCode}), UDP fallback aktif.");
                _listener = null;
                return true;
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] FastDL HTTP başlatma hatası: {ex.Message}");
                _listener = null;
                return false;
            }
        }

        private static bool IsPortAlreadyInUse(int port)
        {
            try
            {
                using (var tcp = new TcpClient())
                {
                    var result = tcp.BeginConnect(IPAddress.Loopback, port, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(300);
                    if (success && tcp.Connected)
                    {
                        tcp.EndConnect(result);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleTcpClientAsync(client, token), token);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (token.IsCancellationRequested) break;
                }
                catch
                {
                    if (token.IsCancellationRequested) break;
                }
            }
        }

        private async Task HandleTcpClientAsync(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 60000;
                    using var stream = client.GetStream();

                    string? requestHeader = await ReadHttpHeaderAsync(stream, token).ConfigureAwait(false);
                    if (requestHeader == null || string.IsNullOrWhiteSpace(requestHeader)) return;

                    var lines = requestHeader.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) return;

                    var parts = lines[0].Split(' ');
                    string method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "GET";
                    string rawTarget = parts.Length > 1 ? parts[1] : "/";
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0) rawTarget = rawTarget.Substring(0, qIdx);
                    if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var absUri))
                    {
                        rawTarget = absUri.AbsolutePath;
                    }
                    string rawUrl = rawTarget.ToLowerInvariant();

                    if (method == "OPTIONS")
                    {
                        await WriteTextResponseAsync(stream, 200, "OK", "text/plain", string.Empty).ConfigureAwait(false);
                        return;
                    }

                    if (rawUrl == "/api/ping" || rawUrl == "/ping" || rawUrl == "/api/info" || rawUrl == "/info")
                    {
                        var srv = CalradiaConnected.Engine.ModContext.Server;
                        string srvName = (srv?.ServerName ?? "Calradia Connected Sunucusu").Replace("\"", "\\\"");
                        int gamePort = srv?.Port ?? (Port - 1);
                        int curPlayers = srv?.Sessions.Count ?? 0;
                        int maxPlayers = srv?.MaxPlayers ?? 16;
                        bool hasPass = !string.IsNullOrWhiteSpace(srv?.RequiredServerPassword);

                        string json = $"{{\"status\":\"Online\",\"server\":\"CalradiaConnected\",\"serverName\":\"{srvName}\",\"gamePort\":{gamePort},\"fastDlPort\":{Port},\"currentPlayers\":{curPlayers},\"maxPlayers\":{maxPlayers},\"hasPassword\":{(hasPass ? "true" : "false")}}}";
                        await WriteTextResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json).ConfigureAwait(false);
                        return;
                    }

                    if (rawUrl == "/api/save" || rawUrl == "/save" || rawUrl.EndsWith("/save", StringComparison.OrdinalIgnoreCase))
                    {
                        string savePath = FindActiveServerSave();
                        if (!string.IsNullOrEmpty(savePath) && File.Exists(savePath))
                        {
                            using (var fs = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                await WriteHeaderAsync(stream, 200, "OK", "application/octet-stream", fs.Length,
                                    $"Content-Disposition: attachment; filename=\"{Path.GetFileName(savePath)}\"\r\n").ConfigureAwait(false);
                                await fs.CopyToAsync(stream).ConfigureAwait(false);
                                await stream.FlushAsync().ConfigureAwait(false);
                            }
                            return;
                        }

                        await WriteTextResponseAsync(stream, 503, "Service Unavailable", "text/plain; charset=utf-8", "World save is not ready yet.").ConfigureAwait(false);
                        return;
                    }

                    await WriteTextResponseAsync(stream, 404, "Not Found", "text/plain", "Not Found").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] FastDL Request error: {ex.Message}");
            }
        }

        private static async Task<string?> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken token)
        {
            var headerBytes = new List<byte>(1024);
            var singleByte = new byte[1];
            int matchState = 0;

            while (headerBytes.Count < 16384 && !token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(singleByte, 0, 1, token).ConfigureAwait(false);
                if (read <= 0) break;

                byte b = singleByte[0];
                headerBytes.Add(b);

                if (b == '\r' && (matchState == 0 || matchState == 2)) matchState++;
                else if (b == '\n' && matchState == 1) matchState = 2;
                else if (b == '\n' && matchState == 3) break;
                else if (b == '\n' && matchState == 0) matchState = 2;
                else matchState = 0;
            }

            return headerBytes.Count > 0 ? Encoding.ASCII.GetString(headerBytes.ToArray()) : null;
        }

        private static Task WriteTextResponseAsync(NetworkStream stream, int code, string reason, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            return WriteBytesResponseAsync(stream, code, reason, contentType, bytes);
        }

        private static async Task WriteBytesResponseAsync(NetworkStream stream, int code, string reason, string contentType, byte[] body)
        {
            await WriteHeaderAsync(stream, code, reason, contentType, body.Length, string.Empty).ConfigureAwait(false);
            if (body.Length > 0)
            {
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteHeaderAsync(NetworkStream stream, int code, string reason, string contentType, long contentLength, string extraHeaders)
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
            byte[] hdrBytes = Encoding.ASCII.GetBytes(hdr);
            await stream.WriteAsync(hdrBytes, 0, hdrBytes.Length).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns a short-lived GZip-compressed buffer of the active server world save for UDP fallback transfer.
        /// </summary>
        public byte[]? GetOrBuildUdpSaveArchive(out string error)
        {
            error = string.Empty;
            string savePath = FindActiveServerSave();
            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath))
            {
                error = "Sunucu dünya kayıt dosyası henüz oluşturulmadı.";
                return null;
            }

            lock (_udpCacheLock)
            {
                if (_cachedCompressedSave != null &&
                    string.Equals(_cachedSourcePath, savePath, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - _cachedCompressedUtc).TotalSeconds < 20)
                {
                    return _cachedCompressedSave;
                }

                try
                {
                    using var ms = new MemoryStream();
                    using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    using (var fs = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.CopyTo(gz);
                    }

                    _cachedCompressedSave = ms.ToArray();
                    _cachedSourcePath = savePath;
                    _cachedCompressedUtc = DateTime.UtcNow;
                    return _cachedCompressedSave;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return null;
                }
            }
        }

        public static string FindActiveServerSave()
        {
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                var searchFolders = new[]
                {
                    Path.Combine(myDocs, "Mount and Blade II Bannerlord", "Game Saves"),
                    Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "Game Saves"),
                    Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "Game Saves"),
                    Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "Game Saves")
                };

                foreach (var folder in searchFolders)
                {
                    if (!Directory.Exists(folder)) continue;

                    // 0. Check for active server title save (e.g. Konasoglu_Sunucusu.sav)
                    string activeSaveName = CalradiaConnected.Engine.ModContext.ActiveServerSaveName;
                    if (!string.IsNullOrWhiteSpace(activeSaveName))
                    {
                        var namedSave = Path.Combine(folder, activeSaveName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ? activeSaveName : (activeSaveName + ".sav"));
                        if (File.Exists(namedSave) && new FileInfo(namedSave).Length > 100000)
                        {
                            return namedSave;
                        }
                    }

                    // 1. Check for Yeni_Oyun.sav (freshly generated sandbox world)
                    var newSave = Path.Combine(folder, "Yeni_Oyun.sav");
                    if (File.Exists(newSave) && new FileInfo(newSave).Length > 100000)
                    {
                        return newSave;
                    }

                    // 2. Check for specific CalradiaConnected save
                    var ccSave = Path.Combine(folder, "CalradiaConnected_World.sav");
                    if (File.Exists(ccSave) && new FileInfo(ccSave).Length > 100000)
                    {
                        return ccSave;
                    }

                    // 3. Check most recent .sav file (recursive handles Native/SandBox subfolders)
                    var files = new DirectoryInfo(folder).GetFiles("*.sav", SearchOption.AllDirectories);
                    FileInfo? latest = null;
                    foreach (var f in files)
                    {
                        if (f.Name.Equals("CalradiaConnected_World.sav", StringComparison.OrdinalIgnoreCase)) continue;
                        if (latest == null || f.LastWriteTimeUtc > latest.LastWriteTimeUtc)
                        {
                            latest = f;
                        }
                    }

                    if (latest != null && latest.Length > 100000)
                    {
                        return latest.FullName;
                    }
                }
            }
            catch { }

            return string.Empty;
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
            }
            catch { }
            finally
            {
                _listener = null;
                _cts = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
