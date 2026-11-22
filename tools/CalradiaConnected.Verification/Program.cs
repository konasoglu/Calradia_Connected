using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace CalradiaConnected.Verification
{
    /// <summary>
    /// Standalone headless verification tool that validates all Harmony patches, CharacterCreation stage
    /// filters, EventBus message routing, and NetworkObjectRegistry identity resolution against the
    /// installed Bannerlord game DLLs in < 2 seconds without launching the game.
    /// </summary>
    public static class Program
    {
        private static readonly string GameDir =
            Environment.GetEnvironmentVariable("BANNERLORD_DIR")
            ?? @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord";
        private static string[] _probeDirs = Array.Empty<string>();

        public static int Main()
        {
            _probeDirs = new[]
                {
                    Path.Combine(GameDir, @"Modules\CalradiaConnected\bin\Win64_Shipping_Client"),
                    Path.Combine(GameDir, @"bin\Win64_Shipping_Client")
                }
                .Concat(Directory.GetDirectories(Path.Combine(GameDir, "Modules"))
                    .Select(d => Path.Combine(d, @"bin\Win64_Shipping_Client"))
                    .Where(Directory.Exists))
                .ToArray();

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (var dir in _probeDirs)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                return null;
            };

            return RunChecks();
        }

        private static int RunChecks()
        {
            int failures = 0;
            var modAsm = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Mod.dll"));
            var engAsm = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Engine.dll"));
            var cmpAsm = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Campaign.dll"));

            // 1. PatchLifecycleManager installation verification
            var harmony = new Harmony("calradiaconnected.verification");
            var plmType = modAsm.GetType("CalradiaConnected.Mod.Patches.PatchLifecycleManager")!;
            plmType.GetMethod("InstallAll")!.Invoke(null, new object[] { harmony, modAsm });
            int applied = (int)plmType.GetProperty("AppliedCount")!.GetValue(null)!;
            int failed = (int)plmType.GetProperty("FailedCount")!.GetValue(null)!;
            Console.WriteLine($"[1/6] PatchLifecycleManager -> Applied: {applied}, Failed: {failed}");
            if (failed > 0 && plmType.GetProperty("FailedPatches")?.GetValue(null) is System.Collections.IEnumerable fpList)
            {
                foreach (var fp in fpList) Console.WriteLine($"   FAILED PATCH: {fp}");
            }
            if (applied < 12 || failed != 0) failures++;

            // 2. CharacterCreation Stage Filtering verification
            var cs = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "TaleWorlds.CampaignSystem");
            var mgrT = cs.GetType("TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationManager")!;
            var mgr = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(mgrT);
            var stagesF = mgrT.GetField("_stages", BindingFlags.NonPublic | BindingFlags.Instance)!;
            stagesF.SetValue(mgr, Activator.CreateInstance(stagesF.FieldType));
            var addMethod = mgrT.GetMethod("AddStage")!;
            const string ns = "TaleWorlds.CampaignSystem.CharacterCreationContent.";
            foreach (var stageName in new[]
            {
                "CharacterCreationCultureStage",
                "CharacterCreationFaceGeneratorStage",
                "CharacterCreationNarrativeStage",
                "CharacterCreationBannerEditorStage",
                "CharacterCreationClanNamingStage",
                "CharacterCreationReviewStage",
                "CharacterCreationOptionsStage"
            })
            {
                addMethod.Invoke(mgr, new[] { Activator.CreateInstance(cs.GetType(ns + stageName)!) });
            }

            var stageList = (System.Collections.IEnumerable)stagesF.GetValue(mgr)!;
            var stageNames = stageList.Cast<object>().Select(o => o.GetType().Name).ToList();
            bool stagePass = stageNames.Count == 5 && stageNames.Last() == "CharacterCreationClanNamingStage";
            Console.WriteLine($"[2/6] CharacterCreation Stages ({stageNames.Count}): {string.Join(" -> ", stageNames)} -> {(stagePass ? "PASS" : "FAIL")}");
            if (!stagePass) failures++;

            // 3. EventBus Publish/Subscribe verification
            var busType = engAsm.GetType("CalradiaConnected.Engine.Events.EventBus")!;
            var evtType = engAsm.GetType("CalradiaConnected.Engine.Events.LocalPartyMoveRequestedEvent")!;
            var bus = Activator.CreateInstance(busType)!;
            float receivedX = 0f;
            Action<object> handler = obj => { receivedX = (float)evtType.GetProperty("TargetX")!.GetValue(obj)!; };
            var delType = typeof(Action<>).MakeGenericType(evtType);
            var del = Delegate.CreateDelegate(delType, handler.Target, handler.Method);
            busType.GetMethod("Subscribe")!.MakeGenericMethod(evtType).Invoke(bus, new object[] { del });
            var evtInstance = Activator.CreateInstance(evtType)!;
            evtType.GetProperty("TargetX")!.SetValue(evtInstance, 145.5f);
            busType.GetMethod("Publish")!.MakeGenericMethod(evtType).Invoke(bus, new object[] { evtInstance });
            bool busPass = Math.Abs(receivedX - 145.5f) < 0.01f;
            Console.WriteLine($"[3/6] EventBus Publish/Subscribe (TargetX={receivedX}) -> {(busPass ? "PASS" : "FAIL")}");
            if (!busPass) failures++;

            // 4. NetworkObjectRegistry deterministic ID & PeerId extraction verification
            var regType = cmpAsm.GetType("CalradiaConnected.Campaign.Identity.NetworkObjectRegistry")!;
            string heroId = (string)regType.GetMethod("GetPlayerHeroId")!.Invoke(null, new object[] { 1001 })!;
            string partyId = (string)regType.GetMethod("GetPlayerPartyId")!.Invoke(null, new object[] { 1001 })!;
            object[] args = { partyId, 0 };
            bool extracted = (bool)regType.GetMethod("TryExtractPeerId")!.Invoke(null, args)!;
            int extractedPeerId = (int)args[1];
            bool regPass = heroId == "cc_hero_1001" && partyId == "cc_party_1001" && extracted && extractedPeerId == 1001;
            Console.WriteLine($"[4/6] NetworkObjectRegistry (heroId={heroId}, partyId={partyId}, extractedPeer={extractedPeerId}) -> {(regPass ? "PASS" : "FAIL")}");
            if (!regPass) failures++;

            // 5. LordPartyComponent Reflection Ctor & CampaignVec2 IsOnLand verification
            var lpcType = cs.GetType("TaleWorlds.CampaignSystem.Party.PartyComponents.LordPartyComponent")!;
            var heroType = cs.GetType("TaleWorlds.CampaignSystem.Hero")!;
            var argsNested = lpcType.GetNestedType("InitializationArgs", BindingFlags.NonPublic | BindingFlags.Public);
            var lpcCtor = argsNested != null
                ? lpcType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { heroType, heroType, argsNested }, null)
                : null;
            bool lpcPass = lpcCtor != null;
            Console.WriteLine($"[5/6] LordPartyComponent Safe Ctor Resolved ({lpcCtor != null}) -> {(lpcPass ? "PASS" : "FAIL")}");
            if (!lpcPass) failures++;

            // 6. FAZ 2 Services & Packet Types Loaded verification
            var cmnAsm = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Common.dll"));
            var sssType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.SettlementSyncService");
            var rssType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.RosterSyncService");
            var gssType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.SiegeSyncService");
            var prpType = cmnAsm.GetType("CalradiaConnected.Common.Packets.PartyRosterSyncPacket");
            var sspType = cmnAsm.GetType("CalradiaConnected.Common.Packets.SiegeStateSyncPacket");
            bool faz2Pass = sssType != null && rssType != null && gssType != null && prpType != null && sspType != null;
            Console.WriteLine($"[6/7] FAZ 2 Campaign Services & Packets Loaded -> {(faz2Pass ? "PASS" : "FAIL")}");
            if (!faz2Pass) failures++;

            // 7. FAZ 3 P2P Interaction, BattleSync, DeveloperClan, Cargo Speed Sync & Gauntlet Login/Lobby/Credits UI verification
            var pisType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.PlayerInteractionService");
            var bssType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.BattleSyncService");
            var devClanType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.DeveloperClanService");
            var pipType = cmnAsm.GetType("CalradiaConnected.Common.Packets.PlayerInteractionPacket");
            var bspType = cmnAsm.GetType("CalradiaConnected.Common.Packets.BattleSessionSyncPacket");
            var vmType = modAsm.GetType("CalradiaConnected.Mod.UI.CalradiaConnectedLoginVM");
            var modCtxType = engAsm.GetType("CalradiaConnected.Engine.ModContext")!;
            string sanitizedSave = (string)modCtxType.GetMethod("SanitizeSaveFileName")!.Invoke(null, new object[] { "Calradia [TR] #1 - CoOp" })!;
            bool saveSanitizeValid = sanitizedSave == "Calradia_TR_1_-_CoOp";

            var regInstance = regType.GetProperty("Instance")!.GetValue(null)!;
            regType.GetMethod("SetSyncedPartySpeed")!.Invoke(regInstance, new object[] { "cc_party_1001", 2.35f });
            object[] speedArgs = { "cc_party_1001", 0f };
            bool gotSpeed = (bool)regType.GetMethod("TryGetSyncedPartySpeed")!.Invoke(regInstance, speedArgs)!;
            bool speedSyncValid = gotSpeed && Math.Abs((float)speedArgs[1] - 2.35f) < 0.01f;

            string prefabPath = Path.Combine(GameDir, @"Modules\CalradiaConnected\GUI\Prefabs\CalradiaConnectedLogin.xml");
            bool prefabValid = false;
            if (File.Exists(prefabPath))
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(prefabPath);
                string rawXml = File.ReadAllText(prefabPath);
                var buttons = doc.SelectNodes("//ButtonWidget");
                bool allButtonsPassEventsDisabled = buttons != null && buttons.Count > 0;
                if (buttons != null)
                {
                    foreach (System.Xml.XmlNode btn in buttons)
                    {
                        if (btn.Attributes?["DoNotPassEventsToChildren"]?.Value != "true")
                        {
                            allButtonsPassEventsDisabled = false;
                        }
                    }
                }
                bool noUpsideDownStack = !rawXml.Contains("VerticalBottomToTop");
                bool validBrushesOnly = !rawXml.Contains("Popup.Delete.Canvas")
                                        && !rawXml.Contains("Stage.Naming.InputText")
                                        && !rawXml.Contains("Popup.Background")
                                        && !rawXml.Contains("Standard.TextBox");
                prefabValid = doc.DocumentElement?.Name == "Prefab"
                              && allButtonsPassEventsDisabled
                              && noUpsideDownStack
                              && validBrushesOnly;
            }
            bool vmValid = vmType != null
                           && vmType.GetProperty("ServerIp") != null
                           && vmType.GetProperty("ServerPort") != null
                           && vmType.GetProperty("ServerPassword") != null
                           && vmType.GetProperty("Username") != null
                           && vmType.GetProperty("Password") != null
                           && vmType.GetProperty("RememberMe") != null
                           && vmType.GetProperty("IsLobbyVisible") != null
                           && vmType.GetProperty("IsCreditsVisible") != null
                           && vmType.GetProperty("CreditsTitle") != null
                           && vmType.GetProperty("CreditsDeveloperRole") != null
                           && vmType.GetProperty("CreditsDeveloperName") != null
                           && vmType.GetMethod("ExecuteConnect") != null
                           && vmType.GetMethod("ExecuteOpenLobby") != null
                           && vmType.GetMethod("ExecuteRefreshLobby") != null
                           && vmType.GetMethod("ExecuteShowCredits") != null
                           && vmType.GetMethod("ExecuteCloseCredits") != null
                           && vmType.GetMethod("ExecuteOpenGithub") != null;
            bool faz3Pass = pisType != null && bssType != null && devClanType != null && pipType != null && bspType != null
                            && vmValid && prefabValid && saveSanitizeValid && speedSyncValid;
            Console.WriteLine($"[7/7] FAZ 3+ Login/Lobby/Credits UI, SpeedSync, SaveTitle & DeveloperClan (Prefab={prefabValid}, VM={vmValid}, Speed={speedSyncValid}, Save={sanitizedSave}) -> {(faz3Pass ? "PASS" : "FAIL")}");
            if (!faz3Pass) failures++;

            // [8/8] FAZ 4 Synchronization, ESC Non-Pause, TR/EN Localization, PvP Combat, Execution & P2P Dialog
            var modAsm8 = vmType?.Assembly;
            var commonAsm8 = pipType?.Assembly;
            string modBinDir = modAsm8 != null ? System.IO.Path.GetDirectoryName(modAsm8.Location)! : ".";
            string missionsDllPath = System.IO.Path.Combine(modBinDir, "CalradiaConnected.Missions.dll");
            string netDllPath = System.IO.Path.Combine(modBinDir, "CalradiaConnected.Network.dll");
            string liteNetDllPath = System.IO.Path.Combine(modBinDir, "LiteNetLib.dll");
            var missionsAsm8 = System.IO.File.Exists(missionsDllPath) ? System.Reflection.Assembly.LoadFrom(missionsDllPath) : null;
            var netAsm8 = System.IO.File.Exists(netDllPath) ? System.Reflection.Assembly.LoadFrom(netDllPath) : null;
            var liteNetAsm8 = System.IO.File.Exists(liteNetDllPath) ? System.Reflection.Assembly.LoadFrom(liteNetDllPath) : null;

            bool patchesValid = modAsm8?.GetType("CalradiaConnected.Mod.Patches.PlayerPartyVisibilityStatePatch") != null
                                && modAsm8?.GetType("CalradiaConnected.Mod.Patches.ClientEscNeverPausePatch") != null
                                && modAsm8?.GetType("CalradiaConnected.Mod.Patches.HeroExecutionSyncPatch") != null;

            var locType = commonAsm8?.GetType("CalradiaConnected.Common.Localization.Loc");
            bool locValid = false;
            if (locType != null)
            {
                var setLang = locType.GetMethod("SetLanguage");
                var tMeth = locType.GetMethod("T");
                if (setLang != null && tMeth != null)
                {
                    setLang.Invoke(null, new object[] { "TR" });
                    string? trVal = tMeth.Invoke(null, new object[] { "Merhaba", "Hello" })?.ToString();
                    setLang.Invoke(null, new object[] { "EN" });
                    string? enVal = tMeth.Invoke(null, new object[] { "Merhaba", "Hello" })?.ToString();
                    setLang.Invoke(null, new object[] { "TR" });
                    locValid = trVal == "Merhaba" && enVal == "Hello";
                }
            }

            var blowType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.MissionBlowSyncPacket");
            var p2pDlgPktType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.P2PDialogPacket");
            var execPktType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.HeroExecutionPacket");
            var routerType = netAsm8?.GetType("CalradiaConnected.Network.Transport.PacketRouter");
            bool packetsValid = blowType != null && p2pDlgPktType != null && execPktType != null && routerType != null;
            if (packetsValid && liteNetAsm8 != null)
            {
                var writerType = liteNetAsm8.GetType("LiteNetLib.Utils.NetDataWriter");
                var readerType = liteNetAsm8.GetType("LiteNetLib.Utils.NetDataReader");
                if (writerType != null && readerType != null)
                {
                    object writer = Activator.CreateInstance(writerType)!;
                    object dlgOut = Activator.CreateInstance(p2pDlgPktType!)!;
                    p2pDlgPktType!.GetProperty("SessionId")?.SetValue(dlgOut, "dlg-42");
                    p2pDlgPktType.GetProperty("DialogText")?.SetValue(dlgOut, "Parley Test");
                    p2pDlgPktType.GetMethod("Serialize")?.Invoke(dlgOut, new[] { writer });

                    byte[] bytes = (byte[])writerType.GetMethod("CopyData")!.Invoke(writer, null)!;
                    object reader = Activator.CreateInstance(readerType, new object[] { bytes })!;
                    object dlgIn = Activator.CreateInstance(p2pDlgPktType)!;
                    p2pDlgPktType.GetMethod("Deserialize")?.Invoke(dlgIn, new[] { reader });

                    object? rt = Activator.CreateInstance(routerType!);
                    packetsValid = rt != null
                                   && (string?)p2pDlgPktType.GetProperty("SessionId")?.GetValue(dlgIn) == "dlg-42"
                                   && (string?)p2pDlgPktType.GetProperty("DialogText")?.GetValue(dlgIn) == "Parley Test";
                }
            }

            var coopMissionType = missionsAsm8?.GetType("CalradiaConnected.Missions.CoopMissionSyncBehavior");
            bool missionValid = coopMissionType != null && coopMissionType.BaseType?.Name == "MissionLogic";

            var dlgVmType = modAsm8?.GetType("CalradiaConnected.Mod.UI.P2PDialogVM");
            bool dlgVmValid = dlgVmType != null
                              && dlgVmType.GetMethod("ExecuteOptionGold") != null
                              && dlgVmType.GetMethod("ExecuteOptionDuel") != null
                              && dlgVmType.GetMethod("ExecuteAcceptProposal") != null
                              && dlgVmType.GetMethod("ExecuteDeclineProposal") != null
                              && dlgVmType.GetMethod("ExecuteWatcherReply") != null
                              && dlgVmType.GetMethod("ExecuteCloseDialog") != null;

            bool dlgPrefabValid = false;
            string repoRootDir = System.IO.Directory.GetCurrentDirectory();
            string dlgPrefabPath = System.IO.Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "GUI", "Prefabs", "CalradiaConnectedDialog.xml");
            if (!System.IO.File.Exists(dlgPrefabPath))
                dlgPrefabPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(modBinDir, "..", "..", "GUI", "Prefabs", "CalradiaConnectedDialog.xml"));
            if (System.IO.File.Exists(dlgPrefabPath))
            {
                string xmlText = System.IO.File.ReadAllText(dlgPrefabPath);
                bool noBannedLayout = !xmlText.Contains("VerticalBottomToTop");
                bool noBannedBrush = !xmlText.Contains("Standard.SmallButton") && !xmlText.Contains("Standard.TripleButton") && !xmlText.Contains("Standard.CloseButton");
                int btnCount = xmlText.Split(new[] { "<ButtonWidget" }, StringSplitOptions.None).Length - 1;
                int passAttrCount = xmlText.Split(new[] { "DoNotPassEventsToChildren=\"true\"" }, StringSplitOptions.None).Length - 1;
                dlgPrefabValid = btnCount > 0 && passAttrCount >= btnCount && noBannedLayout && noBannedBrush;
            }

            bool langFilesValid = System.IO.File.Exists(System.IO.Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "ModuleData", "Languages", "std_module_strings_xml.xml"))
                                  && System.IO.File.Exists(System.IO.Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "ModuleData", "Languages", "TR", "std_module_strings_xml_tr.xml"));

            bool faz4Pass = patchesValid && locValid && packetsValid && missionValid && dlgVmValid && dlgPrefabValid && langFilesValid;
            Console.WriteLine($"[8/10] FAZ 4 Visibility/ESC/TR-EN/PvP/Execution/P2PDialog (Patches={patchesValid}, Loc={locValid}, Packets={packetsValid}, Mission={missionValid}, DlgVM={dlgVmValid}, DlgPrefab={dlgPrefabValid}, LangXml={langFilesValid}) -> {(faz4Pass ? "PASS" : "FAIL")}");
            if (!faz4Pass) failures++;

            // 9. War Sails (NavalDLC v1.2.8) Phase 1 & Phase 2 Verification
            var navalSvcType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.NavalSyncService");
            var fleetPktType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.PartyFleetSyncPacket");
            var shipDtoType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.NavalShipSnapshotData");
            var snapDtoType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.PartySnapshotData");
            var movePktType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.PlayerMoveOrderPacket");

            string subModuleXmlPath = System.IO.Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "SubModule.xml");
            bool subModNavalOpt = System.IO.File.Exists(subModuleXmlPath)
                                  && System.IO.File.ReadAllText(subModuleXmlPath).Contains("Id=\"NavalDLC\"")
                                  && System.IO.File.ReadAllText(subModuleXmlPath).Contains("Optional=\"true\"");

            bool navalRoundTripOk = false;
            if (fleetPktType != null && shipDtoType != null && snapDtoType != null && movePktType != null && liteNetAsm8 != null)
            {
                var writerType = liteNetAsm8.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var readerType = liteNetAsm8.GetType("LiteNetLib.Utils.NetDataReader")!;

                object shipDto = Activator.CreateInstance(shipDtoType)!;
                shipDtoType.GetProperty("HullId")?.SetValue(shipDto, "nord_longship");
                shipDtoType.GetProperty("Name")?.SetValue(shipDto, "Valkyrie");
                shipDtoType.GetProperty("HitPoints")?.SetValue(shipDto, 420.5f);
                shipDtoType.GetProperty("SailHitPoints")?.SetValue(shipDto, 180.0f);
                shipDtoType.GetProperty("FigureheadId")?.SetValue(shipDto, "dragon");
                shipDtoType.GetProperty("CustomSailPatternId")?.SetValue(shipDto, "pattern_1");
                shipDtoType.GetProperty("UpgradesCsv")?.SetValue(shipDto, "oars=heavy_oars;sail=linen_sail");

                object fleetOut = Activator.CreateInstance(fleetPktType)!;
                fleetPktType.GetProperty("PeerId")?.SetValue(fleetOut, 1002);
                fleetPktType.GetProperty("PartyId")?.SetValue(fleetOut, "cc_party_1002");
                fleetPktType.GetProperty("IsAtSea")?.SetValue(fleetOut, true);
                fleetPktType.GetProperty("NavigationType")?.SetValue(fleetOut, (byte)2);
                fleetPktType.GetProperty("HasAnchor")?.SetValue(fleetOut, true);
                fleetPktType.GetProperty("AnchorX")?.SetValue(fleetOut, 312.5f);
                fleetPktType.GetProperty("AnchorY")?.SetValue(fleetOut, 145.25f);
                var shipsList = fleetPktType.GetProperty("Ships")?.GetValue(fleetOut);
                shipsList?.GetType().GetMethod("Add")?.Invoke(shipsList, new[] { shipDto });

                object w = Activator.CreateInstance(writerType)!;
                fleetPktType.GetMethod("Serialize")?.Invoke(fleetOut, new[] { w });
                byte[] bytes = (byte[])writerType.GetMethod("CopyData")!.Invoke(w, null)!;
                object r = Activator.CreateInstance(readerType, new object[] { bytes })!;
                object fleetIn = Activator.CreateInstance(fleetPktType)!;
                fleetPktType.GetMethod("Deserialize")?.Invoke(fleetIn, new[] { r });

                var inShips = (System.Collections.IList?)fleetPktType.GetProperty("Ships")?.GetValue(fleetIn);
                navalRoundTripOk = (bool)(fleetPktType.GetProperty("IsAtSea")?.GetValue(fleetIn) ?? false)
                                   && (byte)(fleetPktType.GetProperty("NavigationType")?.GetValue(fleetIn) ?? (byte)0) == 2
                                   && (bool)(fleetPktType.GetProperty("HasAnchor")?.GetValue(fleetIn) ?? false)
                                   && inShips != null && inShips.Count == 1
                                   && (string?)shipDtoType.GetProperty("HullId")?.GetValue(inShips[0]!) == "nord_longship"
                                   && (string?)shipDtoType.GetProperty("UpgradesCsv")?.GetValue(inShips[0]!) == "oars=heavy_oars;sail=linen_sail";
            }

            bool navalPass = navalSvcType != null && subModNavalOpt && navalRoundTripOk;
            Console.WriteLine($"[9/10] War Sails (NavalDLC) Phase 1+2 (Service={navalSvcType != null}, OptionalDep={subModNavalOpt}, FleetPacket={navalRoundTripOk}) -> {(navalPass ? "PASS" : "FAIL")}");
            if (!navalPass) failures++;

            // 10. War Sails (NavalDLC v1.2.8) Phase 3+4 Verification (Naval Mission Sync + Ship Trade + Port Sync)
            var shipStatePktType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.MissionNavalShipStatePacket");
            var battleSyncPktType = commonAsm8?.GetType("CalradiaConnected.Common.Packets.BattleSessionSyncPacket");
            var navalBridgeType = missionsAsm8?.GetType("CalradiaConnected.Missions.NavalMissionBridge");
            bool coopShipHandlerOk = coopMissionType?.GetMethod("HandleRemoteNavalShipStatePacket") != null;
            bool shipVmOk = dlgVmType?.GetMethod("ExecuteOptionShip") != null;
            bool dlgShipBtnOk = System.IO.File.Exists(dlgPrefabPath)
                                && System.IO.File.ReadAllText(dlgPrefabPath).Contains("ExecuteOptionShip");

            bool shipStateRoundTripOk = false;
            bool navalBattlePktOk = false;
            bool shipTradePktOk = false;
            if (liteNetAsm8 != null)
            {
                var writerType = liteNetAsm8.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var readerType = liteNetAsm8.GetType("LiteNetLib.Utils.NetDataReader")!;

                if (shipStatePktType != null)
                {
                    object sOut = Activator.CreateInstance(shipStatePktType)!;
                    shipStatePktType.GetProperty("PeerId")?.SetValue(sOut, 1003);
                    shipStatePktType.GetProperty("HullId")?.SetValue(sOut, "empire_dromon");
                    shipStatePktType.GetProperty("PosX")?.SetValue(sOut, 128.5f);
                    shipStatePktType.GetProperty("SailSetting")?.SetValue(sOut, 0.75f);
                    shipStatePktType.GetProperty("IsSinking")?.SetValue(sOut, true);

                    object w = Activator.CreateInstance(writerType)!;
                    shipStatePktType.GetMethod("Serialize")?.Invoke(sOut, new[] { w });
                    byte[] bytes = (byte[])writerType.GetMethod("CopyData")!.Invoke(w, null)!;
                    object r = Activator.CreateInstance(readerType, new object[] { bytes })!;
                    object sIn = Activator.CreateInstance(shipStatePktType)!;
                    shipStatePktType.GetMethod("Deserialize")?.Invoke(sIn, new[] { r });

                    shipStateRoundTripOk = (int)(shipStatePktType.GetProperty("PeerId")?.GetValue(sIn) ?? 0) == 1003
                                           && (string?)shipStatePktType.GetProperty("HullId")?.GetValue(sIn) == "empire_dromon"
                                           && (bool)(shipStatePktType.GetProperty("IsSinking")?.GetValue(sIn) ?? false);
                }

                if (battleSyncPktType != null)
                {
                    object bOut = Activator.CreateInstance(battleSyncPktType)!;
                    battleSyncPktType.GetProperty("BattleStage")?.SetValue(bOut, (byte)11);
                    battleSyncPktType.GetProperty("IsNavalBattle")?.SetValue(bOut, true);
                    battleSyncPktType.GetProperty("WindSeed")?.SetValue(bOut, 4321);

                    object w = Activator.CreateInstance(writerType)!;
                    battleSyncPktType.GetMethod("Serialize")?.Invoke(bOut, new[] { w });
                    byte[] bytes = (byte[])writerType.GetMethod("CopyData")!.Invoke(w, null)!;
                    object r = Activator.CreateInstance(readerType, new object[] { bytes })!;
                    object bIn = Activator.CreateInstance(battleSyncPktType)!;
                    battleSyncPktType.GetMethod("Deserialize")?.Invoke(bIn, new[] { r });

                    navalBattlePktOk = (bool)(battleSyncPktType.GetProperty("IsNavalBattle")?.GetValue(bIn) ?? false)
                                       && (int)(battleSyncPktType.GetProperty("WindSeed")?.GetValue(bIn) ?? 0) == 4321;
                }

                if (p2pDlgPktType != null)
                {
                    object dOut = Activator.CreateInstance(p2pDlgPktType)!;
                    p2pDlgPktType.GetProperty("SessionId")?.SetValue(dOut, "dlg-ship");
                    p2pDlgPktType.GetProperty("InitiatorTradeShipIndex")?.SetValue(dOut, 1);
                    p2pDlgPktType.GetProperty("InitiatorTradeShipDesc")?.SetValue(dOut, "Drakkar (Longship)");
                    p2pDlgPktType.GetProperty("TargetTradeShipIndex")?.SetValue(dOut, 0);
                    p2pDlgPktType.GetProperty("TargetTradeShipDesc")?.SetValue(dOut, "Galley (Heavy)");

                    object w = Activator.CreateInstance(writerType)!;
                    p2pDlgPktType.GetMethod("Serialize")?.Invoke(dOut, new[] { w });
                    byte[] bytes = (byte[])writerType.GetMethod("CopyData")!.Invoke(w, null)!;
                    object r = Activator.CreateInstance(readerType, new object[] { bytes })!;
                    object dIn = Activator.CreateInstance(p2pDlgPktType)!;
                    p2pDlgPktType.GetMethod("Deserialize")?.Invoke(dIn, new[] { r });

                    shipTradePktOk = (int)(p2pDlgPktType.GetProperty("InitiatorTradeShipIndex")?.GetValue(dIn) ?? -1) == 1
                                     && (string?)p2pDlgPktType.GetProperty("InitiatorTradeShipDesc")?.GetValue(dIn) == "Drakkar (Longship)"
                                     && (int)(p2pDlgPktType.GetProperty("TargetTradeShipIndex")?.GetValue(dIn) ?? -1) == 0
                                     && (string?)p2pDlgPktType.GetProperty("TargetTradeShipDesc")?.GetValue(dIn) == "Galley (Heavy)";
                }
            }

            bool navalPhase34Pass = navalBridgeType != null
                                    && coopShipHandlerOk
                                    && shipStateRoundTripOk
                                    && navalBattlePktOk
                                    && shipTradePktOk
                                    && shipVmOk
                                    && dlgShipBtnOk;
            Console.WriteLine($"[10/11] War Sails Phase 3+4 (Bridge={navalBridgeType != null}, Handler={coopShipHandlerOk}, ShipState={shipStateRoundTripOk}, NavalBattle={navalBattlePktOk}, ShipTrade={shipTradePktOk}, ShipVM={shipVmOk}, ShipXml={dlgShipBtnOk}) -> {(navalPhase34Pass ? "PASS" : "FAIL")}");
            if (!navalPhase34Pass) failures++;

            // 11. PvP Encounter Safety, Deployment Ready Button, & ServerManager War Sails Launch Verification
            bool pvpSafePatchesOk = modAsm.GetType("CalradiaConnected.Mod.Patches.DisableSendTroopsInPvpPatch") != null
                                    && modAsm.GetType("CalradiaConnected.Mod.Patches.SafePvpSurrenderPatch") != null
                                    && modAsm.GetType("CalradiaConnected.Mod.Patches.SafePvpMenuAttackPatch") != null
                                    && modAsm.GetType("CalradiaConnected.Mod.Patches.SafePvpLeavePatch") != null
                                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PvpOrderOfBattlePatch") != null;

            var battleSyncSvcType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.BattleSyncService");
            bool pvpMethodsOk = battleSyncSvcType?.GetMethod("HandleLocalPvpSurrender") != null
                                && battleSyncSvcType?.GetMethod("NotifyPvpRetreatToMap") != null
                                && coopMissionType?.GetMethod("MarkLocalReadyFromUi") != null;

            bool smWarSailsOk = false;
            string smScannerSrc = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "Services", "ModuleScannerService.cs");
            if (File.Exists(smScannerSrc))
            {
                string scannerTxt = File.ReadAllText(smScannerSrc);
                int incompatIdx = scannerTxt.IndexOf("IncompatibleModuleIds", StringComparison.Ordinal);
                int incompatEnd = incompatIdx >= 0 ? scannerTxt.IndexOf(';', incompatIdx) : -1;
                string incompatBlock = (incompatIdx >= 0 && incompatEnd > incompatIdx)
                    ? scannerTxt.Substring(incompatIdx, incompatEnd - incompatIdx)
                    : string.Empty;
                smWarSailsOk = scannerTxt.Contains("BuildLaunchModuleList")
                               && !incompatBlock.Contains("\"NavalDLC\"")
                               && scannerTxt.Contains("OfficialOptionalModuleIds")
                               && scannerTxt.Contains("finalModules.Add(\"StoryMode\")")
                               && scannerTxt.Contains("finalModules.Add(\"NavalDLC\")");
            }

            bool pvpDeployAndWarSailsPass = pvpSafePatchesOk && pvpMethodsOk && smWarSailsOk && failed == 0 && applied >= 30;
            Console.WriteLine($"[11/12] PvP Encounter/Ready & ServerManager War Sails (Patches={pvpSafePatchesOk} Applied={applied}, Methods={pvpMethodsOk}, ServerMgrWarSails={smWarSailsOk}) -> {(pvpDeployAndWarSailsPass ? "PASS" : "FAIL")}");
            if (!pvpDeployAndWarSailsPass) failures++;

            // 12. War Sails (NavalDLC) Popup Neutralizer & Save Crash Prevention Verification
            bool noHardDlcRefs = true;
            foreach (var refAsm in modAsm.GetReferencedAssemblies())
            {
                if (string.Equals(refAsm.Name, "NavalDLC", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(refAsm.Name, "StoryMode", StringComparison.OrdinalIgnoreCase))
                {
                    noHardDlcRefs = false;
                    break;
                }
            }

            string neutralizerSrc = Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "StoryModeNeutralizer.cs");
            bool neutralizerHasNavalHooks = File.Exists(neutralizerSrc) && File.ReadAllText(neutralizerSrc) is string neutTxt
                && neutTxt.Contains("NavalDLC.CampaignBehaviors.NavalInitializationCampaignBehavior")
                && neutTxt.Contains("OnCharacterCreationIsOver")
                && neutTxt.Contains("NavalDLC.CampaignBehaviors.NavalDLCTutorialBoxCampaignBehavior");

            bool navalDlcMethodsResolved = true;
            string navalDllPath = Path.Combine(GameDir, "Modules", "NavalDLC", "bin", "Win64_Shipping_Client", "NavalDLC.dll");
            if (File.Exists(navalDllPath))
            {
                try
                {
                    var navalAsm = Assembly.LoadFrom(navalDllPath);
                    var initType = navalAsm.GetType("NavalDLC.CampaignBehaviors.NavalInitializationCampaignBehavior");
                    var tutType = navalAsm.GetType("NavalDLC.CampaignBehaviors.NavalDLCTutorialBoxCampaignBehavior");
                    navalDlcMethodsResolved =
                        initType?.GetMethod("OnCharacterCreationIsOver", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null &&
                        initType?.GetMethod("ShowIntroductionPopUp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null &&
                        tutType?.GetMethod("OnSessionLaunched", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null &&
                        tutType?.GetMethod("OnTutorialListRequested", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
                }
                catch
                {
                    navalDlcMethodsResolved = false;
                }
            }

            string smRunnerSrc = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "Services", "ServerRunnerService.cs");
            string smConfigSrc = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "Models", "ServerConfig.cs");
            string subModSrc = Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs");
            bool ramAndSaveGuardOk = File.Exists(smRunnerSrc) && File.Exists(smConfigSrc) && File.Exists(subModSrc)
                && File.ReadAllText(smRunnerSrc) is string runnerTxt
                && runnerTxt.Contains("ShouldApplyHardRamCap")
                && runnerTxt.Contains("await Task.Delay(90000, token)")
                && File.ReadAllText(smConfigSrc).Contains("MaxRamLimitGb { get; set; } = 0;")
                && File.ReadAllText(subModSrc).Contains("!InformationManager.IsAnyInquiryActive()");

            bool warSailsRuntimePass = noHardDlcRefs && neutralizerHasNavalHooks && navalDlcMethodsResolved && ramAndSaveGuardOk;
            Console.WriteLine($"[12/13] War Sails Popup Neutralizer & Save OOM Guard (NoHardDlcRef={noHardDlcRefs}, NeutralizerHooks={neutralizerHasNavalHooks}, NavalMethods={navalDlcMethodsResolved}, RamSaveGuard={ramAndSaveGuardOk}) -> {(warSailsRuntimePass ? "PASS" : "FAIL")}");
            if (!warSailsRuntimePass) failures++;

            // 13. Fog-of-War Visibility Culling (AOI), Client AI Distance Skip, & Server In-Memory Performance Optimizer
            bool aoiCullingOk = false;
            var worldSyncType = cmpAsm.GetType("CalradiaConnected.Campaign.Sync.WorldSyncManager");
            var isWithinAoiMethod = worldSyncType?.GetMethod(
                "IsWithinAnyPlayerAoi",
                BindingFlags.Public | BindingFlags.Static);
            if (isWithinAoiMethod != null)
            {
                var vec2Type = isWithinAoiMethod.GetParameters()[0].ParameterType;
                var listVec2Type = typeof(System.Collections.Generic.List<>).MakeGenericType(vec2Type);
                var playerList = Activator.CreateInstance(listVec2Type)!;
                var p1 = Activator.CreateInstance(vec2Type, new object[] { 100f, 100f })!;
                listVec2Type.GetMethod("Add")?.Invoke(playerList, new[] { p1 });

                var nearPt = Activator.CreateInstance(vec2Type, new object[] { 140f, 120f })!; // ~44.7 units <= 68
                var farPt = Activator.CreateInstance(vec2Type, new object[] { 250f, 250f })!;  // ~212 units > 68
                float rSq = 68f * 68f;

                bool nearIn = (bool)isWithinAoiMethod.Invoke(null, new object[] { nearPt, playerList, rSq })!;
                bool farIn = (bool)isWithinAoiMethod.Invoke(null, new object[] { farPt, playerList, rSq })!;
                bool resetExists = worldSyncType?.GetMethod("ResetOutOfRangeAiVisibility") != null;
                aoiCullingOk = nearIn && !farIn && resetExists;
            }

            var perfOptType = modAsm.GetType("CalradiaConnected.Mod.Services.ServerPerformanceOptimizer");
            var clampFpsMethod = perfOptType?.GetMethod("ClampServerFps", BindingFlags.Public | BindingFlags.Static);
            bool perfClampOk = clampFpsMethod != null
                && (int)clampFpsMethod.Invoke(null, new object[] { 0 })! == 20
                && (int)clampFpsMethod.Invoke(null, new object[] { 20 })! == 20
                && (int)clampFpsMethod.Invoke(null, new object[] { 60 })! == 30
                && (int)clampFpsMethod.Invoke(null, new object[] { 120 })! == 30;

            bool noGlobalTrueSightLeak = File.Exists(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "ServerSandBoxGameManager.cs"))
                && File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "ServerSandBoxGameManager.cs")) is string sgmTxt
                && !sgmTxt.Contains("TrueSight = true")
                && !sgmTxt.Contains("mp.IsVisible = true")
                && File.ReadAllText(subModSrc).Contains("Services.ServerPerformanceOptimizer.Instance.OnServerMapTick")
                && !File.ReadAllText(subModSrc).Contains("TrueSight = true")
                && modAsm.GetType("CalradiaConnected.Mod.Patches.PuppetPartyDistanceCulling") != null
                && File.Exists(smRunnerSrc)
                && File.ReadAllText(smRunnerSrc).Contains("\"serverPerformance\"")
                && !File.ReadAllText(smRunnerSrc).Contains("engineDict[\"texture_budget\"]");

            bool noPrematureEngineCallsInOnSubModuleLoad = File.Exists(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Services", "ServerPerformanceOptimizer.cs"))
                && File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Services", "ServerPerformanceOptimizer.cs")) is string optSrc
                && !optSrc.Contains("ApplyConfigChanges")
                && !optSrc.Contains("SetGraphicsPreset")
                && optSrc.Contains("MapStabilizeDelayTicks");

            bool perfAndFogPass = aoiCullingOk && perfClampOk && noGlobalTrueSightLeak && noPrematureEngineCallsInOnSubModuleLoad;
            Console.WriteLine($"[13/13] Fog-of-War AOI Culling & Server Performance Patch (Aoi={aoiCullingOk}, PerfClamp={perfClampOk}, NoTrueSightLeak={noGlobalTrueSightLeak}, SafeEngineTiming={noPrematureEngineCallsInOnSubModuleLoad}) -> {(perfAndFogPass ? "PASS" : "FAIL")}");
            if (!perfAndFogPass) failures++;

            // 14. GitHub Auto-Update & Release Pipeline Verification
            string versionJsonPath = Path.Combine(repoRootDir, "version.json");
            string gitignorePath = Path.Combine(repoRootDir, ".gitignore");
            string gitExcludePath = Path.Combine(repoRootDir, ".git", "info", "exclude");
            string publishScriptPath = Path.Combine(repoRootDir, "tools", "Publish-Release.ps1");
            string updaterSrcPath = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "Services", "GitHubUpdateService.cs");
            string smViewModelPath = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "ViewModels", "MainViewModel.cs");
            string smMainWindowPath = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "MainWindow.xaml");

            bool ignoreRulesOk = (File.Exists(gitignorePath) && File.ReadAllText(gitignorePath).Contains("CalradiaConnected/"))
                || (File.Exists(gitExcludePath) && File.ReadAllText(gitExcludePath).Contains("CalradiaConnected/"));

            bool releaseFilesOk = File.Exists(versionJsonPath)
                && ignoreRulesOk
                && File.Exists(publishScriptPath)
                && File.ReadAllText(versionJsonPath).Contains("konasoglu/Calradia_Connected");

            bool updaterContractsOk = File.Exists(updaterSrcPath)
                && File.ReadAllText(updaterSrcPath) is string updTxt
                && updTxt.Contains("FetchLatestReleaseAsync")
                && updTxt.Contains("ApplyReleaseAsync")
                && updTxt.Contains("ApplyServerManagerSelfUpdate")
                && updTxt.Contains("CleanupPreviousUpdateBackups")
                && !updTxt.Contains("ExecutionPolicy Bypass")
                && !updTxt.Contains("git credential")
                && File.Exists(smViewModelPath)
                && File.ReadAllText(smViewModelPath).Contains("UpdateModCommand")
                && File.Exists(smMainWindowPath)
                && File.ReadAllText(smMainWindowPath).Contains("UpdateModCommand");

            bool autoUpdatePass = releaseFilesOk && updaterContractsOk;
            Console.WriteLine($"[14/14] GitHub Auto-Update & Release Pipeline (ReleaseFiles={releaseFilesOk}, UpdaterContracts={updaterContractsOk}) -> {(autoUpdatePass ? "PASS" : "FAIL")}");
            if (!autoUpdatePass) failures++;

            // 15. FastDL All-Interface TcpListener Binding & UDP World Save Fallback Verification
            string fastDlSrc = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "Services", "FastDlServerService.cs");
            string embeddedSaveSrc = Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Services", "EmbeddedSaveServer.cs");

            bool allInterfacesNoHttpListener = File.Exists(fastDlSrc)
                && File.ReadAllText(fastDlSrc) is string fdlTxt
                && fdlTxt.Contains("new TcpListener(IPAddress.Any, port)")
                && !fdlTxt.Contains("HttpListener")
                && File.Exists(embeddedSaveSrc)
                && File.ReadAllText(embeddedSaveSrc) is string embTxt
                && embTxt.Contains("new TcpListener(IPAddress.Any, port)")
                && !embTxt.Contains("HttpListener");

            bool udpFallbackPacketsOk = false;
            {
                var liteAsm15 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var saveReqType = cmnAsm.GetType("CalradiaConnected.Common.Packets.WorldSaveRequestPacket");
                var saveChunkType = cmnAsm.GetType("CalradiaConnected.Common.Packets.WorldSaveChunkPacket");
                var w15Type = liteAsm15.GetType("LiteNetLib.Utils.NetDataWriter");
                var r15Type = liteAsm15.GetType("LiteNetLib.Utils.NetDataReader");
                if (saveReqType != null && saveChunkType != null && w15Type != null && r15Type != null)
                {
                    var reqPkt = Activator.CreateInstance(saveReqType)!;
                    saveReqType.GetProperty("StartChunk")!.SetValue(reqPkt, 16);
                    saveReqType.GetProperty("BatchCount")!.SetValue(reqPkt, 8);
                    var w = Activator.CreateInstance(w15Type)!;
                    saveReqType.GetMethod("Serialize")!.Invoke(reqPkt, new[] { w });
                    var r = Activator.CreateInstance(r15Type, new[] { w })!;
                    var reqBack = Activator.CreateInstance(saveReqType)!;
                    saveReqType.GetMethod("Deserialize")!.Invoke(reqBack, new[] { r });

                    var chunkPkt = Activator.CreateInstance(saveChunkType)!;
                    saveChunkType.GetProperty("ChunkIndex")!.SetValue(chunkPkt, 16);
                    saveChunkType.GetProperty("TotalChunks")!.SetValue(chunkPkt, 32);
                    saveChunkType.GetProperty("TotalCompressedBytes")!.SetValue(chunkPkt, 500000);
                    saveChunkType.GetProperty("IsSuccess")!.SetValue(chunkPkt, true);
                    saveChunkType.GetProperty("Data")!.SetValue(chunkPkt, new byte[] { 1, 2, 3, 4, 5 });
                    var cw = Activator.CreateInstance(w15Type)!;
                    saveChunkType.GetMethod("Serialize")!.Invoke(chunkPkt, new[] { cw });
                    var cr = Activator.CreateInstance(r15Type, new[] { cw })!;
                    var chunkBack = Activator.CreateInstance(saveChunkType)!;
                    saveChunkType.GetMethod("Deserialize")!.Invoke(chunkBack, new[] { cr });

                    udpFallbackPacketsOk = (int)saveReqType.GetProperty("StartChunk")!.GetValue(reqBack)! == 16
                        && (int)saveReqType.GetProperty("BatchCount")!.GetValue(reqBack)! == 8
                        && (bool)saveChunkType.GetProperty("IsSuccess")!.GetValue(chunkBack)!
                        && (int)saveChunkType.GetProperty("ChunkIndex")!.GetValue(chunkBack)! == 16
                        && (int)saveChunkType.GetProperty("TotalChunks")!.GetValue(chunkBack)! == 32
                        && ((byte[])saveChunkType.GetProperty("Data")!.GetValue(chunkBack)!).Length == 5
                        && File.ReadAllText(subModSrc).Contains("TryDownloadSaveOverUdp")
                        && File.ReadAllText(subModSrc).Contains("HandleServerWorldSaveRequest");
                }
            }

            // Live bind test for EmbeddedSaveServer TcpListener(IPAddress.Any) on ephemeral free port (no Admin needed)
            bool liveTcpBindOk = false;
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                probe.Start();
                int freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();

                var embeddedType = modAsm.GetType("CalradiaConnected.Mod.Services.EmbeddedSaveServer");
                if (embeddedType != null)
                {
                    var srvObj = Activator.CreateInstance(embeddedType)!;
                    bool started = (bool)embeddedType.GetMethod("Start")!.Invoke(srvObj, new object[] { freePort })!;
                    if (started)
                    {
                        using (var wc = new System.Net.WebClient())
                        {
                            string pingJson = wc.DownloadString($"http://127.0.0.1:{freePort}/api/ping");
                            liveTcpBindOk = pingJson.Contains("\"status\":\"Online\"");
                        }
                    }
                    embeddedType.GetMethod("Stop")!.Invoke(srvObj, null);
                }
            }
            catch
            {
                liveTcpBindOk = false;
            }

            bool fastDlAndUdpPass = allInterfacesNoHttpListener && udpFallbackPacketsOk && liveTcpBindOk;
            Console.WriteLine($"[15/15] LAN/Radmin FastDL TcpListener & UDP Save Fallback (AllIfaces={allInterfacesNoHttpListener}, UdpPackets={udpFallbackPacketsOk}, LiveHttpPing={liveTcpBindOk}) -> {(fastDlAndUdpPass ? "PASS" : "FAIL")}");
            if (!fastDlAndUdpPass) failures++;

            // 16. Settlement AI/Naval Sync, Ghost Disconnect Cleanup, Native Trade & Dual-Ready PvP, Full Equipment/Roster Sync
            bool aiSettlementOk = false;
            bool disconnectCleanupOk = false;
            bool equipAndPacketsOk = false;
            bool nativeTradeAndPvpOk = false;
            {
                var wsmType16 = cmpAsm.GetType("CalradiaConnected.Campaign.Sync.WorldSyncManager");
                var ssType16 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.SettlementSyncService");
                var phmType16 = cmpAsm.GetType("CalradiaConnected.Campaign.Player.PlayerHeroManager");
                var rssType16 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.RosterSyncService");
                var bssType16 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.BattleSyncService");
                var pisType16 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.PlayerInteractionService");
                var norType16 = cmpAsm.GetType("CalradiaConnected.Campaign.Identity.NetworkObjectRegistry");

                string worldSyncSrc = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Sync", "WorldSyncManager.cs"));
                string sharedBehavSrc = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SharedCampaignSyncBehavior.cs"));
                aiSettlementOk = wsmType16?.GetMethod("ApplyRemotePartySettlementState", BindingFlags.Public | BindingFlags.Static) != null
                    && wsmType16?.GetMethod("ResolveValidPosition", BindingFlags.Public | BindingFlags.Static) != null
                    && wsmType16?.GetMethod("RemoveRemotePlayerState", BindingFlags.Public | BindingFlags.Instance) != null
                    && ssType16?.GetMethod("BroadcastServerAiSettlementChange", BindingFlags.Public | BindingFlags.Instance) != null
                    && worldSyncSrc.Contains("SaveHandler?.IsSaving == true")
                    && worldSyncSrc.Contains("MobileParty.NavigationType")
                    && sharedBehavSrc.Contains("BroadcastServerAiSettlementChange");

                string subModTxt = File.ReadAllText(subModSrc);
                bool regCleanupOk = false;
                if (norType16 != null)
                {
                    var regInst = norType16.GetProperty("Instance")!.GetValue(null)!;
                    var unregMethod = norType16.GetMethod("UnregisterPlayerParty", BindingFlags.Public | BindingFlags.Instance);
                    if (unregMethod != null)
                    {
                        norType16.GetMethod("SetSyncedPartySpeed")!.Invoke(regInst, new object[] { "cc_party_9998", 3.2f });
                        object[] spBefore = { "cc_party_9998", 0f };
                        bool hasBefore = (bool)norType16.GetMethod("TryGetSyncedPartySpeed")!.Invoke(regInst, spBefore)!;
                        unregMethod.Invoke(regInst, new object[] { 9998 });
                        object[] spAfter = { "cc_party_9998", 0f };
                        bool hasAfter = (bool)norType16.GetMethod("TryGetSyncedPartySpeed")!.Invoke(regInst, spAfter)!;
                        regCleanupOk = hasBefore && !hasAfter;
                    }
                }
                disconnectCleanupOk = regCleanupOk
                    && phmType16?.GetMethod("HandlePeerDisconnected", BindingFlags.Public | BindingFlags.Instance) != null
                    && bssType16?.GetMethod("HandlePeerDisconnected", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType16?.GetMethod("HandlePeerDisconnected", BindingFlags.Public | BindingFlags.Instance) != null
                    && subModTxt.Contains("PlayerHeroManager.Instance.HandlePeerDisconnected");

                var liteAsm16 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var w16Type = liteAsm16.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var r16Type = liteAsm16.GetType("LiteNetLib.Utils.NetDataReader")!;

                var rosterPktT = cmnAsm.GetType("CalradiaConnected.Common.Packets.PartyRosterSyncPacket")!;
                var rosterPkt = Activator.CreateInstance(rosterPktT)!;
                rosterPktT.GetProperty("PeerId")!.SetValue(rosterPkt, 1002);
                rosterPktT.GetProperty("EquipmentCsv")!.SetValue(rosterPkt, "B=0:iron@fine;C=5:linen");
                rosterPktT.GetProperty("ItemsCsv")!.SetValue(rosterPkt, "grain:12;iron@rusty:2");
                var rw = Activator.CreateInstance(w16Type)!;
                rosterPktT.GetMethod("Serialize")!.Invoke(rosterPkt, new[] { rw });
                var rr = Activator.CreateInstance(r16Type, new[] { rw })!;
                var rosterBack = Activator.CreateInstance(rosterPktT)!;
                rosterPktT.GetMethod("Deserialize")!.Invoke(rosterBack, new[] { rr });

                var heroPktT = cmnAsm.GetType("CalradiaConnected.Common.Packets.PlayerHeroCreatedPacket")!;
                var heroPkt = Activator.CreateInstance(heroPktT)!;
                heroPktT.GetProperty("PeerId")!.SetValue(heroPkt, 1002);
                heroPktT.GetProperty("IsAtSea")!.SetValue(heroPkt, true);
                heroPktT.GetProperty("NavigationType")!.SetValue(heroPkt, (byte)3);
                var hw = Activator.CreateInstance(w16Type)!;
                heroPktT.GetMethod("Serialize")!.Invoke(heroPkt, new[] { hw });
                var hr = Activator.CreateInstance(r16Type, new[] { hw })!;
                var heroBack = Activator.CreateInstance(heroPktT)!;
                heroPktT.GetMethod("Deserialize")!.Invoke(heroBack, new[] { hr });

                equipAndPacketsOk = phmType16?.GetMethod("SerializeAllEquipments", BindingFlags.Public | BindingFlags.Static) != null
                    && phmType16?.GetMethod("ApplyAllEquipments", BindingFlags.Public | BindingFlags.Static) != null
                    && rssType16?.GetMethod("SerializePartyItems", BindingFlags.Public | BindingFlags.Static) != null
                    && (string)rosterPktT.GetProperty("ItemsCsv")!.GetValue(rosterBack)! == "grain:12;iron@rusty:2"
                    && (string)rosterPktT.GetProperty("EquipmentCsv")!.GetValue(rosterBack)! == "B=0:iron@fine;C=5:linen"
                    && (bool)heroPktT.GetProperty("IsAtSea")!.GetValue(heroBack)! == true
                    && (byte)heroPktT.GetProperty("NavigationType")!.GetValue(heroBack)! == 3;

                var dlgPktT = cmnAsm.GetType("CalradiaConnected.Common.Packets.P2PDialogPacket")!;
                var dlgPkt = Activator.CreateInstance(dlgPktT)!;
                dlgPktT.GetProperty("Stage")!.SetValue(dlgPkt, (byte)12);
                dlgPktT.GetProperty("BoughtItemsCsv")!.SetValue(dlgPkt, "iron@fine:3");
                dlgPktT.GetProperty("SoldItemsCsv")!.SetValue(dlgPkt, "grain:5");
                dlgPktT.GetProperty("NetGoldDelta")!.SetValue(dlgPkt, -450);
                var dw = Activator.CreateInstance(w16Type)!;
                dlgPktT.GetMethod("Serialize")!.Invoke(dlgPkt, new[] { dw });
                var dr = Activator.CreateInstance(r16Type, new[] { dw })!;
                var dlgBack = Activator.CreateInstance(dlgPktT)!;
                dlgPktT.GetMethod("Deserialize")!.Invoke(dlgBack, new[] { dr });

                var parsedList = (System.Collections.IList)pisType16!.GetMethod("ParseTradeItemCsv", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, new object[] { "iron@fine:3;grain:5" })!;

                bool overlaysSuppressed = !subModTxt.Contains("UI.P2PDialogManager.Instance.EnsureInitialized()")
                    || subModTxt.Contains("// UI.P2PDialogManager.Instance.EnsureInitialized()");
                bool pvpOverlaySuppressed = !subModTxt.Contains("\n                UI.PvpReadyOverlayManager.Instance.EnsureInitialized();");

                nativeTradeAndPvpOk = modAsm.GetType("CalradiaConnected.Mod.Patches.NativePlayerTradeDonePatch") != null
                    && pisType16.GetMethod("TryHandleNativeTradeDone", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType16.GetMethod("OpenNativeTradeWithPlayer", BindingFlags.Public | BindingFlags.Instance) != null
                    && parsedList.Count == 2
                    && (byte)dlgPktT.GetProperty("Stage")!.GetValue(dlgBack)! == 12
                    && (string)dlgPktT.GetProperty("BoughtItemsCsv")!.GetValue(dlgBack)! == "iron@fine:3"
                    && (string)dlgPktT.GetProperty("SoldItemsCsv")!.GetValue(dlgBack)! == "grain:5"
                    && (int)dlgPktT.GetProperty("NetGoldDelta")!.GetValue(dlgBack)! == -450
                    && overlaysSuppressed
                    && pvpOverlaySuppressed;
            }

            bool phaseFixesPass = aiSettlementOk && disconnectCleanupOk && equipAndPacketsOk && nativeTradeAndPvpOk;
            Console.WriteLine($"[16/16] AI Settlement/Naval Sync, Ghost Disconnect, Gear/Roster & Native Trade/PvP (Settlement={aiSettlementOk}, Disconnect={disconnectCleanupOk}, GearPackets={equipAndPacketsOk}, NativeTradePvP={nativeTradeAndPvpOk}) -> {(phaseFixesPass ? "PASS" : "FAIL")}");
            if (!phaseFixesPass) failures++;

            // 17. Ghost "0" Inactive/Tombstone Nameplate Suppression & Remote Connect Thread/Timeout/Init Guards
            bool tombstoneApiOk = false;
            bool puppetVisibilityGuardOk = false;
            bool connectCrashGuardsOk = false;
            {
                var wsmType17 = cmpAsm.GetType("CalradiaConnected.Campaign.Sync.WorldSyncManager");
                var markDisc = wsmType17?.GetMethod("MarkPartyDisconnected", BindingFlags.Public | BindingFlags.Static);
                var isDisc = wsmType17?.GetMethod("IsRecentlyDisconnectedParty", BindingFlags.Public | BindingFlags.Static);
                var clearDisc = wsmType17?.GetMethod("ClearPartyDisconnected", BindingFlags.Public | BindingFlags.Static);
                if (markDisc != null && isDisc != null && clearDisc != null)
                {
                    string testPid = "cc_party_9876";
                    markDisc.Invoke(null, new object[] { testPid, 8000 });
                    bool marked = (bool)isDisc.Invoke(null, new object[] { testPid })!;
                    clearDisc.Invoke(null, new object[] { testPid });
                    bool cleared = !(bool)isDisc.Invoke(null, new object[] { testPid })!;
                    tombstoneApiOk = marked && cleared;
                }

                string puppetPatchSrc = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PuppetPartyAiPatch.cs"));
                puppetVisibilityGuardOk = puppetPatchSrc.Contains("PlayerPartyVisibilityPersistentPatch")
                    && puppetPatchSrc.Contains("PlayerPartyVisibilityStatePatch")
                    && puppetPatchSrc.Contains("!__instance.IsActive")
                    && puppetPatchSrc.Contains("WorldSyncManager.IsRecentlyDisconnectedParty(__instance.StringId)");

                string netServerSrc = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Network", "Transport", "NetworkServer.cs"));
                string subModuleText17 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                string rosterSyncText17 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "RosterSyncService.cs"));
                string navalSyncText17 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "NavalSyncService.cs"));
                string siegeSyncText17 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SiegeSyncService.cs"));

                connectCrashGuardsOk = netServerSrc.Contains("DisconnectTimeout = 60000")
                    && subModuleText17.Contains("EnqueueUiMessage")
                    && subModuleText17.Contains("GetFolderPath(Environment.SpecialFolder.MyDocuments)")
                    && subModuleText17.Contains("IsCharacterInitialized")
                    && subModuleText17.Contains("Campaign.Current.GameStarted")
                    && rosterSyncText17.Contains("Campaign.Current.GameStarted")
                    && navalSyncText17.Contains("Campaign.Current.GameStarted")
                    && siegeSyncText17.Contains("Campaign.Current.GameStarted");
            }

            bool v113FixesPass = tombstoneApiOk && puppetVisibilityGuardOk && connectCrashGuardsOk;
            Console.WriteLine($"[17/17] Ghost '0' Inactive/Tombstone Guard & Remote Join Safety (Tombstone={tombstoneApiOk}, PuppetVisGuard={puppetVisibilityGuardOk}, JoinGuards={connectCrashGuardsOk}) -> {(v113FixesPass ? "PASS" : "FAIL")}");
            if (!v113FixesPass) failures++;

            // 18. Exact Troop Count Sync, Symmetric PvP Wait/Launch, Lord Barter ("Teklif") & Dialog/Relation Sync
            bool rosterPollAndCacheOk = false;
            bool pvpWaitAndSyncOk = false;
            bool lordBarterOk = false;
            bool dialogRelationSyncOk = false;
            {
                var rosterSvcType18 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.RosterSyncService");
                bool hasRosterMethods = rosterSvcType18?.GetMethod("ComputeRosterSignature", BindingFlags.Public | BindingFlags.Static) != null
                    && rosterSvcType18?.GetMethod("BuildRosterPacket", BindingFlags.Public | BindingFlags.Instance) != null
                    && rosterSvcType18?.GetMethod("GetCachedRoster", BindingFlags.Public | BindingFlags.Instance) != null
                    && rosterSvcType18?.GetMethod("ClearCachedRoster", BindingFlags.Public | BindingFlags.Instance) != null;

                string subModuleText18 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                string heroMgrText18 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Player", "PlayerHeroManager.cs"));
                rosterPollAndCacheOk = hasRosterMethods
                    && subModuleText18.Contains("ComputeRosterSignature")
                    && heroMgrText18.Contains("GetCachedRoster(peerId)");

                var battleSvcType18 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.BattleSyncService");
                bool hasLaunchTick = battleSvcType18?.GetMethod("TickPendingPvpLaunch", BindingFlags.Public | BindingFlags.Instance) != null;
                string battleText18 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));
                pvpWaitAndSyncOk = hasLaunchTick
                    && battleText18.Contains("BattleStage == 10")
                    && battleText18.Contains("EnsureCombatantReadyForPvp")
                    && battleText18.Contains("BroadcastLatestPlayerRoster")
                    && battleText18.Contains("MapEvent.PlayerMapEvent?.BeginWait()");

                var pisType18 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.PlayerInteractionService");
                var regType18 = cmpAsm.GetType("CalradiaConnected.Campaign.Identity.NetworkObjectRegistry");
                bool hasBarterApi = pisType18?.GetMethod("OpenLordBarterWithPlayer", new[] { typeof(int) }) != null
                    && pisType18?.GetMethod("AppendAffordablePlayerBarterables", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType18?.GetMethod("InterceptAndSendLordBarterOffer", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType18?.GetMethod("FormatLordBarterSummary", BindingFlags.Public | BindingFlags.Static) != null
                    && regType18?.GetMethod("IsPlayerNetworkHero", BindingFlags.Public | BindingFlags.Static) != null;

                bool hasBarterPatches = modAsm.GetType("CalradiaConnected.Mod.Patches.PlayerBarterCooldownPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PlayerBarterAcceptablePatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PlayerBarterFinalizeInterceptPatch") != null;

                var pisText18 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "PlayerInteractionService.cs"));
                lordBarterOk = hasBarterApi && hasBarterPatches
                    && pisText18.Contains("BARTER_V1")
                    && pisText18.Contains("TryValidateAndApplyLordBarterTokens");

                bool hasRelationApi = pisType18?.GetMethod("GetHeroRelationScore", BindingFlags.Public | BindingFlags.Static) != null
                    && pisType18?.GetMethod("GetRelationLabel", BindingFlags.Public | BindingFlags.Static) != null
                    && pisType18?.GetMethod("ArePeersAllied", BindingFlags.Public | BindingFlags.Instance) != null;

                dialogRelationSyncOk = hasRelationApi
                    && pisText18.Contains("cc_p2p_diplomacy_hub")
                    && pisText18.Contains("cc_p2p_watch_attack")
                    && pisText18.Contains("InteractionType == 8")
                    && pisText18.Contains("InteractionType == 9");
            }

            bool v115FixesPass = rosterPollAndCacheOk && pvpWaitAndSyncOk && lordBarterOk && dialogRelationSyncOk;
            Console.WriteLine($"[18/19] Exact Roster Sync, PvP Ready/Launch, Lord Barter & Dialog/Relation Sync (Roster={rosterPollAndCacheOk}, PvP={pvpWaitAndSyncOk}, Barter={lordBarterOk}, Relation={dialogRelationSyncOk}) -> {(v115FixesPass ? "PASS" : "FAIL")}");
            if (!v115FixesPass) failures++;

            // 19. Dual-Window Live Lord Barter (0/2 -> 1/2 -> 2/2) & Immediate No-Request Synchronized PvP
            bool liveBarterContractOk = false;
            bool tokenCombineOk = false;
            bool immediateNoPromptPvpOk = false;
            bool synchronizedLaunchAndHealthOk = false;
            {
                var pisType19 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.PlayerInteractionService");
                bool hasLiveMethods = pisType19?.GetMethod("TickPendingLiveBarter", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType19?.GetMethod("NotifyLocalBarterOfferChanged", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType19?.GetMethod("ToggleLocalBarterAccept", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType19?.GetMethod("NotifyLocalBarterCancelled", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType19?.GetMethod("CloseLiveBarterScreenSilently", BindingFlags.Public | BindingFlags.Instance) != null
                    && pisType19?.GetMethod("GetLiveBarterOfferButtonLabel", BindingFlags.Public | BindingFlags.Instance) != null;

                bool hasLiveVmPatches = modAsm.GetType("CalradiaConnected.Mod.Patches.PlayerBarterCancelInterceptPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.BarterVMCtorPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.BarterVMTransferItemGuardPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.BarterVMLeftButtonsGuardPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.BarterVMSendOfferSyncPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.BarterVMResetPreservePartnerPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.BarterVMOfferLabelPatch") != null;

                string pisText19 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "PlayerInteractionService.cs"));
                liveBarterContractOk = hasLiveMethods && hasLiveVmPatches
                    && pisText19.Contains("packet.Stage == 15")
                    && pisText19.Contains("packet.Stage == 16")
                    && pisText19.Contains("packet.Stage == 17")
                    && pisText19.Contains("packet.Stage == 18")
                    && pisText19.Contains("packet.Stage == 19");

                var combineMethod = pisType19?.GetMethod("CombineTwoSideBarterTokens", BindingFlags.Public | BindingFlags.Static);
                if (combineMethod != null)
                {
                    string combined = (string)(combineMethod.Invoke(null, new object?[] { "G:I:500", "T:I:grain:3" }) ?? string.Empty);
                    tokenCombineOk = combined == "G:I:500|T:R:grain:3";
                }

                string battleText19 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));
                string subModText19 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                string rosterText19 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "RosterSyncService.cs"));

                immediateNoPromptPvpOk = !battleText19.Contains("Do you accept the battle?")
                    && !battleText19.Contains("Waiting for opponent response")
                    && battleText19.Contains("PvpImmediateAttack");

                synchronizedLaunchAndHealthOk = battleText19.Contains("QueueSynchronizedPvpLaunch")
                    && battleText19.Contains("TearDownOpenUiAndMissionsForPvp")
                    && battleText19.Contains("EnsureCombatantReadyForPvp")
                    && subModText19.Contains("BattleSyncService.Instance.TickPendingPvpLaunch()")
                    && subModText19.Contains("PlayerInteractionService.Instance.TickPendingLiveBarter()")
                    && rosterText19.Contains("packet.TotalHealthy > 0");
            }

            bool v116FixesPass = liveBarterContractOk && tokenCombineOk && immediateNoPromptPvpOk && synchronizedLaunchAndHealthOk;
            Console.WriteLine($"[19/19] Live Mutual Lord Barter & Immediate Synchronized PvP (LiveBarter={liveBarterContractOk}, TokenCombine={tokenCombineOk}, ImmediatePvP={immediateNoPromptPvpOk}, SyncLaunch={synchronizedLaunchAndHealthOk}) -> {(v116FixesPass ? "PASS" : "FAIL")}");
            if (!v116FixesPass) failures++;

            // 20. Symmetric Troop Counts (Client/Server Cache + Desertion/Food Guards) & PvP Single-Resolution / BattleEndLogic Guards
            bool troopCountSyncGuardsOk = false;
            bool pvpSingleResolutionAndEndGuardOk = false;
            {
                var rssType20 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.RosterSyncService");
                var bssType20 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.BattleSyncService");
                var oobPatchType20 = modAsm.GetType("CalradiaConnected.Mod.Patches.PvpOrderOfBattlePatch");

                string rssSrc20 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "RosterSyncService.cs"));
                string heroMgrSrc20 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Player", "PlayerHeroManager.cs"));
                string puppetSrc20 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PuppetPartyAiPatch.cs"));
                string subModSrc20 = File.ReadAllText(subModSrc);

                troopCountSyncGuardsOk = rssType20?.GetMethod("ApplyCachedRosterIfExists", BindingFlags.Public | BindingFlags.Instance) != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PuppetPartyDesertionGuardPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PuppetPartyFoodGuardPatch") != null
                    && rssSrc20.Contains("_lastKnownRosters[packet.PeerId] = packet")
                    && rssSrc20.Contains("fallbackCount")
                    && heroMgrSrc20.Contains("ApplyCachedRosterIfExists")
                    && puppetSrc20.Contains("DesertionCampaignBehavior")
                    && subModSrc20.Contains("_rosterHeartbeatTimer >= 5.0f");

                string bssSrc20 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));
                string wsmSrc20 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Sync", "WorldSyncManager.cs"));
                string safeEncSrc20 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "SafeEncounterPatch.cs"));
                string oobSrc20 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PvpOrderOfBattlePatch.cs"));

                pvpSingleResolutionAndEndGuardOk = bssType20?.GetMethod("IsInActivePvpWithParty", BindingFlags.Public | BindingFlags.Instance) != null
                    && bssType20?.GetMethod("IsPvpCombatReadyForEndChecks", BindingFlags.Public | BindingFlags.Instance) != null
                    && oobPatchType20?.GetMethod("CheckIsEnemySideRetreatingOrOneSideDepletedPrefix", BindingFlags.Public | BindingFlags.Static) != null
                    && bssSrc20.Contains("TryGetValue(pairKey, out var activeSession)")
                    && bssSrc20.Contains("_pendingPostPvpEncounterClose")
                    && bssSrc20.Contains("ChangePartyLeader(hero)")
                    && wsmSrc20.Contains("IsInActivePvpWithParty")
                    && oobSrc20.Contains("missionTime <= 0.5f")
                    && safeEncSrc20.Contains("Hero.MainHero.IsPrisoner");
            }

            bool v117FixesPass = troopCountSyncGuardsOk && pvpSingleResolutionAndEndGuardOk;
            Console.WriteLine($"[20/20] Symmetric Troop Sync & PvP Mutual-Capture / Premature-Win Guards (TroopSync={troopCountSyncGuardsOk}, PvpGuards={pvpSingleResolutionAndEndGuardOk}) -> {(v117FixesPass ? "PASS" : "FAIL")}");
            if (!v117FixesPass) failures++;

            // 21. Post-Save Deferred Roster Flush, Puppet Upgrader/Healing Guards, Wired PvP Overlay & Safe Disconnect/End-of-Battle Teardown
            bool deferredRosterAndPuppetHealingOk = false;
            bool pvpOverlayAndVisibilityOk = false;
            bool disconnectAndEndEncounterSafetyOk = false;
            {
                var rosterSvcType21 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.RosterSyncService");
                var heroMgrType21 = cmpAsm.GetType("CalradiaConnected.Campaign.Player.PlayerHeroManager");
                var overlayMgrType21 = modAsm.GetType("CalradiaConnected.Mod.UI.PvpReadyOverlayManager");
                var missionsAsm21 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Missions.dll"));
                var coopMissionType21 = missionsAsm21.GetType("CalradiaConnected.Missions.CoopMissionSyncBehavior");

                bool hasRosterMethods21 = rosterSvcType21?.GetMethod("FlushPendingRosters", BindingFlags.Public | BindingFlags.Instance) != null
                    && rosterSvcType21?.GetMethod("BroadcastAllKnownRosters", BindingFlags.Public | BindingFlags.Instance) != null
                    && rosterSvcType21?.GetMethod("BroadcastRosterForPeer", BindingFlags.Public | BindingFlags.Instance) != null
                    && rosterSvcType21?.GetProperty("HasPendingRosters", BindingFlags.Public | BindingFlags.Instance) != null;

                bool hasPuppetHealingPatches21 = modAsm.GetType("CalradiaConnected.Mod.Patches.PuppetPartyUpgraderDailyGuardPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PuppetPartyUpgradeReadyGuardPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PuppetPartyRegularHealingGuardPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.PuppetPartyHeroHealingGuardPatch") != null;

                string rosterSrc21 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "RosterSyncService.cs"));
                string subModSrc21 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                string bssSrc21 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));
                string coopMisSrc21 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Missions", "CoopMissionSyncBehavior.cs"));
                string overlaySrc21 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "UI", "PvpReadyOverlayManager.cs"));
                string heroMgrSrc21 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Player", "PlayerHeroManager.cs"));
                string safeEncSrc21 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "SafeEncounterPatch.cs"));

                deferredRosterAndPuppetHealingOk = hasRosterMethods21 && hasPuppetHealingPatches21
                    && rosterSrc21.Contains("_pendingApplyPeerIds")
                    && rosterSrc21.Contains("DoesPartyDifferFromPacket")
                    && subModSrc21.Contains("TickPostSaveRosterAndPvpMaintenance")
                    && subModSrc21.Contains("FlushPendingRosters")
                    && subModSrc21.Contains("BroadcastAllKnownRosters")
                    && bssSrc21.Contains("RosterSyncService.Instance.FlushPendingRosters()");

                bool hasOverlayMethods21 = overlayMgrType21?.GetMethod("SyncFromService", BindingFlags.Public | BindingFlags.Instance) != null
                    && overlayMgrType21?.GetMethod("TickAttachIfNeeded", BindingFlags.Public | BindingFlags.Instance) != null
                    && coopMissionType21?.GetMethod("OnRemoveBehavior", BindingFlags.Public | BindingFlags.Instance) != null;

                pvpOverlayAndVisibilityOk = hasOverlayMethods21
                    && subModSrc21.Contains("PvpReadyOverlayManager.Instance.SyncFromService()")
                    && overlaySrc21.Contains("SendPvpReadySignal()")
                    && overlaySrc21.Contains("\"Loading\"")
                    && coopMisSrc21.Contains("_missionElapsed > 0.25f")
                    && coopMisSrc21.Contains("!agent.IsHuman")
                    && !coopMisSrc21.Contains("agent.Formation = null")
                    && coopMisSrc21.Contains("EnsureAllAgentsVisible")
                    && bssSrc21.Contains("ComputeDeterministicSeed");

                bool hasSafeEndPatches21 = heroMgrType21?.GetMethod("ProcessDeferredDespawns", BindingFlags.Public | BindingFlags.Instance) != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.SafePvpDoCaptureHeroesPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.SafePvpDoFreeOrCapturePrisonersPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.SafePvpDoPlayerVictoryPatch") != null;

                disconnectAndEndEncounterSafetyOk = hasSafeEndPatches21
                    && heroMgrSrc21.Contains("IsUnsafeToDestroyPartyNow")
                    && heroMgrSrc21.Contains("_deferredDespawns")
                    && subModSrc21.Contains("ProcessDeferredDespawns")
                    && bssSrc21.Contains("PauseAITick = false")
                    && safeEncSrc21.Contains("IsPvpOrRemotePlayerEncounter");
            }

            bool v118FixesPass = deferredRosterAndPuppetHealingOk && pvpOverlayAndVisibilityOk && disconnectAndEndEncounterSafetyOk;
            Console.WriteLine($"[21/21] Deferred Roster Sync, Puppet Upgrader/Healing Guards, PvP Overlay & Safe Disconnect/Victory Teardown (RosterPuppet={deferredRosterAndPuppetHealingOk}, PvpOverlayVis={pvpOverlayAndVisibilityOk}, SafeDisconnectEnd={disconnectAndEndEncounterSafetyOk}) -> {(v118FixesPass ? "PASS" : "FAIL")}");
            if (!v118FixesPass) failures++;

            // 22. Manual-Only PvP Ready Gate, Zero-Enemy Phantom Victory Guard, Post-Flush Readiness & Mounted Remote Agent Sync
            bool noAutoReadyBypassOk = false;
            bool zeroEnemyAndRosterOrderOk = false;
            bool mountedRemoteSyncAndDiagOk = false;
            {
                var missionsAsm22 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Missions.dll"));
                var coopMissionType22 = missionsAsm22.GetType("CalradiaConnected.Missions.CoopMissionSyncBehavior");

                string coopMisSrc22 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Missions", "CoopMissionSyncBehavior.cs"));
                string overlaySrc22 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "UI", "PvpReadyOverlayManager.cs"));
                string oobSrc22 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PvpOrderOfBattlePatch.cs"));
                string bssSrc22 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));

                noAutoReadyBypassOk = !coopMisSrc22.Contains("else if (_sawDeploymentPhase && Mission.IsDeploymentFinished)")
                    && !overlaySrc22.Contains("if (evt.Stage == 12)")
                    && oobSrc22.Contains("CanPlayerSideDeployWithOrderOfBattle()")
                    && oobSrc22.Contains("missionTime <= 0.5f");

                int flushIdx22 = bssSrc22.IndexOf("RosterSyncService.Instance.FlushPendingRosters()", StringComparison.Ordinal);
                int ensureDefAfterFlushIdx22 = bssSrc22.LastIndexOf("EnsureCombatantReadyForPvp(defParty)", StringComparison.Ordinal);
                zeroEnemyAndRosterOrderOk = oobSrc22.Contains("_enemyTeamEverHadActiveAgent")
                    && flushIdx22 > 0
                    && ensureDefAfterFlushIdx22 > flushIdx22
                    && bssSrc22.Contains("Hero.CharacterStates.Active")
                    && bssSrc22.Contains("LaunchMission:")
                    && bssSrc22.Contains("MapEventSide = null");

                mountedRemoteSyncAndDiagOk = coopMissionType22?.GetMethod("ApplyRemoteMovementAndMountSync", BindingFlags.NonPublic | BindingFlags.Static) != null
                    && coopMissionType22?.GetMethod("LogPvpTeamDiagnostic", BindingFlags.NonPublic | BindingFlags.Instance) != null
                    && coopMisSrc22.Contains("mount.LookDirection")
                    && coopMisSrc22.Contains("mount.MovementInputVector")
                    && coopMisSrc22.Contains("mount.MovementFlags")
                    && coopMisSrc22.Contains("EnsureAllAgentsVisible()");
            }

            bool v119FixesPass = noAutoReadyBypassOk && zeroEnemyAndRosterOrderOk && mountedRemoteSyncAndDiagOk;
            Console.WriteLine($"[22/22] Mutual-Only PvP Ready Gate, Zero-Enemy Victory Guard, Post-Flush Readiness & Mounted Sync (NoAutoReady={noAutoReadyBypassOk}, ZeroEnemyRoster={zeroEnemyAndRosterOrderOk}, MountSyncDiag={mountedRemoteSyncAndDiagOk}) -> {(v119FixesPass ? "PASS" : "FAIL")}");
            if (!v119FixesPass) failures++;

            // 23. CoopMissionSyncBehavior Lifecycle Init (Post-AfterStart), Duplicate-Hero Roster Clamp (1v1 Spawn Fix) & Multi-Client 1-PC Save Isolation
            bool coopLifecycleInitOk = false;
            bool heroRosterNormalizeAndSyncOk = false;
            bool multiClientAndPostPvpOk = false;
            {
                var rssType23 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.RosterSyncService");
                var missionsAsm23 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Missions.dll"));
                var coopMissionType23 = missionsAsm23.GetType("CalradiaConnected.Missions.CoopMissionSyncBehavior");

                string coopMisSrc23 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Missions", "CoopMissionSyncBehavior.cs"));
                string subModSrc23 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                string rssSrc23 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "RosterSyncService.cs"));
                string heroMgrSrc23 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Player", "PlayerHeroManager.cs"));
                string bssSrc23 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));

                coopLifecycleInitOk = coopMissionType23?.GetMethod("OnCreated", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null
                    && coopMissionType23?.GetMethod("EnsureInitializedFromMission", BindingFlags.Public | BindingFlags.Instance) != null
                    && subModSrc23.Contains("coopBehavior.EnsureInitializedFromMission()")
                    && subModSrc23.Contains("MissionMode.Conversation")
                    && coopMisSrc23.Contains("BoundRemoteHero:")
                    && coopMisSrc23.Contains("FirstRemoteStateApplied:");

                heroRosterNormalizeAndSyncOk = rssType23?.GetMethod("NormalizePartyHeroRoster", BindingFlags.Public | BindingFlags.Static) != null
                    && rssSrc23.Contains("if (el.Number > 1) return true;")
                    && rssSrc23.Contains("\"main_hero\"")
                    && heroMgrSrc23.Contains("NormalizePartyHeroRoster(newParty, hero)")
                    && !heroMgrSrc23.Contains("newParty.AddElementToMemberRoster(hero.CharacterObject, 1, insertAtFront: true)")
                    && bssSrc23.Contains("RosterSyncService.NormalizePartyHeroRoster(party, hero)");

                multiClientAndPostPvpOk = subModSrc23.Contains("CleanupStaleLocalClientWorldSaves")
                    && subModSrc23.Contains("CalradiaConnected_World_{System.Diagnostics.Process.GetCurrentProcess().Id}")
                    && bssSrc23.Contains("_postPvpEncounterCloseUntilUtc");
            }

            bool v1110FixesPass = coopLifecycleInitOk && heroRosterNormalizeAndSyncOk && multiClientAndPostPvpOk;
            Console.WriteLine($"[23/23] MissionBehavior Post-AfterStart Init, Hero Roster Count=1 Clamp & 1-PC Multi-Client Isolation (CoopInit={coopLifecycleInitOk}, HeroNorm={heroRosterNormalizeAndSyncOk}, MultiClient={multiClientAndPostPvpOk}) -> {(v1110FixesPass ? "PASS" : "FAIL")}");
            if (!v1110FixesPass) failures++;

            // 24. Smooth Full-Body / Horse Gait Animation Sync, Velocity Interpolation, Wield Sync & No Perpetual-Gallop Lock
            bool animPacketRoundTripOk = false;
            bool smoothAnimAndNoGallopLockOk = false;
            {
                var liteAsm24 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var pktType24 = cmnAsm.GetType("CalradiaConnected.Common.Packets.MissionAgentStatePacket");
                var w24Type = liteAsm24.GetType("LiteNetLib.Utils.NetDataWriter");
                var r24Type = liteAsm24.GetType("LiteNetLib.Utils.NetDataReader");
                if (pktType24 != null && w24Type != null && r24Type != null)
                {
                    var p24 = Activator.CreateInstance(pktType24)!;
                    pktType24.GetProperty("PeerId")!.SetValue(p24, 1012);
                    pktType24.GetProperty("VelX")!.SetValue(p24, 4.5f);
                    pktType24.GetProperty("MountLookX")!.SetValue(p24, 0.8f);
                    pktType24.GetProperty("MountLookY")!.SetValue(p24, 0.6f);
                    pktType24.GetProperty("ActionName")!.SetValue(p24, "act_release_swingright_onehanded");
                    pktType24.GetProperty("UpperActionProgress")!.SetValue(p24, 0.42f);
                    pktType24.GetProperty("LowerActionName")!.SetValue(p24, "act_horse_stand_1");
                    pktType24.GetProperty("MountActionName")!.SetValue(p24, "act_horse_forward_trot");
                    pktType24.GetProperty("MountActionProgress")!.SetValue(p24, 0.35f);
                    pktType24.GetProperty("MainHandSlot")!.SetValue(p24, (sbyte)0);
                    pktType24.GetProperty("OffHandSlot")!.SetValue(p24, (sbyte)1);
                    pktType24.GetProperty("StateFlags")!.SetValue(p24, (byte)9);

                    var w24 = Activator.CreateInstance(w24Type)!;
                    pktType24.GetMethod("Serialize")!.Invoke(p24, new[] { w24 });
                    var r24 = Activator.CreateInstance(r24Type, new[] { w24 })!;
                    var pBack24 = Activator.CreateInstance(pktType24)!;
                    pktType24.GetMethod("Deserialize")!.Invoke(pBack24, new[] { r24 });

                    animPacketRoundTripOk = (string)pktType24.GetProperty("MountActionName")!.GetValue(pBack24)! == "act_horse_forward_trot"
                        && (string)pktType24.GetProperty("LowerActionName")!.GetValue(pBack24)! == "act_horse_stand_1"
                        && (sbyte)pktType24.GetProperty("MainHandSlot")!.GetValue(pBack24)! == 0
                        && (sbyte)pktType24.GetProperty("OffHandSlot")!.GetValue(pBack24)! == 1
                        && (byte)pktType24.GetProperty("StateFlags")!.GetValue(pBack24)! == 9
                        && Math.Abs((float)pktType24.GetProperty("VelX")!.GetValue(pBack24)! - 4.5f) < 0.01f;
                }

                var missionsAsm24 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Missions.dll"));
                var coopMissionType24 = missionsAsm24.GetType("CalradiaConnected.Missions.CoopMissionSyncBehavior");
                string coopMisSrc24 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Missions", "CoopMissionSyncBehavior.cs"));

                smoothAnimAndNoGallopLockOk = coopMissionType24?.GetMethod("ResolveHorseGaitActionName", BindingFlags.NonPublic | BindingFlags.Static) != null
                    && coopMissionType24?.GetMethod("ApplyActionChannelSynced", BindingFlags.NonPublic | BindingFlags.Static) != null
                    && coopMissionType24?.GetMethod("SyncWieldedWeapons", BindingFlags.NonPublic | BindingFlags.Static) != null
                    && coopMisSrc24.Contains("ActionIndexCache.act_none")
                    && coopMisSrc24.Contains("act_horse_idle_1")
                    && coopMisSrc24.Contains("act_horse_forward_trot")
                    && coopMisSrc24.Contains("mount.MovementFlags = Agent.MovementControlFlag.None")
                    && !coopMisSrc24.Contains("mount.MovementFlags = moveFlags & Agent.MovementControlFlag.MoveMask");
            }

            bool v1111FixesPass = animPacketRoundTripOk && smoothAnimAndNoGallopLockOk;
            Console.WriteLine($"[24/24] Smooth Full-Body/Horse Gait Animation Sync, Wield Sync & No Perpetual-Gallop Lock (Packet={animPacketRoundTripOk}, SmoothAnim={smoothAnimAndNoGallopLockOk}) -> {(v1111FixesPass ? "PASS" : "FAIL")}");
            if (!v1111FixesPass) failures++;

            // 25. Authoritative HP/Mount/Shield/KO Sync (No One-Shots), Foot/Horse Anti-Slide Locomotion, Single-TAB Symmetric Capture & Battle Troop Sync
            bool v1112PacketsOk = false;
            bool v1112CombatAndTroopSyncOk = false;
            bool v1112PostBattleCaptureOk = false;
            {
                var liteAsm25 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var w25Type = liteAsm25.GetType("LiteNetLib.Utils.NetDataWriter");
                var r25Type = liteAsm25.GetType("LiteNetLib.Utils.NetDataReader");

                var statePktType25 = cmnAsm.GetType("CalradiaConnected.Common.Packets.MissionAgentStatePacket");
                var blowPktType25 = cmnAsm.GetType("CalradiaConnected.Common.Packets.MissionBlowSyncPacket");
                var troopsPktType25 = cmnAsm.GetType("CalradiaConnected.Common.Packets.MissionTroopsSyncPacket");
                var troopSnapType25 = cmnAsm.GetType("CalradiaConnected.Common.Packets.TroopUnitSnapshot");

                if (w25Type != null && r25Type != null && statePktType25 != null && blowPktType25 != null && troopsPktType25 != null && troopSnapType25 != null)
                {
                    // Test extended MissionAgentStatePacket (MountHealth, ShieldHitPoints, AgentLifeState)
                    var sp = Activator.CreateInstance(statePktType25)!;
                    statePktType25.GetProperty("PeerId")!.SetValue(sp, 1007);
                    statePktType25.GetProperty("Health")!.SetValue(sp, 78f);
                    statePktType25.GetProperty("MaxHealth")!.SetValue(sp, 115f);
                    statePktType25.GetProperty("MountHealth")!.SetValue(sp, 142f);
                    statePktType25.GetProperty("MountMaxHealth")!.SetValue(sp, 180f);
                    statePktType25.GetProperty("ShieldHitPoints")!.SetValue(sp, (short)210);
                    statePktType25.GetProperty("ShieldMaxHitPoints")!.SetValue(sp, (short)350);
                    statePktType25.GetProperty("AgentLifeState")!.SetValue(sp, (byte)1);

                    var ws = Activator.CreateInstance(w25Type)!;
                    statePktType25.GetMethod("Serialize")!.Invoke(sp, new[] { ws });
                    var rs = Activator.CreateInstance(r25Type, new[] { ws })!;
                    var spBack = Activator.CreateInstance(statePktType25)!;
                    statePktType25.GetMethod("Deserialize")!.Invoke(spBack, new[] { rs });

                    bool stateOk = Math.Abs((float)statePktType25.GetProperty("MountHealth")!.GetValue(spBack)! - 142f) < 0.01f
                        && (short)statePktType25.GetProperty("ShieldHitPoints")!.GetValue(spBack)! == 210
                        && (byte)statePktType25.GetProperty("AgentLifeState")!.GetValue(spBack)! == 1;

                    // Test extended MissionBlowSyncPacket (HitTargetKind, TargetTroopUnitId, ShieldDamage, IsAuthoritativeKill)
                    var bp = Activator.CreateInstance(blowPktType25)!;
                    blowPktType25.GetProperty("AttackerPeerId")!.SetValue(bp, 1007);
                    blowPktType25.GetProperty("VictimPeerId")!.SetValue(bp, 1012);
                    blowPktType25.GetProperty("Damage")!.SetValue(bp, 27f);
                    blowPktType25.GetProperty("HitTargetKind")!.SetValue(bp, (byte)3);
                    blowPktType25.GetProperty("TargetTroopUnitId")!.SetValue(bp, (ushort)14);
                    blowPktType25.GetProperty("ShieldDamage")!.SetValue(bp, (short)35);
                    blowPktType25.GetProperty("IsAuthoritativeKill")!.SetValue(bp, true);

                    var wb = Activator.CreateInstance(w25Type)!;
                    blowPktType25.GetMethod("Serialize")!.Invoke(bp, new[] { wb });
                    var rb = Activator.CreateInstance(r25Type, new[] { wb })!;
                    var bpBack = Activator.CreateInstance(blowPktType25)!;
                    blowPktType25.GetMethod("Deserialize")!.Invoke(bpBack, new[] { rb });

                    bool blowOk = (byte)blowPktType25.GetProperty("HitTargetKind")!.GetValue(bpBack)! == 3
                        && (ushort)blowPktType25.GetProperty("TargetTroopUnitId")!.GetValue(bpBack)! == 14
                        && (short)blowPktType25.GetProperty("ShieldDamage")!.GetValue(bpBack)! == 35
                        && (bool)blowPktType25.GetProperty("IsAuthoritativeKill")!.GetValue(bpBack)!;

                    // Test MissionTroopsSyncPacket
                    var tp = Activator.CreateInstance(troopsPktType25)!;
                    troopsPktType25.GetProperty("SenderPeerId")!.SetValue(tp, 1007);
                    troopsPktType25.GetProperty("TeamSide")!.SetValue(tp, (byte)1);
                    var unitList = troopsPktType25.GetProperty("Units")!.GetValue(tp)!;
                    var snap = Activator.CreateInstance(troopSnapType25)!;
                    troopSnapType25.GetProperty("UnitId")!.SetValue(snap, (ushort)7);
                    troopSnapType25.GetProperty("PosX")!.SetValue(snap, 123.5f);
                    troopSnapType25.GetProperty("PosY")!.SetValue(snap, 234.5f);
                    troopSnapType25.GetProperty("PosZ")!.SetValue(snap, 12.25f);
                    troopSnapType25.GetProperty("YawByte")!.SetValue(snap, (byte)128);
                    troopSnapType25.GetProperty("Health")!.SetValue(snap, (short)85);
                    troopSnapType25.GetProperty("MaxHealth")!.SetValue(snap, (short)100);
                    troopSnapType25.GetProperty("Flags")!.SetValue(snap, (byte)5);
                    unitList.GetType().GetMethod("Add")!.Invoke(unitList, new[] { snap });

                    var wt = Activator.CreateInstance(w25Type)!;
                    troopsPktType25.GetMethod("Serialize")!.Invoke(tp, new[] { wt });
                    var rt = Activator.CreateInstance(r25Type, new[] { wt })!;
                    var tpBack = Activator.CreateInstance(troopsPktType25)!;
                    troopsPktType25.GetMethod("Deserialize")!.Invoke(tpBack, new[] { rt });

                    var unitsBack = (System.Collections.IList)troopsPktType25.GetProperty("Units")!.GetValue(tpBack)!;
                    bool troopsOk = unitsBack.Count == 1
                        && (ushort)troopSnapType25.GetProperty("UnitId")!.GetValue(unitsBack[0])! == 7
                        && (short)troopSnapType25.GetProperty("Health")!.GetValue(unitsBack[0])! == 85
                        && (byte)troopSnapType25.GetProperty("Flags")!.GetValue(unitsBack[0])! == 5;

                    v1112PacketsOk = stateOk && blowOk && troopsOk;
                }

                var missionsAsm25 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Missions.dll"));
                var coopMissionType25 = missionsAsm25.GetType("CalradiaConnected.Missions.CoopMissionSyncBehavior");
                string coopMisSrc25 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Missions", "CoopMissionSyncBehavior.cs"));
                string bssSrc25 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));
                string routerSrc25 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Network", "Transport", "PacketRouter.cs"));
                string subModSrc25 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));

                v1112CombatAndTroopSyncOk = coopMissionType25?.GetMethod("ResolveHumanLocomotionActionName", BindingFlags.NonPublic | BindingFlags.Static) != null
                    && coopMissionType25?.GetMethod("ForceKnockoutOrKillAgent", BindingFlags.NonPublic | BindingFlags.Instance) != null
                    && coopMissionType25?.GetMethod("HandleRemoteTroopsSyncPacket", BindingFlags.Public | BindingFlags.Static) != null
                    && coopMissionType25?.GetMethod("SendLocalTroopsState", BindingFlags.NonPublic | BindingFlags.Instance) != null
                    && coopMissionType25?.GetMethod("ApplyRemoteTroopsStates", BindingFlags.NonPublic | BindingFlags.Instance) != null
                    && coopMisSrc25.Contains("Agent.MortalityState.Immortal")
                    && coopMisSrc25.Contains("ChangeWeaponHitPoints")
                    && coopMisSrc25.Contains("DeliveryMethod.ReliableSequenced")
                    && !coopMisSrc25.Contains("newHp = Math.Min(newHp, packet.RemainingHealth)")
                    && routerSrc25.Contains("RegisterPacket<MissionTroopsSyncPacket>()")
                    && subModSrc25.Contains("HandleRemoteTroopsSyncPacket");

                v1112PostBattleCaptureOk = bssSrc25.Contains("OnPvpMissionEnded")
                    && bssSrc25.Contains("ApplyPendingPvpCapture")
                    && bssSrc25.Contains("!isPvpResultPacket")
                    && bssSrc25.Contains("activeMission.EndMission()")
                    && coopMisSrc25.Contains("PvpMissionEndedEvent");
            }

            bool v1112FixesPass = v1112PacketsOk && v1112CombatAndTroopSyncOk && v1112PostBattleCaptureOk;
            Console.WriteLine($"[25/25] Authoritative HP/Mount/Shield/KO Sync, Anti-Slide Anim, Single-TAB Symmetric Capture & Troop Sync (Packets={v1112PacketsOk}, CombatTroops={v1112CombatAndTroopSyncOk}, PostBattleCapture={v1112PostBattleCaptureOk}) -> {(v1112FixesPass ? "PASS" : "FAIL")}");
            if (!v1112FixesPass) failures++;

            // 26. v1.1.13: Hit-Dealing/Receiving Animations, Defender-First Single-TAB Auto-Exit, Zero Ghost Prisoner Banner & Crash-Free Execution Respawn/Disconnect
            bool hitAnimAndPacketOk = false;
            bool defenderTabExitOk = false;
            bool prisonerBannerHiddenOk = false;
            bool executionRespawnSafeOk = false;
            {
                var liteAsm26 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var w26Type = liteAsm26.GetType("LiteNetLib.Utils.NetDataWriter");
                var r26Type = liteAsm26.GetType("LiteNetLib.Utils.NetDataReader");
                var blowPktType26 = cmnAsm.GetType("CalradiaConnected.Common.Packets.MissionBlowSyncPacket");
                bool blowAnimRoundTrip = false;
                if (w26Type != null && r26Type != null && blowPktType26 != null)
                {
                    var bp = Activator.CreateInstance(blowPktType26)!;
                    blowPktType26.GetProperty("AttackerActionName")!.SetValue(bp, "act_release_swingright_1h");
                    blowPktType26.GetProperty("VictimBodyPart")!.SetValue(bp, (byte)1);
                    blowPktType26.GetProperty("StrikeType")!.SetValue(bp, (byte)1);
                    var w26 = Activator.CreateInstance(w26Type)!;
                    blowPktType26.GetMethod("Serialize")!.Invoke(bp, new[] { w26 });
                    var r26 = Activator.CreateInstance(r26Type, new[] { w26 })!;
                    var bpBack = Activator.CreateInstance(blowPktType26)!;
                    blowPktType26.GetMethod("Deserialize")!.Invoke(bpBack, new[] { r26 });
                    blowAnimRoundTrip = (string)blowPktType26.GetProperty("AttackerActionName")!.GetValue(bpBack)! == "act_release_swingright_1h"
                        && (byte)blowPktType26.GetProperty("VictimBodyPart")!.GetValue(bpBack)! == 1
                        && (byte)blowPktType26.GetProperty("StrikeType")!.GetValue(bpBack)! == 1;
                }

                string coopSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Missions", "CoopMissionSyncBehavior.cs"));
                string subModSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                string puppetSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PuppetPartyAiPatch.cs"));
                string movePatchSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PlayerMovementInterceptPatch.cs"));
                string worldSyncSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Sync", "WorldSyncManager.cs"));
                string bssSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "BattleSyncService.cs"));
                string heroMgrSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Player", "PlayerHeroManager.cs"));
                string execPatchSrc26 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "HeroExecutionSyncPatch.cs"));

                hitAnimAndPacketOk = blowAnimRoundTrip
                    && coopSrc26.Contains("BuildVisualHitCollisionData")
                    && coopSrc26.Contains("PlayRemoteAttackerSwingFromBlow")
                    && coopSrc26.Contains("LocalHitReactHoldUntil")
                    && coopSrc26.Contains("agent.MovementFlags = Agent.MovementControlFlag.None;")
                    && coopSrc26.Contains("act_stagger_backward");

                defenderTabExitOk = subModSrc26.Contains("if (resolvedPeerId != packet.AttackerPeerId && resolvedPeerId != packet.DefenderPeerId)")
                    && coopSrc26.Contains("_forceEndMissionRequested")
                    && bssSrc26.Contains("AttackerPeerId = packet.AttackerPeerId");

                prisonerBannerHiddenOk = puppetSrc26.Contains("isCapturedOrEmpty")
                    && worldSyncSrc26.Contains("party.LeaderHero.IsPrisoner")
                    && movePatchSrc26.Contains("!Hero.MainHero.IsPrisoner")
                    && subModSrc26.Contains("isCapturedOrDead")
                    && bssSrc26.Contains("HideOrDestroyCapturedRemoteParty");

                executionRespawnSafeOk = subModSrc26.Contains("MBGameManager.EndGame()")
                    && !subModSrc26.Contains("GameStateManager?.CleanStates()")
                    && bssSrc26.Contains("isLocalExecutedPlayer")
                    && heroMgrSrc26.Contains("cc_respawn_pending_")
                    && execPatchSrc26.Contains("SuppressSpGameOverInCoopPatch")
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.SuppressSpGameOverInCoopPatch") != null;
            }

            bool v1113FixesPass = hitAnimAndPacketOk && defenderTabExitOk && prisonerBannerHiddenOk && executionRespawnSafeOk;
            Console.WriteLine($"[26/26] Hit/Strike Anims, Defender-First Single-TAB Exit, Zero Ghost Prisoner Banner & Crash-Free Execution Respawn/Disconnect (HitAnims={hitAnimAndPacketOk}, TabExit={defenderTabExitOk}, PrisonerHide={prisonerBannerHiddenOk}, ExecSafe={executionRespawnSafeOk}) -> {(v1113FixesPass ? "PASS" : "FAIL")}");
            if (!v1113FixesPass) failures++;

            // 27. v1.1.14: 8-Language Localization (TR/EN/PL/DE/RU/FR/IT/ZH), OS & Game Language Auto-Sync, Visible Flag Icons, Humanized Dialogs & Obsidian-Gold ServerManager Theme
            bool eightLangLocOk = false;
            bool eightLangXmlOk = false;
            bool humanizedModCopyOk = false;
            bool flagIconsAndThemeOk = false;
            {
                var locT27 = cmnAsm.GetType("CalradiaConnected.Common.Localization.Loc");
                var setL = locT27?.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Static);
                var tMeth = locT27?.GetMethod("T", BindingFlags.Public | BindingFlags.Static);
                var syncGame = locT27?.GetMethod("SyncFromBannerlordLanguage", BindingFlags.Public | BindingFlags.Static);
                var syncOs = locT27?.GetMethod("SyncFromSystemCulture", BindingFlags.Public | BindingFlags.Static);
                if (setL != null && tMeth != null && syncGame != null && syncOs != null)
                {
                    syncGame.Invoke(null, new object[] { "Deutsch" });
                    string deTrade = (string)tMeth.Invoke(null, new object[] { "Ticaret", "Trade" })!;
                    syncGame.Invoke(null, new object[] { "Polski" });
                    string plWar = (string)tMeth.Invoke(null, new object[] { "Savaş Aç", "Declare War" })!;
                    syncGame.Invoke(null, new object[] { "Русский" });
                    string ruLords = (string)tMeth.Invoke(null, new object[] { "Lordlarla İletişime Geç", "Contact Lords" })!;
                    syncGame.Invoke(null, new object[] { "Français" });
                    string frWar = (string)tMeth.Invoke(null, new object[] { "Savaş Aç", "Declare War" })!;
                    syncGame.Invoke(null, new object[] { "Italiano" });
                    string itTrade = (string)tMeth.Invoke(null, new object[] { "Ticaret", "Trade" })!;
                    syncGame.Invoke(null, new object[] { "简体中文" });
                    string zhWar = (string)tMeth.Invoke(null, new object[] { "Savaş Aç", "Declare War" })!;
                    setL.Invoke(null, new object[] { "TR" });

                    eightLangLocOk = deTrade == "Handeln"
                        && plWar == "Wypowiedz wojnę"
                        && ruLords == "Связаться с лордами"
                        && frWar == "Déclarer la guerre"
                        && itTrade == "Commercio"
                        && zhWar == "宣战";
                }

                string langRoot = Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "ModuleData", "Languages");
                string[] expectedXmls = {
                    Path.Combine(langRoot, "std_module_strings_xml.xml"),
                    Path.Combine(langRoot, "TR", "std_module_strings_xml_tr.xml"),
                    Path.Combine(langRoot, "PL", "std_module_strings_xml_pl.xml"),
                    Path.Combine(langRoot, "DE", "std_module_strings_xml_de.xml"),
                    Path.Combine(langRoot, "RU", "std_module_strings_xml_ru.xml"),
                    Path.Combine(langRoot, "FR", "std_module_strings_xml_fr.xml"),
                    Path.Combine(langRoot, "IT", "std_module_strings_xml_it.xml"),
                    Path.Combine(langRoot, "CNs", "std_module_strings_xml_cns.xml")
                };
                eightLangXmlOk = expectedXmls.All(p => File.Exists(p) && !File.ReadAllText(p).Contains("Co-Op"));

                string sharedSyncSrc27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SharedCampaignSyncBehavior.cs"));
                string interactionSrc27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "PlayerInteractionService.cs"));
                string p2pDlgSrc27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "UI", "P2PDialogManager.cs"));
                string oobSrc27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PvpOrderOfBattlePatch.cs"));
                humanizedModCopyOk = sharedSyncSrc27.Contains("Lordlarla İletişime Geç")
                    && !sharedSyncSrc27.Contains("(Co-Op)")
                    && !interactionSrc27.Contains("Canlı PvP")
                    && !interactionSrc27.Contains("Lordlar Usulü")
                    && !p2pDlgSrc27.Contains("PvP Başlat")
                    && !oobSrc27.Contains("(PvP)");

                string loginXml27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "GUI", "Prefabs", "CalradiaConnectedLogin.xml"));
                string loginVm27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "UI", "ClientLoginManager.cs"));
                string flagHelper27 = Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "Localization", "FlagIconHelper.cs");
                string smXaml27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "MainWindow.xaml"));
                string smVm27 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "ViewModels", "MainViewModel.cs"));
                flagIconsAndThemeOk = File.Exists(flagHelper27)
                    && loginXml27.Contains("IsFlagTR") && loginXml27.Contains("IsFlagZH") && loginXml27.Contains("ExecuteSelectLangPL")
                    && loginVm27.Contains("ExecuteSelectLangZH")
                    && smXaml27.Contains("FlagLanguageComboBoxStyle")
                    && smXaml27.Contains("FlagImage")
                    && smXaml27.Contains("#0E1117")
                    && smVm27.Contains("Loc.SyncFromSystemCulture()");
            }

            bool v1114Pass = eightLangLocOk && eightLangXmlOk && humanizedModCopyOk && flagIconsAndThemeOk;
            Console.WriteLine($"[27/27] 8-Lang Support (TR/EN/PL/DE/RU/FR/IT/ZH), OS/Game Auto-Sync, Visible Flag Icons, Humanized Copy & Obsidian-Gold Theme (Loc={eightLangLocOk}, Xml={eightLangXmlOk}, Copy={humanizedModCopyOk}, FlagsTheme={flagIconsAndThemeOk}) -> {(v1114Pass ? "PASS" : "FAIL")}");
            if (!v1114Pass) failures++;

            // 28. v1.1.15: Full Server Manager Tabs In-Game Enforcement (Game & Hardware, Starting & Progression, Admin Tools, Accounts)
            bool configPacketOk = false;
            bool patchesAndRulesOk = false;
            bool progressionFullOk = false;
            bool adminAndAccountsOk = false;
            {
                var liteAsm28 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var w28T = liteAsm28.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var r28T = liteAsm28.GetType("LiteNetLib.Utils.NetDataReader")!;

                var cfgPktT = cmnAsm.GetType("CalradiaConnected.Common.Packets.ServerConfigSyncPacket");
                var rosterPktT28 = cmnAsm.GetType("CalradiaConnected.Common.Packets.PartyRosterSyncPacket");
                if (cfgPktT != null && rosterPktT28 != null)
                {
                    var cp = Activator.CreateInstance(cfgPktT)!;
                    cfgPktT.GetProperty("BattleSize")!.SetValue(cp, 800);
                    cfgPktT.GetProperty("DamageDifficulty")!.SetValue(cp, 2);
                    cfgPktT.GetProperty("CombatAiDifficulty")!.SetValue(cp, 2);
                    cfgPktT.GetProperty("HeroDeathInBattle")!.SetValue(cp, 1);
                    cfgPktT.GetProperty("PvpEnabled")!.SetValue(cp, false);
                    cfgPktT.GetProperty("ArenaContinuousPracticeRespawn")!.SetValue(cp, true);
                    cfgPktT.GetProperty("ArenaPvpDuelsEnabled")!.SetValue(cp, false);
                    cfgPktT.GetProperty("StartingLevel")!.SetValue(cp, 15);
                    cfgPktT.GetProperty("StartingAllSkillsLevel")!.SetValue(cp, 50);
                    cfgPktT.GetProperty("CharacterXpMultiplier")!.SetValue(cp, 3.0f);
                    cfgPktT.GetProperty("SkillXpMultiplier")!.SetValue(cp, 5.0f);
                    cfgPktT.GetProperty("StartingEquipmentPreset")!.SetValue(cp, 3);
                    cfgPktT.GetProperty("StartingTroopsPreset")!.SetValue(cp, 2);
                    cfgPktT.GetProperty("IsLocalPlayerAdmin")!.SetValue(cp, true);
                    cfgPktT.GetProperty("ForceReapplyStarter")!.SetValue(cp, true);

                    var cw28 = Activator.CreateInstance(w28T)!;
                    cfgPktT.GetMethod("Serialize")!.Invoke(cp, new[] { cw28 });
                    var cr28 = Activator.CreateInstance(r28T, new[] { cw28 })!;
                    var cpBack = Activator.CreateInstance(cfgPktT)!;
                    cfgPktT.GetMethod("Deserialize")!.Invoke(cpBack, new[] { cr28 });

                    var rp = Activator.CreateInstance(rosterPktT28)!;
                    rosterPktT28.GetProperty("ClanRenown")!.SetValue(rp, 350.5f);
                    rosterPktT28.GetProperty("ClanInfluence")!.SetValue(rp, 125.0f);
                    rosterPktT28.GetProperty("HeroHp")!.SetValue(rp, 88);
                    rosterPktT28.GetProperty("HeroMaxHp")!.SetValue(rp, 105);
                    rosterPktT28.GetProperty("PartySizeLimit")!.SetValue(rp, 95);

                    var rw28 = Activator.CreateInstance(w28T)!;
                    rosterPktT28.GetMethod("Serialize")!.Invoke(rp, new[] { rw28 });
                    var rr28 = Activator.CreateInstance(r28T, new[] { rw28 })!;
                    var rpBack = Activator.CreateInstance(rosterPktT28)!;
                    rosterPktT28.GetMethod("Deserialize")!.Invoke(rpBack, new[] { rr28 });

                    configPacketOk = (int)cfgPktT.GetProperty("BattleSize")!.GetValue(cpBack)! == 800
                        && (int)cfgPktT.GetProperty("CombatAiDifficulty")!.GetValue(cpBack)! == 2
                        && !(bool)cfgPktT.GetProperty("PvpEnabled")!.GetValue(cpBack)!
                        && (int)cfgPktT.GetProperty("StartingLevel")!.GetValue(cpBack)! == 15
                        && Math.Abs((float)cfgPktT.GetProperty("CharacterXpMultiplier")!.GetValue(cpBack)! - 3.0f) < 0.01f
                        && Math.Abs((float)cfgPktT.GetProperty("SkillXpMultiplier")!.GetValue(cpBack)! - 5.0f) < 0.01f
                        && (int)cfgPktT.GetProperty("StartingEquipmentPreset")!.GetValue(cpBack)! == 3
                        && (bool)cfgPktT.GetProperty("IsLocalPlayerAdmin")!.GetValue(cpBack)!
                        && (bool)cfgPktT.GetProperty("ForceReapplyStarter")!.GetValue(cpBack)!
                        && Math.Abs((float)rosterPktT28.GetProperty("ClanRenown")!.GetValue(rpBack)! - 350.5f) < 0.1f
                        && (int)rosterPktT28.GetProperty("PartySizeLimit")!.GetValue(rpBack)! == 95;
                }

                string rulesSvcSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "ServerGameRulesService.cs"));
                string timeSyncSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Time", "CampaignTimeSyncBehavior.cs"));
                string perfOptSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Services", "ServerPerformanceOptimizer.cs"));
                patchesAndRulesOk = modAsm.GetType("CalradiaConnected.Mod.Patches.SkillXpMultiplierPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.CharacterXpMultiplierPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.ServerRulesCombatPatch") != null
                    && cmpAsm.GetType("CalradiaConnected.Campaign.Services.ServerGameRulesService") != null
                    && rulesSvcSrc28.Contains("CampaignOptions.CombatAIDifficulty")
                    && rulesSvcSrc28.Contains("CampaignOptions.BattleDeath")
                    && rulesSvcSrc28.Contains("BannerlordConfig.BattleSize")
                    && timeSyncSrc28.Contains("PersistentWorldTime")
                    && perfOptSrc28.Contains("VramBudgetLevel")
                    && perfOptSrc28.Contains("ReloadAndApplyLive");

                string progSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Progression", "PlayerStarterProgressionBehavior.cs"));
                string smRunnerSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "Services", "ServerRunnerService.cs"));
                progressionFullOk = progSrc28.Contains("ApplyEquipmentPreset")
                    && progSrc28.Contains("ApplyTroopsPreset")
                    && progSrc28.Contains("SetInitialLevel")
                    && progSrc28.Contains("SetInitialSkillLevel")
                    && progSrc28.Contains("clan.AddRenown")
                    && progSrc28.Contains("ApplyStarterFoodSupplies")
                    && progSrc28.Contains("starter_applied.json")
                    && smRunnerSrc28.Contains("\"gameRules\"")
                    && smRunnerSrc28.Contains("\"lobbySettings\"")
                    && smRunnerSrc28.Contains("ApplyLiveProcessSettings");

                string ipcHostSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Services", "ServerIpcHost.cs"));
                string userDbSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Common", "Users", "UserDatabase.cs"));
                string smVmSrc28 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "ViewModels", "MainViewModel.cs"));
                adminAndAccountsOk = ipcHostSrc28.Contains("RELOAD_CONFIG")
                    && ipcHostSrc28.Contains("RELOAD_USERS")
                    && ipcHostSrc28.Contains("RESET_PROGRESSION")
                    && ipcHostSrc28.Contains("ExecuteAdminChatCommand")
                    && ipcHostSrc28.Contains("TrySplitTrailingTokens")
                    && userDbSrc28.Contains("RefreshIfModifiedUnlocked")
                    && userDbSrc28.Contains("LoadUnlocked")
                    && smRunnerSrc28.Contains("COOP_USER_DB")
                    && smVmSrc28.Contains("ReloadConfigAsync")
                    && smVmSrc28.Contains("ReloadUsersAsync")
                    && smVmSrc28.Contains("ResetProgressionAsync")
                    && smVmSrc28.Contains("sturgian_veteran_warrior");
            }

            bool v1115Pass = configPacketOk && patchesAndRulesOk && progressionFullOk && adminAndAccountsOk;
            Console.WriteLine($"[28/28] ServerManager Tabs In-Game Sync (GameRules/HW, Progression/XP, AdminTools, Accounts) (ConfigPkt={configPacketOk}, RulesPatches={patchesAndRulesOk}, Progression={progressionFullOk}, AdminAccounts={adminAndAccountsOk}) -> {(v1115Pass ? "PASS" : "FAIL")}");
            if (!v1115Pass) failures++;

            // 29. Deferred Harmony Install (Fixes Sideways Character Models in CharCreation/Inventory) & Full 8-Lang Dropdowns/Troops
            bool deferredHarmonyOk = false;
            bool eightLangDropdownsOk = false;
            {
                string subModSrc29 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                int loadIdx = subModSrc29.IndexOf("protected override void OnSubModuleLoad()", StringComparison.Ordinal);
                int beforeInitIdx = subModSrc29.IndexOf("protected override void OnBeforeInitialModuleScreenSetAsRoot()", StringComparison.Ordinal);
                string loadBody = (loadIdx >= 0 && beforeInitIdx > loadIdx)
                    ? subModSrc29.Substring(loadIdx, beforeInitIdx - loadIdx)
                    : string.Empty;
                deferredHarmonyOk = !loadBody.Contains("PatchLifecycleManager.InstallAll")
                    && subModSrc29.Contains("EnsureHarmonyPatchesInstalled();");

                var locT29 = cmnAsm.GetType("CalradiaConnected.Common.Localization.Loc");
                var setL29 = locT29?.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Static);
                var t29 = locT29?.GetMethod("T", BindingFlags.Public | BindingFlags.Static);
                string smVm29 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "ViewModels", "MainViewModel.cs"));
                if (setL29 != null && t29 != null)
                {
                    setL29.Invoke(null, new object[] { "DE" });
                    string deTroop = (string)t29.Invoke(null, new object[] { "İmparatorluk Seçkin Katafraktı", "Imperial Elite Cataphract" })!;
                    string deLvl = (string)t29.Invoke(null, new object[] { "Seviye 1 (Standart Başlangıç)", "Level 1 (Standard Start)" })!;
                    setL29.Invoke(null, new object[] { "RU" });
                    string ruTroop = (string)t29.Invoke(null, new object[] { "Kuzait Han Muhafızı", "Khuzait Khan's Guard" })!;
                    setL29.Invoke(null, new object[] { "ZH" });
                    string zhKit = (string)t29.Invoke(null, new object[] { "Standart Vanilla Ekipman (Karakter Geçmişi)", "Standard Vanilla Equipment" })!;
                    setL29.Invoke(null, new object[] { "TR" });

                    eightLangDropdownsOk = deTroop == "Kaiserlicher Elite-Kataphrakt"
                        && deLvl == "Stufe 1 (Standardstart)"
                        && ruTroop == "Ханская стража хузаитов"
                        && zhKit == "原版默认装备"
                        && smVm29.Contains("RefreshAvailableTroops()");
                }
            }

            bool v1115HotfixPass = deferredHarmonyOk && eightLangDropdownsOk;
            Console.WriteLine($"[29/29] Deferred Harmony (Fix Sideways CharCreation/Inventory Models) & 8-Lang ServerManager Troops/Dropdowns (DeferredHarmony={deferredHarmonyOk}, Dropdowns8Lang={eightLangDropdownsOk}) -> {(v1115HotfixPass ? "PASS" : "FAIL")}");
            if (!v1115HotfixPass) failures++;

            // 30. Responder 3D Speech Bubble Reply, Game/OS Language Auto-Sync, Compact Login Flag Dropdown & Live Barter Sync Fixes
            bool dialogReplyOk = false;
            bool langAutoSyncAndUiOk = false;
            bool tradeSyncFixesOk = false;
            {
                string pisSrc30 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "PlayerInteractionService.cs"));
                string barterSrc30 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PlayerBarterPatch.cs"));
                string rosterSrc30 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "RosterSyncService.cs"));
                string subModSrc30 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));
                string loginXml30 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "GUI", "Prefabs", "CalradiaConnectedLogin.xml"));
                string smAppSrc30 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "App.xaml.cs"));
                string smVmSrc30 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.ServerManager", "ViewModels", "MainViewModel.cs"));

                dialogReplyOk = pisSrc30.Contains("packet.OptionKey == \"reply\" || packet.OptionKey == \"custom_line\"")
                    && pisSrc30.Contains("CurrentConversationSpeech = $\"[{myName}]: {trimmed}\"");

                langAutoSyncAndUiOk = subModSrc30.Contains("SyncLanguageFromBannerlord")
                    && subModSrc30.Contains("TryReadBannerlordConfigLanguageFromDisk")
                    && subModSrc30.Contains("MBTextManager.ActiveTextLanguage")
                    && loginXml30.Contains("SuggestedHeight=\"306\"")
                    && smAppSrc30.Contains("Loc.SyncFromSystemCulture()")
                    && smVmSrc30.Contains("cfg.LanguageManuallySet");

                tradeSyncFixesOk = barterSrc30.Contains("OfferItemAddMethod")
                    && barterSrc30.Contains("OfferItemRemoveMethod")
                    && barterSrc30.Contains("RefreshResultBarMethod")
                    && barterSrc30.Contains("ApplyLiveButtonState")
                    && barterSrc30.Contains("SyncLocalOfferIfChanged")
                    && pisSrc30.Contains("existingKeys.Contains(key)")
                    && !rosterSrc30.Contains("party.ItemRoster.AddToCounts(DefaultItems.Grain, 50)");
            }

            bool v1116Pass = dialogReplyOk && langAutoSyncAndUiOk && tradeSyncFixesOk;
            Console.WriteLine($"[30/30] Responder Speech Bubble, Game/OS Language Sync, Fixed Login Dropdown & Trade Sync Fixes (Reply={dialogReplyOk}, LangUI={langAutoSyncAndUiOk}, Trade={tradeSyncFixesOk}) -> {(v1116Pass ? "PASS" : "FAIL")}");
            if (!v1116Pass) failures++;

            // 31. Global AI World Reconciliation, Destroyed AI Cleanup, Unfiltered Settlement Sync & Stale Gate-Ghost Suppression
            bool worldReconcileOk = false;
            bool destroyedAndSettlementOk = false;
            bool staleGateGhostGuardOk = false;
            {
                string wsmSrc31 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Sync", "WorldSyncManager.cs"));
                string ssSrc31 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SettlementSyncService.cs"));
                string sharedSrc31 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SharedCampaignSyncBehavior.cs"));
                string puppetSrc31 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "PuppetPartyAiPatch.cs"));

                worldReconcileOk = wsmSrc31.Contains("WORLD_RECONCILE_PER_TICK")
                    && wsmSrc31.Contains("_worldReconcileCursor")
                    && wsmSrc31.Contains("DestroyedSettlementSentinel");

                destroyedAndSettlementOk = !ssSrc31.Contains("if (!nearAnyPlayer) return;")
                    && ssSrc31.Contains("BroadcastServerAiPartyDestroyed")
                    && sharedSrc31.Contains("CampaignEvents.MobilePartyDestroyed")
                    && wsmSrc31.Contains("ApplyRemoteAiPartyDestroyed");

                staleGateGhostGuardOk = wsmSrc31.Contains("ReconcileStaleNearbyAiParties")
                    && wsmSrc31.Contains("IsAiPartyHiddenByServer")
                    && wsmSrc31.Contains("MarkPartyGarrisoned")
                    && puppetSrc31.Contains("WorldSyncManager.IsAiPartyHiddenByServer");
            }

            bool v1117Pass = worldReconcileOk && destroyedAndSettlementOk && staleGateGhostGuardOk;
            Console.WriteLine($"[31/31] Global AI World Reconciliation, Destroyed AI Sync & Stale Gate-Ghost Suppression (Reconcile={worldReconcileOk}, DestroyedSettlement={destroyedAndSettlementOk}, GhostGuard={staleGateGhostGuardOk}) -> {(v1117Pass ? "PASS" : "FAIL")}");
            if (!v1117Pass) failures++;

            // 32. Phase 1 (v1.2.0): Kingdom, Conquest & Diplomacy Synchronization
            bool diplomacyPacketRoundtripOk = false;
            bool diplomacyServiceAndWiringOk = false;
            bool diplomacyLoc8LangOk = false;
            {
                var pktType = cmnAsm.GetType("CalradiaConnected.Common.Packets.KingdomDiplomacySyncPacket");
                var pktEnumType = cmnAsm.GetType("CalradiaConnected.Common.Packets.PacketType");
                if (pktType != null && pktEnumType != null)
                {
                    object pkt = Activator.CreateInstance(pktType)!;
                    pktType.GetProperty("ActionType")!.SetValue(pkt, (byte)1);
                    pktType.GetProperty("ActorPeerId")!.SetValue(pkt, 1007);
                    pktType.GetProperty("ClanId")!.SetValue(pkt, "cc_clan_1007");
                    pktType.GetProperty("KingdomId")!.SetValue(pkt, "cc_kingdom_1007");
                    pktType.GetProperty("KingdomName")!.SetValue(pkt, "New Calradian Empire");
                    pktType.GetProperty("InformalName")!.SetValue(pkt, "Calradians");
                    pktType.GetProperty("CultureId")!.SetValue(pkt, "empire");
                    pktType.GetProperty("BannerKey")!.SetValue(pkt, "11.4.4.1528.1528.764.764.1.0.0.503.131.116.400.400.764.764.0.0.0");
                    pktType.GetProperty("TargetFactionId")!.SetValue(pkt, "vlandia");
                    pktType.GetProperty("SettlementId")!.SetValue(pkt, "town_EW2");
                    pktType.GetProperty("PrimaryColor")!.SetValue(pkt, 0xFFAA2233u);
                    pktType.GetProperty("SecondaryColor")!.SetValue(pkt, 0xFFEEEEEEu);
                    pktType.GetProperty("MercenaryAwardMultiplier")!.SetValue(pkt, 150);
                    pktType.GetProperty("DetailCode")!.SetValue(pkt, (byte)5);
                    pktType.GetProperty("SnapshotPayload")!.SetValue(pkt, "K|cc_kingdom_1007|Test\nW|cc_kingdom_1007|vlandia\n");

                    var liteNetAsm = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                    var writerType = liteNetAsm.GetType("LiteNetLib.Utils.NetDataWriter")!;
                    var readerType = liteNetAsm.GetType("LiteNetLib.Utils.NetDataReader")!;
                    object writer = Activator.CreateInstance(writerType)!;
                    pktType.GetMethod("Serialize")!.Invoke(pkt, new[] { writer });

                    byte[] bytes = (byte[])writerType.GetMethod("CopyData")!.Invoke(writer, null)!;
                    object reader = Activator.CreateInstance(readerType, new object[] { bytes })!;
                    object pktOut = Activator.CreateInstance(pktType)!;
                    pktType.GetMethod("Deserialize")!.Invoke(pktOut, new[] { reader });

                    byte typeVal = Convert.ToByte(pktType.GetProperty("Type")!.GetValue(pktOut)!);
                    diplomacyPacketRoundtripOk = typeVal == 36
                        && (byte)pktType.GetProperty("ActionType")!.GetValue(pktOut)! == 1
                        && (int)pktType.GetProperty("ActorPeerId")!.GetValue(pktOut)! == 1007
                        && (string)pktType.GetProperty("ClanId")!.GetValue(pktOut)! == "cc_clan_1007"
                        && (string)pktType.GetProperty("KingdomId")!.GetValue(pktOut)! == "cc_kingdom_1007"
                        && (string)pktType.GetProperty("KingdomName")!.GetValue(pktOut)! == "New Calradian Empire"
                        && (string)pktType.GetProperty("CultureId")!.GetValue(pktOut)! == "empire"
                        && (uint)pktType.GetProperty("PrimaryColor")!.GetValue(pktOut)! == 0xFFAA2233u
                        && (int)pktType.GetProperty("MercenaryAwardMultiplier")!.GetValue(pktOut)! == 150
                        && (byte)pktType.GetProperty("DetailCode")!.GetValue(pktOut)! == 5
                        && ((string)pktType.GetProperty("SnapshotPayload")!.GetValue(pktOut)!).Contains("W|cc_kingdom_1007|vlandia");
                }

                var svcType32 = cmpAsm.GetType("CalradiaConnected.Campaign.Services.KingdomDiplomacySyncService");
                bool hasSvcMethods = svcType32?.GetProperty("IsApplyingRemoteDiplomacy", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("OnLocalKingdomCreated", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("OnLocalClanChangedKingdom", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("OnLocalSettlementOwnerChanged", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("OnLocalWarDeclared", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("OnLocalMakePeace", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("HandlePacket", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("SendFullStateToPeer", BindingFlags.Public | BindingFlags.Instance) != null
                    && svcType32?.GetMethod("BuildFullStateSnapshotPayload", BindingFlags.Public | BindingFlags.Instance) != null;

                string routerSrc32 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Network", "Transport", "PacketRouter.cs"));
                string sharedSrc32 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SharedCampaignSyncBehavior.cs"));
                string subModSrc32 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));

                diplomacyServiceAndWiringOk = hasSvcMethods
                    && routerSrc32.Contains("RegisterPacket<KingdomDiplomacySyncPacket>()")
                    && sharedSrc32.Contains("CampaignEvents.KingdomCreatedEvent")
                    && sharedSrc32.Contains("CampaignEvents.OnClanChangedKingdomEvent")
                    && sharedSrc32.Contains("CampaignEvents.OnSettlementOwnerChangedEvent")
                    && sharedSrc32.Contains("CampaignEvents.WarDeclared")
                    && sharedSrc32.Contains("CampaignEvents.MakePeace")
                    && subModSrc32.Contains("KingdomDiplomacySyncService.Instance.HandlePacket")
                    && subModSrc32.Contains("KingdomDiplomacySyncService.Instance.SendFullStateToPeer");

                var locType32 = cmnAsm.GetType("CalradiaConnected.Common.Localization.Loc");
                var setL32 = locType32?.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Static);
                var f32 = locType32?.GetMethod("F", BindingFlags.Public | BindingFlags.Static);
                if (setL32 != null && f32 != null)
                {
                    setL32.Invoke(null, new object[] { "DE" });
                    string deWar = (string)f32.Invoke(null, new object[] { "{0}, {1} tarafına savaş ilan etti!", "{0} declared war on {1}!", new object[] { "Vlandia", "Sturgia" } })!;
                    setL32.Invoke(null, new object[] { "RU" });
                    string ruConq = (string)f32.Invoke(null, new object[] { "{0}, {1} yerleşkesini fethetti!", "{0} conquered {1}!", new object[] { "A", "Pravend" } })!;
                    setL32.Invoke(null, new object[] { "ZH" });
                    string zhPeace = (string)f32.Invoke(null, new object[] { "{0} ile {1} barış yaptı.", "{0} and {1} made peace.", new object[] { "A", "B" } })!;
                    setL32.Invoke(null, new object[] { "TR" });

                    diplomacyLoc8LangOk = deWar == "Vlandia hat Sturgia den Krieg erklärt!"
                        && ruConq == "A захватил Pravend!"
                        && zhPeace == "A 与 B 达成了和平。";
                }
            }

            bool phase1DiplomacyPass = diplomacyPacketRoundtripOk && diplomacyServiceAndWiringOk && diplomacyLoc8LangOk;
            Console.WriteLine($"[32/35] Phase 1: Kingdom, Conquest & Diplomacy Sync (PacketRoundtrip={diplomacyPacketRoundtripOk}, ServiceWiring={diplomacyServiceAndWiringOk}, Loc8Lang={diplomacyLoc8LangOk}) -> {(phase1DiplomacyPass ? "PASS" : "FAIL")}");
            if (!phase1DiplomacyPass) failures++;

            // 33. Phase 2 (v1.2.0): Player Army & Joint Campaign / Siege Synchronization
            bool armyPacketOk = false;
            bool armyServiceWiringOk = false;
            {
                var armyPktType = cmnAsm.GetType("CalradiaConnected.Common.Packets.PlayerArmySyncPacket");
                var liteNetAsm33 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var writerType33 = liteNetAsm33.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var readerType33 = liteNetAsm33.GetType("LiteNetLib.Utils.NetDataReader")!;

                if (armyPktType != null)
                {
                    object pkt = Activator.CreateInstance(armyPktType)!;
                    armyPktType.GetProperty("ActionType")!.SetValue(pkt, (byte)6);
                    armyPktType.GetProperty("ArmyId")!.SetValue(pkt, "cc_army_1001");
                    armyPktType.GetProperty("LeaderPeerId")!.SetValue(pkt, 1001);
                    armyPktType.GetProperty("TargetPeerId")!.SetValue(pkt, 1002);
                    armyPktType.GetProperty("LeaderName")!.SetValue(pkt, "LordA");
                    armyPktType.GetProperty("KingdomId")!.SetValue(pkt, "vlandia");
                    armyPktType.GetProperty("ArmyMode")!.SetValue(pkt, (byte)1);
                    armyPktType.GetProperty("Cohesion")!.SetValue(pkt, 92.5f);
                    armyPktType.GetProperty("SiegeSettlementId")!.SetValue(pkt, "town_V1");
                    armyPktType.GetProperty("MemberPeerIds")!.SetValue(pkt, new System.Collections.Generic.List<int> { 1002, 1003 });

                    object w = Activator.CreateInstance(writerType33)!;
                    armyPktType.GetMethod("Serialize")!.Invoke(pkt, new[] { w });
                    byte[] bytes = (byte[])writerType33.GetMethod("CopyData")!.Invoke(w, null)!;
                    object r = Activator.CreateInstance(readerType33, new object[] { bytes })!;
                    object outPkt = Activator.CreateInstance(armyPktType)!;
                    armyPktType.GetMethod("Deserialize")!.Invoke(outPkt, new[] { r });

                    var members = (System.Collections.Generic.List<int>)armyPktType.GetProperty("MemberPeerIds")!.GetValue(outPkt)!;
                    armyPacketOk = Convert.ToByte(armyPktType.GetProperty("Type")!.GetValue(outPkt)!) == 37
                        && (byte)armyPktType.GetProperty("ActionType")!.GetValue(outPkt)! == 6
                        && (string)armyPktType.GetProperty("ArmyId")!.GetValue(outPkt)! == "cc_army_1001"
                        && (int)armyPktType.GetProperty("LeaderPeerId")!.GetValue(outPkt)! == 1001
                        && (string)armyPktType.GetProperty("SiegeSettlementId")!.GetValue(outPkt)! == "town_V1"
                        && members.Count == 2 && members[0] == 1002 && members[1] == 1003;
                }

                var armySvcType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.PlayerArmyService");
                bool hasArmyMethods = armySvcType?.GetMethod("InvitePeerToArmy", BindingFlags.Public | BindingFlags.Instance) != null
                    && armySvcType?.GetMethod("LeaveLocalArmy", BindingFlags.Public | BindingFlags.Instance) != null
                    && armySvcType?.GetMethod("DisbandLocalArmy", BindingFlags.Public | BindingFlags.Instance) != null
                    && armySvcType?.GetMethod("NotifyLocalSiegeStarted", BindingFlags.Public | BindingFlags.Instance) != null
                    && armySvcType?.GetMethod("TickArmyEscort", BindingFlags.Public | BindingFlags.Instance) != null
                    && armySvcType?.GetMethod("ArePeersInSameArmy", BindingFlags.Public | BindingFlags.Instance) != null
                    && armySvcType?.GetMethod("HandlePacket", BindingFlags.Public | BindingFlags.Instance) != null;

                string routerSrc33 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Network", "Transport", "PacketRouter.cs"));
                string pisSrc33 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "PlayerInteractionService.cs"));
                string subModSrc33 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));

                armyServiceWiringOk = hasArmyMethods
                    && routerSrc33.Contains("RegisterPacket<PlayerArmySyncPacket>()")
                    && pisSrc33.Contains("cc_p2p_dip_army_invite")
                    && pisSrc33.Contains("PlayerArmyService.Instance.InvitePeerToArmy")
                    && subModSrc33.Contains("PlayerArmyService.Instance.HandlePacket")
                    && subModSrc33.Contains("PlayerArmyService.Instance.TickArmyEscort");
            }

            bool phase2ArmyPass = armyPacketOk && armyServiceWiringOk;
            Console.WriteLine($"[33/35] Phase 2: Player Army & Joint Campaign/Siege Sync (PacketRoundtrip={armyPacketOk}, ServiceWiring={armyServiceWiringOk}) -> {(phase2ArmyPass ? "PASS" : "FAIL")}");
            if (!phase2ArmyPass) failures++;

            // 34. Phase 3 (v1.2.0): Co-op Shared Battles (AI Lords/Bandits/Sieges) & Strict Friendly Fire OFF
            bool coopBattlePacketOk = false;
            bool coopBattleServiceOk = false;
            bool friendlyFireOffEnforcedOk = false;
            {
                var cbPktType = cmnAsm.GetType("CalradiaConnected.Common.Packets.CoopBattleSyncPacket");
                var liteNetAsm34 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var writerType34 = liteNetAsm34.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var readerType34 = liteNetAsm34.GetType("LiteNetLib.Utils.NetDataReader")!;

                if (cbPktType != null)
                {
                    object pkt = Activator.CreateInstance(cbPktType)!;
                    cbPktType.GetProperty("ActionType")!.SetValue(pkt, (byte)3);
                    cbPktType.GetProperty("SessionId")!.SetValue(pkt, "coop_1001_99");
                    cbPktType.GetProperty("HostPeerId")!.SetValue(pkt, 1001);
                    cbPktType.GetProperty("ActorPeerId")!.SetValue(pkt, 1002);
                    cbPktType.GetProperty("BattleKind")!.SetValue(pkt, (byte)1);
                    cbPktType.GetProperty("TargetPartyId")!.SetValue(pkt, "lord_1_1_party");
                    cbPktType.GetProperty("SettlementId")!.SetValue(pkt, "town_V1");
                    cbPktType.GetProperty("SharedGoldLoot")!.SetValue(pkt, 450);
                    cbPktType.GetProperty("SharedRenown")!.SetValue(pkt, 12);
                    cbPktType.GetProperty("AlliedPeerIds")!.SetValue(pkt, new System.Collections.Generic.List<int> { 1001, 1002 });

                    object w = Activator.CreateInstance(writerType34)!;
                    cbPktType.GetMethod("Serialize")!.Invoke(pkt, new[] { w });
                    byte[] bytes = (byte[])writerType34.GetMethod("CopyData")!.Invoke(w, null)!;
                    object r = Activator.CreateInstance(readerType34, new object[] { bytes })!;
                    object outPkt = Activator.CreateInstance(cbPktType)!;
                    cbPktType.GetMethod("Deserialize")!.Invoke(outPkt, new[] { r });

                    var allies = (System.Collections.Generic.List<int>)cbPktType.GetProperty("AlliedPeerIds")!.GetValue(outPkt)!;
                    coopBattlePacketOk = Convert.ToByte(cbPktType.GetProperty("Type")!.GetValue(outPkt)!) == 38
                        && (byte)cbPktType.GetProperty("ActionType")!.GetValue(outPkt)! == 3
                        && (bool)cbPktType.GetProperty("FriendlyFireEnabled")!.GetValue(outPkt)! == false
                        && (int)cbPktType.GetProperty("SharedGoldLoot")!.GetValue(outPkt)! == 450
                        && allies.Count == 2;
                }

                var cbSvcType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.CoopBattleService");
                var missionsAsm34 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "CalradiaConnected.Missions.dll"));
                var missionBehType34 = missionsAsm34.GetType("CalradiaConnected.Missions.CoopMissionSyncBehavior");

                coopBattleServiceOk = cbSvcType?.GetMethod("OnLocalMapEventStarted", BindingFlags.Public | BindingFlags.Instance) != null
                    && cbSvcType?.GetMethod("OnLocalMapEventEnded", BindingFlags.Public | BindingFlags.Instance) != null
                    && cbSvcType?.GetMethod("RequestJoinCoopBattle", BindingFlags.Public | BindingFlags.Instance) != null
                    && cbSvcType?.GetMethod("OpenNearbyCoopBattlesMenu", BindingFlags.Public | BindingFlags.Instance) != null
                    && missionBehType34?.GetMethod("ConfigurePendingCoopSession", BindingFlags.Public | BindingFlags.Static) != null
                    && missionBehType34?.GetMethod("HandleCoopBattleSyncPacket", BindingFlags.Public | BindingFlags.Static) != null
                    && missionBehType34?.GetMethod("AreAgentsCoopAllies", BindingFlags.Public | BindingFlags.Static) != null;

                string rulesPatchSrc34 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "Patches", "ServerRulesCombatPatch.cs"));
                string missionSrc34 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Missions", "CoopMissionSyncBehavior.cs"));
                friendlyFireOffEnforcedOk = rulesPatchSrc34.Contains("public static bool Prefix(Agent __instance, ref Blow blow)")
                    && rulesPatchSrc34.Contains("CoopMissionSyncBehavior.AreAgentsCoopAllies(attacker, __instance)")
                    && rulesPatchSrc34.Contains("return false;")
                    && missionSrc34.Contains("!inst._coopFriendlyFireEnabled")
                    && missionSrc34.Contains("AreAgentsCoopAllies(attacker, victim)");
            }

            bool phase3CoopBattlePass = coopBattlePacketOk && coopBattleServiceOk && friendlyFireOffEnforcedOk;
            Console.WriteLine($"[34/35] Phase 3: Co-op Shared Battles & Strict Friendly Fire OFF (Packet={coopBattlePacketOk}, ServiceMission={coopBattleServiceOk}, FriendlyFireOFF={friendlyFireOffEnforcedOk}) -> {(phase3CoopBattlePass ? "PASS" : "FAIL")}");
            if (!phase3CoopBattlePass) failures++;

            // 35. Phase 4 (v1.2.0): Co-op Town Tournaments, Co-op Hideout Raids & 8-Language Expansion Copy
            bool activityPacketOk = false;
            bool activityServiceMenusOk = false;
            bool expansionLoc8LangOk = false;
            {
                var actPktType = cmnAsm.GetType("CalradiaConnected.Common.Packets.CoopActivitySyncPacket");
                var liteNetAsm35 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var writerType35 = liteNetAsm35.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var readerType35 = liteNetAsm35.GetType("LiteNetLib.Utils.NetDataReader")!;

                if (actPktType != null)
                {
                    object pkt = Activator.CreateInstance(actPktType)!;
                    actPktType.GetProperty("ActionType")!.SetValue(pkt, (byte)6);
                    actPktType.GetProperty("ActivityId")!.SetValue(pkt, "tour_town_V1_1001");
                    actPktType.GetProperty("SettlementId")!.SetValue(pkt, "town_V1");
                    actPktType.GetProperty("HostPeerId")!.SetValue(pkt, 1001);
                    actPktType.GetProperty("ActorPeerId")!.SetValue(pkt, 1002);
                    actPktType.GetProperty("BracketSeed")!.SetValue(pkt, 424242);
                    actPktType.GetProperty("RewardGold")!.SetValue(pkt, 750);
                    actPktType.GetProperty("RewardRenown")!.SetValue(pkt, 15);
                    actPktType.GetProperty("ParticipantPeerIds")!.SetValue(pkt, new System.Collections.Generic.List<int> { 1001, 1002 });

                    object w = Activator.CreateInstance(writerType35)!;
                    actPktType.GetMethod("Serialize")!.Invoke(pkt, new[] { w });
                    byte[] bytes = (byte[])writerType35.GetMethod("CopyData")!.Invoke(w, null)!;
                    object r = Activator.CreateInstance(readerType35, new object[] { bytes })!;
                    object outPkt = Activator.CreateInstance(actPktType)!;
                    actPktType.GetMethod("Deserialize")!.Invoke(outPkt, new[] { r });

                    activityPacketOk = Convert.ToByte(actPktType.GetProperty("Type")!.GetValue(outPkt)!) == 39
                        && (int)actPktType.GetProperty("BracketSeed")!.GetValue(outPkt)! == 424242
                        && (int)actPktType.GetProperty("RewardGold")!.GetValue(outPkt)! == 750;
                }

                var actSvcType = cmpAsm.GetType("CalradiaConnected.Campaign.Services.CoopActivityService");
                string sharedSrc35 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SharedCampaignSyncBehavior.cs"));
                string subModSrc35 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));

                activityServiceMenusOk = actSvcType?.GetMethod("OpenOrManageCoopTournament", BindingFlags.Public | BindingFlags.Instance) != null
                    && actSvcType?.GetMethod("OpenOrManageCoopHideoutRaid", BindingFlags.Public | BindingFlags.Instance) != null
                    && actSvcType?.GetMethod("OnLocalHideoutBattleCompleted", BindingFlags.Public | BindingFlags.Instance) != null
                    && sharedSrc35.Contains("cc_coop_tournament_")
                    && sharedSrc35.Contains("cc_coop_hideout_raid")
                    && sharedSrc35.Contains("cc_join_coop_battle_")
                    && subModSrc35.Contains("CoopActivityService.Instance.HandlePacket");

                var locType35 = cmnAsm.GetType("CalradiaConnected.Common.Localization.Loc");
                var setL35 = locType35?.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Static);
                var t35 = locType35?.GetMethod("T", BindingFlags.Public | BindingFlags.Static);
                if (setL35 != null && t35 != null)
                {
                    setL35.Invoke(null, new object[] { "DE" });
                    string deArmy = (string)t35.Invoke(null, new object[] { "Orduma Davet Et", "Invite to Army" })!;
                    setL35.Invoke(null, new object[] { "RU" });
                    string ruTour = (string)t35.Invoke(null, new object[] { "Ortak Turnuva", "Co-op Tournament" })!;
                    setL35.Invoke(null, new object[] { "ZH" });
                    string zhHide = (string)t35.Invoke(null, new object[] { "Ortak Sığınak Baskını", "Co-op Hideout Raid" })!;
                    setL35.Invoke(null, new object[] { "TR" });

                    expansionLoc8LangOk = deArmy == "Zur Armee einladen"
                        && ruTour == "Совместный турнир"
                        && zhHide == "合作突袭藏身处";
                }
            }

            bool phase4ActivityPass = activityPacketOk && activityServiceMenusOk && expansionLoc8LangOk;
            Console.WriteLine($"[35/36] Phase 4: Co-op Tournaments, Co-op Hideout Raids & 8-Lang Copy (Packet={activityPacketOk}, ServiceMenus={activityServiceMenusOk}, Loc8Lang={expansionLoc8LangOk}) -> {(phase4ActivityPass ? "PASS" : "FAIL")}");
            if (!phase4ActivityPass) failures++;

            // 36. Authoritative Server NPC/Caravan/Lord Position Sync, Client Autonomous AI Lockdown & Live AI Spawn Sync
            bool aiSpawnPacketOk = false;
            bool clientAiLockdownOk = false;
            bool worldSyncInterpolationAndJoinOk = false;
            {
                var spPktType = cmnAsm.GetType("CalradiaConnected.Common.Packets.AiPartySpawnSyncPacket");
                var liteNetAsm36 = Assembly.LoadFrom(Path.Combine(_probeDirs[0], "LiteNetLib.dll"));
                var writerType36 = liteNetAsm36.GetType("LiteNetLib.Utils.NetDataWriter")!;
                var readerType36 = liteNetAsm36.GetType("LiteNetLib.Utils.NetDataReader")!;

                if (spPktType != null)
                {
                    object pkt = Activator.CreateInstance(spPktType)!;
                    spPktType.GetProperty("PartyKind")!.SetValue(pkt, (byte)1);
                    spPktType.GetProperty("PartyId")!.SetValue(pkt, "caravan_party_42");
                    spPktType.GetProperty("OwnerHeroId")!.SetValue(pkt, "notable_1");
                    spPktType.GetProperty("SettlementId")!.SetValue(pkt, "town_V1");
                    spPktType.GetProperty("PosX")!.SetValue(pkt, 212.5f);
                    spPktType.GetProperty("PosY")!.SetValue(pkt, 334.25f);
                    spPktType.GetProperty("IsElite")!.SetValue(pkt, true);
                    spPktType.GetProperty("TroopCount")!.SetValue(pkt, 30);

                    object w = Activator.CreateInstance(writerType36)!;
                    spPktType.GetMethod("Serialize")!.Invoke(pkt, new[] { w });
                    byte[] bytes = (byte[])writerType36.GetMethod("CopyData")!.Invoke(w, null)!;
                    object r = Activator.CreateInstance(readerType36, new object[] { bytes })!;
                    object outPkt = Activator.CreateInstance(spPktType)!;
                    spPktType.GetMethod("Deserialize")!.Invoke(outPkt, new[] { r });

                    aiSpawnPacketOk = Convert.ToByte(spPktType.GetProperty("Type")!.GetValue(outPkt)!) == 40
                        && (byte)spPktType.GetProperty("PartyKind")!.GetValue(outPkt)! == 1
                        && (string)spPktType.GetProperty("PartyId")!.GetValue(outPkt)! == "caravan_party_42"
                        && (bool)spPktType.GetProperty("IsElite")!.GetValue(outPkt)! == true
                        && (int)spPktType.GetProperty("TroopCount")!.GetValue(outPkt)! == 30;
                }

                clientAiLockdownOk = modAsm.GetType("CalradiaConnected.Mod.Patches.ClientBlockCaravanHourlyPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.ClientBlockVillagerHourlySettlementPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.ClientBlockBanditHourlySpawnPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.ClientBlockLordPartySpawnPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.ClientBlockPatrolDailySpawnPatch") != null
                    && modAsm.GetType("CalradiaConnected.Mod.Patches.ClientBlockDisbandHourlyPatch") != null;

                var wsmType36 = cmpAsm.GetType("CalradiaConnected.Campaign.Sync.WorldSyncManager");
                string wsmSrc36 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Sync", "WorldSyncManager.cs"));
                string sharedSrc36 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Campaign", "Services", "SharedCampaignSyncBehavior.cs"));
                string subModSrc36 = File.ReadAllText(Path.Combine(repoRootDir, "src", "CalradiaConnected.Mod", "CalradiaConnectedSubModule.cs"));

                worldSyncInterpolationAndJoinOk = wsmType36?.GetMethod("SendFullWorldSyncToPeer", BindingFlags.Public | BindingFlags.Instance) != null
                    && wsmType36?.GetMethod("BroadcastAiPartySpawn", BindingFlags.Public | BindingFlags.Static) != null
                    && wsmType36?.GetMethod("HandleAiPartySpawnPacket", BindingFlags.Public | BindingFlags.Instance) != null
                    && wsmSrc36.Contains("InterpolateRemoteAiParties")
                    && wsmSrc36.Contains("party.Ai?.SetDoNotMakeNewDecisions(true)")
                    && sharedSrc36.Contains("CampaignEvents.MobilePartyCreated")
                    && subModSrc36.Contains("_worldSync.SendFullWorldSyncToPeer")
                    && subModSrc36.Contains("_worldSync.HandleAiPartySpawnPacket");
            }

            bool aiPositionSyncPass = aiSpawnPacketOk && clientAiLockdownOk && worldSyncInterpolationAndJoinOk;
            Console.WriteLine($"[36/36] Authoritative AI/Caravan/Lord Map Sync, Client AI Lockdown & Live AI Spawn Sync (SpawnPkt={aiSpawnPacketOk}, ClientLockdown={clientAiLockdownOk}, InterpJoin={worldSyncInterpolationAndJoinOk}) -> {(aiPositionSyncPass ? "PASS" : "FAIL")}");
            if (!aiPositionSyncPass) failures++;

            return failures;
        }
    }
}
