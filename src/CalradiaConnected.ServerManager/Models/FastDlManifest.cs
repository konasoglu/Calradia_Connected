using System.Text.Json.Serialization;

namespace CoopServerManager.Models
{
    public class FastDlManifest
    {
        [JsonPropertyName("serverName")]
        public string ServerName { get; set; } = "Bannerlord Coop Server";

        [JsonPropertyName("gameVersion")]
        public string GameVersion { get; set; } = "v1.4.8";

        [JsonPropertyName("timestamp")]
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("modules")]
        public List<FastDlModuleEntry> Modules { get; set; } = new();

        [JsonPropertyName("enableWarSails")]
        public bool EnableWarSails { get; set; } = true;
    }

    public class FastDlModuleEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("fileSizeBytes")]
        public long FileSizeBytes { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }
    }
}
