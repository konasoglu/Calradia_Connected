using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Progression;
using CalradiaConnected.Campaign.Sync;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace CalradiaConnected.Campaign.Player
{
    public class PlayerHeroManager
    {
        private static PlayerHeroManager? _instance;
        public static PlayerHeroManager Instance => _instance ??= new PlayerHeroManager();

        private bool _creationPromptShown = false;
        private bool _isCharacterInitialized = false;
        private bool _eventsSubscribed = false;
        private readonly Dictionary<string, DisconnectedPartySnapshot> _disconnectedPartySnapshots =
            new Dictionary<string, DisconnectedPartySnapshot>(StringComparer.Ordinal);

        private sealed class DisconnectedPartySnapshot
        {
            public float PosX;
            public float PosY;
            public bool IsAtSea;
            public string SettlementId = string.Empty;
            public readonly List<(string TroopId, int Count, int Wounded)> Members = new List<(string, int, int)>();
            public readonly List<(string ItemId, string ModifierId, int Count)> Items = new List<(string, string, int)>();
        }

        public bool IsCharacterInitialized
        {
            get => _isCharacterInitialized;
            set => _isCharacterInitialized = value;
        }

        public void EnsureEventSubscriptions()
        {
            if (_eventsSubscribed) return;
            _eventsSubscribed = true;

            ModContext.Events.Subscribe<LocalCharacterCreationCompletedEvent>(evt =>
            {
                FinalizeLocalPlayerHero(evt.PeerId, evt.Username);
            });
        }

        public void ResetForNewSession()
        {
            _creationPromptShown = false;
            _isCharacterInitialized = false;
            _disconnectedPartySnapshots.Clear();
            NetworkObjectRegistry.Instance.Clear();
        }

        public void CheckAndPromptCharacterCreation(int peerId, string defaultName)
        {
            if (_creationPromptShown || _isCharacterInitialized) return;
            if (TaleWorlds.CampaignSystem.Campaign.Current == null || !(GameStateManager.Current?.ActiveState is MapState)) return;

            EnsureEventSubscriptions();

            // Reconnect check via NetworkObjectRegistry — never re-assign a dead/executed hero!
            if (NetworkObjectRegistry.Instance.TryResolveExistingPlayer(peerId, defaultName, out var existingHero, out var existingClan, out var existingParty) &&
                existingHero != null &&
                existingHero.IsAlive)
            {
                AssignPlayerControls(existingHero);
                _isCharacterInitialized = true;

                string heroId = NetworkObjectRegistry.GetPlayerHeroId(peerId);
                if (ModContext.IsServerRunning)
                {
                    string username = ModContext.Server?.Sessions.TryGetValue(peerId, out var s) == true ? s.PlayerName : defaultName;
                    if (!string.IsNullOrEmpty(username))
                    {
                        global::Common.Users.UserDatabase.Instance.UpdateHeroInfo(username, heroId, existingHero.Name?.ToString(), existingClan?.Name?.ToString());
                    }
                }
                else if (ModContext.IsClientConnected)
                {
                    var curPos = MobileParty.MainParty?.GetPosition2D ?? Vec2.Zero;
                    var reconnectPacket = new PlayerHeroCreatedPacket
                    {
                        PeerId = peerId,
                        HeroName = existingHero.Name?.ToString() ?? defaultName,
                        ClanName = existingClan?.Name?.ToString() ?? Loc.T("Oyuncu Klanı", "Player Clan"),
                        CultureId = existingHero.Culture?.StringId ?? "empire",
                        BannerKey = existingClan?.Banner?.Serialize() ?? string.Empty,
                        StartingSettlementId = existingHero.HomeSettlement?.StringId ?? string.Empty,
                        StartingGold = existingHero.Gold,
                        StartingTroops = MobileParty.MainParty?.MemberRoster?.TotalRegulars ?? 0,
                        EquipmentCsv = SerializeAllEquipments(existingHero),
                        CurrentX = curPos.X,
                        CurrentY = curPos.Y,
                        IsFemale = existingHero.IsFemale,
                        BodyPropertiesStr = existingHero.BodyProperties.ToString(),
                        IsAtSea = MobileParty.MainParty?.IsCurrentlyAtSea ?? false,
                        NavigationType = (byte)(MobileParty.MainParty?.DesiredAiNavigationType ?? MobileParty.NavigationType.Default)
                    };
                    ModContext.Client?.Send(reconnectPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "HeroReconnected" });
                }

                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.F("[Calradia-Connected] Tekrar hoş geldiniz! Karakteriniz '{0}' ({1}) devralındı.",
                          "[Calradia-Connected] Welcome back! Resumed control of '{0}' ({1}).",
                          existingHero.Name, existingClan?.Name?.ToString() ?? Loc.T("Klan", "Clan")),
                    Colors.Green));
                return;
            }

            _creationPromptShown = true;

            // Launch official native Sandbox Character Creation UI (Culture, 3D FaceGen, Narrative, Banner & Clan Naming)
            LaunchNativeSandboxCharacterCreation(peerId, defaultName);
        }

        public void LaunchNativeSandboxCharacterCreation(int peerId, string defaultName)
        {
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Calradia-Connected] Sandbox Karakter ve Hanedan oluşturma ekranı açılıyor...",
                          "[Calradia-Connected] Opening Sandbox Character & Dynasty Creation..."),
                    Colors.Cyan));

                var state = GameStateManager.Current.CreateState<CharacterCreationState>();
                // Push CharacterCreationState on top of MapState without wiping the campaign map screen stack
                GameStateManager.Current.PushState(state);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] LaunchNativeSandboxCharacterCreation hatası: {ex}");
            }
        }

        public void FinalizeLocalPlayerHero(int peerId, string defaultName)
        {
            try
            {
                var hero = Hero.MainHero;
                if (hero == null) return;

                // Ensure Culture reflects the player's selection in Character Creation
                if (CharacterObject.PlayerCharacter?.Culture != null && hero.Culture != CharacterObject.PlayerCharacter.Culture)
                {
                    hero.Culture = CharacterObject.PlayerCharacter.Culture;
                }

                string chosenHeroName = !string.IsNullOrEmpty(defaultName) ? defaultName : (hero.Name?.ToString() ?? "Oyuncu");
                hero.SetName(new TextObject(chosenHeroName), new TextObject(chosenHeroName));

                string heroId = NetworkObjectRegistry.GetPlayerHeroId(peerId);
                hero.StringId = heroId;
                if (hero.HeroState != Hero.CharacterStates.Active)
                {
                    try { hero.ChangeState(Hero.CharacterStates.Active); } catch { }
                }
                hero.HitPoints = Math.Max(100, hero.MaxHitPoints);

                // Select starting town strictly matching the player's chosen Culture (never use stale pre-creation HomeSettlement)
                var startingSettlement = Settlement.All.FirstOrDefault(s => s.IsTown && s.Culture == hero.Culture)
                                       ?? Settlement.All.FirstOrDefault(s => s.Culture == hero.Culture && !s.IsHideout)
                                       ?? Settlement.All.FirstOrDefault(s => s.IsTown)
                                       ?? Settlement.All.First();
                string startingSettlementId = startingSettlement.StringId;

                hero.BornSettlement = startingSettlement;
                hero.UpdateHomeSettlement();

                string clanId = NetworkObjectRegistry.GetPlayerClanId(peerId);
                if (Clan.PlayerClan != null)
                {
                    Clan.PlayerClan.StringId = clanId;
                    if (hero.Culture != null)
                    {
                        Clan.PlayerClan.Culture = hero.Culture;
                    }
                    if (Clan.PlayerClan.Banner == null)
                    {
                        Clan.PlayerClan.Banner = Banner.CreateRandomClanBanner();
                    }
                    Clan.PlayerClan.SetInitialHomeSettlement(startingSettlement);
                    Clan.PlayerClan.CalculateMidSettlement();
                }

                // Exit any lingering encounter or settlement from the downloaded server save
                if (PlayerEncounter.Current != null)
                {
                    try { PlayerEncounter.Finish(true); } catch { }
                }

                string partyId = NetworkObjectRegistry.GetPlayerPartyId(peerId);
                WorldSyncManager.ClearPartyDisconnected(partyId);
                if (MobileParty.MainParty != null)
                {
                    // Remove any stale inactive/duplicate party in the loaded save that shares this peer's deterministic partyId
                    if (MobileParty.All != null)
                    {
                        foreach (var dup in MobileParty.All.Where(p => p != null && p != MobileParty.MainParty && string.Equals(p.StringId, partyId, StringComparison.Ordinal)).ToList())
                        {
                            DespawnPartySafely(dup, null);
                            if (string.Equals(dup.StringId, partyId, StringComparison.Ordinal))
                            {
                                dup.StringId = $"{partyId}_old_{Environment.TickCount}";
                            }
                        }
                        WorldSyncManager.ClearPartyDisconnected(partyId);
                    }

                    if (MobileParty.MainParty.CurrentSettlement != null)
                    {
                        try { LeaveSettlementAction.ApplyForParty(MobileParty.MainParty); } catch { }
                    }

                    MobileParty.MainParty.StringId = partyId;
                    MobileParty.MainParty.SetLandNavigationAccess(true);
                    MobileParty.MainParty.IsVisible = true;
                    MobileParty.MainParty.ForceAiNoPathMode = false;
                    MobileParty.MainParty.Ai?.SetDoNotMakeNewDecisions(false);
                    MobileParty.MainParty.Ai?.EnableAi();
                    MobileParty.MainParty.IgnoreByOtherPartiesTill(CampaignTime.Now);

                    // Clear inherited server host dummy grain stockpile before applying player's own starting supplies
                    if (DefaultItems.Grain != null && MobileParty.MainParty.ItemRoster.GetItemNumber(DefaultItems.Grain) >= 5000)
                    {
                        MobileParty.MainParty.ItemRoster.Clear();
                    }

                    if (CalradiaConnected.Common.Config.ServerRuntimeSettings.Current.StartingFoodSupplies &&
                        DefaultItems.Grain != null &&
                        MobileParty.MainParty.ItemRoster.GetItemNumber(DefaultItems.Grain) < 10)
                    {
                        MobileParty.MainParty.ItemRoster.AddToCounts(DefaultItems.Grain, 20);
                    }

                    if (startingSettlement != null)
                    {
                        CampaignVec2 spawnPos = startingSettlement.GatePosition.IsValid()
                            ? startingSettlement.GatePosition
                            : new CampaignVec2(startingSettlement.GetPosition2D, isOnLand: true);
                        MobileParty.MainParty.Position = spawnPos;
                        MobileParty.MainParty.SetMoveModeHold();
                    }
                }

                NetworkObjectRegistry.Instance.RegisterPlayerEntities(peerId, hero, Clan.PlayerClan, MobileParty.MainParty);
                _isCharacterInitialized = true;

                // Apply full authoritative Server Manager starting progression package immediately
                PlayerStarterProgressionBehavior.Instance?.ApplyStarterForNewCharacter(hero);

                var curPartyPos = MobileParty.MainParty?.GetPosition2D ?? Vec2.Zero;
                var packet = new PlayerHeroCreatedPacket
                {
                    PeerId = peerId,
                    HeroName = chosenHeroName,
                    ClanName = Clan.PlayerClan?.Name?.ToString() ?? "Oyuncu Klanı",
                    CultureId = hero.Culture?.StringId ?? "empire",
                    BannerKey = Clan.PlayerClan?.Banner?.Serialize() ?? string.Empty,
                    StartingSettlementId = startingSettlementId,
                    StartingGold = hero.Gold,
                    StartingTroops = MobileParty.MainParty?.MemberRoster?.TotalRegulars ?? 0,
                    EquipmentCsv = SerializeAllEquipments(hero),
                    CurrentX = curPartyPos.X,
                    CurrentY = curPartyPos.Y,
                    IsFemale = hero.IsFemale,
                    BodyPropertiesStr = hero.BodyProperties.ToString(),
                    IsAtSea = MobileParty.MainParty?.IsCurrentlyAtSea ?? false,
                    NavigationType = (byte)(MobileParty.MainParty?.DesiredAiNavigationType ?? MobileParty.NavigationType.Default)
                };

                PartyBase.MainParty?.SetVisualAsDirty();
                PartyBase.MainParty?.SetLevelMaskIsDirty();

                if (ModContext.IsClientConnected)
                {
                    ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "HeroFinalized" });
                }

                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.F("[Calradia-Connected] '{0}' ({1}) olarak {2} yakınlarında sefere başladınız!",
                          "[Calradia-Connected] Started your campaign as '{0}' ({1}) near {2}!",
                          packet.HeroName, packet.ClanName, startingSettlement?.Name),
                    Colors.Green));
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] FinalizeLocalPlayerHero hatası: {ex}");
            }
        }

        public void AssignPlayerControls(Hero hero)
        {
            if (hero == null) return;

            try
            {
                if (PlayerEncounter.Current != null)
                {
                    try { PlayerEncounter.Finish(true); } catch { }
                }

                if (Hero.MainHero != hero)
                {
                    if (hero.PartyBelongedTo == null && TaleWorlds.CampaignSystem.Campaign.Current != null)
                    {
                        string pid = NetworkObjectRegistry.GetPlayerPartyId(
                            NetworkObjectRegistry.TryExtractPeerId(hero.StringId, out int extractedPeerId) ? extractedPeerId : 1001);
                        var home = hero.HomeSettlement ?? Settlement.All.FirstOrDefault(s => s.IsTown) ?? Settlement.All.First();
                        var spawn = WorldSyncManager.ResolveValidPosition(Vec2.Invalid, preferLand: true, fallbackSettlement: home);
                        CreatePlayerMobileParty(hero, pid, home, spawn, isAtSea: false, startingTroops: 0);
                    }
                    ChangePlayerCharacterAction.Apply(hero);
                }

                if (MobileParty.MainParty != null)
                {
                    if (MobileParty.MainParty.CurrentSettlement != null)
                    {
                        try { LeaveSettlementAction.ApplyForParty(MobileParty.MainParty); } catch { }
                    }
                    MobileParty.MainParty.SetLandNavigationAccess(true);
                    MobileParty.MainParty.IsVisible = true;
                    MobileParty.MainParty.ForceAiNoPathMode = false;
                    MobileParty.MainParty.Ai?.SetDoNotMakeNewDecisions(false);
                    MobileParty.MainParty.Ai?.EnableAi();
                    MobileParty.MainParty.IgnoreByOtherPartiesTill(CampaignTime.Now);
                }

                PartyBase.MainParty?.SetVisualAsDirty();

                if (GameStateManager.Current?.ActiveState is MapState mapState && mapState.Handler != null)
                {
                    if (mapState.AtMenu)
                    {
                        try { mapState.ExitMenuMode(); } catch { }
                    }
                    mapState.Handler.ResetCamera(resetDistance: true, teleportToMainParty: true);
                    mapState.Handler.TeleportCameraToMainParty();
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] AssignPlayerControls hatası: {ex.Message}");
            }
        }

        public void HandleServerHeroCreatedPacket(PlayerHeroCreatedPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (TaleWorlds.CampaignSystem.Campaign.Current == null ||
                        !TaleWorlds.CampaignSystem.Campaign.Current.GameStarted ||
                        TaleWorlds.CampaignSystem.Campaign.Current.SaveHandler?.IsSaving == true)
                    {
                        return;
                    }

                    if (string.Equals(packet.HeroName, "__SYNC_REQUEST__", StringComparison.Ordinal))
                    {
                        if (ModContext.IsServerRunning)
                        {
                            BroadcastAllExistingPlayers();
                        }
                        return;
                    }

                    // If on client and packet is for local player, ignore (already finalized)
                    if (!ModContext.IsServerRunning && packet.PeerId == (ModContext.Client?.LocalPeerId ?? -1))
                    {
                        return;
                    }

                    string heroId = NetworkObjectRegistry.GetPlayerHeroId(packet.PeerId);
                    string peerPartyId = NetworkObjectRegistry.GetPlayerPartyId(packet.PeerId);
                    WorldSyncManager.ClearPartyDisconnected(peerPartyId);
                    var existing = NetworkObjectRegistry.Instance.ResolveHero(heroId);
                    if (existing != null && existing.IsDead)
                    {
                        // Archive dead hero ID so a newly created successor can cleanly reuse the peer's active slot
                        existing.StringId = $"{heroId}_dead_{Environment.TickCount}";
                        NetworkObjectRegistry.Instance.Clear();
                        existing = null;
                    }
                    if (existing != null)
                    {
                        bool visChanged = ApplyAppearance(existing, packet.IsFemale, packet.BodyPropertiesStr);
                        if (ApplyAllEquipments(existing, packet.EquipmentCsv))
                        {
                            visChanged = true;
                        }
                        if (existing.Clan != null && !string.IsNullOrEmpty(packet.BannerKey))
                        {
                            string existingBanner = existing.Clan.Banner?.Serialize() ?? string.Empty;
                            if (!string.Equals(existingBanner, packet.BannerKey, StringComparison.Ordinal))
                            {
                                try { existing.Clan.Banner = new Banner(packet.BannerKey); visChanged = true; } catch { }
                            }
                        }

                        // If player previously disconnected (their party was cleanly removed), recreate it on reconnect!
                        if (existing.PartyBelongedTo == null)
                        {
                            string reconnectPartyId = NetworkObjectRegistry.GetPlayerPartyId(packet.PeerId);
                            var fallbackSettlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.StartingSettlementId)
                                                     ?? existing.HomeSettlement
                                                     ?? Settlement.All.FirstOrDefault(s => s.IsTown)
                                                     ?? Settlement.All.First();
                            Vec2 rawPos = !float.IsNaN(packet.CurrentX) && !float.IsInfinity(packet.CurrentX) &&
                                          !float.IsNaN(packet.CurrentY) && !float.IsInfinity(packet.CurrentY) &&
                                          (Math.Abs(packet.CurrentX) > 0.01f || Math.Abs(packet.CurrentY) > 0.01f)
                                ? new Vec2(packet.CurrentX, packet.CurrentY)
                                : Vec2.Invalid;
                            bool isAtSea = packet.IsAtSea;
                            if (ModContext.IsServerRunning && _disconnectedPartySnapshots.TryGetValue(existing.StringId, out var savedSnap))
                            {
                                rawPos = new Vec2(savedSnap.PosX, savedSnap.PosY);
                                isAtSea = savedSnap.IsAtSea;
                            }
                            var reconnectPos = WorldSyncManager.ResolveValidPosition(rawPos, preferLand: !isAtSea, fallbackSettlement: fallbackSettlement);
                            var recreated = CreatePlayerMobileParty(existing, reconnectPartyId, fallbackSettlement, reconnectPos, isAtSea, packet.StartingTroops);
                            if (ModContext.IsServerRunning)
                            {
                                RestoreDisconnectedPartySnapshot(existing, recreated);
                            }
                            visChanged = true;
                        }

                        if (existing.PartyBelongedTo != null)
                        {
                            if (!existing.PartyBelongedTo.IsMainParty)
                            {
                                if (packet.IsAtSea != existing.PartyBelongedTo.IsCurrentlyAtSea)
                                {
                                    try
                                    {
                                        existing.PartyBelongedTo.IsCurrentlyAtSea = packet.IsAtSea;
                                        existing.PartyBelongedTo.SetNavalVisualAsDirty();
                                    }
                                    catch { }
                                }
                                if (Services.NavalSyncService.SanitizeNavigationType(existing.PartyBelongedTo, packet.NavigationType, packet.IsAtSea) != MobileParty.NavigationType.Naval)
                                {
                                    existing.PartyBelongedTo.SetLandNavigationAccess(true);
                                }
                                existing.PartyBelongedTo.IsVisible = existing.PartyBelongedTo.CurrentSettlement == null;
                                existing.PartyBelongedTo.IsInspected = true;
                                existing.PartyBelongedTo.ForceAiNoPathMode = false;
                                existing.PartyBelongedTo.Ai?.SetDoNotMakeNewDecisions(false);
                                existing.PartyBelongedTo.Ai?.EnableAi();
                            }
                            if (visChanged)
                            {
                                existing.PartyBelongedTo.Party?.SetVisualAsDirty();
                                existing.PartyBelongedTo.Party?.SetLevelMaskIsDirty();
                            }
                            NetworkObjectRegistry.Instance.RegisterPlayerEntities(packet.PeerId, existing, existing.Clan, existing.PartyBelongedTo);
                        }
                        if (ModContext.IsServerRunning)
                        {
                            BroadcastAllExistingPlayers();
                        }
                        return;
                    }

                    var culture = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CultureObject>(packet.CultureId)
                                  ?? TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CultureObject>("empire")
                                  ?? TaleWorlds.CampaignSystem.Campaign.Current.Factions.First().Culture;

                    var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.StartingSettlementId)
                                     ?? Settlement.All.FirstOrDefault(s => s.Culture == culture && s.IsTown)
                                     ?? Settlement.All.First();

                    string clanId = NetworkObjectRegistry.GetPlayerClanId(packet.PeerId);
                    var clan = NetworkObjectRegistry.Instance.ResolveClan(clanId) ?? Clan.CreateClan(clanId);
                    clan.ChangeClanName(new TextObject(packet.ClanName), new TextObject(packet.ClanName));
                    clan.Culture = culture;
                    var defaultPt = culture.DefaultPartyTemplate
                        ?? TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<PartyTemplateObject>("kingdom_hero_party_empire_template");
                    if (defaultPt != null)
                    {
                        typeof(Clan).GetField("_defaultPartyTemplate", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, defaultPt);
                    }

                    if (!string.IsNullOrEmpty(packet.BannerKey))
                    {
                        try { clan.Banner = new Banner(packet.BannerKey); } catch { }
                    }
                    if (clan.Banner == null)
                    {
                        clan.Banner = Banner.CreateRandomClanBanner();
                    }

                    clan.SetInitialHomeSettlement(settlement);

                    // Pick a major-faction lord template (skip minor faction "spc_" templates that lack standard lord setups)
                    var template = culture.LordTemplates?.FirstOrDefault(t => t != null && !t.StringId.StartsWith("spc_", StringComparison.OrdinalIgnoreCase))
                                   ?? culture.LordTemplates?.FirstOrDefault()
                                   ?? culture.NotableTemplates?.FirstOrDefault()
                                   ?? CharacterObject.PlayerCharacter;

                    Hero newHero = HeroCreator.CreateSpecialHero(template, settlement, clan, null, 25);
                    newHero.StringId = heroId;
                    newHero.SetName(new TextObject(packet.HeroName), new TextObject(packet.HeroName));
                    newHero.Culture = culture;
                    newHero.Gold = packet.StartingGold > 0 ? packet.StartingGold : 1000;
                    newHero.BornSettlement = settlement;
                    newHero.UpdateHomeSettlement();
                    newHero.ChangeState(Hero.CharacterStates.Active);
                    ApplyAppearance(newHero, packet.IsFemale, packet.BodyPropertiesStr);
                    ApplyAllEquipments(newHero, packet.EquipmentCsv);
                    clan.SetLeader(newHero);
                    clan.CalculateMidSettlement();

                    string partyId = NetworkObjectRegistry.GetPlayerPartyId(packet.PeerId);
                    bool hasLivePos = !float.IsNaN(packet.CurrentX) && !float.IsInfinity(packet.CurrentX) &&
                                      !float.IsNaN(packet.CurrentY) && !float.IsInfinity(packet.CurrentY) &&
                                      (Math.Abs(packet.CurrentX) > 0.01f || Math.Abs(packet.CurrentY) > 0.01f);

                    CampaignVec2 spawnPos = WorldSyncManager.ResolveValidPosition(
                        hasLivePos ? new Vec2(packet.CurrentX, packet.CurrentY) : Vec2.Invalid,
                        preferLand: !packet.IsAtSea,
                        fallbackSettlement: settlement);

                    MobileParty newParty = CreatePlayerMobileParty(newHero, partyId, settlement, spawnPos, packet.IsAtSea, packet.StartingTroops);

                    NetworkObjectRegistry.Instance.RegisterPlayerEntities(packet.PeerId, newHero, clan, newParty);

                    // Auto-save the world on the server & sync ALL existing players to all clients
                    if (ModContext.IsServerRunning)
                    {
                        string username = ModContext.Server?.Sessions.TryGetValue(packet.PeerId, out var session) == true ? session.PlayerName : string.Empty;
                        if (!string.IsNullOrEmpty(username))
                        {
                            global::Common.Users.UserDatabase.Instance.UpdateHeroInfo(username, heroId, packet.HeroName, packet.ClanName);
                        }

                        try
                        {
                            var saveHandler = TaleWorlds.CampaignSystem.Campaign.Current.SaveHandler;
                            if (saveHandler != null && !saveHandler.IsSaving)
                            {
                                string saveName = !string.IsNullOrWhiteSpace(ModContext.ActiveServerSaveName)
                                    ? ModContext.ActiveServerSaveName
                                    : "Calradia_Sunucusu";
                                saveHandler.SaveAs(saveName);
                            }
                            else
                            {
                                saveHandler?.SignalAutoSave();
                            }
                        }
                        catch { }

                        BroadcastAllExistingPlayers();
                    }

                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.F("[Calradia-Connected] Oyuncu '{0}' ({1}) dünyaya katıldı!",
                              "[Calradia-Connected] Player '{0}' ({1}) joined the world!",
                              packet.HeroName, packet.ClanName),
                        Colors.Cyan));
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Server hero sync error: {ex}");
                }
            });
        }

        /// <summary>
        /// Clears local character state after permadeath/execution so the player can either create a new character
        /// or disconnect cleanly, preventing continued play with a dead hero without crashing Bannerlord.
        /// </summary>
        public void PrepareRespawnAfterDeath(int peerId)
        {
            if (ModContext.IsServerRunning)
            {
                string heroId = NetworkObjectRegistry.GetPlayerHeroId(peerId);
                var deadHero = NetworkObjectRegistry.Instance.ResolveHero(heroId);
                if (deadHero != null)
                {
                    deadHero.StringId = $"{heroId}_dead_{Environment.TickCount}";
                }
                string clanId = NetworkObjectRegistry.GetPlayerClanId(peerId);
                var deadClan = NetworkObjectRegistry.Instance.ResolveClan(clanId);
                if (deadClan != null)
                {
                    deadClan.StringId = $"{clanId}_dead_{Environment.TickCount}";
                }
                string partyId = NetworkObjectRegistry.GetPlayerPartyId(peerId);
                var deadParty = NetworkObjectRegistry.Instance.ResolveParty(partyId);
                if (deadParty != null)
                {
                    deadParty.StringId = $"{partyId}_dead_{Environment.TickCount}";
                    DespawnPartySafely(deadParty, null);
                }
                NetworkObjectRegistry.Instance.Clear();
                return;
            }

            // Client side: guard against duplicate calls (e.g. local click + ReasonStage=4 server ack)
            if (GameStateManager.Current?.ActiveState is CharacterCreationState)
            {
                return;
            }

            try
            {
                if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);
            }
            catch { }
            try
            {
                if (Hero.MainHero != null && Hero.MainHero.IsPrisoner) PlayerCaptivity.EndCaptivity();
            }
            catch { }
            try
            {
                if (GameStateManager.Current?.ActiveState is MapState ms && ms.AtMenu) ms.ExitMenuMode();
            }
            catch { }

            // Reset local living MainHero & MainParty to a clean starter slate BEFORE launching CharacterCreationState
            try
            {
                var mainHero = Hero.MainHero;
                if (mainHero != null)
                {
                    if (mainHero.HeroState != Hero.CharacterStates.Active)
                    {
                        mainHero.ChangeState(Hero.CharacterStates.Active);
                    }
                    mainHero.HitPoints = Math.Max(100, mainHero.MaxHitPoints);
                    mainHero.Gold = 1000;
                    // Temporarily detach deterministic ID so CheckAndPromptCharacterCreation never mistakes it for an existing living hero
                    mainHero.StringId = $"cc_respawn_pending_{peerId}_{Environment.TickCount}";
                }

                var mainParty = MobileParty.MainParty;
                if (mainParty != null)
                {
                    if (mainParty.CurrentSettlement != null)
                    {
                        try { LeaveSettlementAction.ApplyForParty(mainParty); } catch { }
                    }
                    if (mainParty.MemberRoster != null)
                    {
                        for (int i = mainParty.MemberRoster.Count - 1; i >= 0; i--)
                        {
                            var el = mainParty.MemberRoster.GetElementCopyAtIndex(i);
                            if (el.Character != null && !el.Character.IsHero && el.Number > 0)
                            {
                                mainParty.MemberRoster.AddToCounts(el.Character, -el.Number, false, -el.WoundedNumber);
                            }
                        }
                        if (mainHero != null)
                        {
                            Services.RosterSyncService.NormalizePartyHeroRoster(mainParty, mainHero);
                        }
                    }
                    mainParty.PrisonRoster?.Clear();
                    mainParty.ItemRoster?.Clear();
                    mainParty.StringId = $"cc_party_respawn_{peerId}_{Environment.TickCount}";
                    mainParty.IsVisible = true;
                    mainParty.SetMoveModeHold();
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] PrepareRespawnAfterDeath client reset error: {ex.Message}");
            }

            NetworkObjectRegistry.Instance.Clear();
            _isCharacterInitialized = false;
            _creationPromptShown = false;
        }

        /// <summary>
        /// Broadcasts PlayerHeroCreatedPacket for EVERY active player party in the server world
        /// so late-joining or reconnecting clients immediately create and see all other players at their live positions!
        /// </summary>
        public void BroadcastAllExistingPlayers()
        {
            if (!ModContext.IsServerRunning || TaleWorlds.CampaignSystem.Campaign.Current == null || ModContext.Server == null) return;

            try
            {
                foreach (var party in MobileParty.All)
                {
                    if (party == null || !party.IsActive || !NetworkObjectRegistry.IsPlayerNetworkParty(party)) continue;
                    if (!NetworkObjectRegistry.TryExtractPeerId(party.StringId, out int peerId) || peerId <= 0) continue;

                    var hero = party.LeaderHero ?? NetworkObjectRegistry.Instance.ResolvePlayerHero(peerId);
                    if (hero == null || hero.IsDead) continue;

                    var clan = hero.Clan ?? NetworkObjectRegistry.Instance.ResolveClan(NetworkObjectRegistry.GetPlayerClanId(peerId));
                    var pos = party.GetPosition2D;

                    var syncPacket = new PlayerHeroCreatedPacket
                    {
                        PeerId = peerId,
                        HeroName = hero.Name?.ToString() ?? $"Oyuncu_{peerId}",
                        ClanName = clan?.Name?.ToString() ?? "Oyuncu Klanı",
                        CultureId = hero.Culture?.StringId ?? "empire",
                        BannerKey = clan?.Banner?.Serialize() ?? string.Empty,
                        StartingSettlementId = hero.HomeSettlement?.StringId ?? string.Empty,
                        StartingGold = hero.Gold,
                        StartingTroops = party.MemberRoster?.TotalRegulars ?? 0,
                        EquipmentCsv = SerializeAllEquipments(hero),
                        CurrentX = pos.X,
                        CurrentY = pos.Y,
                        IsFemale = hero.IsFemale,
                        BodyPropertiesStr = hero.BodyProperties.ToString(),
                        IsAtSea = party.IsCurrentlyAtSea,
                        NavigationType = (byte)party.DesiredAiNavigationType
                    };

                    ModContext.Server.Broadcast(syncPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);

                    if (party != null && party.IsActive)
                    {
                        Services.RosterSyncService.NormalizePartyHeroRoster(party, hero);
                        if (party.MemberRoster == null || party.MemberRoster.TotalRegulars == 0)
                        {
                            Services.RosterSyncService.Instance.ApplyCachedRosterIfExists(peerId, party);
                        }
                    }

                    // Always prefer the authoritative client-reported cached roster over rebuilding from server puppet party
                    var rosterPacket = Services.RosterSyncService.Instance.GetCachedRoster(peerId)
                                       ?? Services.RosterSyncService.Instance.BuildRosterPacket(peerId, hero, party);
                    ModContext.Server.Broadcast(rosterPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }

                Services.NavalSyncService.Instance.BroadcastAllExistingPlayerFleets();
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] BroadcastAllExistingPlayers hatası: {ex.Message}");
            }
        }

        public static bool ApplyAppearance(Hero? hero, bool isFemale, string? bodyPropertiesStr)
        {
            if (hero == null) return false;
            bool changed = false;
            try
            {
                if (hero.IsFemale != isFemale)
                {
                    hero.IsFemale = isFemale;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(bodyPropertiesStr) && BodyProperties.FromString(bodyPropertiesStr, out BodyProperties bp))
                {
                    hero.StaticBodyProperties = bp.StaticProperties;
                    hero.Weight = bp.Weight;
                    hero.Build = bp.Build;
                    changed = true;
                }
            }
            catch { }
            return changed;
        }

        private readonly Dictionary<int, (string HeroId, string PartyId)> _deferredDespawns = new Dictionary<int, (string, string)>();

        private static bool IsUnsafeToDestroyPartyNow(MobileParty? party)
        {
            if (TaleWorlds.MountAndBlade.Mission.Current != null)
            {
                return true;
            }

            if (party != null && party.MapEvent != null)
            {
                return true;
            }

            var enc = TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current;
            if (enc != null)
            {
                if (party == null) return true;
                if (TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.EncounteredParty == party.Party ||
                    (TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Battle?.InvolvedParties != null &&
                     TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Battle.InvolvedParties.Contains(party.Party)))
                {
                    return true;
                }
            }

            return TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent != null;
        }

        public void ProcessDeferredDespawns(WorldSyncManager? worldSync = null)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null)
            {
                lock (_deferredDespawns) { _deferredDespawns.Clear(); }
                return;
            }

            if (TaleWorlds.CampaignSystem.Campaign.Current.SaveHandler?.IsSaving == true ||
                TaleWorlds.MountAndBlade.Mission.Current != null ||
                TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current != null ||
                TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent != null)
            {
                return;
            }

            List<KeyValuePair<int, (string HeroId, string PartyId)>> pending;
            lock (_deferredDespawns)
            {
                if (_deferredDespawns.Count == 0) return;
                pending = _deferredDespawns.ToList();
                _deferredDespawns.Clear();
            }

            foreach (var kvp in pending)
            {
                int peerId = kvp.Key;
                string heroId = kvp.Value.HeroId;
                string partyId = kvp.Value.PartyId;

                try
                {
                    WorldSyncManager.MarkPartyDisconnected(partyId);
                    worldSync?.RemoveRemotePlayerState(partyId);

                    var hero = NetworkObjectRegistry.Instance.ResolveHero(heroId);
                    var party = NetworkObjectRegistry.Instance.ResolveParty(partyId) ?? hero?.PartyBelongedTo;

                    if (ModContext.IsServerRunning && party != null && hero != null && party.IsActive)
                    {
                        CaptureDisconnectedPartySnapshot(hero, party);
                    }

                    DespawnPartySafely(party, hero);

                    if (MobileParty.All != null)
                    {
                        foreach (var extra in MobileParty.All.Where(p => p != null && p != party && !p.IsMainParty && string.Equals(p.StringId, partyId, StringComparison.Ordinal)).ToList())
                        {
                            DespawnPartySafely(extra, hero);
                        }
                    }

                    NetworkObjectRegistry.Instance.UnregisterPlayerParty(peerId);
                    worldSync?.RemoveRemotePlayerState(partyId);
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ProcessDeferredDespawns error ({peerId}): {ex.Message}");
                }
            }
        }

        public void HandlePeerDisconnected(int peerId, WorldSyncManager? worldSync = null)
        {
            if (peerId <= 0) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (!ModContext.IsServerRunning && peerId == (ModContext.Client?.LocalPeerId ?? -1))
                    {
                        return;
                    }

                    Services.PlayerInteractionService.Instance.HandlePeerDisconnected(peerId);
                    Services.BattleSyncService.Instance.HandlePeerDisconnected(peerId);

                    if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

                    string heroId = NetworkObjectRegistry.GetPlayerHeroId(peerId);
                    string partyId = NetworkObjectRegistry.GetPlayerPartyId(peerId);
                    WorldSyncManager.MarkPartyDisconnected(partyId);
                    worldSync?.RemoveRemotePlayerState(partyId);

                    var hero = NetworkObjectRegistry.Instance.ResolveHero(heroId);
                    var party = NetworkObjectRegistry.Instance.ResolveParty(partyId) ?? hero?.PartyBelongedTo;

                    if (ModContext.IsServerRunning && party != null && hero != null)
                    {
                        CaptureDisconnectedPartySnapshot(hero, party);
                    }

                    // NEVER destroy a MobileParty while Mission / PlayerEncounter / MapEvent is still active!
                    // Doing so mid-mission crashes native MapEvent / BattleEndLogic teardown.
                    if (IsUnsafeToDestroyPartyNow(party))
                    {
                        lock (_deferredDespawns)
                        {
                            _deferredDespawns[peerId] = (heroId, partyId);
                        }
                        if (party != null)
                        {
                            try
                            {
                                party.SetMoveModeHold();
                                party.Ai?.SetDoNotMakeNewDecisions(true);
                            }
                            catch { }
                        }
                        return;
                    }

                    DespawnPartySafely(party, hero);

                    // Ensure no duplicate/unregistered party with the same deterministic partyId lingers on the map as "0"
                    if (MobileParty.All != null)
                    {
                        foreach (var extra in MobileParty.All.Where(p => p != null && p != party && !p.IsMainParty && string.Equals(p.StringId, partyId, StringComparison.Ordinal)).ToList())
                        {
                            DespawnPartySafely(extra, hero);
                        }
                    }

                    NetworkObjectRegistry.Instance.UnregisterPlayerParty(peerId);
                    worldSync?.RemoveRemotePlayerState(partyId);
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] HandlePeerDisconnected error ({peerId}): {ex.Message}");
                }
            });
        }

        private void CaptureDisconnectedPartySnapshot(Hero hero, MobileParty party)
        {
            try
            {
                var pos = party.GetPosition2D;
                var snap = new DisconnectedPartySnapshot
                {
                    PosX = pos.IsValid ? pos.X : 0f,
                    PosY = pos.IsValid ? pos.Y : 0f,
                    IsAtSea = party.IsCurrentlyAtSea,
                    SettlementId = party.CurrentSettlement?.StringId ?? string.Empty
                };

                if (party.MemberRoster != null)
                {
                    for (int i = 0; i < party.MemberRoster.Count; i++)
                    {
                        var elem = party.MemberRoster.GetElementCopyAtIndex(i);
                        if (elem.Character != null && !elem.Character.IsHero && elem.Number > 0)
                        {
                            snap.Members.Add((elem.Character.StringId, elem.Number, elem.WoundedNumber));
                        }
                    }
                }

                if (party.ItemRoster != null)
                {
                    for (int i = 0; i < party.ItemRoster.Count; i++)
                    {
                        var elem = party.ItemRoster.GetElementCopyAtIndex(i);
                        var item = elem.EquipmentElement.Item;
                        if (item != null && elem.Amount > 0)
                        {
                            string modId = elem.EquipmentElement.ItemModifier?.StringId ?? string.Empty;
                            snap.Items.Add((item.StringId, modId, elem.Amount));
                        }
                    }
                }

                _disconnectedPartySnapshots[hero.StringId] = snap;
            }
            catch { }
        }

        private void RestoreDisconnectedPartySnapshot(Hero hero, MobileParty party)
        {
            if (!_disconnectedPartySnapshots.TryGetValue(hero.StringId, out var snap)) return;
            _disconnectedPartySnapshots.Remove(hero.StringId);

            try
            {
                if (party.MemberRoster != null && snap.Members.Count > 0)
                {
                    for (int i = party.MemberRoster.Count - 1; i >= 0; i--)
                    {
                        var elem = party.MemberRoster.GetElementCopyAtIndex(i);
                        if (elem.Character != null && !elem.Character.IsHero)
                        {
                            party.MemberRoster.AddToCounts(elem.Character, -elem.Number, false, -elem.WoundedNumber);
                        }
                    }
                    foreach (var m in snap.Members)
                    {
                        var troop = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<CharacterObject>(m.TroopId);
                        if (troop != null)
                        {
                            party.MemberRoster.AddToCounts(troop, m.Count, false, Math.Min(m.Count, m.Wounded));
                        }
                    }
                }

                if (party.ItemRoster != null && snap.Items.Count > 0)
                {
                    party.ItemRoster.Clear();
                    foreach (var it in snap.Items)
                    {
                        var itemObj = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<ItemObject>(it.ItemId);
                        if (itemObj != null && it.Count > 0)
                        {
                            var modObj = !string.IsNullOrEmpty(it.ModifierId)
                                ? TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<ItemModifier>(it.ModifierId)
                                : null;
                            party.ItemRoster.AddToCounts(new EquipmentElement(itemObj, modObj), it.Count);
                        }
                    }
                }
            }
            catch { }
        }

        private static void DespawnPartySafely(MobileParty? party, Hero? hero)
        {
            if (party != null && !party.IsMainParty)
            {
                if (!string.IsNullOrEmpty(party.StringId))
                {
                    WorldSyncManager.MarkPartyDisconnected(party.StringId);
                }

                try
                {
                    if (party.CurrentSettlement != null)
                    {
                        LeaveSettlementAction.ApplyForParty(party);
                    }
                }
                catch { }

                try
                {
                    if (party.Army != null)
                    {
                        party.Army = null;
                    }
                    if (party.AttachedTo != null)
                    {
                        party.AttachedTo = null;
                    }
                    if (party.BesiegerCamp != null)
                    {
                        party.BesiegerCamp = null;
                    }
                }
                catch { }

                try
                {
                    if (party.MapEvent != null && party.Party != null)
                    {
                        party.Party.MapEventSide = null;
                    }
                }
                catch { }

                try
                {
                    party.SetMoveModeHold();
                    party.Ai?.SetDoNotMakeNewDecisions(true);
                    if (IsUnsafeToDestroyPartyNow(party))
                    {
                        return;
                    }
                    party.IsVisible = false;
                    party.IsInspected = false;
                    if (party.IsActive)
                    {
                        DestroyPartyAction.Apply(null, party);
                    }
                    party.IsVisible = false;
                    party.IsInspected = false;
                    party.Party?.SetVisualAsDirty();
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] DespawnPartySafely error ({party.StringId}): {ex.Message}");
                    try
                    {
                        party.IsVisible = false;
                        party.IsInspected = false;
                        party.Party?.SetVisualAsDirty();
                    }
                    catch { }
                }
            }

            if (hero != null)
            {
                try
                {
                    if (hero.StayingInSettlement != null)
                    {
                        hero.StayingInSettlement = null;
                    }
                    if (hero.IsFugitive || hero.IsPrisoner)
                    {
                        hero.ChangeState(Hero.CharacterStates.Active);
                    }
                }
                catch { }
            }
        }

        private static MobileParty CreatePlayerMobileParty(
            Hero hero,
            string partyId,
            Settlement settlement,
            CampaignVec2 spawnPos,
            bool isAtSea,
            int startingTroops)
        {
            WorldSyncManager.ClearPartyDisconnected(partyId);
            if (MobileParty.All != null)
            {
                foreach (var staleParty in MobileParty.All.Where(p => p != null && !p.IsMainParty && string.Equals(p.StringId, partyId, StringComparison.Ordinal)).ToList())
                {
                    DespawnPartySafely(staleParty, null);
                }
                WorldSyncManager.ClearPartyDisconnected(partyId);
            }

            var initArgs = new LordPartyComponent.InitializationArgs(spawnPos, 1f, settlement);
            MobileParty newParty;
            var lordCompCtor = typeof(LordPartyComponent).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(Hero), typeof(Hero), typeof(LordPartyComponent.InitializationArgs) },
                null);

            if (lordCompCtor != null)
            {
                var comp = (PartyComponent)lordCompCtor.Invoke(new object[] { hero, hero, initArgs });
                newParty = MobileParty.CreateParty(partyId, comp);
                newParty.StringId = partyId;
                newParty.InitializeMobilePartyAtPosition(spawnPos);
            }
            else
            {
                newParty = LordPartyComponent.CreateLordParty(partyId, hero, spawnPos, 1f, settlement, hero);
                newParty.StringId = partyId;
            }

            // Strip any AI DefaultPartyTemplate troops spawned by LordPartyComponent.InitializeLordPartyProperties
            // and normalize hero count to EXACTLY 1 (never 2!) before applying authoritative cached roster.
            if (newParty.MemberRoster != null)
            {
                for (int i = newParty.MemberRoster.Count - 1; i >= 0; i--)
                {
                    var elem = newParty.MemberRoster.GetElementCopyAtIndex(i);
                    if (elem.Character != null && !elem.Character.IsHero && elem.Number > 0)
                    {
                        newParty.MemberRoster.AddToCounts(elem.Character, -elem.Number, false, -elem.WoundedNumber);
                    }
                }
            }
            Services.RosterSyncService.NormalizePartyHeroRoster(newParty, hero);

            newParty.SetLandNavigationAccess(true);
            if (isAtSea)
            {
                try
                {
                    newParty.IsCurrentlyAtSea = true;
                    newParty.SetNavalVisualAsDirty();
                }
                catch { }
            }
            newParty.IsVisible = true;
            newParty.IsInspected = true;
            newParty.ForceAiNoPathMode = false;
            newParty.Ai?.SetDoNotMakeNewDecisions(false);
            newParty.Ai?.EnableAi();
            newParty.SetMoveModeHold();

            if (DefaultItems.Grain != null && newParty.ItemRoster.GetItemNumber(DefaultItems.Grain) < 10)
            {
                newParty.ItemRoster.AddToCounts(DefaultItems.Grain, 50);
            }

            int extractedPeerId = 0;
            bool hasCachedRoster = NetworkObjectRegistry.TryExtractPeerId(partyId, out extractedPeerId) &&
                                   extractedPeerId > 0 &&
                                   Services.RosterSyncService.Instance.ApplyCachedRosterIfExists(extractedPeerId, newParty);

            if (!hasCachedRoster && startingTroops > 0 && hero.Culture?.BasicTroop != null && newParty.MemberRoster != null && newParty.MemberRoster.TotalRegulars == 0)
            {
                newParty.MemberRoster.AddToCounts(hero.Culture.BasicTroop, startingTroops);
            }

            newParty.Party?.SetVisualAsDirty();
            newParty.Party?.SetLevelMaskIsDirty();
            return newParty;
        }

        public static string SerializeBattleEquipment(Hero? hero) => SerializeAllEquipments(hero);

        public static bool ApplyBattleEquipment(Hero? hero, string? equipmentCsv) => ApplyAllEquipments(hero, equipmentCsv);

        public static string SerializeAllEquipments(Hero? hero)
        {
            if (hero?.BattleEquipment == null) return string.Empty;
            string battle = EncodeEquipmentSlots(hero.BattleEquipment);
            string civilian = hero.CivilianEquipment != null ? EncodeEquipmentSlots(hero.CivilianEquipment) : "__EMPTY__";
            return $"B={battle}|C={civilian}";
        }

        private static string EncodeEquipmentSlots(Equipment eq)
        {
            var parts = new List<string>(12);
            for (int i = 0; i < (int)EquipmentIndex.NumEquipmentSetSlots; i++)
            {
                var idx = (EquipmentIndex)i;
                var elem = eq[idx];
                if (!elem.IsEmpty && elem.Item != null && !string.IsNullOrEmpty(elem.Item.StringId))
                {
                    string modId = elem.ItemModifier != null && !string.IsNullOrEmpty(elem.ItemModifier.StringId)
                        ? elem.ItemModifier.StringId
                        : string.Empty;
                    parts.Add(string.IsNullOrEmpty(modId)
                        ? $"{i}:{elem.Item.StringId}"
                        : $"{i}:{elem.Item.StringId}@{modId}");
                }
            }
            return parts.Count > 0 ? string.Join(",", parts) : "__EMPTY__";
        }

        public static bool ApplyAllEquipments(Hero? hero, string? payload)
        {
            if (hero?.BattleEquipment == null || TaleWorlds.CampaignSystem.Campaign.Current == null) return false;
            if (string.IsNullOrWhiteSpace(payload)) return false;

            bool changed = false;
            if (payload!.IndexOf("B=", StringComparison.Ordinal) >= 0)
            {
                var segments = payload.Split('|');
                for (int s = 0; s < segments.Length; s++)
                {
                    string seg = segments[s];
                    if (seg.StartsWith("B=", StringComparison.Ordinal))
                    {
                        if (DecodeAndApplyEquipment(hero.BattleEquipment, seg.Substring(2)))
                        {
                            changed = true;
                        }
                    }
                    else if (seg.StartsWith("C=", StringComparison.Ordinal) && hero.CivilianEquipment != null)
                    {
                        if (DecodeAndApplyEquipment(hero.CivilianEquipment, seg.Substring(2)))
                        {
                            changed = true;
                        }
                    }
                }
            }
            else
            {
                // Backwards compatibility with legacy comma-separated BattleEquipment strings
                if (DecodeAndApplyEquipment(hero.BattleEquipment, payload))
                {
                    changed = true;
                }
            }

            return changed;
        }

        private static bool DecodeAndApplyEquipment(Equipment targetEq, string? slotData)
        {
            if (targetEq == null || TaleWorlds.CampaignSystem.Campaign.Current == null || string.IsNullOrWhiteSpace(slotData))
            {
                return false;
            }

            var parsedSlots = new Dictionary<int, EquipmentElement>();
            if (!string.Equals(slotData, "__EMPTY__", StringComparison.Ordinal))
            {
                var entries = slotData!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                var objMgr = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager;
                foreach (var entry in entries)
                {
                    int colon = entry.IndexOf(':');
                    if (colon <= 0) continue;
                    if (!int.TryParse(entry.Substring(0, colon), out int slot) || slot < 0 || slot >= (int)EquipmentIndex.NumEquipmentSetSlots) continue;

                    string itemPart = entry.Substring(colon + 1);
                    string itemId = itemPart;
                    string modId = string.Empty;
                    int at = itemPart.IndexOf('@');
                    if (at > 0)
                    {
                        itemId = itemPart.Substring(0, at);
                        modId = itemPart.Substring(at + 1);
                    }

                    var item = objMgr.GetObject<ItemObject>(itemId);
                    if (item != null)
                    {
                        var mod = !string.IsNullOrEmpty(modId) ? objMgr.GetObject<ItemModifier>(modId) : null;
                        parsedSlots[slot] = new EquipmentElement(item, mod);
                    }
                }
            }

            bool changed = false;
            for (int i = 0; i < (int)EquipmentIndex.NumEquipmentSetSlots; i++)
            {
                var idx = (EquipmentIndex)i;
                if (!parsedSlots.TryGetValue(i, out var desiredElem))
                {
                    if (!targetEq[idx].IsEmpty)
                    {
                        targetEq[idx] = EquipmentElement.Invalid;
                        changed = true;
                    }
                }
                else if (!targetEq[idx].IsEqualTo(desiredElem))
                {
                    targetEq[idx] = desiredElem;
                    changed = true;
                }
            }

            return changed;
        }
    }
}
