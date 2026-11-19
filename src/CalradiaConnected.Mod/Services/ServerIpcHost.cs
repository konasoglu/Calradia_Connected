#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CalradiaConnected.Campaign.Progression;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Campaign.Time;
using CalradiaConnected.Common.Config;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine.Threading;
using CalradiaConnected.Network.Transport;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace CalradiaConnected.Mod.Services
{
    public class ServerStatusDto
    {
        [JsonProperty("timeMode")]
        public string TimeMode { get; set; } = "Bilinmiyor";

        [JsonProperty("persistent")]
        public bool Persistent { get; set; } = true;

        [JsonProperty("playersCount")]
        public int PlayersCount { get; set; }

        [JsonProperty("players")]
        public string Players { get; set; } = string.Empty;

        [JsonProperty("day")]
        public double Day { get; set; }

        [JsonProperty("tickRate")]
        public int TickRate { get; set; } = 60;

        [JsonProperty("timeout")]
        public int Timeout { get; set; } = 60;
    }

    public class PlayerInfoModel
    {
        [JsonProperty("controllerId")]
        public string ControllerId { get; set; } = string.Empty;

        [JsonProperty("username")]
        public string Username { get; set; } = string.Empty;

        [JsonProperty("platformName")]
        public string PlatformName { get; set; } = string.Empty;

        [JsonProperty("heroName")]
        public string HeroName { get; set; } = string.Empty;

        [JsonProperty("clanName")]
        public string ClanName { get; set; } = string.Empty;

        [JsonProperty("gold")]
        public int Gold { get; set; }

        [JsonProperty("renown")]
        public double Renown { get; set; }

        [JsonProperty("influence")]
        public double Influence { get; set; }

        [JsonProperty("partyCount")]
        public int PartyCount { get; set; }

        [JsonProperty("partyLimit")]
        public int PartyLimit { get; set; }

        [JsonProperty("woundedCount")]
        public int WoundedCount { get; set; }

        [JsonProperty("hp")]
        public int Hp { get; set; } = 100;

        [JsonProperty("maxHp")]
        public int MaxHp { get; set; } = 100;

        [JsonProperty("isAdmin")]
        public bool IsAdmin { get; set; }

        [JsonProperty("isConnected")]
        public bool IsConnected { get; set; }
    }

    public class ServerIpcHost : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private IEngineDispatcher? _dispatcher;
        private INetworkServer? _server;

        public bool IsRunning => _listener != null;
        public int Port { get; private set; } = 4205;

        // Cached models for zero-latency lock-free replies
        private readonly object _cacheLock = new object();
        private string _cachedStatusJson = "{\"timeMode\":\"Bilinmiyor\",\"persistent\":true,\"playersCount\":0,\"players\":\"\",\"day\":1.0,\"tickRate\":60,\"timeout\":60}";
        private string _cachedPlayersJson = "[]";

        public void Start(int port, IEngineDispatcher dispatcher, INetworkServer server)
        {
            if (IsRunning) Stop();

            Port = port;
            _dispatcher = dispatcher;
            _server = server;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                Task.Run(() => AcceptLoopAsync(_cts.Token));
                TaleWorlds.Library.Debug.Print($"[Calradia-Connected] ServerControl IPC Sunucusu {port} portunda başlatıldı.");
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] IPC Sunucusu başlatılamadı ({port}): {ex.Message}");
                _listener = null;
            }
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

        public void UpdateCacheOnTick()
        {
            try
            {
                var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                string timeModeStr = "Durduruldu";
                double currentDay = 1.0;

                if (campaign != null)
                {
                    timeModeStr = campaign.TimeControlMode switch
                    {
                        CampaignTimeControlMode.Stop => "Durduruldu",
                        CampaignTimeControlMode.StoppableFastForward => "Hızlı",
                        CampaignTimeControlMode.UnstoppableFastForward => "Hızlı",
                        _ => "Normal"
                    };
                    currentDay = CampaignTime.Now.ToDays;
                }

                var playerList = new List<PlayerInfoModel>();
                var sessions = _server?.Sessions.Values.ToList();

                if (sessions != null && sessions.Count > 0)
                {
                    foreach (var s in sessions)
                    {
                        string heroId = $"cc_hero_{s.PeerId}";
                        Hero? hero = null;
                        MobileParty? party = null;

                        if (campaign != null)
                        {
                            hero = campaign.CampaignObjectManager.Find<Hero>(heroId)
                                ?? campaign.AliveHeroes.FirstOrDefault(h => h.StringId == heroId);

                            string partyId = $"cc_party_{s.PeerId}";
                            party = hero?.PartyBelongedTo
                                ?? campaign.CampaignObjectManager.Find<MobileParty>(partyId);
                        }

                        var cachedRoster = RosterSyncService.Instance.GetCachedRoster(s.PeerId);
                        int partyLimit = RosterSyncService.Instance.GetSyncedPartySizeLimit(s.PeerId);
                        if (partyLimit <= 0)
                        {
                            partyLimit = party?.Party?.PartySizeLimit ?? 0;
                        }

                        int gold = hero?.Gold ?? (cachedRoster?.HeroGold ?? 0);
                        double renown = hero?.Clan?.Renown ?? (cachedRoster?.ClanRenown ?? 0);
                        double influence = hero?.Clan?.Influence ?? (cachedRoster?.ClanInfluence ?? 0);
                        int hp = hero?.HitPoints ?? (cachedRoster?.HeroHp ?? 100);
                        int maxHp = hero?.MaxHitPoints ?? (cachedRoster?.HeroMaxHp ?? 100);

                        var model = new PlayerInfoModel
                        {
                            ControllerId = s.PeerId.ToString(),
                            Username = s.PlayerName,
                            PlatformName = "Bannerlord",
                            HeroName = hero?.Name?.ToString() ?? s.PlayerName,
                            ClanName = hero?.Clan?.Name?.ToString() ?? string.Empty,
                            Gold = gold,
                            Renown = Math.Round(renown, 1),
                            Influence = Math.Round(influence, 1),
                            PartyCount = party?.MemberRoster?.TotalManCount ?? ((cachedRoster?.TotalHealthy ?? 0) + (cachedRoster?.TotalWounded ?? 0)),
                            PartyLimit = partyLimit,
                            WoundedCount = party?.MemberRoster?.TotalWounded ?? (cachedRoster?.TotalWounded ?? 0),
                            Hp = hp,
                            MaxHp = maxHp,
                            IsAdmin = global::Common.Users.UserDatabase.Instance.IsAdmin(s.PlayerName),
                            IsConnected = s.Peer != null && s.Peer.ConnectionState == LiteNetLib.ConnectionState.Connected
                        };
                        playerList.Add(model);
                    }
                }

                var statusDto = new ServerStatusDto
                {
                    TimeMode = timeModeStr,
                    Persistent = CampaignTimeSyncBehavior.PersistentWorldTime,
                    PlayersCount = playerList.Count,
                    Players = string.Join(", ", playerList.Select(p => p.Username)),
                    Day = Math.Round(currentDay, 1),
                    TickRate = _server?.TickRateHz ?? ServerRuntimeSettings.Current.ServerTickRateHz,
                    Timeout = _server?.DisconnectTimeoutSec ?? ServerRuntimeSettings.Current.DisconnectTimeoutSec
                };

                lock (_cacheLock)
                {
                    _cachedStatusJson = JsonConvert.SerializeObject(statusDto);
                    _cachedPlayersJson = JsonConvert.SerializeObject(playerList);
                }
            }
            catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client, token));
                }
                catch
                {
                    if (token.IsCancellationRequested) break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true) { AutoFlush = true })
            {
                try
                {
                    string? line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) return;

                    string response = ProcessCommand(line.Trim());
                    await writer.WriteLineAsync(response);
                    await writer.FlushAsync();
                }
                catch { }
            }
        }

        private static bool TrySplitTrailingTokens(string argsText, int trailingCount, out string leadingTarget, out string[] trailing)
        {
            trailing = Array.Empty<string>();
            leadingTarget = string.Empty;
            var tokens = argsText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < trailingCount + 1) return false;

            trailing = new string[trailingCount];
            int leadCount = tokens.Length - trailingCount;
            for (int i = 0; i < trailingCount; i++)
            {
                trailing[i] = tokens[leadCount + i];
            }
            leadingTarget = string.Join(" ", tokens, 0, leadCount);
            return true;
        }

        private string ProcessCommand(string command)
        {
            int firstSpace = command.IndexOf(' ');
            string verb = (firstSpace > 0 ? command.Substring(0, firstSpace) : command).Trim().ToUpperInvariant();
            string rest = firstSpace > 0 ? command.Substring(firstSpace + 1).Trim() : string.Empty;

            if (string.IsNullOrEmpty(verb)) return "ERR Empty command";

            switch (verb)
            {
                case "PING":
                    return "PONG";

                case "GET_STATUS":
                    lock (_cacheLock)
                    {
                        return _cachedStatusJson;
                    }

                case "GET_PLAYERS":
                    lock (_cacheLock)
                    {
                        return _cachedPlayersJson;
                    }

                case "SET_TIME":
                    if (!int.TryParse(rest, out int mode))
                        return "ERR Invalid time mode";

                    _dispatcher?.Enqueue(() =>
                    {
                        CampaignTimeSyncBehavior.SetAuthoritativeSpeedFromServerManager(mode);
                        UpdateCacheOnTick();
                    });
                    return "OK";

                case "SET_PERSISTENT_TIME":
                    bool persistent = rest == "1" || rest.Equals("true", StringComparison.OrdinalIgnoreCase);
                    _dispatcher?.Enqueue(() =>
                    {
                        CampaignTimeSyncBehavior.PersistentWorldTime = persistent;
                        ServerRuntimeSettings.Current.PersistentWorldTime = persistent;
                        ServerGameRulesService.Instance.BroadcastCurrentConfigToAllClients();
                        UpdateCacheOnTick();
                    });
                    return "OK";

                case "SET_NETWORK":
                {
                    var netParts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (netParts.Length < 2 || !int.TryParse(netParts[0], out int tickHz) || !int.TryParse(netParts[1], out int timeoutSec))
                        return "ERR Syntax: SET_NETWORK <tickHz> <timeoutSec> [pollMs]";
                    int pollMs = netParts.Length >= 3 && int.TryParse(netParts[2], out int pm) ? pm : 20;

                    _dispatcher?.Enqueue(() =>
                    {
                        ServerRuntimeSettings.Current.ServerTickRateHz = tickHz;
                        ServerRuntimeSettings.Current.DisconnectTimeoutSec = timeoutSec;
                        ServerRuntimeSettings.Current.NetworkPollIntervalMs = pollMs;
                        _server?.ApplyNetworkSettings(tickHz, timeoutSec, pollMs);
                        UpdateCacheOnTick();
                    });
                    return "OK";
                }

                case "RELOAD_CONFIG":
                    _dispatcher?.Enqueue(() =>
                    {
                        try
                        {
                            ServerGameRulesService.Instance.LoadAndApplyOnServer();
                            var cfg = ServerRuntimeSettings.Current;
                            if (_server != null)
                            {
                                if (!string.IsNullOrWhiteSpace(cfg.ServerName)) _server.ServerName = cfg.ServerName;
                                _server.RequiredServerPassword = cfg.ServerPassword ?? string.Empty;
                                _server.ServerVisibility = string.IsNullOrWhiteSpace(cfg.ServerVisibility) ? "Public" : cfg.ServerVisibility;
                                _server.MaxPlayers = Math.Max(2, cfg.MaxPlayers);
                                _server.ApplyNetworkSettings(cfg.ServerTickRateHz, cfg.DisconnectTimeoutSec, cfg.NetworkPollIntervalMs);
                            }
                            ServerPerformanceOptimizer.Instance.ReloadAndApplyLive();
                            ServerGameRulesService.Instance.BroadcastCurrentConfigToAllClients();
                            UpdateCacheOnTick();
                        }
                        catch (Exception ex)
                        {
                            TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] RELOAD_CONFIG error: {ex.Message}");
                        }
                    });
                    return "OK";

                case "RELOAD_USERS":
                    _dispatcher?.Enqueue(() =>
                    {
                        try
                        {
                            global::Common.Users.UserDatabase.Instance.Load();
                            if (_server != null)
                            {
                                foreach (var session in _server.Sessions.Values.ToList())
                                {
                                    var acc = global::Common.Users.UserDatabase.Instance.GetUser(session.PlayerName);
                                    if (acc == null)
                                    {
                                        _server.KickSession(session.PlayerName, Loc.T("Hesabınız yönetici tarafından silindi.", "Your account was deleted by an administrator."));
                                    }
                                    else if (acc.IsBanned)
                                    {
                                        string reason = string.IsNullOrWhiteSpace(acc.BanReason)
                                            ? Loc.T("Hesabınız sunucudan yasaklandı.", "Your account has been banned.")
                                            : acc.BanReason;
                                        _server.KickSession(session.PlayerName, reason);
                                    }
                                    else
                                    {
                                        ServerGameRulesService.Instance.SendConfigToPeer(session.PeerId, session.PlayerName);
                                    }
                                }
                            }
                            UpdateCacheOnTick();
                        }
                        catch { }
                    });
                    return "OK";

                case "RESET_PROGRESSION":
                    _dispatcher?.Enqueue(() =>
                    {
                        try
                        {
                            ServerGameRulesService.Instance.LoadAndApplyOnServer();
                            PlayerStarterProgressionBehavior.ClearAppliedRecordsOnDisk();
                            ServerGameRulesService.Instance.BroadcastCurrentConfigToAllClients(forceReapplyStarter: true);
                        }
                        catch { }
                    });
                    return "OK";

                case "SAVE":
                    _dispatcher?.Enqueue(() =>
                    {
                        try
                        {
                            string saveName = !string.IsNullOrWhiteSpace(CalradiaConnected.Engine.ModContext.ActiveServerSaveName)
                                ? CalradiaConnected.Engine.ModContext.ActiveServerSaveName
                                : "Calradia_Sunucusu";
                            TaleWorlds.CampaignSystem.Campaign.Current?.SaveHandler?.SaveAs(saveName);
                        }
                        catch { }
                    });
                    return "OK";

                case "KICK":
                    if (string.IsNullOrWhiteSpace(rest)) return "ERR Username required";
                    string kickTarget = rest;
                    _dispatcher?.Enqueue(() =>
                    {
                        _server?.KickSession(kickTarget, Loc.T("Yönetici tarafından sunucudan çıkarıldınız.", "You were kicked by a server administrator."));
                        UpdateCacheOnTick();
                    });
                    return "OK";

                case "GIVE_GOLD":
                    if (!TrySplitTrailingTokens(rest, 1, out string goldTarget, out var goldTrail) ||
                        !int.TryParse(goldTrail[0], out int goldAmount))
                        return "ERR Syntax: GIVE_GOLD <user> <amount>";
                    return ExecuteAdminReward(goldTarget, 1, goldAmount, 0f, string.Empty, (hero, party) =>
                    {
                        hero.ChangeHeroGold(goldAmount);
                        return $"OK {hero.Name} altın güncellendi: {hero.Gold}";
                    });

                case "GIVE_RENOWN":
                    if (!TrySplitTrailingTokens(rest, 1, out string renTarget, out var renTrail) ||
                        !float.TryParse(renTrail[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float renownAmount))
                        return "ERR Syntax: GIVE_RENOWN <user> <amount>";
                    return ExecuteAdminReward(renTarget, 2, 0, renownAmount, string.Empty, (hero, party) =>
                    {
                        if (hero.Clan != null)
                        {
                            hero.Clan.AddRenown(renownAmount, true);
                            return $"OK {hero.Clan.Name} nam güncellendi: {hero.Clan.Renown:0.#}";
                        }
                        return "OK Nam gönderildi";
                    });

                case "GIVE_INFLUENCE":
                    if (!TrySplitTrailingTokens(rest, 1, out string infTarget, out var infTrail) ||
                        !float.TryParse(infTrail[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float infAmount))
                        return "ERR Syntax: GIVE_INFLUENCE <user> <amount>";
                    return ExecuteAdminReward(infTarget, 3, 0, infAmount, string.Empty, (hero, party) =>
                    {
                        if (hero.Clan != null)
                        {
                            if (hero.Clan.Kingdom != null) ChangeClanInfluenceAction.Apply(hero.Clan, infAmount);
                            else hero.Clan.Influence += infAmount;
                            return $"OK {hero.Clan.Name} nüfuz güncellendi: {hero.Clan.Influence:0.#}";
                        }
                        return "OK Nüfuz gönderildi";
                    });

                case "HEAL_HERO":
                    if (string.IsNullOrWhiteSpace(rest)) return "ERR Syntax: HEAL_HERO <user>";
                    return ExecuteAdminReward(rest, 4, 0, 0f, string.Empty, (hero, party) =>
                    {
                        hero.HitPoints = hero.MaxHitPoints;
                        return $"OK {hero.Name} tamamen iyileştirildi ({hero.HitPoints} HP).";
                    });

                case "HEAL_PARTY":
                    if (string.IsNullOrWhiteSpace(rest)) return "ERR Syntax: HEAL_PARTY <user>";
                    return ExecuteAdminReward(rest, 5, 0, 0f, string.Empty, (hero, party) =>
                    {
                        hero.HitPoints = hero.MaxHitPoints;
                        if (party?.MemberRoster != null)
                        {
                            int cleared = 0;
                            for (int i = 0; i < party.MemberRoster.Count; i++)
                            {
                                int wounded = party.MemberRoster.GetElementWoundedNumber(i);
                                if (wounded > 0)
                                {
                                    party.MemberRoster.AddToCountsAtIndex(i, 0, -wounded, 0, true);
                                    cleared += wounded;
                                }
                            }
                            return $"OK Ordudaki {cleared} yaralı asker iyileştirildi.";
                        }
                        return "OK Ordu iyileştirme gönderildi.";
                    });

                case "DUPLICATE_TROOPS":
                {
                    string dupTarget = rest;
                    int dupCount = 10;
                    if (TrySplitTrailingTokens(rest, 1, out string parsedTarget, out var dupTrail) &&
                        int.TryParse(dupTrail[0], out int parsedCount))
                    {
                        dupTarget = parsedTarget;
                        dupCount = parsedCount;
                    }
                    return ExecuteAdminReward(dupTarget, 7, dupCount, 0f, string.Empty, (hero, party) =>
                    {
                        if (party?.MemberRoster != null)
                        {
                            var stacks = new List<CharacterObject>();
                            for (int i = 0; i < party.MemberRoster.Count; i++)
                            {
                                var c = party.MemberRoster.GetCharacterAtIndex(i);
                                if (c != null && !c.IsHero && !stacks.Contains(c))
                                {
                                    stacks.Add(c);
                                }
                            }
                            if (stacks.Count == 0 && hero.Culture?.BasicTroop != null)
                            {
                                stacks.Add(hero.Culture.BasicTroop);
                            }
                            foreach (var st in stacks)
                            {
                                party.MemberRoster.AddToCounts(st, dupCount);
                            }
                            return $"OK {stacks.Count} birlik türüne +{dupCount} asker eklendi.";
                        }
                        return "OK Asker çoğaltma gönderildi.";
                    });
                }

                case "GIVE_TROOP":
                    if (!TrySplitTrailingTokens(rest, 2, out string troopTarget, out var troopTrail) ||
                        !int.TryParse(troopTrail[1], out int troopCount))
                        return "ERR Syntax: GIVE_TROOP <user> <troopId> <count>";
                    string troopId = troopTrail[0];
                    return ExecuteAdminReward(troopTarget, 6, troopCount, 0f, troopId, (hero, party) =>
                    {
                        var troopObj = RosterSyncService.ResolveTroopById(troopId);
                        if (troopObj != null && party?.MemberRoster != null)
                        {
                            party.MemberRoster.AddToCounts(troopObj, troopCount);
                            return $"OK {troopObj.Name} x{troopCount} orduya eklendi.";
                        }
                        return troopObj == null ? $"ERR Birlik bulunamadı: {troopId}" : "OK Birlik gönderildi.";
                    });

                case "SET_ADMIN":
                    if (!TrySplitTrailingTokens(rest, 1, out string adminUser, out var adminTrail) ||
                        !int.TryParse(adminTrail[0], out int adminVal))
                        return "ERR Syntax: SET_ADMIN <username> <1/0>";
                    bool isNowAdmin = adminVal == 1;
                    bool ok = global::Common.Users.UserDatabase.Instance.SetAdmin(adminUser, isNowAdmin);
                    if (ok)
                    {
                        _dispatcher?.Enqueue(() =>
                        {
                            var session = _server?.Sessions.Values.FirstOrDefault(s =>
                                string.Equals(s.PlayerName, adminUser, StringComparison.OrdinalIgnoreCase));
                            if (session != null)
                            {
                                ServerGameRulesService.Instance.SendConfigToPeer(session.PeerId, session.PlayerName);
                            }
                            UpdateCacheOnTick();
                        });
                    }
                    return ok ? "OK" : "ERR User not found";

                default:
                    return $"ERR Unknown command: {verb}";
            }
        }

        /// <summary>
        /// Executes in-game admin slash commands sent via F9 Chat by an authenticated admin user.
        /// Returns a system reply text to send back to the admin.
        /// </summary>
        public string ExecuteAdminChatCommand(int senderPeerId, string senderUsername, string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage) || !rawMessage.StartsWith("/"))
            {
                return string.Empty;
            }

            if (!global::Common.Users.UserDatabase.Instance.IsAdmin(senderUsername))
            {
                return Loc.T("Bu komutu kullanmak için yönetici yetkisi gereklidir.", "Administrator privileges are required to use this command.");
            }

            var tokens = rawMessage.Trim().Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return string.Empty;

            string cmd = tokens[0].ToLowerInvariant();
            string selfTarget = senderPeerId.ToString();

            switch (cmd)
            {
                case "help":
                    return Loc.T(
                        "Komutlar: /gold <miktar> [oyuncu], /renown <miktar> [oyuncu], /influence <miktar> [oyuncu], /heal [oyuncu], /healparty [oyuncu], /troop <id> <adet> [oyuncu], /time <0|1|2>, /save, /kick <oyuncu>, /ban <oyuncu>",
                        "Commands: /gold <amt> [player], /renown <amt> [player], /influence <amt> [player], /heal [player], /healparty [player], /troop <id> <cnt> [player], /time <0|1|2>, /save, /kick <player>, /ban <player>");

                case "gold":
                    if (tokens.Length < 2 || !int.TryParse(tokens[1], out int gAmt))
                        return "Kullanım: /gold <miktar> [oyuncu|all]";
                    string gTarget = tokens.Length >= 3 ? string.Join(" ", tokens.Skip(2)) : selfTarget;
                    return ProcessCommand($"GIVE_GOLD {gTarget} {gAmt}");

                case "renown":
                    if (tokens.Length < 2 || !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float rAmt))
                        return "Kullanım: /renown <miktar> [oyuncu|all]";
                    string rTarget = tokens.Length >= 3 ? string.Join(" ", tokens.Skip(2)) : selfTarget;
                    return ProcessCommand($"GIVE_RENOWN {rTarget} {rAmt.ToString(CultureInfo.InvariantCulture)}");

                case "influence":
                    if (tokens.Length < 2 || !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float iAmt))
                        return "Kullanım: /influence <miktar> [oyuncu|all]";
                    string iTarget = tokens.Length >= 3 ? string.Join(" ", tokens.Skip(2)) : selfTarget;
                    return ProcessCommand($"GIVE_INFLUENCE {iTarget} {iAmt.ToString(CultureInfo.InvariantCulture)}");

                case "heal":
                {
                    string hTarget = tokens.Length >= 2 ? string.Join(" ", tokens.Skip(1)) : selfTarget;
                    return ProcessCommand($"HEAL_HERO {hTarget}");
                }

                case "healparty":
                {
                    string hpTarget = tokens.Length >= 2 ? string.Join(" ", tokens.Skip(1)) : selfTarget;
                    return ProcessCommand($"HEAL_PARTY {hpTarget}");
                }

                case "troop":
                    if (tokens.Length < 3 || !int.TryParse(tokens[2], out int tCnt))
                        return "Kullanım: /troop <birlikId> <adet> [oyuncu|all]";
                    string tTarget = tokens.Length >= 4 ? string.Join(" ", tokens.Skip(3)) : selfTarget;
                    return ProcessCommand($"GIVE_TROOP {tTarget} {tokens[1]} {tCnt}");

                case "time":
                    if (tokens.Length < 2) return "Kullanım: /time <0|1|2>";
                    return ProcessCommand($"SET_TIME {tokens[1]}");

                case "save":
                    return ProcessCommand("SAVE");

                case "kick":
                    if (tokens.Length < 2) return "Kullanım: /kick <oyuncu>";
                    return ProcessCommand($"KICK {string.Join(" ", tokens.Skip(1))}");

                case "ban":
                    if (tokens.Length < 2) return "Kullanım: /ban <oyuncu>";
                    string banUser = string.Join(" ", tokens.Skip(1));
                    global::Common.Users.UserDatabase.Instance.SetBan(banUser, true, $"Banned in-game by {senderUsername}");
                    ProcessCommand($"KICK {banUser}");
                    return $"OK '{banUser}' yasaklandı ve sunucudan çıkarıldı.";

                default:
                    return Loc.F("Bilinmeyen komut: /{0}. Yardım için /help yazın.", "Unknown command: /{0}. Type /help for commands.", cmd);
            }
        }

        private string ExecuteAdminReward(string rawTarget, byte rewardType, int intAmount, float floatAmount, string troopId, Func<Hero, MobileParty?, string> action)
        {
            var tcs = new TaskCompletionSource<string>();

            _dispatcher?.Enqueue(() =>
            {
                try
                {
                    var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                    if (campaign == null)
                    {
                        tcs.TrySetResult("ERR Campaign not running");
                        return;
                    }

                    // Validate troopId early for GIVE_TROOP so we fail cleanly if invalid
                    if (rewardType == 6 && RosterSyncService.ResolveTroopById(troopId) == null)
                    {
                        tcs.TrySetResult($"ERR Birlik bulunamadı: {troopId}");
                        return;
                    }

                    string target = (rawTarget ?? "all").Trim();
                    if (target.StartsWith("user_", StringComparison.OrdinalIgnoreCase))
                    {
                        target = target.Substring(5);
                    }

                    bool isAll = string.IsNullOrEmpty(target) || string.Equals(target, "all", StringComparison.OrdinalIgnoreCase);
                    int targetPeerId = 0;

                    if (!isAll)
                    {
                        // 1. Match by online session username or peerId
                        var session = _server?.Sessions.Values.FirstOrDefault(s =>
                            string.Equals(s.PlayerName, target, StringComparison.OrdinalIgnoreCase) ||
                            s.PeerId.ToString() == target);

                        if (session != null)
                        {
                            targetPeerId = session.PeerId;
                        }
                        else if (int.TryParse(target, out int parsedPeer) && parsedPeer > 0)
                        {
                            targetPeerId = parsedPeer;
                        }
                        else
                        {
                            // 2. Match by HeroName or StringId -> extract PeerId
                            var matchedByName = campaign.AliveHeroes.FirstOrDefault(h =>
                                h != null &&
                                (string.Equals(h.Name?.ToString(), target, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(h.StringId, target, StringComparison.OrdinalIgnoreCase)));
                            if (matchedByName != null &&
                                CalradiaConnected.Campaign.Identity.NetworkObjectRegistry.TryExtractPeerId(matchedByName.StringId, out int extractedId) &&
                                extractedId > 0)
                            {
                                targetPeerId = extractedId;
                            }
                            else
                            {
                                // 3. Fallback: match by UserDatabase AccountId
                                var acc = global::Common.Users.UserDatabase.Instance.GetUser(target);
                                if (acc != null && acc.AccountId > 0)
                                {
                                    targetPeerId = acc.AccountId;
                                }
                            }
                        }

                        if (targetPeerId <= 0)
                        {
                            tcs.TrySetResult($"ERR Oyuncu bulunamadı: '{target}'");
                            return;
                        }
                    }

                    // Send reward packet ONLY to targetPeerId (or Broadcast when isAll)
                    var rewardPkt = new ServerAdminRewardPacket
                    {
                        TargetPeerId = isAll ? 0 : targetPeerId,
                        RewardType = rewardType,
                        IntAmount = intAmount,
                        FloatAmount = floatAmount,
                        TroopId = troopId
                    };

                    bool deliveredOnline = false;
                    if (isAll)
                    {
                        if (_server != null && _server.Sessions.Count > 0)
                        {
                            _server.Broadcast(rewardPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            deliveredOnline = true;
                        }
                    }
                    else if (_server != null && _server.Sessions.ContainsKey(targetPeerId))
                    {
                        _server.SendTo(targetPeerId, rewardPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        deliveredOnline = true;
                    }

                    // Also apply directly on server puppet hero/party
                    var matchedHeroes = new List<(Hero hero, MobileParty? party)>();

                    if (isAll)
                    {
                        if (_server != null)
                        {
                            foreach (var s in _server.Sessions.Values)
                            {
                                string hId = $"cc_hero_{s.PeerId}";
                                var h = campaign.CampaignObjectManager.Find<Hero>(hId)
                                     ?? campaign.AliveHeroes.FirstOrDefault(x => x.StringId == hId);
                                if (h != null)
                                {
                                    var p = h.PartyBelongedTo ?? campaign.CampaignObjectManager.Find<MobileParty>($"cc_party_{s.PeerId}");
                                    matchedHeroes.Add((h, p));
                                }
                            }
                        }

                        if (matchedHeroes.Count == 0)
                        {
                            foreach (var h in campaign.AliveHeroes)
                            {
                                if (h != null && h.StringId != null && h.StringId.StartsWith("cc_hero_", StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedHeroes.Add((h, h.PartyBelongedTo));
                                }
                            }
                        }
                    }
                    else
                    {
                        string heroId = $"cc_hero_{targetPeerId}";
                        Hero? hero = campaign.CampaignObjectManager.Find<Hero>(heroId)
                            ?? campaign.AliveHeroes.FirstOrDefault(h => h.StringId == heroId)
                            ?? campaign.AliveHeroes.FirstOrDefault(h =>
                                string.Equals(h.Name?.ToString(), target, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(h.StringId, target, StringComparison.OrdinalIgnoreCase));

                        if (hero != null)
                        {
                            var party = hero.PartyBelongedTo
                                ?? campaign.CampaignObjectManager.Find<MobileParty>($"cc_party_{targetPeerId}");
                            matchedHeroes.Add((hero, party));
                        }
                    }

                    if (!deliveredOnline && matchedHeroes.Count == 0)
                    {
                        tcs.TrySetResult(isAll
                            ? "ERR Bağlı veya aktif oyuncu bulunamadı."
                            : $"ERR '{target}' çevrimiçi değil veya dünyada henüz karakteri yok.");
                        return;
                    }

                    string lastMsg = "OK Komut istemciye iletildi.";
                    foreach (var (h, p) in matchedHeroes)
                    {
                        lastMsg = action(h, p);
                    }

                    UpdateCacheOnTick();
                    tcs.TrySetResult(lastMsg);
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult($"ERR {ex.Message}");
                }
            });

            if (tcs.Task.Wait(3500))
            {
                return tcs.Task.Result;
            }
            return "ERR Timeout waiting for game thread";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
