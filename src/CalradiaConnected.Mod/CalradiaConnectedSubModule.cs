using System;
using System.IO;
using System.Linq;
using System.Net;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Player;
using CalradiaConnected.Campaign.Progression;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Campaign.Sync;
using CalradiaConnected.Campaign.Time;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using CalradiaConnected.Engine.Threading;
using CalradiaConnected.Missions;
using CalradiaConnected.Network.Transport;
using HarmonyLib;
using LiteNetLib;
using Microsoft.Extensions.DependencyInjection;
using SandBox;
using TaleWorldsCampaign = TaleWorlds.CampaignSystem.Campaign;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem;
using Module = TaleWorlds.MountAndBlade.Module;

namespace CalradiaConnected.Mod
{
    public class CalradiaConnectedSubModule : MBSubModuleBase
    {
        private Harmony? _harmony;
        private IEngineDispatcher? _dispatcher;
        private INetworkServer? _server;
        private INetworkClient? _client;
        private readonly WorldSyncManager _worldSync = new WorldSyncManager();
        private readonly Services.EmbeddedSaveServer _embeddedSaveServer = new Services.EmbeddedSaveServer();
        private readonly Services.ServerIpcHost _ipcHost = new Services.ServerIpcHost();

        public static bool IsServerProcess { get; private set; }

        private bool _menuOptionRegistered = false;
        private bool _patchesInstalled = false;
        private bool _serverAutoStarted = false;
        private bool _isServerProcess = false;
        private string _targetSaveName = "Yeni_Oyun";
        private int _serverPort = 4200;
        private static bool _newGameSaveRequested = false;
        private int _newGameSaveTicks = 0;
        private bool _campaignChatModalOpen = false;
        private bool _executionDialogShown = false;
        private float _equipmentPollTimer = 0f;
        private string _lastLocalEquipmentSignature = string.Empty;
        private float _rosterPollTimer = 0f;
        private float _rosterHeartbeatTimer = 0f;
        private string _lastLocalRosterSignature = string.Empty;
        private float _fleetPollTimer = 0f;
        private string _lastLocalFleetSignature = string.Empty;
        private float _ipcCacheTimer = 0f;
        private bool _clientFogResetDone = false;
        private bool _wasCampaignSaving = false;
        private float _rosterFlushTimer = 0f;
        private float _rosterForceResyncTimer = 0f;
        private float _serverRosterBroadcastTimer = 0f;
        private bool _pvpOverlaySubscribed = false;
        private float _despawnFlushTimer = 0f;
        private float _serverAutoSaveTimer = 0f;

        // UDP Save Transfer Fallback state (when TCP FastDL port is blocked on LAN/Radmin)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _udpSaveChunks = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
        private readonly System.Threading.ManualResetEventSlim _udpChunkSignal = new System.Threading.ManualResetEventSlim(false);
        private volatile int _udpSaveTotalChunks = 0;
        private volatile int _udpSaveTotalBytes = 0;
        private volatile bool _udpSaveFailed = false;
        private string _udpSaveError = string.Empty;

        public static void RequestNewGameSave()
        {
            _newGameSaveRequested = true;
        }

        public static void SyncLanguageFromBannerlord(bool force = false)
        {
            if (!force && Loc.IsManuallyOverridden) return;
            try
            {
                string? active = null;
                try { active = MBTextManager.ActiveTextLanguage; } catch { }
                string? cfgLang = null;
                try { cfgLang = BannerlordConfig.Language; } catch { }
                string? diskLang = TryReadBannerlordConfigLanguageFromDisk();

                // During OnSubModuleLoad, BannerlordConfig.Language & MBTextManager.ActiveTextLanguage can still be default "English"
                // before Bannerlord finishes reading BannerlordConfig.txt. Prefer non-default live language, then disk config.
                if (!string.IsNullOrWhiteSpace(active) && !string.Equals(active, "English", StringComparison.OrdinalIgnoreCase))
                {
                    Loc.SyncFromBannerlordLanguage(active);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(cfgLang) && !string.Equals(cfgLang, "English", StringComparison.OrdinalIgnoreCase))
                {
                    Loc.SyncFromBannerlordLanguage(cfgLang);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(diskLang))
                {
                    Loc.SyncFromBannerlordLanguage(diskLang);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(active))
                {
                    Loc.SyncFromBannerlordLanguage(active);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(cfgLang))
                {
                    Loc.SyncFromBannerlordLanguage(cfgLang);
                    return;
                }
                Loc.SyncFromSystemCulture();
            }
            catch { }
        }

        private static string? TryReadBannerlordConfigLanguageFromDisk()
        {
            try
            {
                string cfgPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord",
                    "Configs",
                    "BannerlordConfig.txt");
                if (!File.Exists(cfgPath)) return null;

                foreach (string raw in File.ReadAllLines(cfgPath))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string line = raw.Trim();
                    if (line.StartsWith("Language=", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = line.Substring("Language=".Length).Trim();
                        if (!string.IsNullOrEmpty(val)) return val;
                    }
                }
            }
            catch { }
            return null;
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            try
            {
                SyncLanguageFromBannerlord();

                // Parse command line arguments
                var args = Environment.GetCommandLineArgs();
                _isServerProcess = args.Any(a => string.Equals(a, "/server", StringComparison.OrdinalIgnoreCase));
                IsServerProcess = _isServerProcess;
                if (_isServerProcess)
                {
                    Services.ServerPerformanceOptimizer.Instance.ApplyStartupThrottles();
                }
                
                string? parsedSave = GetArgumentValue(args, "/coopsave") ?? GetArgumentValue(args, "/save");
                if (!string.IsNullOrWhiteSpace(parsedSave))
                {
                    _targetSaveName = parsedSave;
                }

                string? parsedNewGameSave = GetArgumentValue(args, "/newgamesave");
                string? parsedServerName = GetArgumentValue(args, "/servername");
                string? parsedCoopPassword = GetArgumentValue(args, "/cooppassword");
                string? parsedMaxPlayers = GetArgumentValue(args, "/maxplayers");
                string? parsedVisibility = GetArgumentValue(args, "/coopvisibility");

                string? parsedPort = GetArgumentValue(args, "/port");
                if (!string.IsNullOrWhiteSpace(parsedPort) && int.TryParse(parsedPort, out int p))
                {
                    _serverPort = p;
                }

                // Setup Dependency Injection Container
                var services = new ServiceCollection();

                _dispatcher = new GameThreadDispatcher();
                var eventBus = new EventBus();
                services.AddSingleton<IEngineDispatcher>(_dispatcher);
                services.AddSingleton<IEventBus>(eventBus);
                services.AddSingleton<INetworkServer, NetworkServer>();
                services.AddSingleton<INetworkClient, NetworkClient>();

                var serviceProvider = services.BuildServiceProvider();

                _server = serviceProvider.GetRequiredService<INetworkServer>();
                _client = serviceProvider.GetRequiredService<INetworkClient>();

                if (_isServerProcess)
                {
                    ServerGameRulesService.Instance.LoadAndApplyOnServer();
                    var rtCfg = Common.Config.ServerRuntimeSettings.Current;
                    if (!string.IsNullOrWhiteSpace(rtCfg.ServerName)) _server.ServerName = rtCfg.ServerName;
                    if (!string.IsNullOrWhiteSpace(rtCfg.ServerPassword)) _server.RequiredServerPassword = rtCfg.ServerPassword;
                    if (!string.IsNullOrWhiteSpace(rtCfg.ServerVisibility)) _server.ServerVisibility = rtCfg.ServerVisibility;
                    if (rtCfg.MaxPlayers > 0) _server.MaxPlayers = rtCfg.MaxPlayers;
                    _server.ApplyNetworkSettings(rtCfg.ServerTickRateHz, rtCfg.DisconnectTimeoutSec, rtCfg.NetworkPollIntervalMs);
                }

                if (!string.IsNullOrWhiteSpace(parsedServerName))
                {
                    _server.ServerName = parsedServerName;
                }
                if (parsedCoopPassword != null)
                {
                    _server.RequiredServerPassword = parsedCoopPassword;
                }
                if (!string.IsNullOrWhiteSpace(parsedVisibility))
                {
                    _server.ServerVisibility = parsedVisibility!;
                }
                if (!string.IsNullOrWhiteSpace(parsedMaxPlayers) && int.TryParse(parsedMaxPlayers, out int maxP) && maxP > 0)
                {
                    _server.MaxPlayers = maxP;
                }

                string chosenSaveTitle = !string.IsNullOrWhiteSpace(parsedNewGameSave)
                    ? parsedNewGameSave!
                    : (!string.IsNullOrWhiteSpace(parsedServerName) ? parsedServerName! : _targetSaveName);
                if (!string.Equals(_targetSaveName, "NEW_GAME", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_targetSaveName, "Yeni Oyun", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_targetSaveName, "Yeni_Oyun", StringComparison.OrdinalIgnoreCase))
                {
                    chosenSaveTitle = _targetSaveName;
                }
                ModContext.ActiveServerSaveName = ModContext.SanitizeSaveFileName(chosenSaveTitle);

                ModContext.Initialize(serviceProvider, _dispatcher, eventBus);
                PlayerHeroManager.Instance.EnsureEventSubscriptions();
                Patches.MoveOrderDebouncer.EnsureSubscribed();
                SettlementSyncService.Instance.EnsureSubscribed();
                RosterSyncService.Instance.EnsureSubscribed();
                SiegeSyncService.Instance.EnsureSubscribed();
                BattleSyncService.Instance.EnsureSubscribed();

                ModContext.Events.Subscribe<HeroExecutionReceivedEvent>(OnHeroExecutionReceived);
                ModContext.Events.Subscribe<CoopBattleSessionUpdatedEvent>(evt =>
                {
                    if (evt == null) return;
                    if (evt.IsActive)
                    {
                        CoopMissionSyncBehavior.ConfigurePendingCoopSession(
                            evt.SessionId,
                            evt.IsHost,
                            evt.HostPeerId,
                            evt.AlliedPeerIds,
                            evt.FriendlyFireEnabled);
                    }
                    else
                    {
                        CoopMissionSyncBehavior.ClearPendingCoopSession();
                    }
                });

                _worldSync.OnMissingRemotePlayerDetected += () =>
                {
                    if (!_isServerProcess &&
                        ModContext.IsClientConnected &&
                        PlayerHeroManager.Instance.IsCharacterInitialized &&
                        TaleWorldsCampaign.Current?.GameStarted == true)
                    {
                        ModContext.Client?.Send(new Common.Packets.PlayerHeroCreatedPacket
                        {
                            PeerId = ModContext.Client?.LocalPeerId ?? 1001,
                            HeroName = "__SYNC_REQUEST__"
                        }, DeliveryMethod.ReliableOrdered);
                    }
                };

                // Register network packet handlers for world sync
                _client.Router.Subscribe<Common.Packets.WorldStateSnapshotPacket>((packet, peer) =>
                {
                    _worldSync.ApplyWorldSnapshot(packet);
                });

                _client.Router.Subscribe<Common.Packets.CampaignTimeSpeedPacket>((packet, peer) =>
                {
                    CampaignTimeSyncBehavior.Instance?.HandleServerTimeSpeedPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.PlayerHeroCreatedPacket>((packet, peer) =>
                {
                    PlayerHeroManager.Instance.HandleServerHeroCreatedPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.SettlementInteractionPacket>((packet, peer) =>
                {
                    SettlementSyncService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.PartyRosterSyncPacket>((packet, peer) =>
                {
                    RosterSyncService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.SiegeStateSyncPacket>((packet, peer) =>
                {
                    SiegeSyncService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.ServerAdminRewardPacket>((packet, peer) =>
                {
                    RosterSyncService.Instance.HandleAdminRewardPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.PlayerInteractionPacket>((packet, peer) =>
                {
                    PlayerInteractionService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.BattleSessionSyncPacket>((packet, peer) =>
                {
                    BattleSyncService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.P2PDialogPacket>((packet, peer) =>
                {
                    PlayerInteractionService.Instance.HandleDialogPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.HeroExecutionPacket>((packet, peer) =>
                {
                    BattleSyncService.Instance.HandleHeroExecutionPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.PartyFleetSyncPacket>((packet, peer) =>
                {
                    NavalSyncService.Instance.HandleFleetPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.MissionAgentStatePacket>((packet, peer) =>
                {
                    CoopMissionSyncBehavior.HandleRemoteStatePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.MissionBlowSyncPacket>((packet, peer) =>
                {
                    CoopMissionSyncBehavior.HandleRemoteBlowPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.MissionNavalShipStatePacket>((packet, peer) =>
                {
                    CoopMissionSyncBehavior.HandleRemoteNavalShipStatePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.MissionTroopsSyncPacket>((packet, peer) =>
                {
                    CoopMissionSyncBehavior.HandleRemoteTroopsSyncPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.WorldSaveChunkPacket>((packet, peer) =>
                {
                    HandleWorldSaveChunk(packet);
                });

                _client.Router.Subscribe<Common.Packets.ServerConfigSyncPacket>((packet, peer) =>
                {
                    ServerGameRulesService.Instance.HandleServerConfigSyncPacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.KingdomDiplomacySyncPacket>((packet, peer) =>
                {
                    KingdomDiplomacySyncService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.PlayerArmySyncPacket>((packet, peer) =>
                {
                    PlayerArmyService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.CoopBattleSyncPacket>((packet, peer) =>
                {
                    if (packet.ActionType == 6 || packet.ActionType == 7)
                    {
                        CoopMissionSyncBehavior.HandleCoopBattleSyncPacket(packet);
                    }
                    else
                    {
                        CoopBattleService.Instance.HandlePacket(packet);
                    }
                });

                _client.Router.Subscribe<Common.Packets.CoopActivitySyncPacket>((packet, peer) =>
                {
                    CoopActivityService.Instance.HandlePacket(packet);
                });

                _client.Router.Subscribe<Common.Packets.ChatMessagePacket>((packet, peer) =>
                {
                    _dispatcher?.Enqueue(() =>
                    {
                        if (packet == null) return;
                        int myPeerId = _client?.LocalPeerId ?? -1;
                        if (packet.SenderPeerId > 0 && packet.SenderPeerId == myPeerId) return;

                        string prefix = packet.Channel == 1
                            ? Loc.T("Savaş", "Battle")
                            : (packet.Channel == 2 ? Loc.T("Özel", "Whisper") : Loc.T("Sohbet", "Chat"));
                        Color col = packet.Channel == 1 ? Colors.Magenta : Colors.Yellow;
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[{prefix} - {packet.SenderName}]: {packet.Message}", col));
                    });
                });

                _client.OnPlayerLeft += (packet) =>
                {
                    PlayerHeroManager.Instance.HandlePeerDisconnected(packet.PeerId, _worldSync);
                    BattleSyncService.Instance.HandlePeerDisconnected(packet.PeerId);
                    PlayerInteractionService.Instance.HandlePeerDisconnected(packet.PeerId);
                    PlayerArmyService.Instance.HandlePeerDisconnected(packet.PeerId);
                    CoopBattleService.Instance.HandlePeerDisconnected(packet.PeerId);
                    CoopActivityService.Instance.HandlePeerDisconnected(packet.PeerId);
                };

                // Client: Apply remote player movement orders so remote player parties gallop smoothly at 60 FPS
                _client.Router.Subscribe<Common.Packets.PlayerMoveOrderPacket>((packet, peer) =>
                {
                    _dispatcher?.Enqueue(() =>
                    {
                        if (TaleWorldsCampaign.Current == null ||
                            !TaleWorldsCampaign.Current.GameStarted ||
                            TaleWorldsCampaign.Current.SaveHandler?.IsSaving == true)
                        {
                            return;
                        }
                        if (packet.PeerId == (_client?.LocalPeerId ?? -1)) return;

                        string peerPartyId = NetworkObjectRegistry.GetPlayerPartyId(packet.PeerId);
                        if (WorldSyncManager.IsRecentlyDisconnectedParty(peerPartyId)) return;

                        var party = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.PeerId);
                        if (party == null || !party.IsActive || party.IsMainParty) return;

                        ApplyRemotePlayerMoveOrder(party, packet, _worldSync);
                    });
                });

                _server.OnPlayerLeft += (session, reason) =>
                {
                    if (session != null)
                    {
                        PlayerHeroManager.Instance.HandlePeerDisconnected(session.PeerId, _worldSync);
                        BattleSyncService.Instance.HandlePeerDisconnected(session.PeerId);
                        PlayerInteractionService.Instance.HandlePeerDisconnected(session.PeerId);
                        PlayerArmyService.Instance.HandlePeerDisconnected(session.PeerId);
                        CoopBattleService.Instance.HandlePeerDisconnected(session.PeerId);
                        CoopActivityService.Instance.HandlePeerDisconnected(session.PeerId);
                        RosterSyncService.Instance.ClearCachedRoster(session.PeerId);
                    }
                };

                _server.Router.Subscribe<Common.Packets.PlayerHeroCreatedPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.PeerId = _server.ResolveSessionPeerId(peer, packet.PeerId);
                    }
                    PlayerHeroManager.Instance.HandleServerHeroCreatedPacket(packet);
                    CampaignTimeSyncBehavior.Instance?.BroadcastServerTime();
                    KingdomDiplomacySyncService.Instance.SendFullStateToPeer(packet.PeerId);
                });

                _server.Router.Subscribe<Common.Packets.KingdomDiplomacySyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.ActorPeerId = _server.ResolveSessionPeerId(peer, packet.ActorPeerId);
                    }
                    KingdomDiplomacySyncService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.PlayerArmySyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        int verifiedPeer = _server.ResolveSessionPeerId(peer, packet.LeaderPeerId);
                        if (packet.ActionType == 1 || packet.ActionType == 5 || packet.ActionType == 7)
                        {
                            packet.LeaderPeerId = verifiedPeer;
                        }
                        else if (packet.ActionType == 2 || packet.ActionType == 3 || packet.ActionType == 4)
                        {
                            packet.TargetPeerId = verifiedPeer;
                        }
                    }
                    PlayerArmyService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.CoopBattleSyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        int verifiedPeer = _server.ResolveSessionPeerId(peer, packet.ActorPeerId);
                        packet.ActorPeerId = verifiedPeer;
                        if (packet.ActionType == 1)
                        {
                            packet.HostPeerId = verifiedPeer;
                        }
                    }

                    if (packet.ActionType == 6 || packet.ActionType == 7)
                    {
                        _server?.BroadcastExcept(packet, peer,
                            packet.ActionType == 6 ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        CoopBattleService.Instance.HandlePacket(packet);
                    }
                });

                _server.Router.Subscribe<Common.Packets.CoopActivitySyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        int verifiedPeer = _server.ResolveSessionPeerId(peer, packet.ActorPeerId);
                        packet.ActorPeerId = verifiedPeer;
                        if (packet.ActionType == 1 || packet.ActionType == 10)
                        {
                            packet.HostPeerId = verifiedPeer;
                        }
                    }
                    CoopActivityService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.SettlementInteractionPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.PeerId = _server.ResolveSessionPeerId(peer, packet.PeerId);
                    }
                    SettlementSyncService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.PartyRosterSyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.PeerId = _server.ResolveSessionPeerId(peer, packet.PeerId);
                    }
                    RosterSyncService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.PartyFleetSyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.PeerId = _server.ResolveSessionPeerId(peer, packet.PeerId);
                    }
                    NavalSyncService.Instance.HandleFleetPacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.SiegeStateSyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.PeerId = _server.ResolveSessionPeerId(peer, packet.PeerId);
                    }
                    SiegeSyncService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.PlayerInteractionPacket>((packet, peer) =>
                {
                    if (_server != null && packet.InteractionType != 4)
                    {
                        packet.SenderPeerId = _server.ResolveSessionPeerId(peer, packet.SenderPeerId);
                    }
                    PlayerInteractionService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.BattleSessionSyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        int resolvedPeerId = _server.ResolveSessionPeerId(peer, packet.AttackerPeerId);
                        bool isPvpResult = (packet.BattleStage == 2 || packet.BattleStage == 3) &&
                                           packet.AttackerPeerId > 0 &&
                                           packet.DefenderPeerId > 0;
                        if (isPvpResult)
                        {
                            // Preserve BOTH AttackerPeerId and DefenderPeerId whether Attacker OR Defender pressed TAB first!
                            if (resolvedPeerId != packet.AttackerPeerId && resolvedPeerId != packet.DefenderPeerId)
                            {
                                return;
                            }
                        }
                        else if (packet.BattleStage == 12 || packet.BattleStage == 16 || packet.BattleStage == 17)
                        {
                            // Preserve AttackerPeerId vs DefenderPeerId; carry verified sender peer ID in DefenderTroopsLost.
                            packet.DefenderTroopsLost = resolvedPeerId;
                        }
                        else
                        {
                            packet.AttackerPeerId = resolvedPeerId;
                        }
                    }
                    BattleSyncService.Instance.HandlePacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.P2PDialogPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.SenderPeerId = _server.ResolveSessionPeerId(peer, packet.SenderPeerId);
                    }
                    PlayerInteractionService.Instance.HandleDialogPacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.HeroExecutionPacket>((packet, peer) =>
                {
                    if (_server != null && packet.ReasonStage == 3)
                    {
                        packet.VictimPeerId = _server.ResolveSessionPeerId(peer, packet.VictimPeerId);
                    }
                    BattleSyncService.Instance.HandleHeroExecutionPacket(packet);
                });

                _server.Router.Subscribe<Common.Packets.WorldSaveRequestPacket>((packet, peer) =>
                {
                    HandleServerWorldSaveRequest(packet, peer);
                });

                _server.Router.Subscribe<Common.Packets.MissionAgentStatePacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.PeerId = _server.ResolveSessionPeerId(peer, packet.PeerId);
                    }
                    _server?.BroadcastExcept(packet, peer, DeliveryMethod.Unreliable);
                });

                _server.Router.Subscribe<Common.Packets.MissionBlowSyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.AttackerPeerId = _server.ResolveSessionPeerId(peer, packet.AttackerPeerId);
                    }
                    _server?.BroadcastExcept(packet, peer, DeliveryMethod.ReliableOrdered);
                });

                _server.Router.Subscribe<Common.Packets.MissionNavalShipStatePacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.PeerId = _server.ResolveSessionPeerId(peer, packet.PeerId);
                    }
                    _server?.BroadcastExcept(packet, peer, DeliveryMethod.Unreliable);
                });

                _server.Router.Subscribe<Common.Packets.MissionTroopsSyncPacket>((packet, peer) =>
                {
                    if (_server != null)
                    {
                        packet.SenderPeerId = _server.ResolveSessionPeerId(peer, packet.SenderPeerId);
                    }
                    _server?.BroadcastExcept(packet, peer, DeliveryMethod.Unreliable);
                });

                _server.Router.Subscribe<Common.Packets.ChatMessagePacket>((packet, peer) =>
                {
                    int senderPeerId = _server != null
                        ? _server.ResolveSessionPeerId(peer, packet.SenderPeerId)
                        : packet.SenderPeerId;
                    packet.SenderPeerId = senderPeerId;

                    // Intercept in-game Admin slash commands (e.g. /gold 50000, /heal, /save, /kick, /help)
                    if (!string.IsNullOrWhiteSpace(packet.Message) && packet.Message.TrimStart().StartsWith("/"))
                    {
                        string sessionUser = (_server != null && _server.Sessions.TryGetValue(senderPeerId, out var sess))
                            ? sess.PlayerName
                            : packet.SenderName;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            string reply = _ipcHost.ExecuteAdminChatCommand(senderPeerId, sessionUser, packet.Message.Trim());
                            if (!string.IsNullOrEmpty(reply))
                            {
                                _server?.SendTo(senderPeerId, new Common.Packets.ChatMessagePacket
                                {
                                    SenderPeerId = 0,
                                    SenderName = "Yönetim",
                                    Message = reply,
                                    IsSystem = true,
                                    Channel = 2
                                }, DeliveryMethod.ReliableOrdered);
                            }
                        });
                        return;
                    }

                    _server?.BroadcastExcept(packet, peer, DeliveryMethod.ReliableOrdered);
                    _dispatcher?.Enqueue(() =>
                    {
                        if (packet == null) return;
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[{Loc.T("Sohbet", "Chat")} - {packet.SenderName}]: {packet.Message}", Colors.Yellow));
                    });
                });

                // Server: Authoritative Player Movement Orders & Live Position Sync
                _server.Router.Subscribe<Common.Packets.PlayerMoveOrderPacket>((packet, peer) =>
                {
                    _dispatcher?.Enqueue(() =>
                    {
                        if (TaleWorldsCampaign.Current == null ||
                            !TaleWorldsCampaign.Current.GameStarted ||
                            TaleWorldsCampaign.Current.SaveHandler?.IsSaving == true)
                        {
                            return;
                        }

                        // Secure: Resolve authenticated account session ID from LiteNetLib peer
                        int verifiedPeerId = _server != null
                            ? _server.ResolveSessionPeerId(peer, packet.PeerId)
                            : packet.PeerId;

                        string peerPartyId = NetworkObjectRegistry.GetPlayerPartyId(verifiedPeerId);
                        if (WorldSyncManager.IsRecentlyDisconnectedParty(peerPartyId)) return;

                        var party = NetworkObjectRegistry.Instance.ResolvePlayerParty(verifiedPeerId);
                        if (party != null && party.IsActive)
                        {
                            ApplyRemotePlayerMoveOrder(party, packet, _worldSync);

                            // Broadcast move order to all clients so remote player parties path smoothly at 60 FPS
                            packet.PeerId = verifiedPeerId;
                            try
                            {
                                _server?.Broadcast(packet, packet.MoveType == 3
                                    ? DeliveryMethod.Unreliable
                                    : DeliveryMethod.ReliableOrdered);
                            }
                            catch { }
                        }
                    });
                });

                // Defer Harmony patching until OnBeforeInitialModuleScreenSetAsRoot so engine action sets
                // and ActionIndexCache finish loading before any Agent/Mission type is touched.
                if (!_isServerProcess)
                {
                    RegisterMainMenuOption();
                }

                InformationManager.DisplayMessage(new InformationMessage(
                    _isServerProcess
                        ? Loc.T("[Calradia-Connected] Sunucu modu devrede.", "[Calradia-Connected] Server mode active.")
                        : Loc.T("[Calradia-Connected] İstemci modu devrede.", "[Calradia-Connected] Client mode active."),
                    Colors.Green));
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Failed to load module: {ex}");
            }
        }

        private static void ApplyRemotePlayerMoveOrder(MobileParty party, Common.Packets.PlayerMoveOrderPacket packet, WorldSyncManager? worldSync)
        {
            if (party == null) return;

            // Never move or show a captured, dead, or empty remote player party (prevents ghost banner trailing the captor!)
            bool isCapturedOrDead = !party.IsActive ||
                                    party.LeaderHero == null ||
                                    !party.LeaderHero.IsAlive ||
                                    party.LeaderHero.IsPrisoner ||
                                    (party.MemberRoster != null && party.MemberRoster.TotalManCount <= 0);
            if (isCapturedOrDead)
            {
                try
                {
                    party.IsVisible = false;
                    party.IsInspected = false;
                    party.SetMoveModeHold();
                    party.Party?.SetVisualAsDirty();
                }
                catch { }
                return;
            }

            if (party.CurrentSettlement != null)
            {
                // Passive heartbeat must NOT pull an idling player out of a settlement; only explicit leave/move orders do
                if (packet.MoveType == 3)
                {
                    if (party.IsVisible)
                    {
                        party.IsVisible = false;
                        party.Party?.SetVisualAsDirty();
                    }
                    return;
                }
                try { LeaveSettlementAction.ApplyForParty(party); } catch { }
            }

            bool isOnLand = !packet.IsAtSea;
            if (party.IsCurrentlyAtSea != packet.IsAtSea)
            {
                try
                {
                    party.IsCurrentlyAtSea = packet.IsAtSea;
                    party.SetNavalVisualAsDirty();
                }
                catch { }
            }

            try { party.SetLandNavigationAccess(true); } catch { }
            party.ForceAiNoPathMode = false;
            party.IsVisible = true;
            party.IsInspected = true;
            party.Party?.SetVisualAsDirty();
            party.Ai?.SetDoNotMakeNewDecisions(false);
            party.Ai?.EnableAi();

            var navType = NavalSyncService.SanitizeNavigationType(party, packet.NavigationType, packet.IsAtSea);

            // Sync exact cargo/overburdened campaign map speed so remote parties move at the exact same speed
            if (!float.IsNaN(packet.Speed) && !float.IsInfinity(packet.Speed) && packet.Speed > 0.05f)
            {
                NetworkObjectRegistry.Instance.SetSyncedPartySpeed(party.StringId, packet.Speed);
            }

            bool hasClientPos = !float.IsNaN(packet.CurrentX) && !float.IsInfinity(packet.CurrentX) &&
                                !float.IsNaN(packet.CurrentY) && !float.IsInfinity(packet.CurrentY) &&
                                (Math.Abs(packet.CurrentX) > 0.01f || Math.Abs(packet.CurrentY) > 0.01f);
            Vec2 clientPos = hasClientPos ? new Vec2(packet.CurrentX, packet.CurrentY) : party.GetPosition2D;

            // Only snap position if drift exceeds 2.5 units (never teleport during normal 60 FPS navmesh movement!)
            if (hasClientPos && (party.GetPosition2D - clientPos).Length > 2.5f)
            {
                party.Position = new CampaignVec2(clientPos, isOnLand: isOnLand);
            }

            if (packet.MoveType == 1 && !string.IsNullOrEmpty(packet.TargetSettlementId))
            {
                var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.TargetSettlementId);
                if (settlement != null)
                {
                    CampaignVec2 destPos = (packet.IsTargetingPort && settlement.HasPort && settlement.PortPosition.IsValid())
                        ? settlement.PortPosition
                        : (settlement.GatePosition.IsValid()
                            ? settlement.GatePosition
                            : new CampaignVec2(settlement.GetPosition2D, isOnLand: isOnLand));
                    party.SetMoveGoToPoint(destPos, navType);
                }
                return;
            }

            if (packet.MoveType == 2 && !string.IsNullOrEmpty(packet.TargetPartyId))
            {
                var targetParty = NetworkObjectRegistry.Instance.ResolveParty(packet.TargetPartyId);
                if (targetParty != null)
                {
                    party.SetMoveEngageParty(targetParty, navType);
                }
                return;
            }

            bool hasTarget = !float.IsNaN(packet.TargetX) && !float.IsInfinity(packet.TargetX) &&
                             !float.IsNaN(packet.TargetY) && !float.IsInfinity(packet.TargetY) &&
                             (Math.Abs(packet.TargetX) > 0.01f || Math.Abs(packet.TargetY) > 0.01f);
            if (!hasTarget) return;

            var targetVec = new Vec2(packet.TargetX, packet.TargetY);
            worldSync?.UpdateRemotePlayerMotionHint(
                party.StringId,
                clientPos,
                targetVec,
                packet.Speed > 0.05f ? packet.Speed : 5f,
                isOnLand);

            if (packet.MoveType == 0)
            {
                // Explicit new map click: issue GoToPoint immediately
                if ((targetVec - party.GetPosition2D).LengthSquared > 0.01f)
                {
                    party.SetMoveGoToPoint(new CampaignVec2(targetVec, isOnLand: isOnLand), navType);
                }
            }
            else if (packet.MoveType == 3)
            {
                // Periodic live position heartbeat: never reset path if already walking toward this target!
                if (party.DefaultBehavior == AiBehavior.EngageParty) return;

                if ((targetVec - clientPos).LengthSquared > 0.02f)
                {
                    var currentTarget = party.MoveTargetPoint.ToVec2();
                    if (!party.IsMoving || (currentTarget - targetVec).LengthSquared > 0.25f)
                    {
                        party.SetMoveGoToPoint(new CampaignVec2(targetVec, isOnLand: isOnLand), navType);
                    }
                }
                else if (hasClientPos && (party.GetPosition2D - clientPos).Length > 0.30f)
                {
                    var currentTarget = party.MoveTargetPoint.ToVec2();
                    if (!party.IsMoving || (currentTarget - clientPos).LengthSquared > 0.09f)
                    {
                        party.SetMoveGoToPoint(new CampaignVec2(clientPos, isOnLand: isOnLand), navType);
                    }
                }
                else if (party.IsMoving && (targetVec - party.GetPosition2D).LengthSquared <= 0.02f)
                {
                    party.SetMoveModeHold();
                }
            }
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();

            SyncLanguageFromBannerlord();
            EnsureHarmonyPatchesInstalled();

            if (_isServerProcess)
            {
                StartServerHost();
            }
            else
            {
                RegisterMainMenuOption();
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Calradia-Connected] Çok oyunculu sefer için hazır.", "[Calradia-Connected] Ready for multiplayer campaign."),
                    Colors.Cyan));
            }
        }

        private void EnsureHarmonyPatchesInstalled()
        {
            if (_patchesInstalled) return;
            _patchesInstalled = true;
            try
            {
                _harmony = new Harmony("com.calradiaconnected.mod");
                Patches.PatchLifecycleManager.InstallAll(_harmony);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Harmony patch install error: {ex}");
            }
        }

        private void StartServerHost()
        {
            if (_serverAutoStarted) return;
            _serverAutoStarted = true;

            try
            {
                if (_server != null && !_server.IsRunning)
                {
                    bool started = _server.Start(_serverPort, "CalradiaConnected");
                    if (started)
                    {
                        InformationManager.DisplayMessage(new InformationMessage($"[Calradia-Connected] Sunucu {_serverPort} portunda başlatıldı!", Colors.Green));
                        _embeddedSaveServer.Start(_serverPort + 1);
                        if (_dispatcher != null && _server != null)
                        {
                            _ipcHost.Start(4205, _dispatcher, _server);
                        }
                    }
                    else
                    {
                        InformationManager.DisplayMessage(new InformationMessage($"[Calradia-Connected] Sunucu {_serverPort} portunda başlatılamadı!", Colors.Red));
                    }
                }

                // Auto-load designated campaign save or start new game
                AutoHostCampaign(_targetSaveName);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Sunucu başlatma hatası: {ex}");
            }
        }

        private void AutoHostCampaign(string saveName)
        {
            _dispatcher?.Enqueue(() =>
            {
                try
                {
                    bool isNewGame = string.IsNullOrWhiteSpace(saveName) ||
                                     string.Equals(saveName, "NEW_GAME", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(saveName, "Yeni Oyun", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(saveName, "Yeni_Oyun", StringComparison.OrdinalIgnoreCase);

                    if (isNewGame)
                    {
                        InformationManager.DisplayMessage(new InformationMessage("[Calradia-Connected] Yepyeni Sandbox sefer dünyası oluşturuluyor...", Colors.Cyan));
                        MBGameManager.StartNewGame(new ServerSandBoxGameManager());
                        return;
                    }

                    SaveGameFileInfo? targetSave = null;
                    var saves = MBSaveLoad.GetSaveFiles(null);
                    if (saves != null && saves.Length > 0)
                    {
                        targetSave = saves.FirstOrDefault(s =>
                            string.Equals(s.Name, saveName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileNameWithoutExtension(s.Name), saveName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s.Name, saveName + ".sav", StringComparison.OrdinalIgnoreCase));
                    }

                    if (targetSave != null)
                    {
                        InformationManager.DisplayMessage(new InformationMessage($"[Calradia-Connected] Sunucu dünyası '{targetSave.Name}' yükleniyor...", Colors.Green));
                        SandBoxSaveHelper.TryLoadSave(targetSave, (loadResult) =>
                        {
                            MBGameManager.StartNewGame(new SandBoxGameManager(loadResult));
                        }, () =>
                        {
                            InformationManager.DisplayMessage(new InformationMessage("[Calradia-Connected] Sefer yüklemesi iptal edildi.", Colors.Red));
                        });
                    }
                    else
                    {
                        InformationManager.DisplayMessage(new InformationMessage("[Calradia-Connected] Belirtilen kayıt bulunamadı, yeni Sandbox dünyası açılıyor...", Colors.Yellow));
                        MBGameManager.StartNewGame(new ServerSandBoxGameManager());
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Sefer başlatma hatası: {ex.Message}");
                    MBGameManager.StartNewGame(new ServerSandBoxGameManager());
                }
            });
        }

        private void RegisterMainMenuOption()
        {
            SyncLanguageFromBannerlord();
            if (_menuOptionRegistered) return;

            try
            {
                var joinOption = new InitialStateOption(
                    "CalradiaConnectedJoin",
                    new TextObject("{=cc_join_calradia}" + Loc.T("Kalradya'ya Katıl", "Join Calradia")),
                    9990,
                    OnJoinCalradiaClicked,
                    () => (false, new TextObject(""))
                );

                Module.CurrentModule.AddInitialStateOption(joinOption);
                _menuOptionRegistered = true;
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Menü seçeneği eklenirken hata: {ex.Message}");
            }
        }

        private void OnJoinCalradiaClicked()
        {
            UI.ClientLoginManager.Instance.PromptLogin((host, port, username, rawPassword, serverPassword) =>
            {
                ConnectToServer(host, port, username, rawPassword, serverPassword);
            });
        }

        private void ConnectToServer(string host, int port, string username, string rawPassword, string serverPassword)
        {
            if (string.IsNullOrWhiteSpace(host)) return;

            var client = ModContext.Client;
            if (client == null)
            {
                UI.ClientLoginManager.Instance.SetStatus(Loc.T("✗ HATA: İstemci ağ altyapısı bulunamadı.", "✗ ERROR: Client network service missing."));
                InformationManager.ShowInquiry(new InquiryData(
                    Loc.T("Hata", "Error"),
                    Loc.T("İstemci ağ altyapısı bulunamadı.", "Client network service not found."),
                    true, false, Loc.T("Tamam", "OK"), null, null, null));
                return;
            }

            if (client.IsConnected)
            {
                client.Disconnect();
            }

            PlayerHeroManager.Instance.ResetForNewSession();
            _executionDialogShown = false;

            UI.ClientLoginManager.Instance.SetStatus(Loc.F("⟳ {0}:{1} sunucusuna bağlanılıyor, şifre doğrulanıyor...", "⟳ Connecting to {0}:{1}, verifying credentials...", host, port));
            InformationManager.DisplayMessage(new InformationMessage(Loc.F("[Calradia-Connected] {0}:{1} sunucusuna bağlanılıyor...", "[Calradia-Connected] Connecting to {0}:{1}...", host, port), Colors.Cyan));

            bool handled = false;
            Action<Common.Packets.HandshakeResponsePacket>? onConnectedHandler = null;
            Action<string>? onDisconnectedHandler = null;

            onConnectedHandler = (resp) =>
            {
                if (handled) return;
                handled = true;
                client.OnConnected -= onConnectedHandler;
                client.OnDisconnected -= onDisconnectedHandler;

                _dispatcher?.Enqueue(() =>
                {
                    if (!resp.Success)
                    {
                        UI.ClientLoginManager.Instance.SetStatus(Loc.F("✗ GİRİŞ REDDEDİLDİ: {0}", "✗ LOGIN REJECTED: {0}", resp.Reason));
                        InformationManager.ShowInquiry(new InquiryData(
                            Loc.T("Şifre / Giriş Doğrulama Hatası", "Authentication Error"),
                            Loc.F("Sunucu bağlantısı reddedildi:\n\n{0}", "Server connection rejected:\n\n{0}", resp.Reason),
                            true, false, Loc.T("Tamam", "OK"), null, null, null));
                        return;
                    }

                    UI.ClientLoginManager.Instance.SetStatus(Loc.T("✓ Şifre doğrulandı! Giriş başarılı, dünya indiriliyor...", "✓ Verified! Login succeeded, downloading world..."));
                    UI.ClientLoginManager.Instance.CloseOverlay();

                    string welcome = resp.IsNewAccount
                        ? Loc.F("[Calradia-Connected] Yeni hesap oluşturuldu! Hoş geldiniz {0} (Oyuncu ID: {1}).", "[Calradia-Connected] Account created! Welcome {0} (Player ID: {1}).", username, resp.AssignedPeerId)
                        : Loc.F("[Calradia-Connected] Giriş başarılı! Tekrar hoş geldiniz {0} (Oyuncu ID: {1}).", "[Calradia-Connected] Signed in! Welcome back {0} (Player ID: {1}).", username, resp.AssignedPeerId);
                    InformationManager.DisplayMessage(new InformationMessage(welcome, Colors.Green));

                    int fastDlPort = resp.FastDlPort > 0 ? resp.FastDlPort : port + 1;
                    LoadDownloadedSaveOrStartGame(host, fastDlPort, resp.IsNewAccount);
                });
            };

            onDisconnectedHandler = (reason) =>
            {
                if (handled) return;
                handled = true;
                client.OnConnected -= onConnectedHandler;
                client.OnDisconnected -= onDisconnectedHandler;

                _dispatcher?.Enqueue(() =>
                {
                    UI.ClientLoginManager.Instance.SetStatus(Loc.F("✗ BAĞLANTI HATASI: {0}", "✗ CONNECTION ERROR: {0}", reason));
                    InformationManager.ShowInquiry(new InquiryData(
                        Loc.T("Bağlantı Kurulamadı", "Connection Failed"),
                        Loc.F("Sunucuya bağlanılamadı ({0}:{1}):\n{2}\n\nLütfen sunucunun açık olduğundan, IP/port adresinin ve şifrenizin doğru girildiğinden emin olun.",
                              "Could not connect to server ({0}:{1}):\n{2}\n\nPlease make sure the server is running and IP/port/password are correct.",
                              host, port, reason),
                        true, false, Loc.T("Tamam", "OK"), null, null, null));
                });
            };

            client.OnConnected += onConnectedHandler;
            client.OnDisconnected += onDisconnectedHandler;

            client.Connect(host, port, "CalradiaConnected", username, rawPassword, "", serverPassword);
        }

        private static string GetPrimarySavesFolder()
        {
            var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var candidates = new[]
            {
                Path.Combine(myDocs, "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "OneDrive", "Belgeler", "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(userProfile, "OneDrive", "Documents", "Mount and Blade II Bannerlord", "Game Saves")
            };

            foreach (var dir in candidates)
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return dir;
            }

            return candidates[0];
        }

        private void EnqueueUiMessage(string text, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_dispatcher != null)
            {
                _dispatcher.Enqueue(() => InformationManager.DisplayMessage(new InformationMessage(text, color)));
            }
            else
            {
                TaleWorlds.Library.Debug.Print(text);
            }
        }

        private static void CleanupStaleLocalClientWorldSaves(string saveFolder, string currentSaveBaseName)
        {
            try
            {
                if (!Directory.Exists(saveFolder)) return;
                foreach (var file in Directory.GetFiles(saveFolder, "CalradiaConnected_World_*"))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.StartsWith(currentSaveBaseName, StringComparison.OrdinalIgnoreCase)) continue;

                    // Extract PID suffix and only delete saves left behind by exited client processes
                    string baseNoExt = Path.GetFileNameWithoutExtension(file);
                    if (baseNoExt.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
                    {
                        baseNoExt = Path.GetFileNameWithoutExtension(baseNoExt);
                    }
                    const string prefix = "CalradiaConnected_World_";
                    if (baseNoExt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(baseNoExt.Substring(prefix.Length), out int oldPid) &&
                        oldPid > 0)
                    {
                        bool alive = false;
                        try
                        {
                            using var proc = System.Diagnostics.Process.GetProcessById(oldPid);
                            alive = !proc.HasExited;
                        }
                        catch { alive = false; }

                        if (!alive)
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void LoadDownloadedSaveOrStartGame(string host, int fastDlPort, bool isNewAccount = false)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                string saveFolder = GetPrimarySavesFolder();
                // Per-process save name prevents file-lock / overwrite collisions when running 2+ clients on 1 PC
                string saveBaseName = $"CalradiaConnected_World_{System.Diagnostics.Process.GetCurrentProcess().Id}";
                CleanupStaleLocalClientWorldSaves(saveFolder, saveBaseName);
                string localSavePath = Path.Combine(saveFolder, saveBaseName + ".sav");

                bool downloaded = false;
                string httpError = string.Empty;
                string udpError = string.Empty;

                try
                {
                    string saveUrl = $"http://{host}:{fastDlPort}/api/save";
                    if (!Directory.Exists(saveFolder))
                    {
                        Directory.CreateDirectory(saveFolder);
                    }

                    EnqueueUiMessage(
                        Loc.F("[Calradia-Connected] Sunucu dünya kaydı indiriliyor (http://{0}:{1}/api/save)...",
                              "[Calradia-Connected] Downloading server world save (http://{0}:{1}/api/save)...", host, fastDlPort),
                        Colors.Cyan);

                    downloaded = DownloadHttpSaveWithTimeout(saveUrl, localSavePath, out httpError);
                }
                catch (Exception ex)
                {
                    downloaded = false;
                    httpError = ex.Message;
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] FastDL indirme hatası: {ex.Message}");
                }

                // UDP game-port fallback: if HTTP 4201 is blocked on LAN/Radmin firewall, stream GZip save chunks over already-connected LiteNetLib peer
                if (!downloaded && _client != null && _client.IsConnected)
                {
                    EnqueueUiMessage(
                        Loc.T("[Calradia-Connected] FastDL erişilemedi, dünya oyun portu (UDP) üzerinden aktarılıyor...",
                              "[Calradia-Connected] FastDL unreachable, transferring world over UDP game port..."),
                        Colors.Yellow);
                    downloaded = TryDownloadSaveOverUdp(localSavePath, out udpError);
                }

                // Local host fallback: if connecting to local machine and download failed
                if (!downloaded && (host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "::1"))
                {
                    string activeSaveFile = string.IsNullOrWhiteSpace(ModContext.ActiveServerSaveName)
                        ? "Yeni_Oyun.sav"
                        : ModContext.ActiveServerSaveName + ".sav";
                    var localCandidate = Path.Combine(saveFolder, activeSaveFile);
                    if (!File.Exists(localCandidate))
                    {
                        localCandidate = Path.Combine(saveFolder, "Yeni_Oyun.sav");
                    }
                    if (File.Exists(localCandidate))
                    {
                        try
                        {
                            File.Copy(localCandidate, localSavePath, overwrite: true);
                            downloaded = File.Exists(localSavePath) && new FileInfo(localSavePath).Length > 100000;
                        }
                        catch { }
                    }
                }

                _dispatcher?.Enqueue(() =>
                {
                    try
                    {
                        if (downloaded)
                        {
                            SaveGameFileInfo? targetSave = null;
                            var saves = MBSaveLoad.GetSaveFiles(null);
                            if (saves != null && saves.Length > 0)
                            {
                                targetSave = saves.FirstOrDefault(s =>
                                    string.Equals(Path.GetFileNameWithoutExtension(s.Name), saveBaseName, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(s.Name, saveBaseName, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(s.Name, saveBaseName + ".sav", StringComparison.OrdinalIgnoreCase));
                            }

                            if (targetSave == null)
                            {
                                targetSave = MBSaveLoad.GetSaveFileWithName(saveBaseName);
                            }

                            if (targetSave != null)
                            {
                                InformationManager.DisplayMessage(new InformationMessage($"[Calradia-Connected] Sunucu dünyası ('{targetSave.Name}') yükleniyor, sefere giriliyor...", Colors.Green));
                                SandBoxSaveHelper.TryLoadSave(targetSave, (loadResult) =>
                                {
                                    MBGameManager.StartNewGame(new SandBoxGameManager(loadResult));
                                }, () =>
                                {
                                    InformationManager.DisplayMessage(new InformationMessage("[Calradia-Connected] Sefer yüklemesi iptal edildi.", Colors.Red));
                                });
                                return;
                            }
                            else
                            {
                                InformationManager.ShowInquiry(new InquiryData(
                                    Loc.T("Kayıt Algılanamadı", "Save Not Detected"),
                                    Loc.F("Sunucu dünya kaydı indirildi fakat Bannerlord kayıt motoru tarafından algılanamadı.\nDosya: {0}",
                                          "Server world save downloaded but was not recognized by Bannerlord save engine.\nFile: {0}",
                                          localSavePath),
                                    true, false, Loc.T("Tamam", "OK"), null, null, null));
                                return;
                            }
                        }

                        // Download failed: Show informative dialog including HTTP & UDP details
                        InformationManager.ShowInquiry(new InquiryData(
                            Loc.T("Sunucu Dünyası İndirilemedi", "World Download Failed"),
                            Loc.F("Sunucunun dünya kaydına ulaşılamadı (http://{0}:{1}/api/save).\n\nHTTP: {2}\nUDP Yedek: {3}\n\nLütfen sunucu bilgisayarında {1} TCP portunun güvenlik duvarından engellenmediğinden emin olun.",
                                  "Could not reach server world save (http://{0}:{1}/api/save).\n\nHTTP: {2}\nUDP Fallback: {3}\n\nEnsure TCP port {1} is not blocked by Windows Firewall on the host.",
                                  host, fastDlPort, string.IsNullOrEmpty(httpError) ? "-" : httpError, string.IsNullOrEmpty(udpError) ? "-" : udpError),
                            true, false, Loc.T("Tamam", "OK"), null, null, null));
                    }
                    catch (Exception ex)
                    {
                        TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Sefer başlatma hatası: {ex.Message}");
                        InformationManager.ShowInquiry(new InquiryData(
                            Loc.T("Yükleme Hatası", "Load Error"),
                            Loc.F("Sefer yüklenirken bir hata oluştu:\n{0}", "An error occurred while loading the campaign:\n{0}", ex.Message),
                            true, false, Loc.T("Tamam", "OK"), null, null, null));
                    }
                });
            });
        }

        private static bool DownloadHttpSaveWithTimeout(string saveUrl, string localSavePath, out string error)
        {
            error = string.Empty;
            string tempPath = localSavePath + ".tmp";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }

                    var req = (HttpWebRequest)WebRequest.Create(saveUrl);
                    req.Method = "GET";
                    req.Timeout = 7000;
                    req.ReadWriteTimeout = 90000;

                    using var resp = (HttpWebResponse)req.GetResponse();
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        using (var src = resp.GetResponseStream())
                        using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                        {
                            src.CopyTo(dst);
                            dst.Flush();
                        }

                        if (File.Exists(tempPath) && new FileInfo(tempPath).Length > 100000)
                        {
                            if (File.Exists(localSavePath)) File.Delete(localSavePath);
                            File.Move(tempPath, localSavePath);
                            return true;
                        }
                        try { File.Delete(tempPath); } catch { }
                        error = "Dosya boyutu geçersiz (<100KB).";
                        return false;
                    }
                }
                catch (WebException wex)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    if (wex.Response is HttpWebResponse errResp && (int)errResp.StatusCode == 503 && attempt == 0)
                    {
                        System.Threading.Thread.Sleep(1500);
                        continue;
                    }
                    error = wex.Message;
                    return false;
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    error = ex.Message;
                    return false;
                }
            }
            return false;
        }

        private void HandleServerWorldSaveRequest(Common.Packets.WorldSaveRequestPacket packet, NetPeer peer)
        {
            if (_server == null || peer == null) return;

            try
            {
                byte[]? archive = _embeddedSaveServer.GetOrBuildUdpSaveArchive(out string err);
                if (archive == null || archive.Length == 0)
                {
                    _server.Router.Send(peer, new Common.Packets.WorldSaveChunkPacket
                    {
                        ChunkIndex = 0,
                        TotalChunks = 0,
                        TotalCompressedBytes = 0,
                        IsSuccess = false,
                        ErrorMessage = string.IsNullOrEmpty(err) ? "Kayıt dosyası henüz hazır değil." : err,
                        Data = Array.Empty<byte>()
                    }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                int chunkSize = Services.EmbeddedSaveServer.UdpChunkSize;
                int totalChunks = (archive.Length + chunkSize - 1) / chunkSize;
                int start = Math.Max(0, packet.StartChunk);
                int count = Math.Max(1, Math.Min(16, packet.BatchCount));
                int end = Math.Min(totalChunks, start + count);

                for (int i = start; i < end; i++)
                {
                    int offset = i * chunkSize;
                    int len = Math.Min(chunkSize, archive.Length - offset);
                    var slice = new byte[len];
                    Buffer.BlockCopy(archive, offset, slice, 0, len);

                    _server.Router.Send(peer, new Common.Packets.WorldSaveChunkPacket
                    {
                        ChunkIndex = i,
                        TotalChunks = totalChunks,
                        TotalCompressedBytes = archive.Length,
                        IsSuccess = true,
                        ErrorMessage = string.Empty,
                        Data = slice
                    }, DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] UDP save send error: {ex.Message}");
            }
        }

        private void HandleWorldSaveChunk(Common.Packets.WorldSaveChunkPacket packet)
        {
            if (packet == null) return;

            if (!packet.IsSuccess)
            {
                _udpSaveError = packet.ErrorMessage;
                _udpSaveFailed = true;
                _udpChunkSignal.Set();
                return;
            }

            _udpSaveTotalChunks = packet.TotalChunks;
            _udpSaveTotalBytes = packet.TotalCompressedBytes;
            _udpSaveChunks[packet.ChunkIndex] = packet.Data ?? Array.Empty<byte>();
            _udpChunkSignal.Set();
        }

        private bool TryDownloadSaveOverUdp(string localSavePath, out string error)
        {
            error = string.Empty;
            if (_client == null || !_client.IsConnected)
            {
                error = "UDP istemci bağlantısı aktif değil.";
                return false;
            }

            _udpSaveChunks.Clear();
            _udpSaveTotalChunks = 0;
            _udpSaveTotalBytes = 0;
            _udpSaveFailed = false;
            _udpSaveError = string.Empty;

            const int batchSize = 8;
            int nextChunk = 0;
            var deadline = DateTime.UtcNow.AddSeconds(90);

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (_udpSaveFailed)
                    {
                        error = string.IsNullOrEmpty(_udpSaveError) ? "Sunucu UDP aktarımını reddetti." : _udpSaveError;
                        return false;
                    }

                    if (_udpSaveTotalChunks > 0 && _udpSaveChunks.Count >= _udpSaveTotalChunks)
                    {
                        break;
                    }

                    _udpChunkSignal.Reset();
                    _client.Send(new Common.Packets.WorldSaveRequestPacket
                    {
                        StartChunk = nextChunk,
                        BatchCount = batchSize
                    }, DeliveryMethod.ReliableOrdered);

                    int targetForBatch = _udpSaveTotalChunks > 0
                        ? Math.Min(_udpSaveTotalChunks, nextChunk + batchSize)
                        : (nextChunk + 1);

                    var batchWaitEnd = DateTime.UtcNow.AddSeconds(6);
                    while (DateTime.UtcNow < batchWaitEnd && !_udpSaveFailed)
                    {
                        if (_udpSaveTotalChunks > 0 && _udpSaveChunks.Count >= targetForBatch)
                        {
                            break;
                        }
                        if (_udpSaveTotalChunks == 0 && _udpSaveChunks.Count > 0)
                        {
                            break;
                        }
                        _udpChunkSignal.Wait(150);
                        _udpChunkSignal.Reset();
                    }

                    if (_udpSaveFailed)
                    {
                        error = _udpSaveError;
                        return false;
                    }

                    // Advance past any contiguously received chunks
                    while (_udpSaveChunks.ContainsKey(nextChunk))
                    {
                        nextChunk++;
                    }
                }

                if (_udpSaveTotalChunks <= 0 || _udpSaveChunks.Count < _udpSaveTotalChunks)
                {
                    error = $"UDP aktarım zaman aşımı ({_udpSaveChunks.Count}/{_udpSaveTotalChunks} parça).";
                    return false;
                }

                using var compressedStream = new MemoryStream(_udpSaveTotalBytes > 0 ? _udpSaveTotalBytes : 1024 * 1024);
                for (int i = 0; i < _udpSaveTotalChunks; i++)
                {
                    if (!_udpSaveChunks.TryGetValue(i, out var bytes))
                    {
                        error = $"Eksik UDP parça #{i}.";
                        return false;
                    }
                    compressedStream.Write(bytes, 0, bytes.Length);
                }

                string tempPath = localSavePath + ".tmp";
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }

                compressedStream.Position = 0;
                using (var gz = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress))
                using (var outFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    gz.CopyTo(outFile);
                    outFile.Flush();
                }

                bool ok = File.Exists(tempPath) && new FileInfo(tempPath).Length > 100000;
                if (ok)
                {
                    if (File.Exists(localSavePath)) File.Delete(localSavePath);
                    File.Move(tempPath, localSavePath);
                }
                else
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    error = "Açılan kayıt dosyası geçersiz.";
                }
                return ok;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                _udpSaveChunks.Clear();
            }
        }

        private static string? GetArgumentValue(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        protected override void OnSubModuleUnloaded()
        {
            try
            {
                _ipcHost.Stop();
                _embeddedSaveServer.Stop();
                _harmony?.UnpatchAll("com.calradiaconnected.mod");
                ModContext.Shutdown();
            }
            catch { }

            base.OnSubModuleUnloaded();
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);

            if (!_isServerProcess && ModContext.IsClientConnected && mission != null)
            {
                try
                {
                    string sceneName = mission.SceneName ?? string.Empty;
                    if (mission.Mode == MissionMode.Conversation ||
                        sceneName.IndexOf("conversation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sceneName.IndexOf("banner_editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sceneName.IndexOf("character_creation", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return;
                    }

                    int oppPeerId = BattleSyncService.Instance.ActivePvpOpponentPeerId;
                    string oppName = BattleSyncService.Instance.ActivePvpOpponentName;
                    if (oppPeerId > 0)
                    {
                        CoopMissionSyncBehavior.PendingOpponentPeerId = oppPeerId;
                        CoopMissionSyncBehavior.PendingOpponentName = oppName;
                    }

                    // Note: Bannerlord invokes MBSubModuleBase.OnMissionBehaviorInitialize AFTER Mission.AfterStart
                    // has already run MissionBehavior.OnBehaviorInitialize() on pre-existing behaviors, so we explicitly
                    // initialize CoopMissionSyncBehavior right after adding it.
                    var coopBehavior = new CoopMissionSyncBehavior(oppPeerId, oppName);
                    mission.AddMissionBehavior(coopBehavior);
                    coopBehavior.EnsureInitializedFromMission();
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] CoopMissionSyncBehavior attach error: {ex.Message}");
                }
            }
        }

        private void OnHeroExecutionReceived(HeroExecutionReceivedEvent evt)
        {
            var packet = evt.Packet;
            if (packet == null || _isServerProcess || !ModContext.IsClientConnected) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            if (myPeerId <= 0 || packet.VictimPeerId != myPeerId) return;

            if (packet.ReasonStage == 4) // Server approved respawn reset
            {
                _executionDialogShown = false;
                PlayerHeroManager.Instance.PrepareRespawnAfterDeath(myPeerId);
                return;
            }

            if ((packet.ReasonStage == 1 || packet.ReasonStage == 2) && !_executionDialogShown)
            {
                _executionDialogShown = true;
                string title = packet.ReasonStage == 1
                    ? Loc.T("Kahramanınız İdam Edildi!", "Your Hero Has Been Executed!")
                    : Loc.T("Kahramanınız Savaşta Düştü!", "Your Hero Has Fallen Permanently!");

                string killerDisplay = string.IsNullOrWhiteSpace(packet.KillerName)
                    ? Loc.T("Düşman Hükümdar", "Enemy Lord")
                    : packet.KillerName;

                string body = Loc.F(
                    "Kahramanınız '{0}', '{1}' tarafından kalıcı olarak öldürüldü. Ölen bir kahramanla sefere devam edemezsiniz.\n\nAynı sunucuda yeni bir karakter/varis oluşturabilir veya sunucudan ayrılabilirsiniz.",
                    "Your hero '{0}' was permanently slain by '{1}'. You cannot continue playing a dead hero.\n\nYou may forge a new character/heir on this server or disconnect.",
                    packet.VictimHeroName, killerDisplay);

                InformationManager.ShowInquiry(new InquiryData(
                    titleText: title,
                    text: body,
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: Loc.T("Yeni Karakter Oluştur", "Create New Character"),
                    negativeText: Loc.T("Sunucudan Ayrıl", "Disconnect"),
                    affirmativeAction: () =>
                    {
                        _executionDialogShown = false;
                        UI.P2PDialogManager.Instance.CloseOverlay();
                        ModContext.Client?.Send(new Common.Packets.HeroExecutionPacket
                        {
                            VictimPeerId = myPeerId,
                            KillerPeerId = packet.KillerPeerId,
                            KillerName = killerDisplay,
                            VictimHeroId = packet.VictimHeroId,
                            VictimHeroName = packet.VictimHeroName,
                            ReasonStage = 3 // RespawnResetRequest
                        }, DeliveryMethod.ReliableOrdered);
                        _dispatcher?.Enqueue(() =>
                        {
                            PlayerHeroManager.Instance.PrepareRespawnAfterDeath(myPeerId);
                        });
                    },
                    negativeAction: () =>
                    {
                        _executionDialogShown = false;
                        UI.P2PDialogManager.Instance.CloseOverlay();
                        ModContext.Client?.Disconnect();
                        BattleSyncService.Instance.ClearPvpSessionForRematch();
                        PlayerHeroManager.Instance.ResetForNewSession();
                        _dispatcher?.Enqueue(() =>
                        {
                            try
                            {
                                if (PlayerEncounter.Current != null)
                                {
                                    PlayerEncounter.Finish(true);
                                }
                            }
                            catch { }
                            try
                            {
                                // Always use MBGameManager.EndGame() instead of CleanStates() to return to main menu cleanly without crashing!
                                MBGameManager.EndGame();
                            }
                            catch { }
                        });
                    }), pauseGameActiveState: true);
            }
        }

        private void OpenCampaignQuickChat()
        {
            if (_campaignChatModalOpen || !ModContext.IsClientConnected) return;
            _campaignChatModalOpen = true;

            InformationManager.ShowTextInquiry(new TextInquiryData(
                titleText: Loc.T("Kalradya Genel Sohbet (F9)", "Calradia Global Chat (F9)"),
                text: Loc.T("Tüm sunucu oyuncularına mesaj gönder:", "Send a message to all players on the server:"),
                isAffirmativeOptionShown: true,
                isNegativeOptionShown: true,
                affirmativeText: Loc.T("Gönder", "Send"),
                negativeText: Loc.T("İptal", "Cancel"),
                affirmativeAction: (msg) =>
                {
                    _campaignChatModalOpen = false;
                    if (string.IsNullOrWhiteSpace(msg)) return;

                    int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
                    string senderName = Hero.MainHero?.Name?.ToString() ?? UI.ClientLoginManager.Instance.CurrentUsername;
                    var chatPacket = new Common.Packets.ChatMessagePacket
                    {
                        SenderPeerId = myPeerId,
                        SenderName = senderName,
                        Message = msg.Trim(),
                        IsSystem = false,
                        Channel = 0
                    };
                    ModContext.Client?.Send(chatPacket, DeliveryMethod.ReliableOrdered);
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[{Loc.T("Sohbet", "Chat")} - {senderName}]: {chatPacket.Message}",
                        Colors.Cyan));
                },
                negativeAction: () => { _campaignChatModalOpen = false; }),
                pauseGameActiveState: false);
        }

        protected override void InitializeGameStarter(Game game, IGameStarter starterObject)
        {
            base.InitializeGameStarter(game, starterObject);
            SyncLanguageFromBannerlord();

            if (starterObject is CampaignGameStarter campaignStarter)
            {
                campaignStarter.AddBehavior(new PlayerStarterProgressionBehavior());
                campaignStarter.AddBehavior(new CampaignTimeSyncBehavior());
                campaignStarter.AddBehavior(new SharedCampaignSyncBehavior());
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            try
            {
                _server?.Poll();
                _client?.Poll();
                _dispatcher?.ExecutePending();
                _worldSync?.OnCampaignTick(dt);
                CampaignTimeSyncBehavior.Instance?.OnRealTick(dt);

                if (_isServerProcess)
                {
                    _ipcCacheTimer += dt;
                    float ipcInterval = (_server?.Sessions?.Count ?? 0) > 0 ? 0.50f : 1.50f;
                    if (_ipcCacheTimer >= ipcInterval)
                    {
                        _ipcCacheTimer = 0f;
                        _ipcHost.UpdateCacheOnTick();
                    }

                    if (GameStateManager.Current?.ActiveState is MapState)
                    {
                        Services.ServerPerformanceOptimizer.Instance.OnServerMapTick();
                    }

                    // Disable TrueSight on server so out-of-range parties are not rendered across the entire map,
                    // while keeping the dummy server host party hidden and idle.
                    if (TaleWorldsCampaign.Current != null)
                    {
                        if (TaleWorldsCampaign.Current.TrueSight)
                        {
                            TaleWorldsCampaign.Current.TrueSight = false;
                        }

                        if (PartyBase.MainParty?.MobileParty != null)
                        {
                            var serverHostParty = PartyBase.MainParty.MobileParty;
                            if (serverHostParty.IsVisible)
                            {
                                serverHostParty.IsVisible = false;
                                serverHostParty.IgnoreByOtherPartiesTill(CampaignTime.Never);
                                serverHostParty.SetMoveModeHold();
                            }
                            if (PlayerEncounter.Current != null)
                            {
                                try { PlayerEncounter.Finish(true); } catch { }
                            }
                            if (serverHostParty.CurrentSettlement != null)
                            {
                                try { LeaveSettlementAction.ApplyForParty(serverHostParty); } catch { }
                            }
                        }
                    }

                    // Dedicated server real-time Auto-Save timer (driven by Server Manager AutoSaveIntervalMinutes)
                    int autoSaveMins = Common.Config.ServerRuntimeSettings.Current.AutoSaveIntervalMinutes;
                    if (autoSaveMins > 0 &&
                        !_newGameSaveRequested &&
                        GameStateManager.Current?.ActiveState is MapState &&
                        !InformationManager.IsAnyInquiryActive() &&
                        TaleWorldsCampaign.Current?.GameStarted == true &&
                        !(TaleWorldsCampaign.Current.SaveHandler?.IsSaving ?? false))
                    {
                        _serverAutoSaveTimer += dt;
                        if (_serverAutoSaveTimer >= autoSaveMins * 60f)
                        {
                            _serverAutoSaveTimer = 0f;
                            try
                            {
                                string saveName = string.IsNullOrWhiteSpace(ModContext.ActiveServerSaveName)
                                    ? "Calradia_Sunucusu"
                                    : ModContext.ActiveServerSaveName;
                                TaleWorldsCampaign.Current.SaveHandler?.SaveAs(saveName);
                                InformationManager.DisplayMessage(new InformationMessage(
                                    $"[Calradia-Connected] Otomatik dünya kaydı alındı: '{saveName}.sav' ({autoSaveMins} dk aralık).",
                                    Colors.Green));
                            }
                            catch { }
                        }
                    }

                    if (_newGameSaveRequested &&
                        GameStateManager.Current?.ActiveState is MapState &&
                        !InformationManager.IsAnyInquiryActive() &&
                        !(TaleWorldsCampaign.Current?.SaveHandler?.IsSaving ?? false))
                    {
                        _newGameSaveTicks++;
                        if (_newGameSaveTicks >= 90)
                        {
                            _newGameSaveRequested = false;
                            _newGameSaveTicks = 0;
                            try
                            {
                                string saveName = string.IsNullOrWhiteSpace(ModContext.ActiveServerSaveName)
                                    ? "Yeni_Oyun"
                                    : ModContext.ActiveServerSaveName;
                                // Save once; FastDL & EmbeddedSaveServer both resolve {saveName}.sav first.
                                TaleWorldsCampaign.Current?.SaveHandler?.SaveAs(saveName);
                                InformationManager.DisplayMessage(new InformationMessage(
                                    $"[Calradia-Connected] Yepyeni Sandbox sefer dünyası '{saveName}.sav' olarak kaydedildi!",
                                    Colors.Green));
                            }
                            catch (Exception ex)
                            {
                                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Yeni oyun kaydetme hatası: {ex}");
                            }
                        }
                    }
                }

                if (!_isServerProcess && !ModContext.IsClientConnected)
                {
                    _clientFogResetDone = false;
                }

                if (!_isServerProcess && ModContext.IsClientConnected)
                {
                    if (TaleWorldsCampaign.Current != null)
                    {
                        if (TaleWorldsCampaign.Current.TrueSight)
                        {
                            TaleWorldsCampaign.Current.TrueSight = false;
                        }

                        if (!_clientFogResetDone &&
                            MobileParty.MainParty != null &&
                            GameStateManager.Current?.ActiveState is MapState)
                        {
                            _clientFogResetDone = true;
                            WorldSyncManager.ResetOutOfRangeAiVisibility(MobileParty.MainParty);
                        }
                    }

                    Patches.MoveOrderDebouncer.TickPending();
                    BattleSyncService.Instance.TickPendingPvpLaunch();
                    PlayerInteractionService.Instance.TickPendingLiveBarter();
                    PlayerArmyService.Instance.TickArmyEscort(dt);

                    if (TaleWorldsCampaign.Current != null &&
                        TaleWorldsCampaign.Current.GameStarted &&
                        Hero.MainHero != null &&
                        PlayerHeroManager.Instance.IsCharacterInitialized &&
                        !(GameStateManager.Current?.ActiveState is CharacterCreationState))
                    {
                        _equipmentPollTimer += dt;
                        if (_equipmentPollTimer >= 0.20f)
                        {
                            _equipmentPollTimer = 0f;
                            string eqSig = PlayerHeroManager.SerializeAllEquipments(Hero.MainHero);
                            if (!string.Equals(eqSig, _lastLocalEquipmentSignature, StringComparison.Ordinal))
                            {
                                _lastLocalEquipmentSignature = eqSig;
                                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "LiveEquipmentChanged" });
                            }
                        }

                        _rosterPollTimer += dt;
                        _rosterHeartbeatTimer += dt;
                        if (_rosterPollTimer >= 0.40f && MobileParty.MainParty != null)
                        {
                            _rosterPollTimer = 0f;
                            string rosterSig = RosterSyncService.ComputeRosterSignature(Hero.MainHero, MobileParty.MainParty);
                            if (!string.Equals(rosterSig, _lastLocalRosterSignature, StringComparison.Ordinal) ||
                                _rosterHeartbeatTimer >= 5.0f)
                            {
                                _rosterHeartbeatTimer = 0f;
                                _lastLocalRosterSignature = rosterSig;
                                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "LiveRosterOrGoldChanged" });
                            }
                        }

                        _fleetPollTimer += dt;
                        if (_fleetPollTimer >= 1.0f && MobileParty.MainParty != null)
                        {
                            _fleetPollTimer = 0f;
                            string fleetSig = NavalSyncService.ComputeFleetSignature(MobileParty.MainParty);
                            if (!string.Equals(fleetSig, _lastLocalFleetSignature, StringComparison.Ordinal))
                            {
                                _lastLocalFleetSignature = fleetSig;
                                NavalSyncService.Instance.SyncLocalPlayerFleetToServer();
                            }
                        }
                    }
                }

                // Client: Prompt custom Character & Clan creation upon entering campaign
                // (Server process does NOT create a character; it is purely the host!)
                if (!_isServerProcess && ModContext.IsClientConnected && GameStateManager.Current?.ActiveState is MapState mapState)
                {
                    if (!mapState.AtMenu && !_campaignChatModalOpen && Input.IsKeyPressed(InputKey.F9))
                    {
                        OpenCampaignQuickChat();
                    }

                    PlayerHeroManager.Instance.CheckAndPromptCharacterCreation(
                        ModContext.Client?.LocalPeerId ?? 1001,
                        UI.ClientLoginManager.Instance.CurrentUsername);
                }

                TickPostSaveRosterAndPvpMaintenance(dt);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Tick exception: {ex.Message}");
            }
        }

        private void TickPostSaveRosterAndPvpMaintenance(float dt)
        {
            var campaign = TaleWorldsCampaign.Current;
            bool isGameStarted = campaign != null && campaign.GameStarted;
            bool isSaving = campaign?.SaveHandler?.IsSaving == true;

            // Detect SaveHandler IsSaving true -> false transition on both Server & Client
            if (_wasCampaignSaving && !isSaving)
            {
                if (_isServerProcess)
                {
                    RosterSyncService.Instance.BroadcastAllKnownRosters();
                }
                else if (ModContext.IsClientConnected && isGameStarted)
                {
                    RosterSyncService.Instance.FlushPendingRosters();
                    _lastLocalRosterSignature = string.Empty;
                    _rosterHeartbeatTimer = 99f;
                    _rosterForceResyncTimer = 99f;
                }
            }
            _wasCampaignSaving = isSaving;

            if (_isServerProcess)
            {
                if (isGameStarted && !isSaving)
                {
                    _rosterFlushTimer += dt;
                    if (RosterSyncService.Instance.HasPendingRosters || _rosterFlushTimer >= 0.5f)
                    {
                        _rosterFlushTimer = 0f;
                        RosterSyncService.Instance.FlushPendingRosters();
                    }

                    _serverRosterBroadcastTimer += dt;
                    if (_serverRosterBroadcastTimer >= 8.0f)
                    {
                        _serverRosterBroadcastTimer = 0f;
                        RosterSyncService.Instance.BroadcastAllKnownRosters();
                    }

                    _despawnFlushTimer += dt;
                    if (_despawnFlushTimer >= 0.5f &&
                        TaleWorlds.MountAndBlade.Mission.Current == null &&
                        TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current == null &&
                        TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent == null)
                    {
                        _despawnFlushTimer = 0f;
                        PlayerHeroManager.Instance.ProcessDeferredDespawns(_worldSync);
                    }
                }
            }
            else if (ModContext.IsClientConnected)
            {
                if (!_pvpOverlaySubscribed)
                {
                    _pvpOverlaySubscribed = true;
                    UI.PvpReadyOverlayManager.Instance.SyncFromService();
                }

                // Ensure PvP Ready overlay attaches once MissionScreen opens & closes cleanly when battle starts/ends
                UI.PvpReadyOverlayManager.Instance.SyncFromService();

                if (isGameStarted && !isSaving)
                {
                    _rosterFlushTimer += dt;
                    if (RosterSyncService.Instance.HasPendingRosters || _rosterFlushTimer >= 0.5f)
                    {
                        _rosterFlushTimer = 0f;
                        RosterSyncService.Instance.FlushPendingRosters();
                    }

                    // Periodic full-roster re-broadcast even when local signature didn't change so late joiners never miss troops
                    _rosterForceResyncTimer += dt;
                    if (_rosterForceResyncTimer >= 8.0f &&
                        TaleWorlds.MountAndBlade.Mission.Current == null &&
                        MobileParty.MainParty != null &&
                        PlayerHeroManager.Instance.IsCharacterInitialized)
                    {
                        _rosterForceResyncTimer = 0f;
                        _lastLocalRosterSignature = string.Empty;
                        _rosterHeartbeatTimer = 99f;
                    }

                    _despawnFlushTimer += dt;
                    if (_despawnFlushTimer >= 0.5f &&
                        TaleWorlds.MountAndBlade.Mission.Current == null &&
                        TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current == null &&
                        TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent == null)
                    {
                        _despawnFlushTimer = 0f;
                        PlayerHeroManager.Instance.ProcessDeferredDespawns(_worldSync);
                    }
                }
            }
        }
    }
}
