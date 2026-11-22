using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Handles player-to-player interactions on the campaign map and inside settlements,
    /// including 3D conversations, live mutual barter, diplomacy, friendly duels, rallies, and messaging.
    /// </summary>
    public sealed class PlayerInteractionService
    {
        private static PlayerInteractionService? _instance;
        public static PlayerInteractionService Instance => _instance ??= new PlayerInteractionService();

        private static readonly MethodInfo? ProcessPartnerSentenceMethod =
            typeof(ConversationManager).GetMethod("ProcessPartnerSentence", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class ServerDialogSession
        {
            public string SessionId = string.Empty;
            public int InitiatorPeerId;
            public int TargetPeerId;
        }

        private sealed class ServerTradeState
        {
            public int InitiatorGold;
            public string InitiatorTroopId = string.Empty;
            public int InitiatorTroopCount;
            public string InitiatorItemId = string.Empty;
            public int InitiatorItemCount;
            public int InitiatorShipIndex = -1;
            public string InitiatorShipDesc = string.Empty;
            public bool InitiatorAccepted;

            public int TargetGold;
            public string TargetTroopId = string.Empty;
            public int TargetTroopCount;
            public string TargetItemId = string.Empty;
            public int TargetItemCount;
            public int TargetShipIndex = -1;
            public string TargetShipDesc = string.Empty;
            public bool TargetAccepted;

            public bool HasAnyOffer() =>
                InitiatorGold > 0 ||
                (InitiatorTroopCount > 0 && !string.IsNullOrEmpty(InitiatorTroopId)) ||
                (InitiatorItemCount > 0 && !string.IsNullOrEmpty(InitiatorItemId)) ||
                InitiatorShipIndex >= 0 ||
                TargetGold > 0 ||
                (TargetTroopCount > 0 && !string.IsNullOrEmpty(TargetTroopId)) ||
                (TargetItemCount > 0 && !string.IsNullOrEmpty(TargetItemId)) ||
                TargetShipIndex >= 0;

            public void Reset()
            {
                InitiatorGold = 0;
                InitiatorTroopId = string.Empty;
                InitiatorTroopCount = 0;
                InitiatorItemId = string.Empty;
                InitiatorItemCount = 0;
                InitiatorShipIndex = -1;
                InitiatorShipDesc = string.Empty;
                InitiatorAccepted = false;
                TargetGold = 0;
                TargetTroopId = string.Empty;
                TargetTroopCount = 0;
                TargetItemId = string.Empty;
                TargetItemCount = 0;
                TargetShipIndex = -1;
                TargetShipDesc = string.Empty;
                TargetAccepted = false;
            }
        }

        private sealed class PendingNativeTradeOffer
        {
            public string SessionId = string.Empty;
            public int ProposerPeerId;
            public int PartnerPeerId;
            public string ProposerName = string.Empty;
            public string BoughtItemsCsv = string.Empty;
            public string SoldItemsCsv = string.Empty;
            public int NetGoldDelta;
            public DateTime CreatedUtc;
        }

        private sealed class ServerLiveBarterSession
        {
            public string SessionId = string.Empty;
            public int InitiatorPeerId;
            public int TargetPeerId;
            public string InitiatorTokens = string.Empty;
            public string TargetTokens = string.Empty;
            public bool InitiatorAccepted;
            public bool TargetAccepted;
        }

        private static readonly FieldInfo? InventoryTransactionHistoryField =
            typeof(InventoryLogic).GetField("_transactionHistory", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? SetPrisonerFreeHeroField =
            typeof(SetPrisonerFreeBarterable).GetField("_prisonerCharacter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TransferPrisonerHeroField =
            typeof(TransferPrisonerBarterable).GetField("_prisonerCharacter", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Dictionary<string, ServerDialogSession> _serverSessions = new();
        private readonly Dictionary<string, ServerTradeState> _serverTrades = new();
        private readonly Dictionary<string, PendingNativeTradeOffer> _pendingNativeTrades = new();
        private readonly Dictionary<string, ServerLiveBarterSession> _serverLiveBarters = new();
        private readonly Dictionary<int, string> _peerActiveSession = new();
        private readonly HashSet<string> _alliedPairs = new();

        public bool IsInDialog { get; private set; }
        public string ActiveSessionId { get; private set; } = string.Empty;
        public int ActiveInitiatorPeerId { get; private set; }
        public int ActiveTargetPeerId { get; private set; }

        public string CurrentConversationSpeech { get; private set; } = string.Empty;
        public string PendingProposalKey { get; private set; } = string.Empty;
        private int _pendingProposalSenderPeerId;

        private bool _dialogsRegistered;
        private bool _isSuppressingConversationEnd;
        private int _pendingPvpTargetPeerId;
        private int _pendingTradeTargetPeerId;

        // Live Dual-Window Lord Barter state & UI bridge events
        public bool IsLiveBarterOpen { get; private set; }
        public string LiveBarterSessionId { get; private set; } = string.Empty;
        public int LiveBarterInitiatorPeerId { get; private set; }
        public int LiveBarterTargetPeerId { get; private set; }
        public int LiveBarterPartnerPeerId { get; private set; }
        public bool LiveBarterLocalAccepted { get; private set; }
        public bool LiveBarterPartnerAccepted { get; private set; }
        public string LiveBarterLocalTokens { get; private set; } = string.Empty;
        public string LiveBarterPartnerTokens { get; private set; } = string.Empty;
        public bool IsApplyingRemoteBarterSync { get; set; }
        private bool _suppressLocalBarterCancelNotify;
        private int _pendingLiveBarterOpenPeerId;
        private string _pendingLiveBarterSessionId = string.Empty;
        private int _pendingLiveBarterInitPeerId;
        private int _pendingLiveBarterTargPeerId;
        private DateTime _pendingLiveBarterQueuedUtc = DateTime.MinValue;

        public event Action<string>? PartnerBarterTokensUpdated;
        public event Action? LiveBarterStatusChanged;
        public event Action? LiveBarterCloseRequested;

        public void ClearLiveBarterState()
        {
            IsLiveBarterOpen = false;
            LiveBarterSessionId = string.Empty;
            LiveBarterInitiatorPeerId = 0;
            LiveBarterTargetPeerId = 0;
            LiveBarterPartnerPeerId = 0;
            LiveBarterLocalAccepted = false;
            LiveBarterPartnerAccepted = false;
            LiveBarterLocalTokens = string.Empty;
            LiveBarterPartnerTokens = string.Empty;
            IsApplyingRemoteBarterSync = false;
            _pendingLiveBarterOpenPeerId = 0;
            _pendingLiveBarterSessionId = string.Empty;
            _pendingLiveBarterInitPeerId = 0;
            _pendingLiveBarterTargPeerId = 0;
        }

        public void ClearLocalDialogState()
        {
            IsInDialog = false;
            ActiveSessionId = string.Empty;
            ActiveInitiatorPeerId = 0;
            ActiveTargetPeerId = 0;
            CurrentConversationSpeech = string.Empty;
            PendingProposalKey = string.Empty;
            _pendingProposalSenderPeerId = 0;
        }

        private static string GetPairKey(int peerA, int peerB)
        {
            int min = Math.Min(peerA, peerB);
            int max = Math.Max(peerA, peerB);
            return $"{min}_{max}";
        }

        public bool ArePeersAllied(int peerA, int peerB)
        {
            if (peerA <= 0 || peerB <= 0 || peerA == peerB) return false;
            return _alliedPairs.Contains(GetPairKey(peerA, peerB));
        }

        public static int GetHeroRelationScore(Hero? heroA, Hero? heroB)
        {
            if (heroA == null || heroB == null || ReferenceEquals(heroA, heroB)) return 0;
            return CharacterRelationManager.GetHeroRelation(heroA, heroB);
        }

        public static string GetRelationLabel(int relation)
        {
            if (relation >= 40) return Loc.T("Sadık Müttefik", "Sworn Ally");
            if (relation >= 15) return Loc.T("Dost", "Friendly");
            if (relation <= -40) return Loc.T("Kan Düşmanı", "Bitter Enemy");
            if (relation <= -10) return Loc.T("Soğuk / Hasım", "Hostile");
            return Loc.T("Nötr", "Neutral");
        }

        public string GetDiplomaticStanceLabel(int partnerPeerId, Hero? partnerHero)
        {
            if (Hero.MainHero?.MapFaction != null && partnerHero?.MapFaction != null &&
                FactionManager.IsAtWarAgainstFaction(Hero.MainHero.MapFaction, partnerHero.MapFaction))
            {
                return Loc.T("Savaşta", "At War");
            }

            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            if (ArePeersAllied(myPeerId, partnerPeerId))
            {
                return Loc.T("Müttefik", "Allied");
            }

            return Loc.T("Barışta", "At Peace");
        }

        private static int ApplyServerRelationDelta(int peerA, int peerB, Hero? heroA, Hero? heroB, int delta, string reason)
        {
            if (heroA == null || heroB == null || ReferenceEquals(heroA, heroB)) return 0;

            int current = CharacterRelationManager.GetHeroRelation(heroA, heroB);
            int updated = Math.Max(-100, Math.Min(100, current + delta));
            CharacterRelationManager.SetHeroRelation(heroA, heroB, updated);

            if (ModContext.IsServerRunning)
            {
                ModContext.Server?.Broadcast(new PlayerInteractionPacket
                {
                    SenderPeerId = peerA,
                    SenderName = heroA.Name?.ToString() ?? string.Empty,
                    TargetPeerId = peerB,
                    TargetName = heroB.Name?.ToString() ?? string.Empty,
                    InteractionType = 8, // RelationSync
                    IntAmount = updated,
                    Message = reason
                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            return updated;
        }

        public void HandlePeerDisconnected(int peerId)
        {
            if (peerId <= 0) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                if (_pendingTradeTargetPeerId == peerId) _pendingTradeTargetPeerId = 0;
                if (_pendingPvpTargetPeerId == peerId) _pendingPvpTargetPeerId = 0;

                if (IsInDialog && (ActiveInitiatorPeerId == peerId || ActiveTargetPeerId == peerId))
                {
                    CloseActiveDialogLocalOnly();
                }

                if (_peerActiveSession.TryGetValue(peerId, out string? sid) &&
                    _serverSessions.TryGetValue(sid, out var sess))
                {
                    int otherPeer = sess.InitiatorPeerId == peerId ? sess.TargetPeerId : sess.InitiatorPeerId;
                    ClearServerSession(sid, sess.InitiatorPeerId, sess.TargetPeerId);
                    if (otherPeer > 0 && ModContext.IsServerRunning)
                    {
                        ModContext.Server?.SendTo(otherPeer, new P2PDialogPacket
                        {
                            SessionId = sid,
                            InitiatorPeerId = sess.InitiatorPeerId,
                            TargetPeerId = sess.TargetPeerId,
                            SenderPeerId = peerId,
                            Stage = 5,
                            OptionKey = "disconnect"
                        }, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }

                foreach (var tradeKey in _pendingNativeTrades.Keys.ToList())
                {
                    if (_pendingNativeTrades.TryGetValue(tradeKey, out var trade) &&
                        (trade.ProposerPeerId == peerId || trade.PartnerPeerId == peerId))
                    {
                        _pendingNativeTrades.Remove(tradeKey);
                    }
                }

                foreach (var liveKey in _serverLiveBarters.Keys.ToList())
                {
                    if (_serverLiveBarters.TryGetValue(liveKey, out var live) &&
                        (live.InitiatorPeerId == peerId || live.TargetPeerId == peerId))
                    {
                        _serverLiveBarters.Remove(liveKey);
                        int otherPeer = live.InitiatorPeerId == peerId ? live.TargetPeerId : live.InitiatorPeerId;
                        if (otherPeer > 0 && ModContext.IsServerRunning)
                        {
                            ModContext.Server?.SendTo(otherPeer, new P2PDialogPacket
                            {
                                SessionId = live.SessionId,
                                InitiatorPeerId = live.InitiatorPeerId,
                                TargetPeerId = live.TargetPeerId,
                                SenderPeerId = peerId,
                                Stage = 19,
                                OptionKey = "disconnect"
                            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        }
                    }
                }

                if (IsLiveBarterOpen && LiveBarterPartnerPeerId == peerId)
                {
                    CloseLiveBarterScreenSilently();
                }
            });
        }

        private bool IsLocalInitiator =>
            (ModContext.Client?.LocalPeerId ?? -1) == ActiveInitiatorPeerId;

        private int GetActivePartnerPeerId()
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            if (myPeerId == ActiveInitiatorPeerId) return ActiveTargetPeerId;
            if (myPeerId == ActiveTargetPeerId) return ActiveInitiatorPeerId;
            return 0;
        }

        private bool IsActiveP2PConversationPartner()
        {
            if (!IsInDialog) return false;
            var convHero = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager?.OneToOneConversationHero;
            if (convHero == null) return false;

            int partnerPeerId = GetActivePartnerPeerId();
            if (partnerPeerId <= 0) return false;

            var partnerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(partnerPeerId);
            return partnerHero != null && convHero == partnerHero;
        }

        private string BuildLiveStatusSpeech(string? speech)
        {
            int partnerPeerId = GetActivePartnerPeerId();
            var partnerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(partnerPeerId);
            var partnerParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(partnerPeerId);
            int rel = GetHeroRelationScore(Hero.MainHero, partnerHero);
            string relSign = rel > 0 ? $"+{rel}" : rel.ToString();
            string relLabel = GetRelationLabel(rel);
            string stance = GetDiplomaticStanceLabel(partnerPeerId, partnerHero);
            int troops = partnerParty?.MemberRoster?.TotalHealthyCount ?? partnerParty?.MemberRoster?.TotalManCount ?? 1;

            string header = $"[{stance} | {Loc.T("İlişki", "Relation")}: {relSign} ({relLabel}) | {troops} {Loc.T("Asker", "Troops")}]";
            string line = string.IsNullOrWhiteSpace(speech)
                ? Loc.T("Sizi dinliyorum.", "I am listening.")
                : speech!;
            return $"{header}\n{line}";
        }

        private void SetConversationSpeechVariables(string? speech, string? proposalKey = null)
        {
            MBTextManager.SetTextVariable("CC_P2P_SPEECH", BuildLiveStatusSpeech(speech), false);

            string acceptText = proposalKey switch
            {
                "peace_proposal" => Loc.T("[Kabul Et] Barış antlaşmasını kabul ediyorum (+10 İlişki).", "[Accept] I accept the peace treaty (+10 Relation)."),
                "alliance_proposal" => Loc.T("[Kabul Et] İttifak ve saldırmazlık antlaşmasını onaylıyorum (+10 İlişki).", "[Accept] I accept the alliance & non-aggression pact (+10 Relation)."),
                string s when s.StartsWith("rally") => Loc.T("[Kabul Et] Ortak harekata katılıyorum!", "[Accept] I will join the rally!"),
                _ => Loc.T("[Kabul Et] Dostluk düellosunu kabul ediyorum (+5 İlişki).", "[Accept] I accept the friendly duel (+5 Relation).")
            };
            MBTextManager.SetTextVariable("CC_P2P_ACCEPT_TEXT", acceptText, false);
        }

        private void OpenNative3DConversation(MobileParty? partnerParty, Hero? partnerHero, string initialSpeech)
        {
            try
            {
                if (Hero.MainHero?.CharacterObject == null || MobileParty.MainParty?.Party == null)
                {
                    return;
                }

                var hero = partnerHero ?? partnerParty?.LeaderHero;
                var character = hero?.CharacterObject
                    ?? (partnerParty?.MemberRoster != null && partnerParty.MemberRoster.Count > 0
                        ? partnerParty.MemberRoster.GetCharacterAtIndex(0)
                        : null);
                if (character == null)
                {
                    return;
                }

                CurrentConversationSpeech = initialSpeech;
                SetConversationSpeechVariables(CurrentConversationSpeech, PendingProposalKey);

                var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
                if (cm != null && cm.IsConversationInProgress)
                {
                    RefreshActiveNativeConversation();
                    return;
                }

                var playerData = new ConversationCharacterData(
                    CharacterObject.PlayerCharacter,
                    MobileParty.MainParty.Party,
                    noHorse: false,
                    noWeapon: false,
                    spawnAfterFight: false,
                    isCivilianEquipmentRequiredForLeader: false,
                    isCivilianEquipmentRequiredForBodyGuardCharacters: false,
                    noBodyguards: false);

                var partnerData = new ConversationCharacterData(
                    character,
                    partnerParty?.Party,
                    noHorse: false,
                    noWeapon: false,
                    spawnAfterFight: false,
                    isCivilianEquipmentRequiredForLeader: false,
                    isCivilianEquipmentRequiredForBodyGuardCharacters: false,
                    noBodyguards: false);

                CampaignMapConversation.OpenConversation(playerData, partnerData);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] OpenNative3DConversation failed: {ex.Message}");
            }
        }

        private void RefreshActiveNativeConversation()
        {
            try
            {
                var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
                if (cm == null || !cm.IsConversationInProgress) return;
                if (!IsActiveP2PConversationPartner()) return;

                SetConversationSpeechVariables(CurrentConversationSpeech, PendingProposalKey);
                string token = IsLocalInitiator ? "cc_p2p_refresh_init" : "cc_p2p_refresh_watch";
                int idx = cm.GetStateIndex(token);
                if (idx >= 0)
                {
                    cm.ActiveToken = idx;
                    ProcessPartnerSentenceMethod?.Invoke(cm, null);
                    cm.DoConversationContinuedCallback();
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] RefreshActiveNativeConversation failed: {ex.Message}");
            }
        }

        private void CloseNative3DConversation()
        {
            try
            {
                var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
                if (cm != null && cm.IsConversationInProgress)
                {
                    _isSuppressingConversationEnd = true;
                    try
                    {
                        cm.EndConversation();
                    }
                    finally
                    {
                        _isSuppressingConversationEnd = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _isSuppressingConversationEnd = false;
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] CloseNative3DConversation failed: {ex.Message}");
            }
        }

        public void NotifyNativeConversationEnded()
        {
            try
            {
                if (_isSuppressingConversationEnd) return;

                int pendingPvp = _pendingPvpTargetPeerId;
                _pendingPvpTargetPeerId = 0;
                int pendingTrade = _pendingTradeTargetPeerId;
                _pendingTradeTargetPeerId = 0;

                if (IsInDialog)
                {
                    string sid = ActiveSessionId;
                    int initPeer = ActiveInitiatorPeerId;
                    int targPeer = ActiveTargetPeerId;
                    int myPeer = ModContext.Client?.LocalPeerId ?? 1001;
                    string myName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Oyuncu", "Player");

                    ClearLocalDialogState();
                    CloseDialogLocalRequested?.Invoke();

                    if (!string.IsNullOrEmpty(sid) && initPeer > 0 && targPeer > 0)
                    {
                        SendDialogPacket(new P2PDialogPacket
                        {
                            SessionId = sid,
                            InitiatorPeerId = initPeer,
                            TargetPeerId = targPeer,
                            SenderPeerId = myPeer,
                            SenderName = myName,
                            Stage = 5,
                            OptionKey = pendingPvp > 0 ? "attack_player" : (pendingTrade > 0 ? "open_trade" : "close"),
                            SpeakerName = myName,
                            DialogText = Loc.T("Görüşmeden ayrıldı.", "Left the conversation.")
                        });
                    }
                }

                if (pendingPvp > 0)
                {
                    var targetParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(pendingPvp);
                    if (targetParty != null)
                    {
                        BattleSyncService.Instance.RequestPvpBattle(targetParty);
                    }
                }
                else if (pendingTrade > 0)
                {
                    OpenLordBarterWithPlayer(pendingTrade);
                }
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] NotifyNativeConversationEnded failed: {ex.Message}");
            }
        }

        private bool IsAtWarWithActivePartner()
        {
            int partnerId = GetActivePartnerPeerId();
            var partnerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(partnerId);
            return Hero.MainHero?.MapFaction != null &&
                   partnerHero?.MapFaction != null &&
                   FactionManager.IsAtWarAgainstFaction(Hero.MainHero.MapFaction, partnerHero.MapFaction);
        }

        private bool HasIncomingProposalForMe()
        {
            if (string.IsNullOrEmpty(PendingProposalKey)) return false;
            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            return _pendingProposalSenderPeerId != myPeerId;
        }

        public void RegisterConversationDialogs(CampaignGameStarter starter)
        {
            if (starter == null || _dialogsRegistered) return;
            _dialogsRegistered = true;

            SetConversationSpeechVariables(Loc.T("Selamlar dostum.", "Greetings, my friend."));

            // Initial entry point in native 3D conversation (high priority overrides default NPC greeting)
            starter.AddDialogLine("cc_p2p_start_init", "start", "cc_p2p_init_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner() && IsLocalInitiator, null, priority: 10000);
            starter.AddDialogLine("cc_p2p_start_watch", "start", "cc_p2p_watch_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner() && !IsLocalInitiator, null, priority: 10000);

            // Live refresh tokens (pushed via ProcessPartnerSentence when remote player acts or replies)
            starter.AddDialogLine("cc_p2p_refresh_npc_init", "cc_p2p_refresh_init", "cc_p2p_init_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner(), null, priority: 10000);
            starter.AddDialogLine("cc_p2p_refresh_npc_watch", "cc_p2p_refresh_watch", "cc_p2p_watch_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner(), null, priority: 10000);

            // Loopback lines after player picks an in-dialog option or finishes Barter UI
            starter.AddDialogLine("cc_p2p_init_loop", "cc_p2p_init_wait", "cc_p2p_init_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner(), null, priority: 10000);
            starter.AddDialogLine("cc_p2p_watch_loop", "cc_p2p_watch_wait", "cc_p2p_watch_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner(), null, priority: 10000);

            // Diplomacy & Relations NPC prompt & loopback
            starter.AddDialogLine("cc_p2p_dip_npc", "cc_p2p_diplomacy_enter", "cc_p2p_diplomacy_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner(), null, priority: 10000);
            starter.AddDialogLine("cc_p2p_dip_back_init", "cc_p2p_diplomacy_back", "cc_p2p_init_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner() && IsLocalInitiator, null, priority: 10000);
            starter.AddDialogLine("cc_p2p_dip_back_watch", "cc_p2p_diplomacy_back", "cc_p2p_watch_hub", "{=!}{CC_P2P_SPEECH}",
                () => IsActiveP2PConversationPartner() && !IsLocalInitiator, null, priority: 10000);

            // INITIATOR HUB OPTIONS
            starter.AddPlayerLine("cc_p2p_init_accept", "cc_p2p_init_hub", "cc_p2p_init_wait",
                "{=!}{CC_P2P_ACCEPT_TEXT}",
                () => IsActiveP2PConversationPartner() && HasIncomingProposalForMe(),
                () => AcceptActiveProposal(),
                priority: 10050);

            starter.AddPlayerLine("cc_p2p_init_decline", "cc_p2p_init_hub", "cc_p2p_init_wait",
                Loc.T("Teklifini reddediyorum.", "I must decline your offer."),
                () => IsActiveP2PConversationPartner() && HasIncomingProposalForMe(),
                () => DeclineActiveProposal(),
                priority: 10040);

            starter.AddPlayerLine("cc_p2p_opt_barter", "cc_p2p_init_hub", "cc_p2p_init_wait",
                Loc.T("Ticaret", "Trade"),
                () => IsActiveP2PConversationPartner(),
                () => OpenLordBarterWithPlayer(GetActivePartnerPeerId()),
                priority: 10030);

            starter.AddPlayerLine("cc_p2p_opt_peace", "cc_p2p_init_hub", "cc_p2p_init_wait",
                Loc.T("Barış Teklif Et", "Offer Peace"),
                () => IsActiveP2PConversationPartner() && IsAtWarWithActivePartner(),
                () => ExecuteDialogAction("peace_proposal", GetActivePartnerPeerId(), ActiveSessionId),
                priority: 10025);

            starter.AddPlayerLine("cc_p2p_opt_diplomacy", "cc_p2p_init_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Diplomasi ve İlişkiler", "Diplomacy & Relations"),
                () => IsActiveP2PConversationPartner(),
                null,
                priority: 10020);

            starter.AddPlayerLine("cc_p2p_opt_say", "cc_p2p_init_hub", "cc_p2p_init_wait",
                Loc.T("Mesaj Yaz", "Speak"),
                () => IsActiveP2PConversationPartner(),
                () => ExecuteDialogAction("say", GetActivePartnerPeerId(), ActiveSessionId),
                priority: 10015);

            starter.AddPlayerLine("cc_p2p_opt_attack", "cc_p2p_init_hub", "close_window",
                Loc.T("Savaş Aç", "Declare War"),
                () => IsActiveP2PConversationPartner() && CalradiaConnected.Common.Config.ServerRuntimeSettings.Current.PvpEnabled,
                () => { _pendingPvpTargetPeerId = GetActivePartnerPeerId(); },
                priority: 10010);

            starter.AddPlayerLine("cc_p2p_opt_leave", "cc_p2p_init_hub", "close_window",
                Loc.T("Görüşmeyi Sonlandır", "End Conversation"),
                () => IsActiveP2PConversationPartner(),
                null,
                priority: 10000);

            // WATCHER / TARGET HUB OPTIONS (Symmetric capabilities!)
            starter.AddPlayerLine("cc_p2p_watch_accept", "cc_p2p_watch_hub", "cc_p2p_watch_wait",
                "{=!}{CC_P2P_ACCEPT_TEXT}",
                () => IsActiveP2PConversationPartner() && HasIncomingProposalForMe(),
                () => AcceptActiveProposal(),
                priority: 10050);

            starter.AddPlayerLine("cc_p2p_watch_decline", "cc_p2p_watch_hub", "cc_p2p_watch_wait",
                Loc.T("Teklifini reddediyorum.", "I must decline your offer."),
                () => IsActiveP2PConversationPartner() && HasIncomingProposalForMe(),
                () => DeclineActiveProposal(),
                priority: 10040);

            starter.AddPlayerLine("cc_p2p_watch_barter", "cc_p2p_watch_hub", "cc_p2p_watch_wait",
                Loc.T("Ticaret", "Trade"),
                () => IsActiveP2PConversationPartner(),
                () => OpenLordBarterWithPlayer(GetActivePartnerPeerId()),
                priority: 10030);

            starter.AddPlayerLine("cc_p2p_watch_peace", "cc_p2p_watch_hub", "cc_p2p_watch_wait",
                Loc.T("Barış Teklif Et", "Offer Peace"),
                () => IsActiveP2PConversationPartner() && IsAtWarWithActivePartner(),
                () => ExecuteDialogAction("peace_proposal", GetActivePartnerPeerId(), ActiveSessionId),
                priority: 10025);

            starter.AddPlayerLine("cc_p2p_watch_diplomacy", "cc_p2p_watch_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Diplomasi ve İlişkiler", "Diplomacy & Relations"),
                () => IsActiveP2PConversationPartner(),
                null,
                priority: 10020);

            starter.AddPlayerLine("cc_p2p_watch_reply", "cc_p2p_watch_hub", "cc_p2p_watch_wait",
                Loc.T("Mesaj Yaz", "Speak"),
                () => IsActiveP2PConversationPartner(),
                () => OpenWatcherReplyInquiry(),
                priority: 10015);

            starter.AddPlayerLine("cc_p2p_watch_attack", "cc_p2p_watch_hub", "close_window",
                Loc.T("Savaş Aç", "Declare War"),
                () => IsActiveP2PConversationPartner() && CalradiaConnected.Common.Config.ServerRuntimeSettings.Current.PvpEnabled,
                () => { _pendingPvpTargetPeerId = GetActivePartnerPeerId(); },
                priority: 10010);

            starter.AddPlayerLine("cc_p2p_watch_leave", "cc_p2p_watch_hub", "close_window",
                Loc.T("Görüşmeyi Sonlandır", "End Conversation"),
                () => IsActiveP2PConversationPartner(),
                null,
                priority: 10000);

            // SHARED DIPLOMACY & RELATIONSHIP SUBMENU
            starter.AddPlayerLine("cc_p2p_dip_gift", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Hediye Sun (500 Altın)", "Offer a Gift (500 Gold)"),
                () => IsActiveP2PConversationPartner(),
                () => ExecuteDialogAction("gift_gold", GetActivePartnerPeerId(), ActiveSessionId),
                priority: 10040);

            starter.AddPlayerLine("cc_p2p_dip_alliance", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_enter",
                Loc.T("İttifak Teklif Et", "Propose an Alliance"),
                () => IsActiveP2PConversationPartner() && !IsAtWarWithActivePartner() && !ArePeersAllied(ModContext.Client?.LocalPeerId ?? -1, GetActivePartnerPeerId()),
                () => ExecuteDialogAction("alliance_proposal", GetActivePartnerPeerId(), ActiveSessionId),
                priority: 10035);

            starter.AddPlayerLine("cc_p2p_dip_duel", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Düello Teklif Et", "Challenge to a Friendly Duel"),
                () => IsActiveP2PConversationPartner() && CalradiaConnected.Common.Config.ServerRuntimeSettings.Current.ArenaPvpDuelsEnabled,
                () => ExecuteDialogAction("duel", GetActivePartnerPeerId(), ActiveSessionId),
                priority: 10030);

            starter.AddPlayerLine("cc_p2p_dip_rally", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Ortak Sefer Çağrısı Yap", "Call to Arms"),
                () => IsActiveP2PConversationPartner(),
                () => ExecuteDialogAction("rally", GetActivePartnerPeerId(), ActiveSessionId),
                priority: 10020);

            starter.AddPlayerLine("cc_p2p_dip_army_invite", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Orduma Davet Et", "Invite to Army"),
                () => IsActiveP2PConversationPartner() &&
                      !IsAtWarWithActivePartner() &&
                      !PlayerArmyService.Instance.ArePeersInSameArmy(ModContext.Client?.LocalPeerId ?? -1, GetActivePartnerPeerId()),
                () => PlayerArmyService.Instance.InvitePeerToArmy(GetActivePartnerPeerId()),
                priority: 10018);

            starter.AddPlayerLine("cc_p2p_dip_army_leave", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Ordudan Ayrıl", "Leave Army"),
                () => IsActiveP2PConversationPartner() &&
                      PlayerArmyService.Instance.IsPeerInAnyArmy(ModContext.Client?.LocalPeerId ?? -1) &&
                      !PlayerArmyService.Instance.IsPeerArmyLeader(ModContext.Client?.LocalPeerId ?? -1),
                () => PlayerArmyService.Instance.LeaveLocalArmy(),
                priority: 10016);

            starter.AddPlayerLine("cc_p2p_dip_army_disband", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_enter",
                Loc.T("Orduyu Dağıt", "Disband Army"),
                () => IsActiveP2PConversationPartner() &&
                      PlayerArmyService.Instance.IsPeerArmyLeader(ModContext.Client?.LocalPeerId ?? -1),
                () => PlayerArmyService.Instance.DisbandLocalArmy(),
                priority: 10015);

            starter.AddPlayerLine("cc_p2p_dip_back", "cc_p2p_diplomacy_hub", "cc_p2p_diplomacy_back",
                Loc.T("Geri Dön", "Back"),
                () => IsActiveP2PConversationPartner(),
                null,
                priority: 10000);
        }

        private void AcceptActiveProposal()
        {
            string responseKey = PendingProposalKey switch
            {
                "peace_proposal" => "accept_peace",
                "alliance_proposal" => "accept_alliance",
                string s when s.StartsWith("rally") => "accept_rally",
                _ => "accept_duel"
            };
            PendingProposalKey = string.Empty;
            _pendingProposalSenderPeerId = 0;
            SendNativeResponsePacket(responseKey, Loc.T("Teklifini kabul ediyorum!", "I accept your proposal!"));
        }

        private void DeclineActiveProposal()
        {
            string responseKey = PendingProposalKey switch
            {
                "peace_proposal" => "decline_peace",
                "alliance_proposal" => "decline_alliance",
                string s when s.StartsWith("rally") => "decline_rally",
                _ => "decline_duel"
            };
            PendingProposalKey = string.Empty;
            _pendingProposalSenderPeerId = 0;
            SendNativeResponsePacket(responseKey, Loc.T("Bu sefer teklifini reddediyorum.", "I must decline this time."));
        }

        private void OpenWatcherReplyInquiry()
        {
            int partnerId = GetActivePartnerPeerId();
            var partnerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(partnerId);
            string partnerName = partnerHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", partnerId);
            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);

            SuspendDialogForInquiryRequested?.Invoke();
            InformationManager.ShowTextInquiry(new TextInquiryData(
                titleText: Loc.T("Diyalog Yanıtı", "Dialog Reply"),
                text: Loc.F("'{0}' oyuncusuna canlı diyalog yanıtınızı yazın:", "Write your live dialog reply to '{0}':", partnerName),
                isAffirmativeOptionShown: true,
                isNegativeOptionShown: true,
                affirmativeText: Loc.T("Gönder", "Send"),
                negativeText: Loc.T("Vazgeç", "Cancel"),
                affirmativeAction: (text) =>
                {
                    ResumeDialogAfterInquiryRequested?.Invoke();
                    if (string.IsNullOrWhiteSpace(text)) return;
                    string trimmed = text!.Trim();

                    CurrentConversationSpeech = $"[{myName}]: {trimmed}";
                    RefreshActiveNativeConversation();

                    SendDialogPacket(new P2PDialogPacket
                    {
                        SessionId = ActiveSessionId,
                        InitiatorPeerId = ActiveInitiatorPeerId,
                        TargetPeerId = ActiveTargetPeerId,
                        SenderPeerId = myPeerId,
                        SenderName = myName,
                        Stage = 2,
                        OptionKey = "custom_line",
                        SpeakerName = myName,
                        DialogText = trimmed
                    });
                },
                negativeAction: () => { ResumeDialogAfterInquiryRequested?.Invoke(); },
                shouldInputBeObfuscated: false,
                defaultInputText: Loc.T("Seni dinliyorum yoldaş!", "I hear you, comrade!")));
        }

        private void SendNativeResponsePacket(string optionKey, string text)
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);
            CurrentConversationSpeech = text;
            SendDialogPacket(new P2PDialogPacket
            {
                SessionId = ActiveSessionId,
                InitiatorPeerId = ActiveInitiatorPeerId,
                TargetPeerId = ActiveTargetPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 4,
                OptionKey = optionKey,
                SpeakerName = myName,
                DialogText = text
            });
            RefreshActiveNativeConversation();
        }

        public void ExecuteOpenTrade()
        {
            int partnerPeerId = GetActivePartnerPeerId();
            if (partnerPeerId <= 0) return;
            OpenLordBarterWithPlayer(partnerPeerId);
        }

        public void ExecuteCloseTradeOverlay()
        {
            if (!IsInDialog)
            {
                CloseDialogLocalRequested?.Invoke();
                return;
            }

            ClearLocalDialogState();
            CloseDialogLocalRequested?.Invoke();
        }

        public void OpenNativeTradeWithPlayer(int partnerPeerId) => OpenLordBarterWithPlayer(partnerPeerId);

        public void OpenLordBarterWithPlayer(int partnerPeerId) => OpenLordBarterWithPlayer(partnerPeerId, notifyPartner: true);

        public void OpenLordBarterWithPlayer(int partnerPeerId, bool notifyPartner, string? explicitSessionId = null, int? explicitInitPeer = null, int? explicitTargPeer = null)
        {
            if (partnerPeerId <= 0) return;

            var partnerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(partnerPeerId);
            var partnerParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(partnerPeerId);
            if (Hero.MainHero == null || partnerHero == null || partnerParty == null || !partnerParty.IsActive)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Teklif] Karşı oyuncunun kahramanı veya birliği haritada bulunamadı.", "[Barter] Partner player hero or party was not found."),
                    Colors.Red));
                return;
            }

            try
            {
                try { InformationManager.HideInquiry(); } catch { }

                int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
                string sessionId = !string.IsNullOrEmpty(explicitSessionId)
                    ? explicitSessionId!
                    : (!string.IsNullOrEmpty(ActiveSessionId) ? ActiveSessionId : $"livebarter_{Math.Min(myPeerId, partnerPeerId)}_{Math.Max(myPeerId, partnerPeerId)}");

                int initPeer = explicitInitPeer ?? (ActiveInitiatorPeerId > 0 ? ActiveInitiatorPeerId : myPeerId);
                int targPeer = explicitTargPeer ?? (ActiveTargetPeerId > 0 ? ActiveTargetPeerId : partnerPeerId);

                if (IsLiveBarterOpen && LiveBarterPartnerPeerId == partnerPeerId && LiveBarterSessionId == sessionId)
                {
                    return;
                }

                // Ensure native 3D conversation is active BEFORE StartBarterOffer so Gauntlet BarterView always opens.
                var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
                if (cm == null || !cm.IsConversationInProgress)
                {
                    IsInDialog = true;
                    ActiveSessionId = sessionId;
                    ActiveInitiatorPeerId = initPeer;
                    ActiveTargetPeerId = targPeer;

                    _pendingLiveBarterOpenPeerId = partnerPeerId;
                    _pendingLiveBarterSessionId = sessionId;
                    _pendingLiveBarterInitPeerId = initPeer;
                    _pendingLiveBarterTargPeerId = targPeer;
                    _pendingLiveBarterQueuedUtc = DateTime.UtcNow;

                    OpenNative3DConversation(partnerParty, partnerHero, Loc.T("Canlı takas teklif penceresi açılıyor...", "Opening live barter window..."));

                    if (notifyPartner)
                    {
                        SendLiveBarterOpenPacket(sessionId, initPeer, targPeer, myPeerId);
                    }
                    return;
                }

                StartLiveBarterOfferInternal(partnerPeerId, partnerHero, partnerParty, sessionId, initPeer, targPeer, notifyPartner);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] OpenLordBarterWithPlayer failed: {ex.Message}");
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("Ticaret ekranı açılamadı.", "Could not open trade screen."),
                    Colors.Red));
            }
        }

        private void StartLiveBarterOfferInternal(int partnerPeerId, Hero partnerHero, MobileParty partnerParty, string sessionId, int initPeer, int targPeer, bool notifyPartner)
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            _pendingLiveBarterOpenPeerId = 0;

            RosterSyncService.Instance.SyncLocalPlayerRosterToServer();
            if (partnerParty.LeaderHero != null)
            {
                partnerParty.PartyTradeGold = Math.Max(0, partnerParty.LeaderHero.Gold);
            }

            IsLiveBarterOpen = true;
            LiveBarterSessionId = sessionId;
            LiveBarterInitiatorPeerId = initPeer;
            LiveBarterTargetPeerId = targPeer;
            LiveBarterPartnerPeerId = partnerPeerId;
            LiveBarterLocalAccepted = false;
            LiveBarterPartnerAccepted = false;
            LiveBarterLocalTokens = string.Empty;
            LiveBarterPartnerTokens = string.Empty;
            IsApplyingRemoteBarterSync = false;

            if (notifyPartner)
            {
                SendLiveBarterOpenPacket(sessionId, initPeer, targPeer, myPeerId);
            }

            typeof(BarterManager)
                .GetMethod("ClearHeroCooldowns", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(BarterManager.Instance, null);
            BarterManager.Instance.StartBarterOffer(
                Hero.MainHero,
                partnerHero,
                PartyBase.MainParty,
                partnerParty.Party,
                null,
                null,
                0,
                false,
                null);
        }

        private void SendLiveBarterOpenPacket(string sessionId, int initPeer, int targPeer, int myPeerId)
        {
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);
            SendDialogPacket(new P2PDialogPacket
            {
                SessionId = sessionId,
                InitiatorPeerId = initPeer,
                TargetPeerId = targPeer,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 15, // LiveBarterOpen
                OptionKey = "BARTER_LIVE_OPEN",
                SpeakerName = myName
            });
        }

        public void TickPendingLiveBarter()
        {
            if (_pendingLiveBarterOpenPeerId <= 0 || ModContext.IsServerRunning || !ModContext.IsClientConnected)
            {
                return;
            }

            if ((DateTime.UtcNow - _pendingLiveBarterQueuedUtc).TotalSeconds > 4.0)
            {
                _pendingLiveBarterOpenPeerId = 0;
                return;
            }

            var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
            if (cm == null || !cm.IsConversationInProgress)
            {
                return;
            }

            int peerId = _pendingLiveBarterOpenPeerId;
            string sid = _pendingLiveBarterSessionId;
            int initPeer = _pendingLiveBarterInitPeerId;
            int targPeer = _pendingLiveBarterTargPeerId;
            _pendingLiveBarterOpenPeerId = 0;

            var partnerHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(peerId);
            var partnerParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(peerId);
            if (Hero.MainHero != null && partnerHero != null && partnerParty != null && partnerParty.IsActive)
            {
                StartLiveBarterOfferInternal(peerId, partnerHero, partnerParty, sid, initPeer, targPeer, notifyPartner: false);
            }
        }

        public void NotifyLocalBarterOfferChanged(string localTokens)
        {
            if (!IsLiveBarterOpen || IsApplyingRemoteBarterSync || string.IsNullOrEmpty(LiveBarterSessionId))
            {
                return;
            }

            localTokens ??= string.Empty;
            if (string.Equals(LiveBarterLocalTokens, localTokens, StringComparison.Ordinal))
            {
                return;
            }

            LiveBarterLocalTokens = localTokens;
            LiveBarterLocalAccepted = false;
            LiveBarterPartnerAccepted = false;
            LiveBarterStatusChanged?.Invoke();

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);

            SendDialogPacket(new P2PDialogPacket
            {
                SessionId = LiveBarterSessionId,
                InitiatorPeerId = LiveBarterInitiatorPeerId,
                TargetPeerId = LiveBarterTargetPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 16, // LiveBarterSync
                OptionKey = "BARTER_LIVE_SYNC",
                SpeakerName = myName,
                BoughtItemsCsv = localTokens
            });
        }

        public void ToggleLocalBarterAccept(string localTokens)
        {
            if (!IsLiveBarterOpen || string.IsNullOrEmpty(LiveBarterSessionId))
            {
                return;
            }

            localTokens ??= string.Empty;
            if (string.IsNullOrEmpty(localTokens) && string.IsNullOrEmpty(LiveBarterPartnerTokens))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Teklif] Masada hiçbir teklif bulunmuyor.", "[Barter] Nothing has been placed on the table."),
                    Colors.Yellow));
                return;
            }

            LiveBarterLocalTokens = localTokens;
            LiveBarterLocalAccepted = !LiveBarterLocalAccepted;
            LiveBarterStatusChanged?.Invoke();

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);

            SendDialogPacket(new P2PDialogPacket
            {
                SessionId = LiveBarterSessionId,
                InitiatorPeerId = LiveBarterInitiatorPeerId,
                TargetPeerId = LiveBarterTargetPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 17, // LiveBarterAccept
                OptionKey = "BARTER_LIVE_ACCEPT",
                SpeakerName = myName,
                BoughtItemsCsv = localTokens,
                OfferAmount = LiveBarterLocalAccepted ? 1 : 0
            });
        }

        public void NotifyLocalBarterCancelled()
        {
            if (_suppressLocalBarterCancelNotify || !IsLiveBarterOpen)
            {
                ClearLiveBarterState();
                return;
            }

            string sid = LiveBarterSessionId;
            int initPeer = LiveBarterInitiatorPeerId;
            int targPeer = LiveBarterTargetPeerId;
            ClearLiveBarterState();

            if (string.IsNullOrEmpty(sid) || !ModContext.IsClientConnected)
            {
                return;
            }

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);

            SendDialogPacket(new P2PDialogPacket
            {
                SessionId = sid,
                InitiatorPeerId = initPeer,
                TargetPeerId = targPeer,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 19, // LiveBarterClosed
                OptionKey = "BARTER_LIVE_CANCEL",
                SpeakerName = myName
            });
        }

        public void CloseLiveBarterScreenSilently()
        {
            bool wasOpen = IsLiveBarterOpen;
            ClearLiveBarterState();
            if (!wasOpen) return;

            _suppressLocalBarterCancelNotify = true;
            try
            {
                LiveBarterCloseRequested?.Invoke();
                typeof(BarterManager)
                    .GetProperty(nameof(BarterManager.LastBarterIsAccepted), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.SetValue(BarterManager.Instance, false);
                BarterManager.Instance.Close();
            }
            catch { }
            finally
            {
                _suppressLocalBarterCancelNotify = false;
            }

            var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
            if (cm != null && cm.IsConversationInProgress)
            {
                RefreshActiveNativeConversation();
            }
        }

        public string GetLiveBarterOfferButtonLabel()
        {
            if (!IsLiveBarterOpen)
            {
                return Loc.T("Teklif Ver", "Offer");
            }

            if (LiveBarterLocalAccepted && LiveBarterPartnerAccepted)
            {
                return Loc.T("ONAYLANIYOR (2/2)", "COMMITTING (2/2)");
            }
            if (LiveBarterLocalAccepted)
            {
                return Loc.T("ONAYLANDI (1/2) - İPTAL", "ACCEPTED (1/2) - UNDO");
            }
            if (LiveBarterPartnerAccepted)
            {
                return Loc.T("ONAYLA (KARŞI HAZIR 1/2)", "CONFIRM (PARTNER READY 1/2)");
            }
            return Loc.T("TEKLİFİ ONAYLA (0/2)", "CONFIRM OFFER (0/2)");
        }

        public static string RewriteBarterTokensSide(string? tokensCsv, string newSide)
        {
            if (string.IsNullOrWhiteSpace(tokensCsv)) return string.Empty;

            var rewritten = new List<string>();
            foreach (string raw in tokensCsv!.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] parts = raw.Split(':');
                if (parts.Length < 3) continue;

                if (parts[0] == "P")
                {
                    rewritten.Add(raw);
                    continue;
                }

                parts[1] = newSide;
                rewritten.Add(string.Join(":", parts));
            }
            return string.Join("|", rewritten);
        }

        public static string CombineTwoSideBarterTokens(string? initSideTokens, string? targSideTokens)
        {
            string left = RewriteBarterTokensSide(initSideTokens, "I");
            string right = RewriteBarterTokensSide(targSideTokens, "R");
            if (string.IsNullOrEmpty(left)) return right;
            if (string.IsNullOrEmpty(right)) return left;
            return left + "|" + right;
        }

        /// <summary>
        /// Ensures items valued &lt;= 100 (grain, food, mounts, arrows) also appear when bartering with another player,
        /// because native ItemBarterBehavior filters out items with base value &lt;= 100.
        /// </summary>
        public void AppendAffordablePlayerBarterables(BarterData? args)
        {
            if (args?.OtherHero == null || !ModContext.IsClientConnected) return;
            if (!NetworkObjectRegistry.IsPlayerNetworkHero(args.OtherHero)) return;

            try
            {
                AddAffordablePartyItems(args, args.OffererHero, args.OtherHero, args.OffererParty, args.OtherParty);
                AddAffordablePartyItems(args, args.OtherHero, args.OffererHero, args.OtherParty, args.OffererParty);
            }
            catch { }
        }

        private static void AddAffordablePartyItems(BarterData args, Hero ownerHero, Hero otherHero, PartyBase? ownerParty, PartyBase? otherParty)
        {
            if (ownerParty?.ItemRoster == null || ownerHero == null || otherHero == null) return;

            var existingKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var existing in args.GetBarterables())
            {
                if (existing is ItemBarterable ib && ib.OriginalOwner == ownerHero && ib.ItemRosterElement.EquipmentElement.Item != null)
                {
                    var eq = ib.ItemRosterElement.EquipmentElement;
                    string k = eq.ItemModifier != null ? $"{eq.Item.StringId}@{eq.ItemModifier.StringId}" : eq.Item.StringId;
                    existingKeys.Add(k);
                }
            }

            for (int i = 0; i < ownerParty.ItemRoster.Count; i++)
            {
                var elem = ownerParty.ItemRoster.GetElementCopyAtIndex(i);
                if (elem.Amount <= 0 || elem.EquipmentElement.Item == null) continue;

                var eq = elem.EquipmentElement;
                string key = eq.ItemModifier != null ? $"{eq.Item.StringId}@{eq.ItemModifier.StringId}" : eq.Item.StringId;
                if (existingKeys.Contains(key)) continue;

                int baseVal = Math.Max(1, eq.GetBaseValue());
                args.AddBarterable<ItemBarterGroup>(
                    new ItemBarterable(ownerHero, otherHero, ownerParty, otherParty, elem, baseVal),
                    false);
                existingKeys.Add(key);
            }
        }

        public void InterceptAndSendLordBarterOffer(Hero? offererHero, Hero? otherHero, BarterData? barterData)
        {
            if (otherHero == null || barterData == null) return;
            if (!NetworkObjectRegistry.TryExtractPeerId(otherHero.StringId, out int partnerPeerId) || partnerPeerId <= 0) return;

            var offered = barterData.GetOfferedBarterables();
            if (offered == null || offered.Count == 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Teklif] Masaya herhangi bir teklif koyulmadı.", "[Barter] No barterables were offered."),
                    Colors.Yellow));
                return;
            }

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            if (partnerPeerId == myPeerId) return;

            string tokensCsv = EncodeLordBarterTokens(offererHero ?? Hero.MainHero, offered);
            if (string.IsNullOrEmpty(tokensCsv)) return;

            string partnerName = otherHero.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", partnerPeerId);
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);
            string sessionId = !string.IsNullOrEmpty(ActiveSessionId) ? ActiveSessionId : $"barter_{myPeerId}_{partnerPeerId}_{Environment.TickCount}";

            SendDialogPacket(new P2PDialogPacket
            {
                SessionId = $"barter_{myPeerId}_{partnerPeerId}_{Environment.TickCount}",
                InitiatorPeerId = myPeerId,
                TargetPeerId = partnerPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 12,
                OptionKey = "BARTER_V1",
                SpeakerName = myName,
                BoughtItemsCsv = tokensCsv,
                DialogText = FormatLordBarterSummary(tokensCsv, perspectiveIsPartner: false, partnerName, myName),
                OfferObjectId = sessionId
            });

            CurrentConversationSpeech = Loc.F("Teklifimi sundum: {0}", "I sent my offer: {0}",
                FormatLordBarterSummary(tokensCsv, perspectiveIsPartner: false, partnerName, myName).Replace("\n", " | "));
            SetConversationSpeechVariables(CurrentConversationSpeech, PendingProposalKey);

            InformationManager.DisplayMessage(new InformationMessage(
                Loc.F("{0} adlı lorda ticaret teklifi gönderildi (Yanıt bekleniyor).",
                      "Trade offer sent to {0} (Awaiting response).", partnerName),
                Colors.Cyan));
        }

        public static string EncodeLordBarterTokens(Hero? offererHero, IReadOnlyList<Barterable>? offered, Hero? filterOwner = null)
        {
            if (offered == null || offered.Count == 0) return string.Empty;

            var tokens = new List<string>(offered.Count);
            foreach (var b in offered)
            {
                if (b == null) continue;
                if (filterOwner != null && b.OriginalOwner != filterOwner) continue;
                string side = (offererHero != null && b.OriginalOwner == offererHero) ? "I" : "R";

                if (b is GoldBarterable && b.CurrentAmount > 0)
                {
                    tokens.Add($"G:{side}:{b.CurrentAmount}");
                }
                else if (b is ItemBarterable itemBarter && b.CurrentAmount > 0 && itemBarter.ItemRosterElement.EquipmentElement.Item != null)
                {
                    var eq = itemBarter.ItemRosterElement.EquipmentElement;
                    string itemKey = eq.ItemModifier != null ? $"{eq.Item.StringId}@{eq.ItemModifier.StringId}" : eq.Item.StringId;
                    tokens.Add($"T:{side}:{itemKey}:{b.CurrentAmount}");
                }
                else if (b is FiefBarterable fiefBarter && fiefBarter.TargetSettlement != null)
                {
                    tokens.Add($"F:{side}:{fiefBarter.TargetSettlement.StringId}");
                }
                else if (b is PeaceBarterable peaceBarter && peaceBarter.PeaceOfferingFaction != null && peaceBarter.OfferedFaction != null)
                {
                    tokens.Add($"P:{peaceBarter.PeaceOfferingFaction.StringId}:{peaceBarter.OfferedFaction.StringId}");
                }
                else if (b is SetPrisonerFreeBarterable)
                {
                    var prisoner = SetPrisonerFreeHeroField?.GetValue(b) as Hero;
                    if (prisoner != null) tokens.Add($"R:{side}:{prisoner.StringId}");
                }
                else if (b is TransferPrisonerBarterable)
                {
                    var prisoner = TransferPrisonerHeroField?.GetValue(b) as Hero;
                    if (prisoner != null) tokens.Add($"X:{side}:{prisoner.StringId}");
                }
            }

            return string.Join("|", tokens);
        }

        public static string FormatLordBarterSummary(string? tokensCsv, bool perspectiveIsPartner, string partnerName, string proposerName)
        {
            if (string.IsNullOrEmpty(tokensCsv)) return Loc.T("Boş teklif", "Empty offer");

            var youGive = new List<string>();
            var youGet = new List<string>();
            var diplomatic = new List<string>();

            foreach (string raw in tokensCsv!.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] parts = raw.Split(':');
                if (parts.Length < 3) continue;

                string kind = parts[0];
                if (kind == "P")
                {
                    diplomatic.Add(Loc.T("Kalıcı Barış Antlaşması (Savaşı Bitir)", "Peace Treaty (End War)"));
                    continue;
                }

                bool proposerGives = parts[1] == "I";
                bool viewerGives = perspectiveIsPartner ? !proposerGives : proposerGives;
                var bucket = viewerGives ? youGive : youGet;

                if (kind == "G" && int.TryParse(parts[2], out int gold) && gold > 0)
                {
                    bucket.Add($"{gold} {Loc.T("Altın", "Gold")}");
                }
                else if (kind == "T" && parts.Length >= 4 && int.TryParse(parts[3], out int amt) && amt > 0)
                {
                    string[] itemParts = parts[2].Split('@');
                    var itemObj = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<ItemObject>(itemParts[0]);
                    string name = itemObj?.Name?.ToString() ?? itemParts[0];
                    bucket.Add($"{amt}x {name}");
                }
                else if (kind == "F")
                {
                    var s = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<Settlement>(parts[2]);
                    bucket.Add(Loc.F("Yerleşke: {0}", "Fief: {0}", s?.Name?.ToString() ?? parts[2]));
                }
                else if (kind == "R")
                {
                    var h = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<Hero>(parts[2]);
                    bucket.Add(Loc.F("Esir Salıverme: {0}", "Release Prisoner: {0}", h?.Name?.ToString() ?? parts[2]));
                }
                else if (kind == "X")
                {
                    var h = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<Hero>(parts[2]);
                    bucket.Add(Loc.F("Esir Devri: {0}", "Transfer Prisoner: {0}", h?.Name?.ToString() ?? parts[2]));
                }
            }

            string giveStr = youGive.Count > 0 ? string.Join(", ", youGive) : Loc.T("Yok", "None");
            string getStr = youGet.Count > 0 ? string.Join(", ", youGet) : Loc.T("Yok", "None");
            string dipStr = diplomatic.Count > 0 ? $"\n{Loc.T("Diplomatik", "Diplomatic")}: {string.Join(", ", diplomatic)}" : string.Empty;

            return perspectiveIsPartner
                ? Loc.F(
                    "{0} size bir ticaret teklifi sundu:\n\n" +
                    "Sizin Verecekleriniz: {1}\n" +
                    "Sizin Alacaklarınız: {2}{3}\n\n" +
                    "Teklifi kabul ediyor musunuz?",
                    "{0} offered a trade:\n\n" +
                    "You Give: {1}\n" +
                    "You Receive: {2}{3}\n\n" +
                    "Do you accept this offer?",
                    proposerName, giveStr, getStr, dipStr)
                : Loc.F(
                    "{0} -> {1} [Vereceğiniz: {2} | Alacağınız: {3}{4}]",
                    "{0} -> {1} [Offering: {2} | Requesting: {3}{4}]",
                    proposerName, partnerName, giveStr, getStr, dipStr);
        }

        public bool TryHandleNativeTradeDone(InventoryLogic logic, out bool doneResult)
        {
            doneResult = false;
            if (logic == null || !logic.IsTrading) return false;

            var otherParty = logic.OtherParty?.MobileParty;
            if (otherParty == null || !NetworkObjectRegistry.TryExtractPeerId(otherParty.StringId, out int partnerPeerId))
            {
                return false;
            }

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            if (partnerPeerId == myPeerId) return false;

            object? history = InventoryTransactionHistoryField?.GetValue(logic);
            if (history == null) return false;

            var historyType = history.GetType();
            var boughtRaw = historyType.GetMethod("GetBoughtItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(history, null)
                as List<ValueTuple<ItemRosterElement, int>>;
            var soldRaw = historyType.GetMethod("GetSoldItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(history, null)
                as List<ValueTuple<ItemRosterElement, int>>;

            string boughtCsv = SerializeTransactionItems(boughtRaw);
            string soldCsv = SerializeTransactionItems(soldRaw);
            int netGoldDelta = logic.TotalAmount;

            if (string.IsNullOrEmpty(boughtCsv) && string.IsNullOrEmpty(soldCsv))
            {
                // Empty trade: let native InventoryLogic.DoneLogic exit cleanly with no side effects.
                return false;
            }

            int myGold = Hero.MainHero?.Gold ?? 0;
            int partnerGold = otherParty.LeaderHero?.Gold ?? otherParty.PartyTradeGold;
            if (myGold + netGoldDelta < 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Ticaret] Bu takası tamamlamak için yeterli altınınız yok!", "[Trade] You do not have enough gold for this trade!"),
                    Colors.Red));
                doneResult = false;
                return true;
            }
            if (partnerGold - netGoldDelta < 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Ticaret] Karşı oyuncunun bu takas için yeterli altını yok!", "[Trade] Partner does not have enough gold for this trade!"),
                    Colors.Red));
                doneResult = false;
                return true;
            }

            // Reset local rosters/gold immediately so neither side mutates state until partner approves & server commits.
            try
            {
                logic.Reset(false);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] Native trade Reset(false) warning: {ex.Message}");
            }

            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);
            string partnerName = otherParty.LeaderHero?.Name?.ToString() ?? otherParty.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", partnerPeerId);
            string tradeSessionId = $"ntrade_{myPeerId}_{partnerPeerId}_{Environment.TickCount}";

            SendDialogPacket(new P2PDialogPacket
            {
                SessionId = tradeSessionId,
                InitiatorPeerId = myPeerId,
                TargetPeerId = partnerPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 12, // NativeTradeOffer
                OptionKey = "native_trade_offer",
                SpeakerName = myName,
                BoughtItemsCsv = boughtCsv,
                SoldItemsCsv = soldCsv,
                NetGoldDelta = netGoldDelta
            });

            InformationManager.DisplayMessage(new InformationMessage(
                Loc.F("[Ticaret] {0} adlı oyuncuya ticaret teklifi gönderildi (Onayı bekleniyor).", "[Trade] Trade offer sent to {0} (Awaiting approval).", partnerName),
                Colors.Cyan));

            doneResult = true;
            return true;
        }

        public static string SerializeTransactionItems(List<ValueTuple<ItemRosterElement, int>>? entries)
        {
            if (entries == null || entries.Count == 0) return string.Empty;

            var aggregated = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var eq = entry.Item1.EquipmentElement;
                int count = entry.Item1.Amount;
                if (eq.Item == null || count <= 0) continue;

                string key = eq.ItemModifier != null
                    ? $"{eq.Item.StringId}@{eq.ItemModifier.StringId}"
                    : eq.Item.StringId;

                if (aggregated.TryGetValue(key, out int existing))
                {
                    aggregated[key] = existing + count;
                }
                else
                {
                    aggregated[key] = count;
                }
            }

            if (aggregated.Count == 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var kv in aggregated)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key).Append(':').Append(kv.Value);
            }
            return sb.ToString();
        }

        public static List<(string ItemId, string ModifierId, int Count)> ParseTradeItemCsv(string? csv)
        {
            var result = new List<(string, string, int)>();
            if (string.IsNullOrWhiteSpace(csv)) return result;

            string[] tokens = csv!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in tokens)
            {
                int colonIdx = raw.LastIndexOf(':');
                if (colonIdx <= 0 || colonIdx >= raw.Length - 1) continue;

                string keyPart = raw.Substring(0, colonIdx);
                if (!int.TryParse(raw.Substring(colonIdx + 1), out int count) || count <= 0) continue;

                int atIdx = keyPart.IndexOf('@');
                string itemId = atIdx > 0 ? keyPart.Substring(0, atIdx) : keyPart;
                string modId = atIdx > 0 && atIdx < keyPart.Length - 1 ? keyPart.Substring(atIdx + 1) : string.Empty;
                result.Add((itemId, modId, count));
            }
            return result;
        }

        private static string FormatTradeItemsDisplay(string? csv)
        {
            var parsed = ParseTradeItemCsv(csv);
            if (parsed.Count == 0) return Loc.T("Yok", "None");

            var om = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager;
            var parts = new List<string>(parsed.Count);
            foreach (var (itemId, modId, count) in parsed)
            {
                var item = om?.GetObject<ItemObject>(itemId);
                var mod = !string.IsNullOrEmpty(modId) ? om?.GetObject<ItemModifier>(modId) : null;
                string itemName = item?.Name?.ToString() ?? itemId;
                string label = mod != null ? $"{count}x {mod.Name} {itemName}" : $"{count}x {itemName}";
                parts.Add(label);
            }
            return string.Join(", ", parts);
        }

        private static bool TryValidateAndApplyLordBarterTokens(
            string? tokensCsv,
            Hero? proposerHero,
            Hero? partnerHero,
            MobileParty? proposerParty,
            MobileParty? partnerParty,
            out bool hasWorldDiplomacyChange,
            out string failReason)
        {
            hasWorldDiplomacyChange = false;
            failReason = string.Empty;

            if (string.IsNullOrWhiteSpace(tokensCsv) || TaleWorlds.CampaignSystem.Campaign.Current == null)
            {
                failReason = Loc.T("Geçersiz teklif verisi.", "Invalid barter payload.");
                return false;
            }

            var om = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager;
            string[] rawTokens = tokensCsv!.Split('|');

            int proposerGoldSpend = 0;
            int partnerGoldSpend = 0;
            var proposerItemSpend = new Dictionary<(string id, string mod), int>();
            var partnerItemSpend = new Dictionary<(string id, string mod), int>();

            // Phase 1: Validate totals across both parties before mutating any game object
            foreach (string raw in rawTokens)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] parts = raw.Split(':');
                if (parts.Length < 3) continue;

                string kind = parts[0];
                if (kind == "P")
                {
                    hasWorldDiplomacyChange = true;
                    continue;
                }

                bool fromProposer = parts[1] == "I";
                var srcHero = fromProposer ? proposerHero : partnerHero;
                var srcParty = fromProposer ? proposerParty : partnerParty;

                if (kind == "G")
                {
                    if (!int.TryParse(parts[2], out int gold) || gold <= 0) continue;
                    if (fromProposer) proposerGoldSpend += gold;
                    else partnerGoldSpend += gold;
                }
                else if (kind == "T" && parts.Length >= 4 && int.TryParse(parts[3], out int amt) && amt > 0)
                {
                    string[] ip = parts[2].Split('@');
                    var dict = fromProposer ? proposerItemSpend : partnerItemSpend;
                    var key = (ip[0], ip.Length > 1 ? ip[1] : string.Empty);
                    dict[key] = dict.TryGetValue(key, out int cur) ? cur + amt : amt;
                }
                else if (kind == "F")
                {
                    var s = om.GetObject<Settlement>(parts[2]);
                    if (s == null || srcHero?.Clan == null || s.OwnerClan != srcHero.Clan)
                    {
                        failReason = Loc.T("Teklif edilen yerleşke sahipliği değişmiş.", "Settlement ownership changed.");
                        return false;
                    }
                    hasWorldDiplomacyChange = true;
                }
                else if (kind == "R" || kind == "X")
                {
                    var prisoner = om.GetObject<Hero>(parts[2]);
                    if (prisoner == null || !prisoner.IsPrisoner || prisoner.PartyBelongedToAsPrisoner != srcParty?.Party)
                    {
                        failReason = Loc.T("Teklif edilen esir artık birlikte değil.", "Prisoner is no longer held by the party.");
                        return false;
                    }
                }
            }

            if (proposerHero == null || partnerHero == null || proposerHero.Gold < proposerGoldSpend || partnerHero.Gold < partnerGoldSpend)
            {
                failReason = Loc.T("Taraflardan birinin teklif için yeterli altını kalmamış.", "One of the players no longer has enough gold.");
                return false;
            }

            if (!HasAllPartyItems(proposerParty, om, proposerItemSpend) || !HasAllPartyItems(partnerParty, om, partnerItemSpend))
            {
                failReason = Loc.T("Taraflardan birinin teklif ettiği eşyalar envanterde bulunamadı.", "One of the players no longer holds all offered items.");
                return false;
            }

            // Phase 2: Commit atomically
            proposerHero.Gold = proposerHero.Gold - proposerGoldSpend + partnerGoldSpend;
            partnerHero.Gold = partnerHero.Gold - partnerGoldSpend + proposerGoldSpend;

            foreach (string raw in rawTokens)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] parts = raw.Split(':');
                if (parts.Length < 3) continue;

                string kind = parts[0];
                if (kind == "P")
                {
                    var f1 = proposerHero.MapFaction;
                    var f2 = partnerHero.MapFaction;
                    if (f1 != null && f2 != null && FactionManager.IsAtWarAgainstFaction(f1, f2))
                    {
                        try { MakePeaceAction.Apply(f1, f2); } catch { }
                    }
                    continue;
                }

                bool fromProposer = parts[1] == "I";
                var srcHero = fromProposer ? proposerHero : partnerHero;
                var dstHero = fromProposer ? partnerHero : proposerHero;
                var srcParty = fromProposer ? proposerParty : partnerParty;
                var dstParty = fromProposer ? partnerParty : proposerParty;

                if (kind == "T" && parts.Length >= 4 && int.TryParse(parts[3], out int amt) && amt > 0)
                {
                    string[] ip = parts[2].Split('@');
                    var item = om.GetObject<ItemObject>(ip[0]);
                    var mod = ip.Length > 1 && !string.IsNullOrEmpty(ip[1]) ? om.GetObject<ItemModifier>(ip[1]) : null;
                    if (item != null)
                    {
                        var eq = new EquipmentElement(item, mod);
                        srcParty?.ItemRoster?.AddToCounts(eq, -amt);
                        dstParty?.ItemRoster?.AddToCounts(eq, amt);
                    }
                }
                else if (kind == "F")
                {
                    var settlement = om.GetObject<Settlement>(parts[2]);
                    if (settlement != null && dstHero != null)
                    {
                        try { ChangeOwnerOfSettlementAction.ApplyByBarter(dstHero, settlement); } catch { }
                    }
                }
                else if (kind == "R")
                {
                    var prisoner = om.GetObject<Hero>(parts[2]);
                    if (prisoner != null && dstHero != null)
                    {
                        try { EndCaptivityAction.ApplyByRansom(prisoner, dstHero); } catch { }
                    }
                }
                else if (kind == "X")
                {
                    var prisoner = om.GetObject<Hero>(parts[2]);
                    if (prisoner != null && srcParty?.Party != null && dstParty?.Party != null)
                    {
                        try { TransferPrisonerAction.Apply(prisoner.CharacterObject, srcParty.Party, dstParty.Party); } catch { }
                    }
                }
            }

            return true;
        }

        private static bool HasAllPartyItems(MobileParty? party, TaleWorlds.ObjectSystem.MBObjectManager om, Dictionary<(string id, string mod), int> required)
        {
            if (required.Count == 0) return true;
            if (party?.ItemRoster == null) return false;

            foreach (var kv in required)
            {
                var item = om.GetObject<ItemObject>(kv.Key.id);
                var mod = !string.IsNullOrEmpty(kv.Key.mod) ? om.GetObject<ItemModifier>(kv.Key.mod) : null;
                if (item == null) return false;

                int idx = party.ItemRoster.FindIndexOfElement(new EquipmentElement(item, mod));
                int avail = idx >= 0 ? party.ItemRoster.GetElementNumber(idx) : 0;
                if (avail < kv.Value) return false;
            }
            return true;
        }

        private static void ApplyClientLordBarterSide(string? tokensCsv, int myPeerId, int proposerPeerId, int partnerPeerId)
        {
            if (string.IsNullOrWhiteSpace(tokensCsv) || TaleWorlds.CampaignSystem.Campaign.Current == null) return;

            var om = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager;
            var proposerHero = myPeerId == proposerPeerId ? Hero.MainHero : NetworkObjectRegistry.Instance.ResolvePlayerHero(proposerPeerId);
            var partnerHero = myPeerId == partnerPeerId ? Hero.MainHero : NetworkObjectRegistry.Instance.ResolvePlayerHero(partnerPeerId);
            var proposerParty = myPeerId == proposerPeerId ? MobileParty.MainParty : NetworkObjectRegistry.Instance.ResolvePlayerParty(proposerPeerId);
            var partnerParty = myPeerId == partnerPeerId ? MobileParty.MainParty : NetworkObjectRegistry.Instance.ResolvePlayerParty(partnerPeerId);

            bool iAmInvolved = myPeerId == proposerPeerId || myPeerId == partnerPeerId;
            bool iAmProposer = myPeerId == proposerPeerId;

            foreach (string raw in tokensCsv!.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] parts = raw.Split(':');
                if (parts.Length < 3) continue;

                string kind = parts[0];
                if (kind == "P")
                {
                    if (proposerHero?.MapFaction != null && partnerHero?.MapFaction != null &&
                        FactionManager.IsAtWarAgainstFaction(proposerHero.MapFaction, partnerHero.MapFaction))
                    {
                        try { MakePeaceAction.Apply(proposerHero.MapFaction, partnerHero.MapFaction); } catch { }
                    }
                    continue;
                }

                bool fromProposer = parts[1] == "I";
                if (kind == "F")
                {
                    var settlement = om.GetObject<Settlement>(parts[2]);
                    var dstHero = fromProposer ? partnerHero : proposerHero;
                    if (settlement != null && dstHero != null && settlement.Owner != dstHero)
                    {
                        try { ChangeOwnerOfSettlementAction.ApplyByBarter(dstHero, settlement); } catch { }
                    }
                    continue;
                }

                if (!iAmInvolved) continue;

                bool affectsMeAsGiver = (iAmProposer && fromProposer) || (!iAmProposer && !fromProposer);
                int sign = affectsMeAsGiver ? -1 : +1;
                var remoteHero = iAmProposer ? partnerHero : proposerHero;
                var remoteParty = iAmProposer ? partnerParty : proposerParty;

                if (kind == "G" && int.TryParse(parts[2], out int gold) && gold > 0)
                {
                    if (Hero.MainHero != null)
                    {
                        Hero.MainHero.Gold = Math.Max(0, Hero.MainHero.Gold + (gold * sign));
                    }
                    if (remoteHero != null && remoteHero != Hero.MainHero)
                    {
                        remoteHero.Gold = Math.Max(0, remoteHero.Gold - (gold * sign));
                        if (remoteParty != null)
                        {
                            remoteParty.PartyTradeGold = remoteHero.Gold;
                        }
                    }
                }
                else if (kind == "T" && parts.Length >= 4 && int.TryParse(parts[3], out int amt) && amt > 0)
                {
                    string[] ip = parts[2].Split('@');
                    var item = om.GetObject<ItemObject>(ip[0]);
                    var mod = ip.Length > 1 && !string.IsNullOrEmpty(ip[1]) ? om.GetObject<ItemModifier>(ip[1]) : null;
                    if (item != null)
                    {
                        var eq = new EquipmentElement(item, mod);
                        if (MobileParty.MainParty?.ItemRoster != null)
                        {
                            if (sign > 0)
                            {
                                MobileParty.MainParty.ItemRoster.AddToCounts(eq, amt);
                            }
                            else
                            {
                                int idx = MobileParty.MainParty.ItemRoster.FindIndexOfElement(eq);
                                int avail = idx >= 0 ? MobileParty.MainParty.ItemRoster.GetElementNumber(idx) : 0;
                                if (avail > 0)
                                {
                                    MobileParty.MainParty.ItemRoster.AddToCounts(eq, -Math.Min(avail, amt));
                                }
                            }
                        }
                        if (remoteParty != null && remoteParty != MobileParty.MainParty && remoteParty.ItemRoster != null)
                        {
                            if (sign < 0)
                            {
                                remoteParty.ItemRoster.AddToCounts(eq, amt);
                            }
                            else
                            {
                                int rIdx = remoteParty.ItemRoster.FindIndexOfElement(eq);
                                int rAvail = rIdx >= 0 ? remoteParty.ItemRoster.GetElementNumber(rIdx) : 0;
                                if (rAvail > 0)
                                {
                                    remoteParty.ItemRoster.AddToCounts(eq, -Math.Min(rAvail, amt));
                                }
                            }
                        }
                    }
                }
                else if (kind == "R" && !affectsMeAsGiver)
                {
                    var prisoner = om.GetObject<Hero>(parts[2]);
                    if (prisoner != null && Hero.MainHero != null)
                    {
                        try { EndCaptivityAction.ApplyByRansom(prisoner, Hero.MainHero); } catch { }
                    }
                }
                else if (kind == "X")
                {
                    var prisoner = om.GetObject<Hero>(parts[2]);
                    var srcParty = fromProposer ? proposerParty : partnerParty;
                    var dstParty = fromProposer ? partnerParty : proposerParty;
                    if (prisoner != null && srcParty?.Party != null && dstParty?.Party != null)
                    {
                        try { TransferPrisonerAction.Apply(prisoner.CharacterObject, srcParty.Party, dstParty.Party); } catch { }
                    }
                }
            }
        }

        private static void ApplyTradeItemCsvDelta(MobileParty? party, string? csv, int sign)
        {
            if (party?.ItemRoster == null || string.IsNullOrWhiteSpace(csv) || TaleWorlds.CampaignSystem.Campaign.Current == null) return;

            var om = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager;
            var roster = party.ItemRoster;
            foreach (var (itemId, modId, count) in ParseTradeItemCsv(csv))
            {
                var item = om.GetObject<ItemObject>(itemId);
                if (item == null || count <= 0) continue;

                var mod = !string.IsNullOrEmpty(modId) ? om.GetObject<ItemModifier>(modId) : null;
                var eq = new EquipmentElement(item, mod);

                if (sign >= 0)
                {
                    roster.AddToCounts(eq, count);
                }
                else
                {
                    int idx = roster.FindIndexOfElement(eq);
                    int avail = idx >= 0 ? roster.GetElementNumber(idx) : 0;
                    if (avail > 0)
                    {
                        roster.AddToCounts(eq, -Math.Min(avail, count));
                    }
                    else if (mod == null)
                    {
                        int fallbackIdx = roster.FindIndexOfItem(item);
                        int fallbackAvail = fallbackIdx >= 0 ? roster.GetElementNumber(fallbackIdx) : 0;
                        if (fallbackAvail > 0)
                        {
                            roster.AddToCounts(item, -Math.Min(fallbackAvail, count));
                        }
                    }
                }
            }
        }

        private static void HaltLocalPartyAndSyncStop()
        {
            var mainParty = MobileParty.MainParty;
            if (mainParty == null) return;

            mainParty.SetMoveModeHold();
            var pos = mainParty.GetPosition2D;
            ModContext.Events.Publish(new LocalPartyMoveRequestedEvent
            {
                MoveType = 0,
                TargetSettlementId = string.Empty,
                TargetPartyId = string.Empty,
                CurrentX = pos.X,
                CurrentY = pos.Y,
                TargetX = pos.X,
                TargetY = pos.Y,
                Speed = 0f
            });
        }

        /// <summary>
        /// Returns all other active connected player parties (cc_party_*) currently inside or at the gate of the given settlement.
        /// </summary>
        public List<MobileParty> GetOtherPlayersInSettlement(Settlement? settlement)
        {
            var result = new List<MobileParty>();
            if (settlement == null || TaleWorlds.CampaignSystem.Campaign.Current == null) return result;

            int localPeerId = ModContext.Client?.LocalPeerId ?? -1;
            Vec2 gatePos = settlement.GatePosition.IsValid() ? settlement.GatePosition.ToVec2() : settlement.GetPosition2D;

            foreach (var party in MobileParty.All)
            {
                if (party == null || !party.IsActive || party.IsMainParty) continue;
                if (!NetworkObjectRegistry.IsPlayerNetworkParty(party)) continue;
                if (NetworkObjectRegistry.TryExtractPeerId(party.StringId, out int peerId) && peerId == localPeerId) continue;

                bool inSettlement = (party.CurrentSettlement == settlement);
                bool atGate = (party.GetPosition2D - gatePos).Length <= 6.5f;

                if (inSettlement || atGate)
                {
                    result.Add(party);
                }
            }

            return result;
        }

        /// <summary>
        /// Opens the player selection list for players in the current settlement (or all online players on the map).
        /// </summary>
        public void OpenSettlementPlayersMenu(Settlement? settlement)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

            var playersHere = GetOtherPlayersInSettlement(settlement);
            if (playersHere.Count == 1)
            {
                OpenInteractionMenu(playersHere[0]);
                return;
            }

            var candidates = playersHere.Count > 0
                ? playersHere
                : MobileParty.All.Where(p => p != null && p.IsActive && !p.IsMainParty && NetworkObjectRegistry.IsPlayerNetworkParty(p)).ToList();

            if (candidates.Count == 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Calradia-Connected] Şu anda etkileşime geçilebilecek başka bir aktif oyuncu bulunamadı.",
                          "[Calradia-Connected] No other active players available to interact with."),
                    Colors.Yellow));
                return;
            }

            var elements = new List<InquiryElement>();
            foreach (var p in candidates)
            {
                string heroName = p.LeaderHero?.Name?.ToString() ?? p.Name?.ToString() ?? p.StringId;
                string clanName = p.LeaderHero?.Clan?.Name?.ToString() ?? Loc.T("Klan", "Clan");
                string locText = p.CurrentSettlement != null
                    ? Loc.F("Yerleşkede ({0})", "In Settlement ({0})", p.CurrentSettlement.Name)
                    : Loc.F("Haritada ({0} Asker)", "On Map ({0} Troops)", p.MemberRoster?.TotalManCount ?? 1);

                elements.Add(new InquiryElement(
                    p,
                    $"{heroName} [{clanName}] — {locText}",
                    null,
                    true,
                    Loc.T("Bu oyuncuyla canlı diyalog başlatmak için seçin.", "Select to open a synchronized dialog with this player.")));
            }

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                titleText: settlement != null
                    ? Loc.F("{0} — Oyuncu Etkileşimi", "{0} — Player Interaction", settlement.Name)
                    : Loc.T("Kalradya — Oyuncu Etkileşimi", "Calradia — Player Interaction"),
                descriptionText: Loc.T("Konuşmak istediğiniz oyuncuyu seçin:", "Select the player to start a dialog with:"),
                inquiryElements: elements,
                isExitShown: true,
                minSelectableOptionCount: 1,
                maxSelectableOptionCount: 1,
                affirmativeText: Loc.T("Konuşmayı Başlat", "Start Dialog"),
                negativeText: Loc.T("Kapat", "Close"),
                affirmativeAction: (selected) =>
                {
                    var chosen = selected?.FirstOrDefault()?.Identifier as MobileParty;
                    if (chosen != null)
                    {
                        OpenInteractionMenu(chosen);
                    }
                },
                negativeAction: (_) => { }),
                pauseGameActiveState: false,
                prioritize: true);
        }

        public event Action? CloseDialogLocalRequested;
        public event Action? SuspendDialogForInquiryRequested;
        public event Action? ResumeDialogAfterInquiryRequested;

        public void CloseActiveDialogLocalOnly()
        {
            ClearLocalDialogState();
            CloseDialogLocalRequested?.Invoke();
            CloseNative3DConversation();
        }

        private void GetSessionEndpoints(string sessionId, int fallbackOtherPeerId, out int initiatorPeerId, out int targetPeerId)
        {
            if (!string.IsNullOrEmpty(sessionId) && sessionId == ActiveSessionId && ActiveInitiatorPeerId > 0 && ActiveTargetPeerId > 0)
            {
                initiatorPeerId = ActiveInitiatorPeerId;
                targetPeerId = ActiveTargetPeerId;
                return;
            }

            initiatorPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            targetPeerId = fallbackOtherPeerId;
        }

        /// <summary>
        /// Opens a synchronized Native 3D Conversation Scene:
        /// Both players stop on the map immediately, and both enter the Bannerlord 3D conversation scene facing each other's synced hero.
        /// </summary>
        public void OpenInteractionMenu(MobileParty? targetParty)
        {
            if (targetParty == null || !NetworkObjectRegistry.TryExtractPeerId(targetParty.StringId, out int targetPeerId))
            {
                return;
            }

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            if (targetPeerId == myPeerId) return;

            if (IsInDialog)
            {
                if (ActiveTargetPeerId == targetPeerId || ActiveInitiatorPeerId == targetPeerId)
                {
                    return;
                }
                CloseActiveDialogLocalOnly();
            }

            HaltLocalPartyAndSyncStop();
            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "OpenP2PDialog" });

            var targetHero = targetParty.LeaderHero ?? NetworkObjectRegistry.Instance.ResolvePlayerHero(targetPeerId);
            string targetName = targetHero?.Name?.ToString() ?? targetParty.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", targetPeerId);
            string targetClan = targetHero?.Clan?.Name?.ToString() ?? Loc.T("Bağımsız Klan", "Independent Clan");
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);
            int targetTroops = targetParty.MemberRoster?.TotalHealthyCount ?? targetParty.MemberRoster?.TotalManCount ?? 1;
            string locationDesc = targetParty.CurrentSettlement != null
                ? Loc.F("Yerleşke: {0}", "Settlement: {0}", targetParty.CurrentSettlement.Name)
                : Loc.T("Sefer Haritası", "Campaign Map");

            string sessionId = $"{myPeerId}_{targetPeerId}_{Environment.TickCount}";
            string initialLine = Loc.F(
                "Selamlar {0}. Ben {1} ({2}). Ne konuşmak istersin? [{3} | {4} Hazır Asker]",
                "Greetings {0}. I am {1} ({2}). What do you wish to discuss? [{3} | {4} Healthy Troops]",
                myName, targetName, targetClan, locationDesc, targetTroops);

            IsInDialog = true;
            ActiveSessionId = sessionId;
            ActiveInitiatorPeerId = myPeerId;
            ActiveTargetPeerId = targetPeerId;
            PendingProposalKey = string.Empty;
            _pendingProposalSenderPeerId = 0;

            var openPacket = new P2PDialogPacket
            {
                SessionId = sessionId,
                InitiatorPeerId = myPeerId,
                TargetPeerId = targetPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 1, // Open
                OptionKey = "open",
                SpeakerName = myName,
                DialogText = Loc.F("Selamlar {0} ({1}), seninle konuşmak istiyorum!", "Greetings {0} ({1}), let us parley!", targetName, targetClan),
                OfferAmount = targetTroops,
                OfferObjectId = targetClan
            };

            OpenNative3DConversation(targetParty, targetHero, initialLine);
            SendDialogPacket(openPacket);
        }

        public void SendDialogPacket(P2PDialogPacket packet)
        {
            if (ModContext.IsServerRunning)
            {
                HandleDialogPacket(packet);
            }
            else if (ModContext.IsClientConnected)
            {
                ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        private static P2PDialogPacket CreateTradeSnapshotPacket(
            string sessionId,
            int initiatorPeerId,
            int targetPeerId,
            int senderPeerId,
            string senderName,
            byte stage,
            string optionKey,
            string speakerName,
            string dialogText,
            ServerTradeState trade)
        {
            return new P2PDialogPacket
            {
                SessionId = sessionId,
                InitiatorPeerId = initiatorPeerId,
                TargetPeerId = targetPeerId,
                SenderPeerId = senderPeerId,
                SenderName = senderName,
                Stage = stage,
                OptionKey = optionKey,
                SpeakerName = speakerName,
                DialogText = dialogText,
                InitiatorTradeGold = trade.InitiatorGold,
                InitiatorTradeTroopId = trade.InitiatorTroopId,
                InitiatorTradeTroopCount = trade.InitiatorTroopCount,
                InitiatorTradeItemId = trade.InitiatorItemId,
                InitiatorTradeItemCount = trade.InitiatorItemCount,
                InitiatorTradeShipIndex = trade.InitiatorShipIndex,
                InitiatorTradeShipDesc = trade.InitiatorShipDesc,
                InitiatorTradeAccepted = trade.InitiatorAccepted,
                TargetTradeGold = trade.TargetGold,
                TargetTradeTroopId = trade.TargetTroopId,
                TargetTradeTroopCount = trade.TargetTroopCount,
                TargetTradeItemId = trade.TargetItemId,
                TargetTradeItemCount = trade.TargetItemCount,
                TargetTradeShipIndex = trade.TargetShipIndex,
                TargetTradeShipDesc = trade.TargetShipDesc,
                TargetTradeAccepted = trade.TargetAccepted
            };
        }

        private void ClearServerSession(string sessionId, int initPeerId, int targPeerId)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                _serverSessions.Remove(sessionId);
                _serverTrades.Remove(sessionId);
            }
            if (_peerActiveSession.TryGetValue(initPeerId, out string? initSid) && initSid == sessionId)
            {
                _peerActiveSession.Remove(initPeerId);
            }
            if (_peerActiveSession.TryGetValue(targPeerId, out string? targSid) && targSid == sessionId)
            {
                _peerActiveSession.Remove(targPeerId);
            }
        }

        public void HandleDialogPacket(P2PDialogPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

                    if (ModContext.IsServerRunning)
                    {
                        var initHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.InitiatorPeerId);
                        var targHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.TargetPeerId);
                        var initParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.InitiatorPeerId);
                        var targParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.TargetPeerId);
                        string initName = initHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", packet.InitiatorPeerId);
                        string targName = targHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", packet.TargetPeerId);

                        if (packet.Stage == 1) // Open Dialog -> Lock BOTH parties on map and sync target ONLY
                        {
                            initParty?.SetMoveModeHold();
                            targParty?.SetMoveModeHold();

                            if (_peerActiveSession.TryGetValue(packet.TargetPeerId, out string? existingTargetSid) &&
                                _serverSessions.TryGetValue(existingTargetSid, out var existingSess))
                            {
                                bool samePair =
                                    (existingSess.InitiatorPeerId == packet.InitiatorPeerId && existingSess.TargetPeerId == packet.TargetPeerId) ||
                                    (existingSess.InitiatorPeerId == packet.TargetPeerId && existingSess.TargetPeerId == packet.InitiatorPeerId);
                                if (samePair)
                                {
                                    // Two players interacted simultaneously: keep existing session, tell Initiator to close duplicate if different ID.
                                    if (existingTargetSid != packet.SessionId)
                                    {
                                        return;
                                    }
                                }
                                else
                                {
                                    ClearServerSession(existingTargetSid, existingSess.InitiatorPeerId, existingSess.TargetPeerId);
                                }
                            }

                            _serverSessions[packet.SessionId] = new ServerDialogSession
                            {
                                SessionId = packet.SessionId,
                                InitiatorPeerId = packet.InitiatorPeerId,
                                TargetPeerId = packet.TargetPeerId
                            };
                            _serverTrades[packet.SessionId] = new ServerTradeState();
                            _peerActiveSession[packet.InitiatorPeerId] = packet.SessionId;
                            _peerActiveSession[packet.TargetPeerId] = packet.SessionId;

                            // Initiator opened locally; only forward Stage 1 to Target so Target joins immediately.
                            ModContext.Server?.SendTo(packet.TargetPeerId, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 5) // Close Dialog
                        {
                            ClearServerSession(packet.SessionId, packet.InitiatorPeerId, packet.TargetPeerId);
                            int otherPeer = packet.SenderPeerId == packet.InitiatorPeerId ? packet.TargetPeerId : packet.InitiatorPeerId;
                            ModContext.Server?.SendTo(otherPeer, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 15) // LiveBarterOpen -> Create session (or reuse existing active session for same pair) and ensure BOTH sides open BarterVM
                        {
                            if (string.IsNullOrEmpty(packet.SessionId) || packet.InitiatorPeerId <= 0 || packet.TargetPeerId <= 0 || packet.InitiatorPeerId == packet.TargetPeerId)
                            {
                                return;
                            }

                            if (!_serverLiveBarters.TryGetValue(packet.SessionId, out var existingLive) ||
                                !((existingLive.InitiatorPeerId == packet.InitiatorPeerId && existingLive.TargetPeerId == packet.TargetPeerId) ||
                                  (existingLive.InitiatorPeerId == packet.TargetPeerId && existingLive.TargetPeerId == packet.InitiatorPeerId)))
                            {
                                _serverLiveBarters[packet.SessionId] = new ServerLiveBarterSession
                                {
                                    SessionId = packet.SessionId,
                                    InitiatorPeerId = packet.InitiatorPeerId,
                                    TargetPeerId = packet.TargetPeerId
                                };
                            }

                            if (initParty != null)
                            {
                                ModContext.Server?.Broadcast(RosterSyncService.Instance.BuildRosterPacket(packet.InitiatorPeerId, initHero, initParty), LiteNetLib.DeliveryMethod.ReliableOrdered);
                            }
                            if (targParty != null)
                            {
                                ModContext.Server?.Broadcast(RosterSyncService.Instance.BuildRosterPacket(packet.TargetPeerId, targHero, targParty), LiteNetLib.DeliveryMethod.ReliableOrdered);
                            }

                            int partnerPeer = packet.SenderPeerId == packet.InitiatorPeerId ? packet.TargetPeerId : packet.InitiatorPeerId;
                            ModContext.Server?.SendTo(partnerPeer, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 16) // LiveBarterSync -> Update sender's side & reset BOTH acceptances
                        {
                            if (!_serverLiveBarters.TryGetValue(packet.SessionId, out var live))
                            {
                                live = new ServerLiveBarterSession
                                {
                                    SessionId = packet.SessionId,
                                    InitiatorPeerId = packet.InitiatorPeerId,
                                    TargetPeerId = packet.TargetPeerId
                                };
                                _serverLiveBarters[packet.SessionId] = live;
                            }

                            if (packet.SenderPeerId == live.InitiatorPeerId)
                            {
                                live.InitiatorTokens = packet.BoughtItemsCsv ?? string.Empty;
                            }
                            else if (packet.SenderPeerId == live.TargetPeerId)
                            {
                                live.TargetTokens = packet.BoughtItemsCsv ?? string.Empty;
                            }
                            else
                            {
                                return;
                            }

                            live.InitiatorAccepted = false;
                            live.TargetAccepted = false;

                            var syncPkt = new P2PDialogPacket
                            {
                                SessionId = live.SessionId,
                                InitiatorPeerId = live.InitiatorPeerId,
                                TargetPeerId = live.TargetPeerId,
                                SenderPeerId = packet.SenderPeerId,
                                SenderName = packet.SenderName,
                                Stage = 16,
                                OptionKey = "BARTER_LIVE_SYNC",
                                SpeakerName = packet.SenderName,
                                BoughtItemsCsv = live.InitiatorTokens,
                                SoldItemsCsv = live.TargetTokens,
                                InitiatorTradeAccepted = false,
                                TargetTradeAccepted = false
                            };
                            ModContext.Server?.SendTo(live.InitiatorPeerId, syncPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            ModContext.Server?.SendTo(live.TargetPeerId, syncPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 17) // LiveBarterAccept -> Toggle acceptance; commit ONLY when 2/2 accepted
                        {
                            if (!_serverLiveBarters.TryGetValue(packet.SessionId, out var live))
                            {
                                return;
                            }

                            string incomingTokens = packet.BoughtItemsCsv ?? string.Empty;
                            bool wantsAccept = packet.OfferAmount != 0;

                            if (packet.SenderPeerId == live.InitiatorPeerId)
                            {
                                if (!string.Equals(live.InitiatorTokens, incomingTokens, StringComparison.Ordinal))
                                {
                                    live.InitiatorTokens = incomingTokens;
                                    live.TargetAccepted = false;
                                }
                                live.InitiatorAccepted = wantsAccept;
                            }
                            else if (packet.SenderPeerId == live.TargetPeerId)
                            {
                                if (!string.Equals(live.TargetTokens, incomingTokens, StringComparison.Ordinal))
                                {
                                    live.TargetTokens = incomingTokens;
                                    live.InitiatorAccepted = false;
                                }
                                live.TargetAccepted = wantsAccept;
                            }
                            else
                            {
                                return;
                            }

                            if (live.InitiatorAccepted && live.TargetAccepted)
                            {
                                string combinedTokens = CombineTwoSideBarterTokens(live.InitiatorTokens, live.TargetTokens);
                                var propHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(live.InitiatorPeerId);
                                var partHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(live.TargetPeerId);
                                var propParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(live.InitiatorPeerId);
                                var partParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(live.TargetPeerId);

                                string failReason = string.Empty;
                                if (string.IsNullOrEmpty(combinedTokens) ||
                                    !TryValidateAndApplyLordBarterTokens(
                                        combinedTokens,
                                        propHero,
                                        partHero,
                                        propParty,
                                        partParty,
                                        out bool hasWorldDiplomacyChange,
                                        out failReason))
                                {
                                    live.InitiatorAccepted = false;
                                    live.TargetAccepted = false;
                                    var failPkt = new P2PDialogPacket
                                    {
                                        SessionId = live.SessionId,
                                        InitiatorPeerId = live.InitiatorPeerId,
                                        TargetPeerId = live.TargetPeerId,
                                        SenderPeerId = 0,
                                        SenderName = Loc.T("Sunucu", "Server"),
                                        Stage = 17,
                                        OptionKey = "BARTER_LIVE_FAIL",
                                        SpeakerName = Loc.T("Sunucu", "Server"),
                                        BoughtItemsCsv = live.InitiatorTokens,
                                        SoldItemsCsv = live.TargetTokens,
                                        InitiatorTradeAccepted = false,
                                        TargetTradeAccepted = false,
                                        DialogText = Loc.F("[Teklif İptal] {0}", "[Barter Failed] {0}",
                                            string.IsNullOrEmpty(failReason) ? Loc.T("Teklif geçersiz.", "Invalid offer.") : failReason)
                                    };
                                    ModContext.Server?.SendTo(live.InitiatorPeerId, failPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    ModContext.Server?.SendTo(live.TargetPeerId, failPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    return;
                                }

                                _serverLiveBarters.Remove(packet.SessionId);
                                ApplyServerRelationDelta(live.InitiatorPeerId, live.TargetPeerId, propHero, partHero, +2, "LordBarterAccepted");
                                if (propParty != null)
                                {
                                    ModContext.Server?.Broadcast(RosterSyncService.Instance.BuildRosterPacket(live.InitiatorPeerId, propHero, propParty), LiteNetLib.DeliveryMethod.ReliableOrdered);
                                }
                                if (partParty != null)
                                {
                                    ModContext.Server?.Broadcast(RosterSyncService.Instance.BuildRosterPacket(live.TargetPeerId, partHero, partParty), LiteNetLib.DeliveryMethod.ReliableOrdered);
                                }

                                string pInitName = propHero?.Name?.ToString() ?? initName;
                                string pTargName = partHero?.Name?.ToString() ?? targName;
                                var commitPkt = new P2PDialogPacket
                                {
                                    SessionId = live.SessionId,
                                    InitiatorPeerId = live.InitiatorPeerId,
                                    TargetPeerId = live.TargetPeerId,
                                    SenderPeerId = 0,
                                    SenderName = Loc.T("Sunucu", "Server"),
                                    Stage = 18,
                                    OptionKey = "BARTER_V1",
                                    SpeakerName = Loc.T("Sunucu", "Server"),
                                    BoughtItemsCsv = combinedTokens,
                                    DialogText = Loc.F(
                                        "[Teklif Kabul Edildi] {0} <-> {1} karşılıklı antlaşmayı onayladı! (+2 İlişki)",
                                        "[Barter Finalized] Mutual deal completed between {0} & {1}! (+2 Relation)",
                                        pInitName, pTargName)
                                };

                                if (hasWorldDiplomacyChange)
                                {
                                    ModContext.Server?.Broadcast(commitPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                }
                                else
                                {
                                    ModContext.Server?.SendTo(live.InitiatorPeerId, commitPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    ModContext.Server?.SendTo(live.TargetPeerId, commitPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                }
                                return;
                            }

                            var statusPkt = new P2PDialogPacket
                            {
                                SessionId = live.SessionId,
                                InitiatorPeerId = live.InitiatorPeerId,
                                TargetPeerId = live.TargetPeerId,
                                SenderPeerId = packet.SenderPeerId,
                                SenderName = packet.SenderName,
                                Stage = 17,
                                OptionKey = "BARTER_LIVE_ACCEPT",
                                SpeakerName = packet.SenderName,
                                BoughtItemsCsv = live.InitiatorTokens,
                                SoldItemsCsv = live.TargetTokens,
                                InitiatorTradeAccepted = live.InitiatorAccepted,
                                TargetTradeAccepted = live.TargetAccepted
                            };
                            ModContext.Server?.SendTo(live.InitiatorPeerId, statusPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            ModContext.Server?.SendTo(live.TargetPeerId, statusPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 19) // LiveBarterClosed -> Cancel & close partner window
                        {
                            _serverLiveBarters.Remove(packet.SessionId);
                            int partnerPeer = packet.SenderPeerId == packet.InitiatorPeerId ? packet.TargetPeerId : packet.InitiatorPeerId;
                            if (partnerPeer > 0)
                            {
                                ModContext.Server?.SendTo(partnerPeer, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            }
                            return;
                        }

                        if (packet.Stage == 12) // Lord Barter / Trade Offer
                        {
                            if (string.IsNullOrEmpty(packet.SessionId) || packet.InitiatorPeerId <= 0 || packet.TargetPeerId <= 0 || packet.InitiatorPeerId == packet.TargetPeerId)
                            {
                                return;
                            }

                            _pendingNativeTrades[packet.SessionId] = new PendingNativeTradeOffer
                            {
                                SessionId = packet.SessionId,
                                ProposerPeerId = packet.InitiatorPeerId,
                                PartnerPeerId = packet.TargetPeerId,
                                ProposerName = initName,
                                BoughtItemsCsv = packet.BoughtItemsCsv ?? string.Empty,
                                SoldItemsCsv = packet.SoldItemsCsv ?? string.Empty,
                                NetGoldDelta = packet.NetGoldDelta,
                                CreatedUtc = DateTime.UtcNow
                            };

                            ModContext.Server?.SendTo(packet.TargetPeerId, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 13) // Lord Barter / Trade Accept -> Server validates & commits atomically
                        {
                            if (!_pendingNativeTrades.TryGetValue(packet.SessionId, out var pending))
                            {
                                return;
                            }
                            if (packet.SenderPeerId != pending.PartnerPeerId)
                            {
                                return;
                            }

                            _pendingNativeTrades.Remove(packet.SessionId);

                            bool isLordBarter = packet.OptionKey == "BARTER_V1";
                            bool hasWorldDiplomacyChange = false;
                            if (isLordBarter)
                            {
                                var propHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(pending.ProposerPeerId);
                                var partHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(pending.PartnerPeerId);
                                var propParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(pending.ProposerPeerId);
                                var partParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(pending.PartnerPeerId);

                                if (!TryValidateAndApplyLordBarterTokens(
                                    pending.BoughtItemsCsv,
                                    propHero,
                                    partHero,
                                    propParty,
                                    partParty,
                                    out hasWorldDiplomacyChange,
                                    out string failReason))
                                {
                                    var failPkt = new P2PDialogPacket
                                    {
                                        SessionId = pending.SessionId,
                                        InitiatorPeerId = pending.ProposerPeerId,
                                        TargetPeerId = pending.PartnerPeerId,
                                        SenderPeerId = pending.PartnerPeerId,
                                        SenderName = targName,
                                        Stage = 14,
                                        OptionKey = "BARTER_FAIL",
                                        SpeakerName = targName,
                                        DialogText = Loc.F("[Teklif İptal] {0}", "[Barter Failed] {0}", failReason)
                                    };
                                    ModContext.Server?.SendTo(pending.ProposerPeerId, failPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    ModContext.Server?.SendTo(pending.PartnerPeerId, failPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    return;
                                }

                                ApplyServerRelationDelta(pending.ProposerPeerId, pending.PartnerPeerId, propHero, partHero, +2, "LordBarterAccepted");
                                if (propParty != null)
                                {
                                    ModContext.Server?.Broadcast(RosterSyncService.Instance.BuildRosterPacket(pending.ProposerPeerId, propHero, propParty), LiteNetLib.DeliveryMethod.ReliableOrdered);
                                }
                                if (partParty != null)
                                {
                                    ModContext.Server?.Broadcast(RosterSyncService.Instance.BuildRosterPacket(pending.PartnerPeerId, partHero, partParty), LiteNetLib.DeliveryMethod.ReliableOrdered);
                                }
                            }

                            var commitPkt = new P2PDialogPacket
                            {
                                SessionId = pending.SessionId,
                                InitiatorPeerId = pending.ProposerPeerId,
                                TargetPeerId = pending.PartnerPeerId,
                                SenderPeerId = pending.PartnerPeerId,
                                SenderName = targName,
                                Stage = 13,
                                OptionKey = isLordBarter ? "BARTER_V1" : "native_trade_accept",
                                SpeakerName = targName,
                                BoughtItemsCsv = pending.BoughtItemsCsv,
                                SoldItemsCsv = pending.SoldItemsCsv,
                                NetGoldDelta = pending.NetGoldDelta,
                                DialogText = Loc.F(
                                    "[Teklif Kabul Edildi] {0} ↔ {1} karşılıklı antlaşmayı onayladı! (+2 İlişki)",
                                    "[Barter Finalized] Mutual deal completed between {0} & {1}! (+2 Relation)",
                                    pending.ProposerName, targName)
                            };

                            if (hasWorldDiplomacyChange)
                            {
                                ModContext.Server?.Broadcast(commitPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            }
                            else
                            {
                                ModContext.Server?.SendTo(pending.ProposerPeerId, commitPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(pending.PartnerPeerId, commitPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            }
                            return;
                        }

                        if (packet.Stage == 14) // Lord Barter / Trade Decline
                        {
                            if (_pendingNativeTrades.TryGetValue(packet.SessionId, out var pending))
                            {
                                _pendingNativeTrades.Remove(packet.SessionId);
                                ModContext.Server?.SendTo(pending.ProposerPeerId, new P2PDialogPacket
                                {
                                    SessionId = pending.SessionId,
                                    InitiatorPeerId = pending.ProposerPeerId,
                                    TargetPeerId = pending.PartnerPeerId,
                                    SenderPeerId = pending.PartnerPeerId,
                                    SenderName = targName,
                                    Stage = 14,
                                    OptionKey = packet.OptionKey == "BARTER_V1" ? "BARTER_V1" : "native_trade_decline",
                                    SpeakerName = targName,
                                    DialogText = string.Empty
                                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            }
                            return;
                        }

                        if (packet.Stage == 10 || packet.Stage == 11)
                        {
                            // Custom P2P trade overlay disabled; native NPC Trade flow (Stages 12-14) is used instead.
                            return;
                        }

                        if (packet.Stage == 6) // TradeOfferUpdate (Server-authoritative)
                        {
                            if (!_serverTrades.TryGetValue(packet.SessionId, out var trade))
                            {
                                trade = new ServerTradeState();
                                _serverTrades[packet.SessionId] = trade;
                            }

                            if (packet.SenderPeerId != packet.InitiatorPeerId && packet.SenderPeerId != packet.TargetPeerId)
                            {
                                return;
                            }

                            bool isInit = packet.SenderPeerId == packet.InitiatorPeerId;
                            var senderHero = isInit ? initHero : targHero;
                            var senderParty = isInit ? initParty : targParty;
                            string senderName = isInit ? initName : targName;
                            string msgText;

                            if (packet.OptionKey == "offer_gold")
                            {
                                int serverGold = senderHero?.Gold ?? 0;
                                int validatedGold = Math.Max(0, Math.Min(packet.OfferAmount, serverGold));
                                if (isInit) trade.InitiatorGold = validatedGold;
                                else trade.TargetGold = validatedGold;

                                msgText = Loc.F(
                                    "{0} takas masasına {1} altın teklif koydu. (Karşılıklı onay sıfırlandı)",
                                    "{0} placed {1} gold onto the trade table. (Mutual acceptance reset)",
                                    senderName, validatedGold);
                            }
                            else if (packet.OptionKey == "offer_troop")
                            {
                                var troop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(packet.OfferObjectId);
                                int healthy = 0;
                                if (troop != null && !troop.IsHero && senderParty?.MemberRoster != null)
                                {
                                    int idx = senderParty.MemberRoster.FindIndexOfTroop(troop);
                                    if (idx >= 0)
                                    {
                                        healthy = Math.Max(0, senderParty.MemberRoster.GetElementNumber(idx) - senderParty.MemberRoster.GetElementWoundedNumber(idx));
                                    }
                                }

                                int validatedCount = Math.Max(0, Math.Min(packet.OfferAmount, healthy));
                                string validId = (troop != null && validatedCount > 0) ? troop.StringId : string.Empty;
                                string troopLabel = troop?.Name?.ToString() ?? packet.OfferObjectId;

                                if (isInit)
                                {
                                    trade.InitiatorTroopId = validId;
                                    trade.InitiatorTroopCount = validatedCount;
                                }
                                else
                                {
                                    trade.TargetTroopId = validId;
                                    trade.TargetTroopCount = validatedCount;
                                }

                                msgText = Loc.F(
                                    "{0} takas masasına {1}x {2} teklif koydu. (Karşılıklı onay sıfırlandı)",
                                    "{0} offered {1}x {2} on the trade table. (Mutual acceptance reset)",
                                    senderName, validatedCount, troopLabel);
                            }
                            else if (packet.OptionKey == "offer_item")
                            {
                                var item = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<ItemObject>(packet.OfferObjectId);
                                int ownedCount = 0;
                                if (item != null && senderParty?.ItemRoster != null)
                                {
                                    int idx = senderParty.ItemRoster.FindIndexOfItem(item);
                                    if (idx >= 0)
                                    {
                                        ownedCount = Math.Max(0, senderParty.ItemRoster.GetElementNumber(idx));
                                    }
                                }

                                int validatedCount = Math.Max(0, Math.Min(packet.OfferAmount, ownedCount));
                                string validId = (item != null && validatedCount > 0) ? item.StringId : string.Empty;
                                string itemLabel = item?.Name?.ToString() ?? packet.OfferObjectId;

                                if (isInit)
                                {
                                    trade.InitiatorItemId = validId;
                                    trade.InitiatorItemCount = validatedCount;
                                }
                                else
                                {
                                    trade.TargetItemId = validId;
                                    trade.TargetItemCount = validatedCount;
                                }

                                msgText = Loc.F(
                                    "{0} takas masasına {1}x {2} eşyasını koydu. (Karşılıklı onay sıfırlandı)",
                                    "{0} offered {1}x {2} on the trade table. (Mutual acceptance reset)",
                                    senderName, validatedCount, itemLabel);
                            }
                            else if (packet.OptionKey == "offer_ship")
                            {
                                int reqIdx = packet.OfferAmount;
                                int validIdx = -1;
                                string shipDesc = string.Empty;

                                if (reqIdx >= 0 && senderParty?.Ships != null && reqIdx < senderParty.Ships.Count)
                                {
                                    var ship = senderParty.Ships[reqIdx];
                                    if (ship != null && ship.IsTradeable && !ship.IsUsedByQuest)
                                    {
                                        validIdx = reqIdx;
                                        string hullName = ship.ShipHull?.Name?.ToString() ?? Loc.T("Gemi", "Ship");
                                        shipDesc = $"{ship.Name} ({hullName})";
                                    }
                                }

                                if (isInit)
                                {
                                    trade.InitiatorShipIndex = validIdx;
                                    trade.InitiatorShipDesc = shipDesc;
                                }
                                else
                                {
                                    trade.TargetShipIndex = validIdx;
                                    trade.TargetShipDesc = shipDesc;
                                }

                                msgText = validIdx >= 0
                                    ? Loc.F("{0} takas masasına '{1}' gemisini koydu. (Karşılıklı onay sıfırlandı)", "{0} offered ship '{1}' on the trade table. (Mutual acceptance reset)", senderName, shipDesc)
                                    : Loc.F("{0} takas masasından gemi teklifini kaldırdı.", "{0} removed their ship offer.", senderName);
                            }
                            else
                            {
                                return;
                            }

                            // Modifying any offer ALWAYS resets both acceptances to prevent bait-and-switch!
                            trade.InitiatorAccepted = false;
                            trade.TargetAccepted = false;

                            var updatePacket = CreateTradeSnapshotPacket(
                                packet.SessionId,
                                packet.InitiatorPeerId,
                                packet.TargetPeerId,
                                packet.SenderPeerId,
                                senderName,
                                6,
                                packet.OptionKey,
                                senderName,
                                msgText,
                                trade);

                            ModContext.Server?.SendTo(packet.InitiatorPeerId, updatePacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            ModContext.Server?.SendTo(packet.TargetPeerId, updatePacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 7) // TradeToggleAccept
                        {
                            if (!_serverTrades.TryGetValue(packet.SessionId, out var trade) || !trade.HasAnyOffer())
                            {
                                var emptyNotice = CreateTradeSnapshotPacket(
                                    packet.SessionId,
                                    packet.InitiatorPeerId,
                                    packet.TargetPeerId,
                                    packet.SenderPeerId,
                                    packet.SenderName,
                                    7,
                                    "trade_empty",
                                    Loc.T("Sunucu", "Server"),
                                    Loc.T("Takas masasında henüz hiçbir altın, eşya veya asker teklifi yok!", "There is no gold, item or troop offer on the trade table yet!"),
                                    trade ?? new ServerTradeState());
                                ModContext.Server?.SendTo(packet.SenderPeerId, emptyNotice, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                return;
                            }

                            if (packet.SenderPeerId == packet.InitiatorPeerId)
                            {
                                trade.InitiatorAccepted = !trade.InitiatorAccepted;
                            }
                            else if (packet.SenderPeerId == packet.TargetPeerId)
                            {
                                trade.TargetAccepted = !trade.TargetAccepted;
                            }
                            else
                            {
                                return;
                            }

                            if (trade.InitiatorAccepted && trade.TargetAccepted)
                            {
                                // Server-authoritative validation of BOTH sides before executing atomic swap!
                                bool valid = true;
                                string failReason = string.Empty;

                                if (trade.InitiatorGold > 0 && (initHero == null || initHero.Gold < trade.InitiatorGold))
                                {
                                    valid = false;
                                    failReason = Loc.F("{0} yeterli altına sahip değil!", "{0} does not have enough gold!", initName);
                                }
                                if (valid && trade.TargetGold > 0 && (targHero == null || targHero.Gold < trade.TargetGold))
                                {
                                    valid = false;
                                    failReason = Loc.F("{0} yeterli altına sahip değil!", "{0} does not have enough gold!", targName);
                                }

                                CharacterObject? initTroop = null;
                                CharacterObject? targTroop = null;
                                if (valid && trade.InitiatorTroopCount > 0 && !string.IsNullOrEmpty(trade.InitiatorTroopId))
                                {
                                    initTroop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(trade.InitiatorTroopId);
                                    int idx = (initTroop != null && initParty?.MemberRoster != null) ? initParty.MemberRoster.FindIndexOfTroop(initTroop) : -1;
                                    int healthy = idx >= 0 ? initParty!.MemberRoster.GetElementNumber(idx) - initParty.MemberRoster.GetElementWoundedNumber(idx) : 0;
                                    if (initTroop == null || healthy < trade.InitiatorTroopCount)
                                    {
                                        valid = false;
                                        failReason = Loc.F("{0} yeterli sağlıklı askere sahip değil!", "{0} does not have enough healthy troops!", initName);
                                    }
                                }
                                if (valid && trade.TargetTroopCount > 0 && !string.IsNullOrEmpty(trade.TargetTroopId))
                                {
                                    targTroop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(trade.TargetTroopId);
                                    int idx = (targTroop != null && targParty?.MemberRoster != null) ? targParty.MemberRoster.FindIndexOfTroop(targTroop) : -1;
                                    int healthy = idx >= 0 ? targParty!.MemberRoster.GetElementNumber(idx) - targParty.MemberRoster.GetElementWoundedNumber(idx) : 0;
                                    if (targTroop == null || healthy < trade.TargetTroopCount)
                                    {
                                        valid = false;
                                        failReason = Loc.F("{0} yeterli sağlıklı askere sahip değil!", "{0} does not have enough healthy troops!", targName);
                                    }
                                }

                                ItemObject? initItem = null;
                                ItemObject? targItem = null;
                                if (valid && trade.InitiatorItemCount > 0 && !string.IsNullOrEmpty(trade.InitiatorItemId))
                                {
                                    initItem = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<ItemObject>(trade.InitiatorItemId);
                                    int idx = (initItem != null && initParty?.ItemRoster != null) ? initParty.ItemRoster.FindIndexOfItem(initItem) : -1;
                                    int owned = idx >= 0 ? initParty!.ItemRoster.GetElementNumber(idx) : 0;
                                    if (initItem == null || owned < trade.InitiatorItemCount)
                                    {
                                        valid = false;
                                        failReason = Loc.F("{0} yeterli eşyaya sahip değil!", "{0} does not have enough items!", initName);
                                    }
                                }
                                if (valid && trade.TargetItemCount > 0 && !string.IsNullOrEmpty(trade.TargetItemId))
                                {
                                    targItem = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<ItemObject>(trade.TargetItemId);
                                    int idx = (targItem != null && targParty?.ItemRoster != null) ? targParty.ItemRoster.FindIndexOfItem(targItem) : -1;
                                    int owned = idx >= 0 ? targParty!.ItemRoster.GetElementNumber(idx) : 0;
                                    if (targItem == null || owned < trade.TargetItemCount)
                                    {
                                        valid = false;
                                        failReason = Loc.F("{0} yeterli eşyaya sahip değil!", "{0} does not have enough items!", targName);
                                    }
                                }

                                Ship? initShip = null;
                                Ship? targShip = null;
                                if (valid && trade.InitiatorShipIndex >= 0)
                                {
                                    if (initParty?.Ships == null || trade.InitiatorShipIndex >= initParty.Ships.Count)
                                    {
                                        valid = false;
                                        failReason = Loc.F("{0} artık teklif ettiği gemiye sahip değil!", "{0} no longer owns the offered ship!", initName);
                                    }
                                    else
                                    {
                                        initShip = initParty.Ships[trade.InitiatorShipIndex];
                                        if (initShip == null || !initShip.IsTradeable || initShip.IsUsedByQuest)
                                        {
                                            valid = false;
                                            failReason = Loc.F("{0} gemisi takas edilemez!", "{0}'s ship is not tradeable!", initName);
                                        }
                                    }
                                }
                                if (valid && trade.TargetShipIndex >= 0)
                                {
                                    if (targParty?.Ships == null || trade.TargetShipIndex >= targParty.Ships.Count)
                                    {
                                        valid = false;
                                        failReason = Loc.F("{0} artık teklif ettiği gemiye sahip değil!", "{0} no longer owns the offered ship!", targName);
                                    }
                                    else
                                    {
                                        targShip = targParty.Ships[trade.TargetShipIndex];
                                        if (targShip == null || !targShip.IsTradeable || targShip.IsUsedByQuest)
                                        {
                                            valid = false;
                                            failReason = Loc.F("{0} gemisi takas edilemez!", "{0}'s ship is not tradeable!", targName);
                                        }
                                    }
                                }

                                if (!valid)
                                {
                                    trade.InitiatorAccepted = false;
                                    trade.TargetAccepted = false;
                                    var failPkt = CreateTradeSnapshotPacket(
                                        packet.SessionId,
                                        packet.InitiatorPeerId,
                                        packet.TargetPeerId,
                                        0,
                                        Loc.T("Sunucu", "Server"),
                                        9,
                                        "trade_failed",
                                        Loc.T("Sunucu", "Server"),
                                        Loc.F("Takas iptal edildi: {0}", "Trade cancelled: {0}", failReason),
                                        trade);
                                    ModContext.Server?.SendTo(packet.InitiatorPeerId, failPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    ModContext.Server?.SendTo(packet.TargetPeerId, failPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    return;
                                }

                                // Execute atomic swap on server campaign world state
                                if (initHero != null && targHero != null)
                                {
                                    initHero.Gold = Math.Max(0, initHero.Gold - trade.InitiatorGold) + trade.TargetGold;
                                    targHero.Gold = Math.Max(0, targHero.Gold - trade.TargetGold) + trade.InitiatorGold;
                                }
                                if (initTroop != null && trade.InitiatorTroopCount > 0)
                                {
                                    initParty?.MemberRoster?.AddToCounts(initTroop, -trade.InitiatorTroopCount);
                                    targParty?.MemberRoster?.AddToCounts(initTroop, trade.InitiatorTroopCount);
                                }
                                if (targTroop != null && trade.TargetTroopCount > 0)
                                {
                                    targParty?.MemberRoster?.AddToCounts(targTroop, -trade.TargetTroopCount);
                                    initParty?.MemberRoster?.AddToCounts(targTroop, trade.TargetTroopCount);
                                }
                                if (initItem != null && trade.InitiatorItemCount > 0)
                                {
                                    initParty?.ItemRoster?.AddToCounts(initItem, -trade.InitiatorItemCount);
                                    targParty?.ItemRoster?.AddToCounts(initItem, trade.InitiatorItemCount);
                                }
                                if (targItem != null && trade.TargetItemCount > 0)
                                {
                                    targParty?.ItemRoster?.AddToCounts(targItem, -trade.TargetItemCount);
                                    initParty?.ItemRoster?.AddToCounts(targItem, trade.TargetItemCount);
                                }
                                if (initShip != null && targParty?.Party != null)
                                {
                                    try { ChangeShipOwnerAction.ApplyByTrade(targParty.Party, initShip); } catch { }
                                }
                                if (targShip != null && initParty?.Party != null)
                                {
                                    try { ChangeShipOwnerAction.ApplyByTrade(initParty.Party, targShip); } catch { }
                                }

                                string doneText = Loc.F(
                                    "[TAKAS TAMAMLANDI] {0} <-> {1} arasındaki takas sunucu tarafından karşılıklı onayla gerçekleştirildi!",
                                    "[TRADE COMPLETED] Mutual trade between {0} and {1} has been atomically executed by the server!",
                                    initName, targName);

                                var commitPacket = CreateTradeSnapshotPacket(
                                    packet.SessionId,
                                    packet.InitiatorPeerId,
                                    packet.TargetPeerId,
                                    0,
                                    Loc.T("Sunucu", "Server"),
                                    8, // TradeCommitted (carries exact committed amounts for client delta application!)
                                    "trade_committed",
                                    Loc.T("Sunucu", "Server"),
                                    doneText,
                                    trade);

                                trade.Reset();

                                InformationManager.DisplayMessage(new InformationMessage(doneText, Colors.Green));
                                ModContext.Server?.SendTo(packet.InitiatorPeerId, commitPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(packet.TargetPeerId, commitPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                return;
                            }

                            string actorName = packet.SenderPeerId == packet.InitiatorPeerId ? initName : targName;
                            bool actorAccepted = packet.SenderPeerId == packet.InitiatorPeerId ? trade.InitiatorAccepted : trade.TargetAccepted;
                            string statusLine = actorAccepted
                                ? Loc.F("{0} takası ONAYLADI. Diğer oyuncunun onayı bekleniyor...", "{0} ACCEPTED the trade. Waiting for the other player...", actorName)
                                : Loc.F("{0} takas onayını geri çekti.", "{0} withdrew trade acceptance.", actorName);

                            var acceptStatusPacket = CreateTradeSnapshotPacket(
                                packet.SessionId,
                                packet.InitiatorPeerId,
                                packet.TargetPeerId,
                                packet.SenderPeerId,
                                actorName,
                                7,
                                "trade_accept_status",
                                actorName,
                                statusLine,
                                trade);

                            ModContext.Server?.SendTo(packet.InitiatorPeerId, acceptStatusPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            ModContext.Server?.SendTo(packet.TargetPeerId, acceptStatusPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 9) // TradeReset
                        {
                            if (!_serverTrades.TryGetValue(packet.SessionId, out var trade))
                            {
                                trade = new ServerTradeState();
                                _serverTrades[packet.SessionId] = trade;
                            }
                            trade.Reset();

                            string actor = packet.SenderPeerId == packet.InitiatorPeerId ? initName : targName;
                            var resetPkt = CreateTradeSnapshotPacket(
                                packet.SessionId,
                                packet.InitiatorPeerId,
                                packet.TargetPeerId,
                                packet.SenderPeerId,
                                actor,
                                9,
                                "trade_reset",
                                actor,
                                Loc.F("{0} takas masasını sıfırladı.", "{0} cleared the trade table.", actor),
                                trade);
                            ModContext.Server?.SendTo(packet.InitiatorPeerId, resetPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            ModContext.Server?.SendTo(packet.TargetPeerId, resetPkt, LiteNetLib.DeliveryMethod.ReliableOrdered);
                            return;
                        }

                        if (packet.Stage == 4) // Partner response inside P2P dialog (symmetric!)
                        {
                            string responderName = packet.SenderPeerId == packet.InitiatorPeerId ? initName : targName;
                            int partnerPeer = packet.SenderPeerId == packet.InitiatorPeerId ? packet.TargetPeerId : packet.InitiatorPeerId;

                            if (packet.OptionKey == "accept_duel")
                            {
                                float initPower = (initHero?.Level ?? 10) * 3f + (initHero?.HitPoints ?? 100) + MBRandom.RandomFloatRanged(0f, 35f);
                                float targPower = (targHero?.Level ?? 10) * 3f + (targHero?.HitPoints ?? 100) + MBRandom.RandomFloatRanged(0f, 35f);
                                bool initWon = initPower >= targPower;

                                int winnerPeerId = initWon ? packet.InitiatorPeerId : packet.TargetPeerId;
                                string winnerName = initWon ? initName : targName;
                                string loserName = initWon ? targName : initName;
                                var winnerHero = initWon ? initHero : targHero;

                                winnerHero?.Clan?.AddRenown(5f, true);
                                ApplyServerRelationDelta(packet.InitiatorPeerId, packet.TargetPeerId, initHero, targHero, +5, "FriendlyDuel");

                                string duelMsg = Loc.F(
                                    "[Düello Sonucu] '{0}', '{1}' karşısında dostluk düellosunu kazandı! (+5 Klan Namı, +5 İlişki)",
                                    "[Duel Result] '{0}' won the friendly duel against '{1}'! (+5 Clan Renown, +5 Relation)",
                                    winnerName, loserName);

                                var resultPacket = new PlayerInteractionPacket
                                {
                                    SenderPeerId = packet.InitiatorPeerId,
                                    SenderName = initName,
                                    TargetPeerId = packet.TargetPeerId,
                                    TargetName = targName,
                                    InteractionType = 5,
                                    IntAmount = winnerPeerId,
                                    Message = duelMsg
                                };

                                var resultDialog = new P2PDialogPacket
                                {
                                    SessionId = packet.SessionId,
                                    InitiatorPeerId = packet.InitiatorPeerId,
                                    TargetPeerId = packet.TargetPeerId,
                                    SenderPeerId = packet.SenderPeerId,
                                    SenderName = responderName,
                                    Stage = 2,
                                    OptionKey = "duel_result",
                                    SpeakerName = winnerName,
                                    DialogText = duelMsg
                                };

                                InformationManager.DisplayMessage(new InformationMessage(duelMsg, Colors.Magenta));
                                ModContext.Server?.Broadcast(resultPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(packet.InitiatorPeerId, resultDialog, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(packet.TargetPeerId, resultDialog, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                return;
                            }
                            else if (packet.OptionKey == "accept_peace")
                            {
                                if (initHero?.MapFaction != null && targHero?.MapFaction != null &&
                                    FactionManager.IsAtWarAgainstFaction(initHero.MapFaction, targHero.MapFaction))
                                {
                                    try { MakePeaceAction.Apply(initHero.MapFaction, targHero.MapFaction); } catch { }
                                }
                                ApplyServerRelationDelta(packet.InitiatorPeerId, packet.TargetPeerId, initHero, targHero, +10, "PeaceTreaty");

                                string peaceMsg = Loc.F(
                                    "[Barış Antlaşması] {0} ve {1} kılıçları kınına koyup kalıcı barış imzaladı! (+10 İlişki)",
                                    "[Peace Treaty] {0} and {1} sheathed their swords and signed peace! (+10 Relation)",
                                    initName, targName);

                                ModContext.Server?.Broadcast(new PlayerInteractionPacket
                                {
                                    SenderPeerId = packet.InitiatorPeerId,
                                    SenderName = initName,
                                    TargetPeerId = packet.TargetPeerId,
                                    TargetName = targName,
                                    InteractionType = 9,
                                    ObjectId = "ACCEPT_PEACE",
                                    Message = peaceMsg
                                }, LiteNetLib.DeliveryMethod.ReliableOrdered);

                                var followUp = new P2PDialogPacket
                                {
                                    SessionId = packet.SessionId,
                                    InitiatorPeerId = packet.InitiatorPeerId,
                                    TargetPeerId = packet.TargetPeerId,
                                    SenderPeerId = packet.SenderPeerId,
                                    SenderName = responderName,
                                    Stage = 2,
                                    OptionKey = "accept_peace",
                                    SpeakerName = responderName,
                                    DialogText = peaceMsg
                                };
                                ModContext.Server?.SendTo(packet.InitiatorPeerId, followUp, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(packet.TargetPeerId, followUp, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                return;
                            }
                            else if (packet.OptionKey == "accept_alliance")
                            {
                                if (initHero?.MapFaction != null && targHero?.MapFaction != null &&
                                    FactionManager.IsAtWarAgainstFaction(initHero.MapFaction, targHero.MapFaction))
                                {
                                    try { MakePeaceAction.Apply(initHero.MapFaction, targHero.MapFaction); } catch { }
                                }
                                _alliedPairs.Add(GetPairKey(packet.InitiatorPeerId, packet.TargetPeerId));
                                ApplyServerRelationDelta(packet.InitiatorPeerId, packet.TargetPeerId, initHero, targHero, +10, "Alliance");

                                string allyMsg = Loc.F(
                                    "[İttifak Kuruldu] {0} ile {1} arasında ittifak ve saldırmazlık antlaşması imzalandı! (+10 İlişki)",
                                    "[Alliance Formed] {0} and {1} signed an alliance and non-aggression pact! (+10 Relation)",
                                    initName, targName);

                                ModContext.Server?.Broadcast(new PlayerInteractionPacket
                                {
                                    SenderPeerId = packet.InitiatorPeerId,
                                    SenderName = initName,
                                    TargetPeerId = packet.TargetPeerId,
                                    TargetName = targName,
                                    InteractionType = 9,
                                    ObjectId = "ACCEPT_ALLY",
                                    Message = allyMsg
                                }, LiteNetLib.DeliveryMethod.ReliableOrdered);

                                var followUp = new P2PDialogPacket
                                {
                                    SessionId = packet.SessionId,
                                    InitiatorPeerId = packet.InitiatorPeerId,
                                    TargetPeerId = packet.TargetPeerId,
                                    SenderPeerId = packet.SenderPeerId,
                                    SenderName = responderName,
                                    Stage = 2,
                                    OptionKey = "accept_alliance",
                                    SpeakerName = responderName,
                                    DialogText = allyMsg
                                };
                                ModContext.Server?.SendTo(packet.InitiatorPeerId, followUp, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(packet.TargetPeerId, followUp, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                return;
                            }
                            else if (packet.OptionKey == "reply" || packet.OptionKey == "custom_line")
                            {
                                var replyPacket = new P2PDialogPacket
                                {
                                    SessionId = packet.SessionId,
                                    InitiatorPeerId = packet.InitiatorPeerId,
                                    TargetPeerId = packet.TargetPeerId,
                                    SenderPeerId = packet.SenderPeerId,
                                    SenderName = responderName,
                                    Stage = 2,
                                    OptionKey = "custom_line",
                                    SpeakerName = responderName,
                                    DialogText = packet.DialogText ?? string.Empty
                                };
                                ModContext.Server?.SendTo(packet.InitiatorPeerId, replyPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(packet.TargetPeerId, replyPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                return;
                            }
                            else
                            {
                                if (packet.OptionKey == "accept_rally")
                                {
                                    ApplyServerRelationDelta(packet.InitiatorPeerId, packet.TargetPeerId, initHero, targHero, +3, "JointRally");
                                }

                                string text = packet.OptionKey switch
                                {
                                    "accept_rally" => Loc.F("{0} ortak harekat çağrısını kabul etti, yola çıkıyor! (+3 İlişki)", "{0} accepted the joint rally call and is moving! (+3 Relation)", responderName),
                                    "decline_peace" => Loc.F("{0} barış teklifini reddetti.", "{0} declined the peace proposal.", responderName),
                                    "decline_alliance" => Loc.F("{0} ittifak teklifini reddetti.", "{0} declined the alliance proposal.", responderName),
                                    "decline_rally" => Loc.F("{0} ortak harekat çağrısını şimdilik reddetti.", "{0} declined the joint rally call for now.", responderName),
                                    _ => Loc.F("{0} dostluk düellosu teklifini nazikçe reddetti.", "{0} politely declined the friendly duel.", responderName)
                                };

                                var followUp = new P2PDialogPacket
                                {
                                    SessionId = packet.SessionId,
                                    InitiatorPeerId = packet.InitiatorPeerId,
                                    TargetPeerId = packet.TargetPeerId,
                                    SenderPeerId = packet.SenderPeerId,
                                    SenderName = responderName,
                                    Stage = 2,
                                    OptionKey = packet.OptionKey,
                                    SpeakerName = responderName,
                                    DialogText = text
                                };
                                ModContext.Server?.SendTo(packet.InitiatorPeerId, followUp, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                ModContext.Server?.SendTo(packet.TargetPeerId, followUp, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                return;
                            }
                        }

                        // Preserve any existing server trade snapshot onto Stage 2/3 live dialog updates so UI doesn't wipe the trade table.
                        if (_serverTrades.TryGetValue(packet.SessionId, out var activeTrade))
                        {
                            packet.InitiatorTradeGold = activeTrade.InitiatorGold;
                            packet.InitiatorTradeTroopId = activeTrade.InitiatorTroopId;
                            packet.InitiatorTradeTroopCount = activeTrade.InitiatorTroopCount;
                            packet.InitiatorTradeItemId = activeTrade.InitiatorItemId;
                            packet.InitiatorTradeItemCount = activeTrade.InitiatorItemCount;
                            packet.InitiatorTradeShipIndex = activeTrade.InitiatorShipIndex;
                            packet.InitiatorTradeShipDesc = activeTrade.InitiatorShipDesc;
                            packet.InitiatorTradeAccepted = activeTrade.InitiatorAccepted;
                            packet.TargetTradeGold = activeTrade.TargetGold;
                            packet.TargetTradeTroopId = activeTrade.TargetTroopId;
                            packet.TargetTradeTroopCount = activeTrade.TargetTroopCount;
                            packet.TargetTradeItemId = activeTrade.TargetItemId;
                            packet.TargetTradeItemCount = activeTrade.TargetItemCount;
                            packet.TargetTradeShipIndex = activeTrade.TargetShipIndex;
                            packet.TargetTradeShipDesc = activeTrade.TargetShipDesc;
                            packet.TargetTradeAccepted = activeTrade.TargetAccepted;
                        }

                        ModContext.Server?.SendTo(packet.InitiatorPeerId, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        ModContext.Server?.SendTo(packet.TargetPeerId, packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        return;
                    }

                    // CLIENT SIDE PROCESSING
                    int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
                    bool isGlobalBarterCommit = packet.Stage == 13 && packet.OptionKey == "BARTER_V1";
                    if (!isGlobalBarterCommit && myPeerId != packet.InitiatorPeerId && myPeerId != packet.TargetPeerId) return;

                    if (packet.Stage == 1) // Open received from Initiator -> Stop local player immediately and open native 3D conversation!
                    {
                        if (IsInDialog && ActiveSessionId == packet.SessionId)
                        {
                            return;
                        }

                        try
                        {
                            if (PlayerEncounter.Current != null)
                            {
                                PlayerEncounter.Finish(true);
                            }
                        }
                        catch { }

                        HaltLocalPartyAndSyncStop();
                        IsInDialog = true;
                        ActiveSessionId = packet.SessionId;
                        ActiveInitiatorPeerId = packet.InitiatorPeerId;
                        ActiveTargetPeerId = packet.TargetPeerId;
                        PendingProposalKey = string.Empty;
                        _pendingProposalSenderPeerId = 0;

                        ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "TargetJoinedP2PDialog" });

                        var initParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.InitiatorPeerId);
                        var initHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.InitiatorPeerId);
                        OpenNative3DConversation(initParty, initHero, packet.DialogText);
                        return;
                    }

                    if (packet.Stage == 5)
                    {
                        ClearLocalDialogState();
                        CloseDialogLocalRequested?.Invoke();
                        CloseNative3DConversation();
                        ModContext.Events.Publish(new P2PDialogUpdatedEvent { Packet = packet });
                        return;
                    }

                    if (packet.Stage >= 2 && packet.Stage <= 4)
                    {
                        if (!string.IsNullOrWhiteSpace(packet.DialogText))
                        {
                            CurrentConversationSpeech = $"[{packet.SpeakerName}]: {packet.DialogText}";
                        }

                        if (packet.Stage == 3 && packet.SenderPeerId != myPeerId)
                        {
                            PendingProposalKey = packet.OptionKey ?? string.Empty;
                            _pendingProposalSenderPeerId = packet.SenderPeerId;
                        }
                        else if (packet.Stage == 2 || packet.Stage == 4)
                        {
                            PendingProposalKey = string.Empty;
                            _pendingProposalSenderPeerId = 0;
                        }

                        RefreshActiveNativeConversation();
                        return;
                    }

                    if (packet.Stage == 8) // Server-committed atomic trade -> apply local delta cleanly!
                    {
                        bool iAmInit = myPeerId == packet.InitiatorPeerId;
                        int outGold = iAmInit ? packet.InitiatorTradeGold : packet.TargetTradeGold;
                        int inGold = iAmInit ? packet.TargetTradeGold : packet.InitiatorTradeGold;
                        string outTroopId = iAmInit ? packet.InitiatorTradeTroopId : packet.TargetTradeTroopId;
                        int outTroopCount = iAmInit ? packet.InitiatorTradeTroopCount : packet.TargetTradeTroopCount;
                        string inTroopId = iAmInit ? packet.TargetTradeTroopId : packet.InitiatorTradeTroopId;
                        int inTroopCount = iAmInit ? packet.TargetTradeTroopCount : packet.InitiatorTradeTroopCount;
                        string outItemId = iAmInit ? packet.InitiatorTradeItemId : packet.TargetTradeItemId;
                        int outItemCount = iAmInit ? packet.InitiatorTradeItemCount : packet.TargetTradeItemCount;
                        string inItemId = iAmInit ? packet.TargetTradeItemId : packet.InitiatorTradeItemId;
                        int inItemCount = iAmInit ? packet.TargetTradeItemCount : packet.InitiatorTradeItemCount;
                        int outShipIdx = iAmInit ? packet.InitiatorTradeShipIndex : packet.TargetTradeShipIndex;
                        int inShipIdx = iAmInit ? packet.TargetTradeShipIndex : packet.InitiatorTradeShipIndex;

                        if (Hero.MainHero != null)
                        {
                            Hero.MainHero.Gold = Math.Max(0, Hero.MainHero.Gold - outGold) + inGold;
                        }

                        var roster = MobileParty.MainParty?.MemberRoster;
                        if (roster != null)
                        {
                            if (outTroopCount > 0 && !string.IsNullOrEmpty(outTroopId))
                            {
                                var outTroop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(outTroopId);
                                if (outTroop != null)
                                {
                                    int cur = roster.GetTroopCount(outTroop);
                                    if (cur > 0) roster.AddToCounts(outTroop, -Math.Min(cur, outTroopCount));
                                }
                            }
                            if (inTroopCount > 0 && !string.IsNullOrEmpty(inTroopId))
                            {
                                var inTroop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(inTroopId);
                                if (inTroop != null)
                                {
                                    roster.AddToCounts(inTroop, inTroopCount);
                                }
                            }
                        }

                        var itemRoster = MobileParty.MainParty?.ItemRoster;
                        if (itemRoster != null)
                        {
                            if (outItemCount > 0 && !string.IsNullOrEmpty(outItemId))
                            {
                                var outItem = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<ItemObject>(outItemId);
                                if (outItem != null)
                                {
                                    int idx = itemRoster.FindIndexOfItem(outItem);
                                    int cur = idx >= 0 ? itemRoster.GetElementNumber(idx) : 0;
                                    if (cur > 0) itemRoster.AddToCounts(outItem, -Math.Min(cur, outItemCount));
                                }
                            }
                            if (inItemCount > 0 && !string.IsNullOrEmpty(inItemId))
                            {
                                var inItem = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<ItemObject>(inItemId);
                                if (inItem != null)
                                {
                                    itemRoster.AddToCounts(inItem, inItemCount);
                                }
                            }
                        }

                        if (outShipIdx >= 0 || inShipIdx >= 0)
                        {
                            int partnerPeerId = iAmInit ? packet.TargetPeerId : packet.InitiatorPeerId;
                            var partnerParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(partnerPeerId);
                            var mainParty = MobileParty.MainParty;
                            Ship? outShip = (outShipIdx >= 0 && mainParty?.Ships != null && outShipIdx < mainParty.Ships.Count)
                                ? mainParty.Ships[outShipIdx]
                                : null;
                            Ship? inShip = (inShipIdx >= 0 && partnerParty?.Ships != null && inShipIdx < partnerParty.Ships.Count)
                                ? partnerParty.Ships[inShipIdx]
                                : null;

                            if (outShip != null && partnerParty?.Party != null)
                            {
                                try { ChangeShipOwnerAction.ApplyByTrade(partnerParty.Party, outShip); } catch { }
                            }
                            if (inShip != null && PartyBase.MainParty != null)
                            {
                                try { ChangeShipOwnerAction.ApplyByTrade(PartyBase.MainParty, inShip); } catch { }
                            }
                            NavalSyncService.Instance.SyncLocalPlayerFleetToServer();
                        }

                        ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "P2PTradeCommitted" });
                        InformationManager.DisplayMessage(new InformationMessage(packet.DialogText, Colors.Green));

                        // Clear local UI trade fields on the packet copy after applying delta so the table resets cleanly.
                        packet.InitiatorTradeGold = 0;
                        packet.InitiatorTradeTroopId = string.Empty;
                        packet.InitiatorTradeTroopCount = 0;
                        packet.InitiatorTradeItemId = string.Empty;
                        packet.InitiatorTradeItemCount = 0;
                        packet.InitiatorTradeShipIndex = -1;
                        packet.InitiatorTradeShipDesc = string.Empty;
                        packet.InitiatorTradeAccepted = false;
                        packet.TargetTradeGold = 0;
                        packet.TargetTradeTroopId = string.Empty;
                        packet.TargetTradeTroopCount = 0;
                        packet.TargetTradeItemId = string.Empty;
                        packet.TargetTradeItemCount = 0;
                        packet.TargetTradeShipIndex = -1;
                        packet.TargetTradeShipDesc = string.Empty;
                        packet.TargetTradeAccepted = false;
                    }

                    if (packet.Stage == 15) // LiveBarterOpen -> Partner opens live Barter UI too!
                    {
                        int partnerPeer = packet.SenderPeerId == packet.InitiatorPeerId ? packet.InitiatorPeerId : (packet.InitiatorPeerId == myPeerId ? packet.TargetPeerId : packet.InitiatorPeerId);
                        if (partnerPeer > 0 && partnerPeer != myPeerId)
                        {
                            OpenLordBarterWithPlayer(
                                partnerPeer,
                                notifyPartner: false,
                                explicitSessionId: packet.SessionId,
                                explicitInitPeer: packet.InitiatorPeerId,
                                explicitTargPeer: packet.TargetPeerId);
                        }
                        return;
                    }

                    if (packet.Stage == 16) // LiveBarterSync -> Update partner's side on open BarterVM + reset acceptances
                    {
                        if (!IsLiveBarterOpen || LiveBarterSessionId != packet.SessionId) return;

                        bool iAmInit = myPeerId == LiveBarterInitiatorPeerId;
                        string partnerTokens = iAmInit ? (packet.SoldItemsCsv ?? string.Empty) : (packet.BoughtItemsCsv ?? string.Empty);

                        LiveBarterLocalAccepted = false;
                        LiveBarterPartnerAccepted = false;

                        if (!string.Equals(LiveBarterPartnerTokens, partnerTokens, StringComparison.Ordinal))
                        {
                            LiveBarterPartnerTokens = partnerTokens;
                            PartnerBarterTokensUpdated?.Invoke(partnerTokens);
                        }
                        LiveBarterStatusChanged?.Invoke();
                        return;
                    }

                    if (packet.Stage == 17) // LiveBarterAccept -> Mutual readiness status update or validation fail
                    {
                        if (!IsLiveBarterOpen || LiveBarterSessionId != packet.SessionId) return;

                        bool iAmInit = myPeerId == LiveBarterInitiatorPeerId;
                        string partnerTokens = iAmInit ? (packet.SoldItemsCsv ?? string.Empty) : (packet.BoughtItemsCsv ?? string.Empty);
                        LiveBarterLocalAccepted = iAmInit ? packet.InitiatorTradeAccepted : packet.TargetTradeAccepted;
                        LiveBarterPartnerAccepted = iAmInit ? packet.TargetTradeAccepted : packet.InitiatorTradeAccepted;

                        if (!string.Equals(LiveBarterPartnerTokens, partnerTokens, StringComparison.Ordinal))
                        {
                            LiveBarterPartnerTokens = partnerTokens;
                            PartnerBarterTokensUpdated?.Invoke(partnerTokens);
                        }
                        if (packet.OptionKey == "BARTER_LIVE_FAIL" && !string.IsNullOrEmpty(packet.DialogText))
                        {
                            InformationManager.DisplayMessage(new InformationMessage(packet.DialogText, Colors.Red));
                        }
                        LiveBarterStatusChanged?.Invoke();
                        return;
                    }

                    if (packet.Stage == 18) // LiveBarterCommitted -> Commit atomically, close BOTH Barter UIs & resume conversation
                    {
                        ApplyClientLordBarterSide(packet.BoughtItemsCsv, myPeerId, packet.InitiatorPeerId, packet.TargetPeerId);
                        if (myPeerId == packet.InitiatorPeerId || myPeerId == packet.TargetPeerId)
                        {
                            CurrentConversationSpeech = packet.DialogText;
                            CloseLiveBarterScreenSilently();
                            ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "LordBarterCommitted" });
                            RosterSyncService.Instance.SyncLocalPlayerRosterToServer();
                            InformationManager.DisplayMessage(new InformationMessage(packet.DialogText, Colors.Green));
                        }
                        return;
                    }

                    if (packet.Stage == 19) // LiveBarterClosed -> Partner cancelled or disconnected
                    {
                        if (IsLiveBarterOpen && (string.IsNullOrEmpty(packet.SessionId) || LiveBarterSessionId == packet.SessionId))
                        {
                            CloseLiveBarterScreenSilently();
                            string msg = packet.OptionKey == "disconnect"
                                ? Loc.T("[Teklif] Karşı oyuncu ayrıldığı için takas sonlandı.", "[Barter] Partner disconnected; barter closed.")
                                : Loc.F("[Teklif] {0} takas masasını kapattı.", "[Barter] {0} closed the barter window.", packet.SenderName);
                            InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));
                        }
                        return;
                    }

                    if (packet.Stage == 12) // Lord Barter / Trade Offer received -> Partner confirms via native Inquiry
                    {
                        if (myPeerId != packet.TargetPeerId) return;

                        bool isLordBarter = packet.OptionKey == "BARTER_V1";
                        string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);

                        string bodyText;
                        string titleText;
                        if (isLordBarter)
                        {
                            titleText = Loc.F("{0} — Ticaret Teklifi", "{0} — Trade Offer", packet.SenderName);
                            bodyText = FormatLordBarterSummary(packet.BoughtItemsCsv, perspectiveIsPartner: true, myName, packet.SenderName);
                        }
                        else
                        {
                            string giveSummary = FormatTradeItemsDisplay(packet.BoughtItemsCsv);
                            string getSummary = FormatTradeItemsDisplay(packet.SoldItemsCsv);
                            string goldSummary = packet.NetGoldDelta > 0
                                ? Loc.F("Ödeyeceğiniz Altın: {0}", "Gold You Pay: {0}", packet.NetGoldDelta)
                                : (packet.NetGoldDelta < 0
                                    ? Loc.F("Alacağınız Altın: {0}", "Gold You Receive: {0}", -packet.NetGoldDelta)
                                    : Loc.T("Altın Farkı: 0", "Net Gold: 0"));

                            titleText = Loc.F("{0} — Ticaret Teklifi", "{0} — Trade Offer", packet.SenderName);
                            bodyText = Loc.F(
                                "{0} adlı oyuncu size bir ticaret teklifi gönderdi:\n\n" +
                                "Vereceğiniz Eşyalar: {1}\n" +
                                "Alacağınız Eşyalar: {2}\n" +
                                "{3}\n\nBu ticareti onaylıyor musunuz?",
                                "{0} sent you a trade offer:\n\n" +
                                "Items You Give: {1}\n" +
                                "Items You Receive: {2}\n" +
                                "{3}\n\nDo you accept this trade?",
                                packet.SenderName, giveSummary, getSummary, goldSummary);
                        }

                        string sid = packet.SessionId;
                        int initPeer = packet.InitiatorPeerId;
                        int targPeer = packet.TargetPeerId;
                        string optKey = isLordBarter ? "BARTER_V1" : "native_trade_accept";
                        string decKey = isLordBarter ? "BARTER_V1" : "native_trade_decline";

                        InformationManager.ShowInquiry(new InquiryData(
                            titleText: titleText,
                            text: bodyText,
                            isAffirmativeOptionShown: true,
                            isNegativeOptionShown: true,
                            affirmativeText: Loc.T("Kabul Et", "Accept"),
                            negativeText: Loc.T("Reddet", "Decline"),
                            affirmativeAction: () =>
                            {
                                SendDialogPacket(new P2PDialogPacket
                                {
                                    SessionId = sid,
                                    InitiatorPeerId = initPeer,
                                    TargetPeerId = targPeer,
                                    SenderPeerId = myPeerId,
                                    SenderName = myName,
                                    Stage = 13,
                                    OptionKey = optKey,
                                    SpeakerName = myName
                                });
                            },
                            negativeAction: () =>
                            {
                                SendDialogPacket(new P2PDialogPacket
                                {
                                    SessionId = sid,
                                    InitiatorPeerId = initPeer,
                                    TargetPeerId = targPeer,
                                    SenderPeerId = myPeerId,
                                    SenderName = myName,
                                    Stage = 14,
                                    OptionKey = decKey,
                                    SpeakerName = myName
                                });
                            }), pauseGameActiveState: false, prioritize: true);
                        return;
                    }

                    if (packet.Stage == 13) // Lord Barter / Trade committed by server
                    {
                        if (packet.OptionKey == "BARTER_V1")
                        {
                            ApplyClientLordBarterSide(packet.BoughtItemsCsv, myPeerId, packet.InitiatorPeerId, packet.TargetPeerId);
                            if (myPeerId == packet.InitiatorPeerId || myPeerId == packet.TargetPeerId)
                            {
                                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "LordBarterCommitted" });
                                RosterSyncService.Instance.SyncLocalPlayerRosterToServer();
                                InformationManager.DisplayMessage(new InformationMessage(packet.DialogText, Colors.Green));
                                if (IsInDialog)
                                {
                                    CurrentConversationSpeech = packet.DialogText;
                                    RefreshActiveNativeConversation();
                                }
                            }
                            return;
                        }

                        bool iAmProposer = myPeerId == packet.InitiatorPeerId;
                        var mainParty = MobileParty.MainParty;
                        if (iAmProposer)
                        {
                            ApplyTradeItemCsvDelta(mainParty, packet.SoldItemsCsv, -1);
                            ApplyTradeItemCsvDelta(mainParty, packet.BoughtItemsCsv, +1);
                            if (Hero.MainHero != null)
                            {
                                Hero.MainHero.Gold = Math.Max(0, Hero.MainHero.Gold + packet.NetGoldDelta);
                            }
                        }
                        else
                        {
                            ApplyTradeItemCsvDelta(mainParty, packet.BoughtItemsCsv, -1);
                            ApplyTradeItemCsvDelta(mainParty, packet.SoldItemsCsv, +1);
                            if (Hero.MainHero != null)
                            {
                                Hero.MainHero.Gold = Math.Max(0, Hero.MainHero.Gold - packet.NetGoldDelta);
                            }
                        }

                        ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "NativeP2PTradeCommitted" });
                        RosterSyncService.Instance.SyncLocalPlayerRosterToServer();
                        InformationManager.DisplayMessage(new InformationMessage(packet.DialogText, Colors.Green));
                        return;
                    }

                    if (packet.Stage == 14) // Lord Barter / Trade Declined or Failed Validation
                    {
                        string msg = packet.OptionKey == "BARTER_FAIL" && !string.IsNullOrEmpty(packet.DialogText)
                            ? packet.DialogText
                            : Loc.F("[Teklif] {0} teklifinizi reddetti.", "[Barter] {0} declined your offer.", packet.SenderName);
                        InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));
                        if (IsInDialog)
                        {
                            CurrentConversationSpeech = msg;
                            RefreshActiveNativeConversation();
                        }
                        return;
                    }

                    if (packet.Stage == 10 || packet.Stage == 11)
                    {
                        return;
                    }

                    ModContext.Events.Publish(new P2PDialogUpdatedEvent { Packet = packet });
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] HandleDialogPacket error: {ex.Message}");
                }
            });
        }

        public void ExecuteDialogAction(string actionKey, int targetPeerId, string sessionId)
        {
            var targetParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(targetPeerId);
            var targetHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(targetPeerId);
            string targetName = targetHero?.Name?.ToString() ?? targetParty?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", targetPeerId);

            GetSessionEndpoints(sessionId, targetPeerId, out int sessionInitPeer, out int sessionTargPeer);
            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Oyuncu", "Player");

            if (actionKey == "open_trade" || actionKey == "trade")
            {
                OpenNativeTradeWithPlayer(targetPeerId);
                return;
            }

            if (actionKey == "attack_player" && targetParty != null)
            {
                SendDialogPacket(new P2PDialogPacket
                {
                    SessionId = sessionId,
                    InitiatorPeerId = sessionInitPeer,
                    TargetPeerId = sessionTargPeer,
                    SenderPeerId = myPeerId,
                    SenderName = myName,
                    Stage = 5, // Close
                    OptionKey = "attack_player",
                    SpeakerName = myName,
                    DialogText = Loc.T("Sözün bittiği yerdeyiz! Kılıçlar konuşsun!", "Enough talk! Prepare for battle!")
                });
                CloseActiveDialogLocalOnly();
                BattleSyncService.Instance.RequestPvpBattle(targetParty);
                return;
            }

            if (actionKey == "trade_accept")
            {
                SendDialogPacket(new P2PDialogPacket
                {
                    SessionId = sessionId,
                    InitiatorPeerId = sessionInitPeer,
                    TargetPeerId = sessionTargPeer,
                    SenderPeerId = myPeerId,
                    SenderName = myName,
                    Stage = 7,
                    OptionKey = "trade_accept",
                    SpeakerName = myName
                });
                return;
            }

            if (actionKey == "trade_reset")
            {
                SendDialogPacket(new P2PDialogPacket
                {
                    SessionId = sessionId,
                    InitiatorPeerId = sessionInitPeer,
                    TargetPeerId = sessionTargPeer,
                    SenderPeerId = myPeerId,
                    SenderName = myName,
                    Stage = 9,
                    OptionKey = "trade_reset",
                    SpeakerName = myName
                });
                return;
            }

            if (targetParty != null)
            {
                ExecuteChosenInteraction(actionKey, targetPeerId, targetName, targetParty, sessionId);
            }
        }

        private void ExecuteChosenInteraction(string actionKey, int targetPeerId, string targetName, MobileParty targetParty, string dialogSessionId = "")
        {
            int localPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string senderName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Oyuncu", "Player");
            GetSessionEndpoints(dialogSessionId, targetPeerId, out int sessionInitPeer, out int sessionTargPeer);

            string normalizedKey = actionKey switch
            {
                "gold" => "send_gold",
                "troops" => "send_troops",
                "item" => "offer_item",
                "ship" => "offer_ship",
                "duel" => "duel_request",
                "rally" => "rally_escort",
                "say" => "direct_message",
                _ => actionKey
            };

            switch (normalizedKey)
            {
                case "offer_ship":
                {
                    if (!NavalSyncService.IsNavalDlcActive || MobileParty.MainParty?.Ships == null || MobileParty.MainParty.Ships.Count == 0 || string.IsNullOrEmpty(dialogSessionId))
                    {
                        InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Takas] Filonuzda teklif edebileceğiniz gemi bulunmuyor.", "[Trade] No ships available in your fleet."), Colors.Red));
                        return;
                    }

                    var shipElements = new List<InquiryElement>
                    {
                        new InquiryElement(-1, Loc.T("Gemi Teklifini Kaldır", "Clear Ship Offer"), null, true, Loc.T("Masadaki gemi teklifini kaldır.", "Remove ship offer from trade table."))
                    };

                    var ships = MobileParty.MainParty.Ships;
                    for (int i = 0; i < ships.Count; i++)
                    {
                        var ship = ships[i];
                        if (ship == null || !ship.IsTradeable || ship.IsUsedByQuest) continue;
                        string hullName = ship.ShipHull?.Name?.ToString() ?? Loc.T("Gemi", "Ship");
                        shipElements.Add(new InquiryElement(
                            i,
                            $"{ship.Name} ({hullName} - {(int)ship.HitPoints}/{(int)ship.MaxHitPoints})",
                            null,
                            true,
                            Loc.T("Bu gemiyi takas masasına koy.", "Offer this ship on the trade table.")));
                    }

                    if (shipElements.Count <= 1)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Takas] Takas edilebilir uygun gemi yok.", "[Trade] No tradeable ships available."), Colors.Red));
                        return;
                    }

                    SuspendDialogForInquiryRequested?.Invoke();
                    MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                        titleText: Loc.F("{0} İle Takas — Gemi Seç", "Trade with {0} — Select Ship", targetName),
                        descriptionText: Loc.T("Takas masasına koymak istediğiniz gemiyi seçin:", "Choose a ship to offer on the trade table:"),
                        inquiryElements: shipElements,
                        isExitShown: true,
                        minSelectableOptionCount: 1,
                        maxSelectableOptionCount: 1,
                        affirmativeText: Loc.T("Seç", "Select"),
                        negativeText: Loc.T("İptal", "Cancel"),
                        affirmativeAction: (selected) =>
                        {
                            ResumeDialogAfterInquiryRequested?.Invoke();
                            if (selected == null || selected.Count == 0 || selected[0].Identifier is not int shipIdx) return;

                            SendDialogPacket(new P2PDialogPacket
                            {
                                SessionId = dialogSessionId,
                                InitiatorPeerId = sessionInitPeer,
                                TargetPeerId = sessionTargPeer,
                                SenderPeerId = localPeerId,
                                SenderName = senderName,
                                Stage = 6,
                                OptionKey = "offer_ship",
                                SpeakerName = senderName,
                                OfferAmount = shipIdx
                            });
                        },
                        negativeAction: (_) => { ResumeDialogAfterInquiryRequested?.Invoke(); }),
                        pauseGameActiveState: false,
                        prioritize: true);
                    break;
                }
                case "offer_item":
                {
                    var itemRoster = MobileParty.MainParty?.ItemRoster;
                    if (itemRoster == null || itemRoster.Count == 0 || string.IsNullOrEmpty(dialogSessionId))
                    {
                        InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Takas] Envanterinizde teklif edilebilecek eşya yok.", "[Trade] No items available in inventory."), Colors.Red));
                        return;
                    }

                    var itemElements = new List<InquiryElement>();
                    for (int i = 0; i < itemRoster.Count; i++)
                    {
                        var elem = itemRoster.GetElementCopyAtIndex(i);
                        var item = elem.EquipmentElement.Item;
                        if (item == null || elem.Amount <= 0) continue;

                        itemElements.Add(new InquiryElement(
                            item,
                            $"{item.Name} ({Loc.F("Adet: {0}", "Count: {0}", elem.Amount)})",
                            null,
                            true,
                            Loc.T("Bu eşyayı takas masasına koy.", "Offer this item on the trade table.")));
                    }

                    if (itemElements.Count == 0)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Takas] Seçilebilir eşya bulunamadı.", "[Trade] No selectable items found."), Colors.Red));
                        return;
                    }

                    SuspendDialogForInquiryRequested?.Invoke();
                    MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                        titleText: Loc.F("{0} İle Takas — Eşya Seç", "Trade with {0} — Select Item", targetName),
                        descriptionText: Loc.T("Takas masasına koymak istediğiniz eşyayı seçin:", "Choose an item to offer on the trade table:"),
                        inquiryElements: itemElements,
                        isExitShown: true,
                        minSelectableOptionCount: 1,
                        maxSelectableOptionCount: 1,
                        affirmativeText: Loc.T("Seç", "Select"),
                        negativeText: Loc.T("İptal", "Cancel"),
                        affirmativeAction: (selected) =>
                        {
                            var chosenItem = selected?.FirstOrDefault()?.Identifier as ItemObject;
                            if (chosenItem == null || MobileParty.MainParty?.ItemRoster == null)
                            {
                                ResumeDialogAfterInquiryRequested?.Invoke();
                                return;
                            }

                            int idx = MobileParty.MainParty.ItemRoster.FindIndexOfItem(chosenItem);
                            int avail = idx >= 0 ? Math.Max(0, MobileParty.MainParty.ItemRoster.GetElementNumber(idx)) : 0;
                            if (avail <= 0)
                            {
                                ResumeDialogAfterInquiryRequested?.Invoke();
                                return;
                            }

                            InformationManager.ShowTextInquiry(new TextInquiryData(
                                titleText: Loc.F("{0} Takas Teklifi", "Offer {0}", chosenItem.Name),
                                text: Loc.F("Kaç adet '{0}' koymak istiyorsunuz? (En fazla: {1}, sıfırlamak için 0):", "How many '{0}' do you want to offer? (Max: {1}, 0 to clear):", chosenItem.Name, avail),
                                isAffirmativeOptionShown: true,
                                isNegativeOptionShown: true,
                                affirmativeText: Loc.T("Masaya Koy", "Offer Item"),
                                negativeText: Loc.T("İptal", "Cancel"),
                                affirmativeAction: (countStr) =>
                                {
                                    ResumeDialogAfterInquiryRequested?.Invoke();
                                    if (!int.TryParse(countStr?.Trim(), out int count) || count < 0) return;

                                    int idxNow = MobileParty.MainParty?.ItemRoster?.FindIndexOfItem(chosenItem) ?? -1;
                                    int availNow = idxNow >= 0 ? Math.Max(0, MobileParty.MainParty!.ItemRoster.GetElementNumber(idxNow)) : 0;
                                    count = Math.Min(count, availNow);

                                    SendDialogPacket(new P2PDialogPacket
                                    {
                                        SessionId = dialogSessionId,
                                        InitiatorPeerId = sessionInitPeer,
                                        TargetPeerId = sessionTargPeer,
                                        SenderPeerId = localPeerId,
                                        SenderName = senderName,
                                        Stage = 6,
                                        OptionKey = "offer_item",
                                        SpeakerName = senderName,
                                        OfferObjectId = chosenItem.StringId,
                                        OfferAmount = count
                                    });
                                },
                                negativeAction: () => { ResumeDialogAfterInquiryRequested?.Invoke(); },
                                shouldInputBeObfuscated: false,
                                defaultInputText: Math.Min(1, avail).ToString()));
                        },
                        negativeAction: (_) => { ResumeDialogAfterInquiryRequested?.Invoke(); }),
                        pauseGameActiveState: false,
                        prioritize: true);
                    break;
                }
                case "send_gold":
                {
                    int myGold = Hero.MainHero?.Gold ?? 0;
                    if (myGold <= 0 && string.IsNullOrEmpty(dialogSessionId))
                    {
                        InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Oyuncu Etkileşimi] Gönderebilecek altının yok!", "[Player Interaction] You have no gold to send!"), Colors.Red));
                        return;
                    }

                    bool isTradeOffer = !string.IsNullOrEmpty(dialogSessionId);
                    SuspendDialogForInquiryRequested?.Invoke();
                    InformationManager.ShowTextInquiry(new TextInquiryData(
                        titleText: isTradeOffer
                            ? Loc.F("{0} İle Takas — Altın Teklifi", "Trade with {0} — Gold Offer", targetName)
                            : Loc.F("{0} Adlı Oyuncuya Altın Gönder", "Send Gold to {0}", targetName),
                        text: isTradeOffer
                            ? Loc.F("Takas masasına koymak istediğiniz altın miktarını girin (Mevcut: {0}, sıfırlamak için 0):", "Enter gold amount to put on the trade table (Available: {0}, 0 to clear):", myGold)
                            : Loc.F("Göndermek istediğiniz altın miktarını girin (Mevcut Altınınız: {0}):", "Enter gold amount to send (Available: {0}):", myGold),
                        isAffirmativeOptionShown: true,
                        isNegativeOptionShown: true,
                        affirmativeText: isTradeOffer ? Loc.T("Masaya Koy", "Offer Gold") : Loc.T("Gönder", "Send"),
                        negativeText: Loc.T("İptal", "Cancel"),
                        affirmativeAction: (input) =>
                        {
                            ResumeDialogAfterInquiryRequested?.Invoke();

                            if (!int.TryParse(input?.Trim(), out int amount) || (isTradeOffer ? amount < 0 : amount <= 0))
                            {
                                InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Oyuncu Etkileşimi] Geçersiz altın miktarı.", "[Player Interaction] Invalid gold amount."), Colors.Red));
                                return;
                            }

                            int available = Hero.MainHero?.Gold ?? 0;
                            if (amount > available)
                            {
                                InformationManager.DisplayMessage(new InformationMessage(Loc.F("[Oyuncu Etkileşimi] Yetersiz altın! En fazla {0} altın teklif edebilirsiniz.", "[Player Interaction] Insufficient gold! Max available: {0}.", available), Colors.Red));
                                return;
                            }

                            if (isTradeOffer)
                            {
                                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "P2PTradeOfferSync" });
                                SendDialogPacket(new P2PDialogPacket
                                {
                                    SessionId = dialogSessionId,
                                    InitiatorPeerId = sessionInitPeer,
                                    TargetPeerId = sessionTargPeer,
                                    SenderPeerId = localPeerId,
                                    SenderName = senderName,
                                    Stage = 6, // TradeOfferUpdate (Server-Authoritative, does NOT deduct yet)
                                    OptionKey = "offer_gold",
                                    SpeakerName = senderName,
                                    OfferAmount = amount
                                });
                            }
                            else
                            {
                                if (Hero.MainHero != null)
                                {
                                    Hero.MainHero.Gold -= amount;
                                }

                                SendInteractionPacket(new PlayerInteractionPacket
                                {
                                    SenderPeerId = localPeerId,
                                    SenderName = senderName,
                                    TargetPeerId = targetPeerId,
                                    TargetName = targetName,
                                    InteractionType = 1, // SendGold
                                    IntAmount = amount
                                });

                                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "GoldSentToPlayer" });
                                InformationManager.DisplayMessage(new InformationMessage(
                                    Loc.F("[Oyuncu Etkileşimi] '{0}' adlı oyuncuya {1} Altın gönderildi!", "[Player Interaction] Sent {1} Gold to '{0}'!", targetName, amount),
                                    Colors.Green));
                            }
                        },
                        negativeAction: () =>
                        {
                            ResumeDialogAfterInquiryRequested?.Invoke();
                        },
                        shouldInputBeObfuscated: false,
                        defaultInputText: Math.Min(500, Math.Max(0, myGold)).ToString()));
                    break;
                }

                case "send_troops":
                {
                    var roster = MobileParty.MainParty?.MemberRoster;
                    if (roster == null || roster.TotalHealthyCount <= 1)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Oyuncu Etkileşimi] Teklif edebileceğiniz sağlıklı asker bulunmuyor.", "[Player Interaction] No healthy troops available to offer."), Colors.Red));
                        return;
                    }

                    var troopElements = new List<InquiryElement>();
                    for (int i = 0; i < roster.Count; i++)
                    {
                        var elem = roster.GetElementCopyAtIndex(i);
                        if (elem.Character == null || elem.Character.IsHero) continue;
                        int healthy = elem.Number - elem.WoundedNumber;
                        if (healthy <= 0) continue;

                        troopElements.Add(new InquiryElement(
                            elem.Character,
                            $"{elem.Character.Name} ({Loc.F("Sağlıklı: {0}", "Healthy: {0}", healthy)})",
                            null,
                            true,
                            Loc.T("Bu birliği takasa/transfere seçin.", "Select this troop type.")));
                    }

                    if (troopElements.Count == 0)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(Loc.T("[Oyuncu Etkileşimi] Seçilebilir sağlıklı asker yok.", "[Player Interaction] No healthy troops available."), Colors.Red));
                        return;
                    }

                    bool isTradeOffer = !string.IsNullOrEmpty(dialogSessionId);
                    SuspendDialogForInquiryRequested?.Invoke();
                    MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                        titleText: isTradeOffer
                            ? Loc.F("{0} İle Takas — Asker Seç", "Trade with {0} — Select Troop", targetName)
                            : Loc.F("{0} Birliğine Asker Gönder", "Send Troops to {0}", targetName),
                        descriptionText: Loc.T("Asker türünü seçin:", "Choose troop type:"),
                        inquiryElements: troopElements,
                        isExitShown: true,
                        minSelectableOptionCount: 1,
                        maxSelectableOptionCount: 1,
                        affirmativeText: Loc.T("Seç", "Select"),
                        negativeText: Loc.T("İptal", "Cancel"),
                        affirmativeAction: (selected) =>
                        {
                            var chosenTroop = selected?.FirstOrDefault()?.Identifier as CharacterObject;
                            if (chosenTroop == null || MobileParty.MainParty?.MemberRoster == null)
                            {
                                ResumeDialogAfterInquiryRequested?.Invoke();
                                return;
                            }

                            int currentHealthy = 0;
                            int idx = MobileParty.MainParty.MemberRoster.FindIndexOfTroop(chosenTroop);
                            if (idx >= 0)
                            {
                                currentHealthy = Math.Max(0, MobileParty.MainParty.MemberRoster.GetElementNumber(idx) - MobileParty.MainParty.MemberRoster.GetElementWoundedNumber(idx));
                            }
                            if (currentHealthy <= 0)
                            {
                                ResumeDialogAfterInquiryRequested?.Invoke();
                                return;
                            }

                            InformationManager.ShowTextInquiry(new TextInquiryData(
                                titleText: isTradeOffer
                                    ? Loc.F("{0} Takas Teklifi", "Offer {0}", chosenTroop.Name)
                                    : Loc.F("{0} Gönder", "Send {0}", chosenTroop.Name),
                                text: Loc.F("Kaç adet '{0}' koymak istiyorsunuz? (En fazla: {1}):", "How many '{0}' do you want to offer? (Max: {1}):", chosenTroop.Name, currentHealthy),
                                isAffirmativeOptionShown: true,
                                isNegativeOptionShown: true,
                                affirmativeText: isTradeOffer ? Loc.T("Masaya Koy", "Offer Troops") : Loc.T("Askerleri Gönder", "Send Troops"),
                                negativeText: Loc.T("İptal", "Cancel"),
                                affirmativeAction: (countStr) =>
                                {
                                    ResumeDialogAfterInquiryRequested?.Invoke();

                                    if (!int.TryParse(countStr?.Trim(), out int count) || (isTradeOffer ? count < 0 : count <= 0)) return;
                                    int idxNow = MobileParty.MainParty?.MemberRoster?.FindIndexOfTroop(chosenTroop) ?? -1;
                                    int avail = idxNow >= 0
                                        ? Math.Max(0, MobileParty.MainParty!.MemberRoster.GetElementNumber(idxNow) - MobileParty.MainParty.MemberRoster.GetElementWoundedNumber(idxNow))
                                        : 0;
                                    count = Math.Min(count, avail);
                                    if (!isTradeOffer && count <= 0) return;

                                    if (isTradeOffer)
                                    {
                                        ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "P2PTradeOfferSync" });
                                        SendDialogPacket(new P2PDialogPacket
                                        {
                                            SessionId = dialogSessionId,
                                            InitiatorPeerId = sessionInitPeer,
                                            TargetPeerId = sessionTargPeer,
                                            SenderPeerId = localPeerId,
                                            SenderName = senderName,
                                            Stage = 6,
                                            OptionKey = "offer_troop",
                                            SpeakerName = senderName,
                                            OfferObjectId = chosenTroop.StringId,
                                            OfferAmount = count
                                        });
                                    }
                                    else
                                    {
                                        MobileParty.MainParty!.MemberRoster.AddToCounts(chosenTroop, -count);

                                        SendInteractionPacket(new PlayerInteractionPacket
                                        {
                                            SenderPeerId = localPeerId,
                                            SenderName = senderName,
                                            TargetPeerId = targetPeerId,
                                            TargetName = targetName,
                                            InteractionType = 2, // SendTroops
                                            ObjectId = chosenTroop.StringId,
                                            IntAmount = count,
                                            Message = chosenTroop.Name?.ToString() ?? chosenTroop.StringId
                                        });

                                        ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "TroopsSentToPlayer" });
                                        InformationManager.DisplayMessage(new InformationMessage(
                                            Loc.F("[Oyuncu Etkileşimi] '{0}' birliğine {1}x {2} gönderildi!", "[Player Interaction] Sent {1}x {2} to '{0}'!", targetName, count, chosenTroop.Name),
                                            Colors.Green));
                                    }
                                },
                                negativeAction: () =>
                                {
                                    ResumeDialogAfterInquiryRequested?.Invoke();
                                },
                                shouldInputBeObfuscated: false,
                                defaultInputText: Math.Min(5, currentHealthy).ToString()));
                        },
                        negativeAction: (_) =>
                        {
                            ResumeDialogAfterInquiryRequested?.Invoke();
                        }),
                        pauseGameActiveState: false,
                        prioritize: true);
                    break;
                }

                case "gift_gold":
                {
                    const int giftAmount = 500;
                    if (Hero.MainHero == null || Hero.MainHero.Gold < giftAmount)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            Loc.T("[İlişki] Hediye göndermek için en az 500 Altınınız olmalı.", "[Relation] You need at least 500 Gold to send a gift."),
                            Colors.Red));
                        break;
                    }

                    Hero.MainHero.Gold -= giftAmount;
                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "GiftGoldSent" });
                    SendInteractionPacket(new PlayerInteractionPacket
                    {
                        SenderPeerId = localPeerId,
                        SenderName = senderName,
                        TargetPeerId = targetPeerId,
                        TargetName = targetName,
                        InteractionType = 1, // SendGold (+5 Relation applied server-side)
                        IntAmount = giftAmount,
                        ObjectId = "GIFT",
                        Message = Loc.F("{0}, dostluk göstergesi olarak 500 Altın hediye etti!", "{0} sent a 500 Gold friendship gift!", senderName)
                    });

                    if (!string.IsNullOrEmpty(dialogSessionId))
                    {
                        SendDialogPacket(new P2PDialogPacket
                        {
                            SessionId = dialogSessionId,
                            InitiatorPeerId = sessionInitPeer,
                            TargetPeerId = sessionTargPeer,
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            Stage = 2,
                            OptionKey = "gift_gold",
                            SpeakerName = senderName,
                            DialogText = Loc.F("Dostluğumuzun nişanesi olarak sana {0} Altın sunuyorum (+5 İlişki).", "As a token of friendship, I gift you {0} Gold (+5 Relation).", giftAmount)
                        });
                    }
                    break;
                }

                case "peace_proposal":
                {
                    if (!string.IsNullOrEmpty(dialogSessionId))
                    {
                        SendDialogPacket(new P2PDialogPacket
                        {
                            SessionId = dialogSessionId,
                            InitiatorPeerId = sessionInitPeer,
                            TargetPeerId = sessionTargPeer,
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            Stage = 3,
                            OptionKey = "peace_proposal",
                            SpeakerName = senderName,
                            DialogText = Loc.T("Bu kanlı savaşı bitirip kalıcı barış yapmaya ne dersin? (+10 İlişki)", "Shall we end this war and sign a lasting peace treaty? (+10 Relation)")
                        });
                    }
                    else
                    {
                        SendInteractionPacket(new PlayerInteractionPacket
                        {
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            TargetPeerId = targetPeerId,
                            TargetName = targetName,
                            InteractionType = 9,
                            ObjectId = "PROPOSE_PEACE"
                        });
                    }
                    break;
                }

                case "alliance_proposal":
                {
                    if (!string.IsNullOrEmpty(dialogSessionId))
                    {
                        SendDialogPacket(new P2PDialogPacket
                        {
                            SessionId = dialogSessionId,
                            InitiatorPeerId = sessionInitPeer,
                            TargetPeerId = sessionTargPeer,
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            Stage = 3,
                            OptionKey = "alliance_proposal",
                            SpeakerName = senderName,
                            DialogText = Loc.T("Kaderlerimizi birleştirip ittifak ve saldırmazlık antlaşması imzalayalım mı? (+10 İlişki)", "Shall we forge an alliance and non-aggression pact? (+10 Relation)")
                        });
                    }
                    else
                    {
                        SendInteractionPacket(new PlayerInteractionPacket
                        {
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            TargetPeerId = targetPeerId,
                            TargetName = targetName,
                            InteractionType = 9,
                            ObjectId = "PROPOSE_ALLY"
                        });
                    }
                    break;
                }

                case "duel_request":
                {
                    if (!string.IsNullOrEmpty(dialogSessionId))
                    {
                        SendDialogPacket(new P2PDialogPacket
                        {
                            SessionId = dialogSessionId,
                            InitiatorPeerId = sessionInitPeer,
                            TargetPeerId = sessionTargPeer,
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            Stage = 3, // Proposal
                            OptionKey = "duel_request",
                            SpeakerName = senderName,
                            DialogText = Loc.T("Onur ve nam (+5) için dostluk düellosuna var mısın?", "Will you accept a friendly duel for honor and renown (+5)?")
                        });
                    }
                    else
                    {
                        SendInteractionPacket(new PlayerInteractionPacket
                        {
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            TargetPeerId = targetPeerId,
                            TargetName = targetName,
                            InteractionType = 3, // DuelRequest
                            IntAmount = 0
                        });
                    }

                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.F("[Oyuncu Etkileşimi] '{0}' adlı oyuncuya dostluk düellosu daveti gönderildi!", "[Player Interaction] Friendly duel invite sent to '{0}'!", targetName),
                        Colors.Cyan));
                    break;
                }

                case "rally_escort":
                {
                    if (MobileParty.MainParty != null && MobileParty.MainParty.CurrentSettlement == null && targetParty.CurrentSettlement == null)
                    {
                        MobileParty.MainParty.SetMoveEscortParty(targetParty, MobileParty.NavigationType.Default, false);
                    }

                    string loc = Settlement.CurrentSettlement?.Name?.ToString()
                                 ?? MobileParty.MainParty?.CurrentSettlement?.Name?.ToString()
                                 ?? Loc.T("Sefer Haritası", "Campaign Map");

                    SendInteractionPacket(new PlayerInteractionPacket
                    {
                        SenderPeerId = localPeerId,
                        SenderName = senderName,
                        TargetPeerId = targetPeerId,
                        TargetName = targetName,
                        InteractionType = 6, // RallyCall
                        Message = loc
                    });

                    if (!string.IsNullOrEmpty(dialogSessionId))
                    {
                        SendDialogPacket(new P2PDialogPacket
                        {
                            SessionId = dialogSessionId,
                            InitiatorPeerId = sessionInitPeer,
                            TargetPeerId = sessionTargPeer,
                            SenderPeerId = localPeerId,
                            SenderName = senderName,
                            Stage = 3,
                            OptionKey = "rally_escort",
                            SpeakerName = senderName,
                            DialogText = Loc.T("Birliklerimizi birleştirip birlikte ilerleyelim!", "Let us join forces and march together!")
                        });
                    }

                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.F("[Ortak Harekat] '{0}' birliğine ortak harekat bildirimi gönderildi.", "[Joint March] Joint operation notice sent to '{0}'.", targetName),
                        Colors.Cyan));
                    break;
                }

                case "direct_message":
                {
                    SuspendDialogForInquiryRequested?.Invoke();
                    InformationManager.ShowTextInquiry(new TextInquiryData(
                        titleText: Loc.F("{0} Adlı Oyuncuya Söz Söyle", "Speak to {0}", targetName),
                        text: Loc.T("Söylemek istediğiniz mesajı yazın:", "Type what you wish to say:"),
                        isAffirmativeOptionShown: true,
                        isNegativeOptionShown: true,
                        affirmativeText: Loc.T("Söyle / Gönder", "Say / Send"),
                        negativeText: Loc.T("İptal", "Cancel"),
                        affirmativeAction: (msg) =>
                        {
                            ResumeDialogAfterInquiryRequested?.Invoke();

                            if (string.IsNullOrWhiteSpace(msg)) return;
                            string trimmed = msg!.Trim();
                            if (!string.IsNullOrEmpty(dialogSessionId))
                            {
                                CurrentConversationSpeech = $"[{senderName}]: {trimmed}";
                                RefreshActiveNativeConversation();

                                SendDialogPacket(new P2PDialogPacket
                                {
                                    SessionId = dialogSessionId,
                                    InitiatorPeerId = sessionInitPeer,
                                    TargetPeerId = sessionTargPeer,
                                    SenderPeerId = localPeerId,
                                    SenderName = senderName,
                                    Stage = 2,
                                    OptionKey = "custom_line",
                                    SpeakerName = senderName,
                                    DialogText = trimmed
                                });
                            }
                            else
                            {
                                SendInteractionPacket(new PlayerInteractionPacket
                                {
                                    SenderPeerId = localPeerId,
                                    SenderName = senderName,
                                    TargetPeerId = targetPeerId,
                                    TargetName = targetName,
                                    InteractionType = 7, // DirectMessage
                                    Message = trimmed
                                });
                            }

                            InformationManager.DisplayMessage(new InformationMessage(
                                $"[{Loc.T("Diyalog", "Dialog")} -> {targetName}]: {trimmed}",
                                Colors.Cyan));
                        },
                        negativeAction: () =>
                        {
                            ResumeDialogAfterInquiryRequested?.Invoke();
                        },
                        shouldInputBeObfuscated: false,
                        defaultInputText: Loc.T("Selamlar! Birlikte hareket edelim mi?", "Greetings! Shall we ride together?")));
                    break;
                }
            }
        }

        public void SendInteractionPacket(PlayerInteractionPacket packet)
        {
            if (ModContext.IsServerRunning)
            {
                HandlePacket(packet);
            }
            else if (ModContext.IsClientConnected)
            {
                ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        public void HandlePacket(PlayerInteractionPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

                    // 1. Server Authoritative Processing & Forwarding
                    if (ModContext.IsServerRunning)
                    {
                        var senderHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.SenderPeerId);
                        var senderParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.SenderPeerId);
                        var targetHero = NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.TargetPeerId);
                        var targetParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.TargetPeerId);

                        if (senderHero != null && string.IsNullOrEmpty(packet.SenderName))
                        {
                            packet.SenderName = senderHero.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", packet.SenderPeerId);
                        }
                        if (targetHero != null && string.IsNullOrEmpty(packet.TargetName))
                        {
                            packet.TargetName = targetHero.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", packet.TargetPeerId);
                        }

                        switch (packet.InteractionType)
                        {
                            case 1: // SendGold
                                if (packet.IntAmount > 0)
                                {
                                    if (senderHero != null) senderHero.Gold = Math.Max(0, senderHero.Gold - packet.IntAmount);
                                    if (targetHero != null) targetHero.Gold += packet.IntAmount;
                                    if (packet.ObjectId == "GIFT" || packet.IntAmount >= 250)
                                    {
                                        ApplyServerRelationDelta(packet.SenderPeerId, packet.TargetPeerId, senderHero, targetHero, +5, "GoldGift");
                                    }
                                }
                                break;

                            case 2: // SendTroops
                                if (!string.IsNullOrEmpty(packet.ObjectId) && packet.IntAmount > 0)
                                {
                                    var troop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(packet.ObjectId);
                                    if (troop != null)
                                    {
                                        if (senderParty?.MemberRoster != null)
                                        {
                                            int cur = senderParty.MemberRoster.GetTroopCount(troop);
                                            if (cur > 0) senderParty.MemberRoster.AddToCounts(troop, -Math.Min(cur, packet.IntAmount));
                                        }
                                        if (targetParty?.MemberRoster != null)
                                        {
                                            targetParty.MemberRoster.AddToCounts(troop, packet.IntAmount);
                                        }
                                    }
                                }
                                break;

                            case 4: // DuelResponse (Target accepted or declined)
                                if (packet.IntAmount == 1)
                                {
                                    // Resolve friendly duel on the Server
                                    float senderPower = (senderHero?.Level ?? 10) * 3f + (senderHero?.HitPoints ?? 100) + MBRandom.RandomFloatRanged(0f, 35f);
                                    float targetPower = (targetHero?.Level ?? 10) * 3f + (targetHero?.HitPoints ?? 100) + MBRandom.RandomFloatRanged(0f, 35f);
                                    bool senderWon = senderPower >= targetPower;

                                    int winnerPeerId = senderWon ? packet.SenderPeerId : packet.TargetPeerId;
                                    string winnerName = senderWon ? packet.SenderName : packet.TargetName;
                                    string loserName = senderWon ? packet.TargetName : packet.SenderName;
                                    var winnerHero = senderWon ? senderHero : targetHero;

                                    if (winnerHero?.Clan != null)
                                    {
                                        winnerHero.Clan.AddRenown(5f, true);
                                    }

                                    var resultPacket = new PlayerInteractionPacket
                                    {
                                        SenderPeerId = packet.SenderPeerId,
                                        SenderName = packet.SenderName,
                                        TargetPeerId = packet.TargetPeerId,
                                        TargetName = packet.TargetName,
                                        InteractionType = 5, // DuelResult
                                        IntAmount = winnerPeerId,
                                        Message = Loc.F(
                                            "[Düello Sonucu] '{0}', '{1}' karşısında dostluk düellosunu kazandı! (+5 Klan Namı)",
                                            "[Duel Result] '{0}' won the friendly duel against '{1}'! (+5 Clan Renown)",
                                            winnerName, loserName)
                                    };

                                    InformationManager.DisplayMessage(new InformationMessage(resultPacket.Message, Colors.Magenta));
                                    ModContext.Server?.Broadcast(resultPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                                    return;
                                }
                                break;

                            case 9: // DiplomacyAction (Peace, Alliance, War)
                                if (packet.ObjectId == "ACCEPT_PEACE")
                                {
                                    if (senderHero?.MapFaction != null && targetHero?.MapFaction != null &&
                                        FactionManager.IsAtWarAgainstFaction(senderHero.MapFaction, targetHero.MapFaction))
                                    {
                                        try { MakePeaceAction.Apply(senderHero.MapFaction, targetHero.MapFaction); } catch { }
                                    }
                                    ApplyServerRelationDelta(packet.SenderPeerId, packet.TargetPeerId, senderHero, targetHero, +10, "PeaceTreaty");
                                }
                                else if (packet.ObjectId == "ACCEPT_ALLY")
                                {
                                    if (senderHero?.MapFaction != null && targetHero?.MapFaction != null &&
                                        FactionManager.IsAtWarAgainstFaction(senderHero.MapFaction, targetHero.MapFaction))
                                    {
                                        try { MakePeaceAction.Apply(senderHero.MapFaction, targetHero.MapFaction); } catch { }
                                    }
                                    _alliedPairs.Add(GetPairKey(packet.SenderPeerId, packet.TargetPeerId));
                                    ApplyServerRelationDelta(packet.SenderPeerId, packet.TargetPeerId, senderHero, targetHero, +10, "Alliance");
                                }
                                else if (packet.ObjectId == "DECLARE_WAR")
                                {
                                    _alliedPairs.Remove(GetPairKey(packet.SenderPeerId, packet.TargetPeerId));
                                    if (senderHero?.MapFaction != null && targetHero?.MapFaction != null &&
                                        !FactionManager.IsAtWarAgainstFaction(senderHero.MapFaction, targetHero.MapFaction))
                                    {
                                        try { DeclareWarAction.ApplyByPlayerHostility(senderHero.MapFaction, targetHero.MapFaction); } catch { }
                                    }
                                    ApplyServerRelationDelta(packet.SenderPeerId, packet.TargetPeerId, senderHero, targetHero, -25, "WarDeclared");
                                }
                                break;
                        }

                        // Broadcast interaction packet so both target and other clients stay in sync
                        ModContext.Server?.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        return;
                    }

                    // 2. Client Processing
                    int myPeerId = ModContext.Client?.LocalPeerId ?? -1;

                    // Update remote puppet states for non-involved observers too
                    if (packet.InteractionType == 2 && !string.IsNullOrEmpty(packet.ObjectId) && packet.IntAmount > 0)
                    {
                        var troop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(packet.ObjectId);
                        if (troop != null)
                        {
                            if (packet.SenderPeerId != myPeerId)
                            {
                                var sp = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.SenderPeerId);
                                if (sp?.MemberRoster != null)
                                {
                                    int cur = sp.MemberRoster.GetTroopCount(troop);
                                    if (cur > 0) sp.MemberRoster.AddToCounts(troop, -Math.Min(cur, packet.IntAmount));
                                }
                            }
                            if (packet.TargetPeerId != myPeerId)
                            {
                                var tp = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.TargetPeerId);
                                tp?.MemberRoster?.AddToCounts(troop, packet.IntAmount);
                            }
                        }
                    }

                    if (packet.InteractionType == 8) // RelationSync worldwide broadcast
                    {
                        var heroA = packet.SenderPeerId == myPeerId
                            ? Hero.MainHero
                            : NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.SenderPeerId);
                        var heroB = packet.TargetPeerId == myPeerId
                            ? Hero.MainHero
                            : NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.TargetPeerId);
                        if (heroA != null && heroB != null && !ReferenceEquals(heroA, heroB))
                        {
                            CharacterRelationManager.SetHeroRelation(heroA, heroB, packet.IntAmount);
                            if ((packet.SenderPeerId == myPeerId || packet.TargetPeerId == myPeerId) && IsInDialog)
                            {
                                RefreshActiveNativeConversation();
                            }
                        }
                        return;
                    }

                    if (packet.InteractionType == 9) // DiplomacyAction worldwide sync (Peace, Alliance, War)
                    {
                        var heroA = packet.SenderPeerId == myPeerId
                            ? Hero.MainHero
                            : NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.SenderPeerId);
                        var heroB = packet.TargetPeerId == myPeerId
                            ? Hero.MainHero
                            : NetworkObjectRegistry.Instance.ResolvePlayerHero(packet.TargetPeerId);

                        if (packet.ObjectId == "ACCEPT_PEACE" || packet.ObjectId == "ACCEPT_ALLY")
                        {
                            if (heroA?.MapFaction != null && heroB?.MapFaction != null &&
                                FactionManager.IsAtWarAgainstFaction(heroA.MapFaction, heroB.MapFaction))
                            {
                                try { MakePeaceAction.Apply(heroA.MapFaction, heroB.MapFaction); } catch { }
                            }
                            if (packet.ObjectId == "ACCEPT_ALLY")
                            {
                                _alliedPairs.Add(GetPairKey(packet.SenderPeerId, packet.TargetPeerId));
                            }
                            if (!string.IsNullOrEmpty(packet.Message))
                            {
                                InformationManager.DisplayMessage(new InformationMessage(packet.Message, Colors.Green));
                            }
                            return;
                        }

                        if (packet.ObjectId == "DECLARE_WAR")
                        {
                            _alliedPairs.Remove(GetPairKey(packet.SenderPeerId, packet.TargetPeerId));
                            if (heroA?.MapFaction != null && heroB?.MapFaction != null &&
                                !FactionManager.IsAtWarAgainstFaction(heroA.MapFaction, heroB.MapFaction))
                            {
                                try { DeclareWarAction.ApplyByPlayerHostility(heroA.MapFaction, heroB.MapFaction); } catch { }
                            }
                            return;
                        }
                    }

                    if (packet.InteractionType == 5) // DuelResult broadcast
                    {
                        if (packet.IntAmount == myPeerId && Clan.PlayerClan != null)
                        {
                            Clan.PlayerClan.AddRenown(5f, true);
                        }
                        InformationManager.DisplayMessage(new InformationMessage(packet.Message, Colors.Magenta));
                        return;
                    }

                    // Target-specific actions when local player is TargetPeerId
                    if (packet.TargetPeerId != myPeerId) return;

                    switch (packet.InteractionType)
                    {
                        case 1: // SendGold received
                            if (packet.IntAmount > 0 && Hero.MainHero != null)
                            {
                                Hero.MainHero.Gold += packet.IntAmount;
                                InformationManager.DisplayMessage(new InformationMessage(
                                    Loc.F("[Oyuncu Etkileşimi] '{0}' size {1} Altın gönderdi!",
                                          "[Player Interaction] '{0}' sent you {1} Gold!",
                                          packet.SenderName, packet.IntAmount),
                                    Colors.Green));
                                ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "GoldReceivedFromPlayer" });
                            }
                            break;

                        case 2: // SendTroops received
                            if (!string.IsNullOrEmpty(packet.ObjectId) && packet.IntAmount > 0 && MobileParty.MainParty?.MemberRoster != null)
                            {
                                var troop = TaleWorlds.CampaignSystem.Campaign.Current.ObjectManager.GetObject<CharacterObject>(packet.ObjectId);
                                if (troop != null)
                                {
                                    MobileParty.MainParty.MemberRoster.AddToCounts(troop, packet.IntAmount);
                                    InformationManager.DisplayMessage(new InformationMessage(
                                        Loc.F("[Oyuncu Etkileşimi] '{0}' birliğinize {1}x {2} takviyesi gönderdi!",
                                              "[Player Interaction] '{0}' reinforced your party with {1}x {2}!",
                                              packet.SenderName, packet.IntAmount, troop.Name),
                                        Colors.Green));
                                    ModContext.Events.Publish(new LocalRosterChangedEvent { Reason = "TroopsReceivedFromPlayer" });
                                }
                            }
                            break;

                        case 3: // DuelRequest received
                            InformationManager.ShowInquiry(new InquiryData(
                                titleText: Loc.T("Dostluk Düellosu Daveti", "Friendly Duel Invitation"),
                                text: Loc.F(
                                    "'{0}' sizi onur ve nam (+5 Nam) için dostluk düellosuna davet ediyor!\n\nKabul ediyor musunuz?",
                                    "'{0}' invites you to a friendly duel for honor and renown (+5 Renown)!\n\nDo you accept?",
                                    packet.SenderName),
                                isAffirmativeOptionShown: true,
                                isNegativeOptionShown: true,
                                affirmativeText: Loc.T("Kabul Et", "Accept"),
                                negativeText: Loc.T("Reddet", "Decline"),
                                affirmativeAction: () =>
                                {
                                    SendInteractionPacket(new PlayerInteractionPacket
                                    {
                                        SenderPeerId = packet.SenderPeerId,
                                        SenderName = packet.SenderName,
                                        TargetPeerId = myPeerId,
                                        TargetName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Oyuncu", "Player"),
                                        InteractionType = 4, // DuelResponse
                                        IntAmount = 1
                                    });
                                },
                                negativeAction: () =>
                                {
                                    SendInteractionPacket(new PlayerInteractionPacket
                                    {
                                        SenderPeerId = packet.SenderPeerId,
                                        SenderName = packet.SenderName,
                                        TargetPeerId = myPeerId,
                                        TargetName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Oyuncu", "Player"),
                                        InteractionType = 4, // DuelResponse
                                        IntAmount = 0
                                    });
                                }));
                            break;

                        case 6: // RallyCall received
                            InformationManager.DisplayMessage(new InformationMessage(
                                Loc.F("[Ortak Harekat] '{0}' sizi ortak harekata çağırıyor! (Konum: {1})",
                                      "[Joint Rally] '{0}' calls you to rally together! (Location: {1})",
                                      packet.SenderName, packet.Message),
                                Colors.Cyan));
                            break;

                        case 7: // DirectMessage received
                            InformationManager.DisplayMessage(new InformationMessage(
                                Loc.F("[Özel Mesaj - {0}]: {1}", "[Direct Message - {0}]: {1}", packet.SenderName, packet.Message),
                                Colors.Yellow));
                            InformationManager.ShowInquiry(new InquiryData(
                                titleText: Loc.F("Özel Mesaj — {0}", "Direct Message — {0}", packet.SenderName),
                                text: $"{packet.Message}",
                                isAffirmativeOptionShown: true,
                                isNegativeOptionShown: true,
                                affirmativeText: Loc.T("Yanıtla", "Reply"),
                                negativeText: Loc.T("Kapat", "Close"),
                                affirmativeAction: () =>
                                {
                                    var senderParty = NetworkObjectRegistry.Instance.ResolvePlayerParty(packet.SenderPeerId);
                                    if (senderParty != null)
                                    {
                                        OpenInteractionMenu(senderParty);
                                    }
                                },
                                negativeAction: null));
                            break;

                        case 9: // Diplomacy proposal received outside active 3D conversation
                            if (packet.ObjectId == "PROPOSE_PEACE" || packet.ObjectId == "PROPOSE_ALLY")
                            {
                                bool isPeace = packet.ObjectId == "PROPOSE_PEACE";
                                string acceptCode = isPeace ? "ACCEPT_PEACE" : "ACCEPT_ALLY";
                                string declineCode = isPeace ? "DECLINE_PEACE" : "DECLINE_ALLY";
                                string title = isPeace ? Loc.T("Barış Teklifi", "Peace Treaty Proposal") : Loc.T("İttifak Teklifi", "Alliance Proposal");
                                string body = isPeace
                                    ? Loc.F("'{0}' savaşı bitirmek için kalıcı barış teklif ediyor (+10 İlişki).\nKabul ediyor musunuz?", "'{0}' offers a lasting peace treaty (+10 Relation).\nDo you accept?", packet.SenderName)
                                    : Loc.F("'{0}' ittifak ve saldırmazlık antlaşması öneriyor (+10 İlişki).\nKabul ediyor musunuz?", "'{0}' proposes an alliance and non-aggression pact (+10 Relation).\nDo you accept?", packet.SenderName);

                                InformationManager.ShowInquiry(new InquiryData(
                                    titleText: title,
                                    text: body,
                                    isAffirmativeOptionShown: true,
                                    isNegativeOptionShown: true,
                                    affirmativeText: Loc.T("Kabul Et", "Accept"),
                                    negativeText: Loc.T("Reddet", "Decline"),
                                    affirmativeAction: () =>
                                    {
                                        SendInteractionPacket(new PlayerInteractionPacket
                                        {
                                            SenderPeerId = packet.SenderPeerId,
                                            SenderName = packet.SenderName,
                                            TargetPeerId = myPeerId,
                                            TargetName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Oyuncu", "Player"),
                                            InteractionType = 9,
                                            ObjectId = acceptCode,
                                            Message = isPeace
                                                ? Loc.F("[Barış Antlaşması] {0} ve {1} barış imzaladı! (+10 İlişki)", "[Peace Treaty] {0} and {1} signed peace! (+10 Relation)", packet.SenderName, Hero.MainHero?.Name)
                                                : Loc.F("[İttifak Kuruldu] {0} ve {1} müttefik oldu! (+10 İlişki)", "[Alliance Formed] {0} and {1} formed an alliance! (+10 Relation)", packet.SenderName, Hero.MainHero?.Name)
                                        });
                                    },
                                    negativeAction: () =>
                                    {
                                        SendInteractionPacket(new PlayerInteractionPacket
                                        {
                                            SenderPeerId = packet.SenderPeerId,
                                            SenderName = packet.SenderName,
                                            TargetPeerId = myPeerId,
                                            TargetName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Oyuncu", "Player"),
                                            InteractionType = 9,
                                            ObjectId = declineCode
                                        });
                                    }), pauseGameActiveState: false, prioritize: true);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] PlayerInteractionService hatası: {ex.Message}");
                }
            });
        }
    }
}
