using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CoopServerManager.Services
{
    public class SaveGameService
    {
        public string GetPrimarySavesFolder()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var candidates = new[]
            {
                Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(myDocs, "Mount and Blade II Bannerlord", "Game Saves")
            };

            foreach (var dir in candidates)
            {
                if (Directory.Exists(dir))
                    return dir;
            }

            return candidates[0];
        }

        public string EnsureYeniOyunSaveExists()
        {
            // Fresh world is generated on-demand by the dedicated server process.
            return "Yeni_Oyun";
        }

        public static string SanitizeSaveFileName(string? serverTitle)
        {
            if (string.IsNullOrWhiteSpace(serverTitle))
                return "Calradia_Sunucusu";

            string normalized = serverTitle.Trim()
                .Replace('ş', 's').Replace('Ş', 'S')
                .Replace('ı', 'i').Replace('İ', 'I')
                .Replace('ğ', 'g').Replace('Ğ', 'G')
                .Replace('ü', 'u').Replace('Ü', 'U')
                .Replace('ö', 'o').Replace('Ö', 'O')
                .Replace('ç', 'c').Replace('Ç', 'C');

            var sb = new System.Text.StringBuilder(normalized.Length);
            bool lastWasUnderscore = false;
            foreach (char c in normalized)
            {
                if (char.IsLetterOrDigit(c) || c == '-')
                {
                    sb.Append(c);
                    lastWasUnderscore = false;
                }
                else if (char.IsWhiteSpace(c) || c == '_')
                {
                    if (!lastWasUnderscore && sb.Length > 0)
                    {
                        sb.Append('_');
                        lastWasUnderscore = true;
                    }
                }
            }

            string result = sb.ToString().Trim('_', '-');
            if (string.IsNullOrWhiteSpace(result) ||
                result.Equals("Yeni_Oyun", StringComparison.OrdinalIgnoreCase) ||
                result.Equals("NEW_GAME", StringComparison.OrdinalIgnoreCase))
            {
                return "Calradia_Sunucusu";
            }

            return result.Length > 48 ? result.Substring(0, 48).TrimEnd('_') : result;
        }

        public List<string> GetSaveFiles()
        {
            var saves = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var candidateDirs = new List<string>();

                var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(myDocs))
                    candidateDirs.Add(Path.Combine(myDocs, "Mount and Blade II Bannerlord", "Game Saves"));

                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    candidateDirs.Add(Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "Game Saves"));
                    candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "Game Saves"));
                    candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "Game Saves"));
                }

                foreach (var dir in candidateDirs)
                {
                    if (Directory.Exists(dir))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        foreach (var f in dirInfo.GetFiles("*.sav"))
                        {
                            var saveName = Path.GetFileNameWithoutExtension(f.Name);
                            if (saveName.Equals("CalradiaConnected_World", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!saves.ContainsKey(saveName) || f.LastWriteTimeUtc > saves[saveName])
                            {
                                saves[saveName] = f.LastWriteTimeUtc;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore IO errors when searching for saves
            }

            return saves.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        }
    }
}
