using System;
using System.Collections.Generic;
using System.Text;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace CalradiaConnected.Mod.UI
{
    public class P2PDialogVM : ViewModel
    {
        private readonly Action _onClose;
        private readonly List<string> _historyLines = new List<string>();

        private string _sessionId = "";
        private int _initiatorPeerId;
        private int _targetPeerId;
        private int _partnerPeerId;
        private bool _isInitiatorMode;
        private string _speakerHeaderText = "";
        private string _currentSpeechText = "";
        private string _pendingProposalKey = "";
        private string _partnerName = "";
        private string _initiatorName = "";
        private string _targetName = "";

        private int _initTradeGold;
        private string _initTradeTroopId = "";
        private int _initTradeTroopCount;
        private string _initTradeItemId = "";
        private int _initTradeItemCount;
        private int _initTradeShipIndex = -1;
        private string _initTradeShipDesc = "";
        private bool _initTradeAccepted;

        private int _targTradeGold;
        private string _targTradeTroopId = "";
        private int _targTradeTroopCount;
        private string _targTradeItemId = "";
        private int _targTradeItemCount;
        private int _targTradeShipIndex = -1;
        private string _targTradeShipDesc = "";
        private bool _targTradeAccepted;

        public P2PDialogVM(P2PDialogPacket initialPacket, bool isInitiatorMode, Action onClose)
        {
            _onClose = onClose;
            ApplyPacket(initialPacket, isInitiatorMode);
        }

        private static string ResolveTroopDisplay(string troopId, int count)
        {
            if (count <= 0 || string.IsNullOrEmpty(troopId)) return string.Empty;
            try
            {
                var troop = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<CharacterObject>(troopId);
                string label = troop?.Name?.ToString() ?? troopId;
                return $"{count}x {label}";
            }
            catch
            {
                return $"{count}x {troopId}";
            }
        }

        private static string ResolveItemDisplay(string itemId, int count)
        {
            if (count <= 0 || string.IsNullOrEmpty(itemId)) return string.Empty;
            try
            {
                var item = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager?.GetObject<TaleWorlds.Core.ItemObject>(itemId);
                string label = item?.Name?.ToString() ?? itemId;
                return $"{count}x {label}";
            }
            catch
            {
                return $"{count}x {itemId}";
            }
        }

        public void ApplyPacket(P2PDialogPacket packet, bool isInitiatorMode)
        {
            _sessionId = packet.SessionId ?? _sessionId;
            _initiatorPeerId = packet.InitiatorPeerId;
            _targetPeerId = packet.TargetPeerId;
            _isInitiatorMode = isInitiatorMode;

            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            _partnerPeerId = myPeerId == _initiatorPeerId ? _targetPeerId : _initiatorPeerId;

            var initHero = TaleWorlds.CampaignSystem.Campaign.Current != null
                ? CalradiaConnected.Campaign.Identity.NetworkObjectRegistry.Instance.ResolvePlayerHero(_initiatorPeerId)
                : null;
            var targHero = TaleWorlds.CampaignSystem.Campaign.Current != null
                ? CalradiaConnected.Campaign.Identity.NetworkObjectRegistry.Instance.ResolvePlayerHero(_targetPeerId)
                : null;

            _initiatorName = initHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", _initiatorPeerId);
            _targetName = targHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", _targetPeerId);
            _partnerName = myPeerId == _initiatorPeerId ? _targetName : _initiatorName;

            string speaker = string.IsNullOrWhiteSpace(packet.SpeakerName) ? packet.SenderName : packet.SpeakerName;
            _speakerHeaderText = string.IsNullOrWhiteSpace(speaker)
                ? Loc.T("Kalradya Müzakeresi", "Calradia Parley")
                : speaker;

            _currentSpeechText = packet.DialogText ?? "";
            if (!string.IsNullOrWhiteSpace(_currentSpeechText))
            {
                string formatted = $"[{_speakerHeaderText}]: {_currentSpeechText}";
                if (_historyLines.Count == 0 || _historyLines[_historyLines.Count - 1] != formatted)
                {
                    _historyLines.Add(formatted);
                    if (_historyLines.Count > 5) _historyLines.RemoveAt(0);
                }
            }

            if (packet.Stage == 3 && !isInitiatorMode)
            {
                _pendingProposalKey = packet.OptionKey ?? "";
            }
            else if (packet.Stage == 2 || packet.Stage == 4 || packet.Stage == 5)
            {
                _pendingProposalKey = "";
            }

            _initTradeGold = packet.InitiatorTradeGold;
            _initTradeTroopId = packet.InitiatorTradeTroopId ?? "";
            _initTradeTroopCount = packet.InitiatorTradeTroopCount;
            _initTradeItemId = packet.InitiatorTradeItemId ?? "";
            _initTradeItemCount = packet.InitiatorTradeItemCount;
            _initTradeShipIndex = packet.InitiatorTradeShipIndex;
            _initTradeShipDesc = packet.InitiatorTradeShipDesc ?? "";
            _initTradeAccepted = packet.InitiatorTradeAccepted;

            _targTradeGold = packet.TargetTradeGold;
            _targTradeTroopId = packet.TargetTradeTroopId ?? "";
            _targTradeTroopCount = packet.TargetTradeTroopCount;
            _targTradeItemId = packet.TargetTradeItemId ?? "";
            _targTradeItemCount = packet.TargetTradeItemCount;
            _targTradeShipIndex = packet.TargetTradeShipIndex;
            _targTradeShipDesc = packet.TargetTradeShipDesc ?? "";
            _targTradeAccepted = packet.TargetTradeAccepted;

            RefreshAllProperties();
        }

        private void RefreshAllProperties()
        {
            OnPropertyChangedWithValue(TitleText, nameof(TitleText));
            OnPropertyChangedWithValue(RoleIndicatorText, nameof(RoleIndicatorText));
            OnPropertyChangedWithValue(SpeakerHeaderText, nameof(SpeakerHeaderText));
            OnPropertyChangedWithValue(CurrentSpeechText, nameof(CurrentSpeechText));
            OnPropertyChangedWithValue(HistoryText, nameof(HistoryText));
            OnPropertyChangedWithValue(IsInitiatorMode, nameof(IsInitiatorMode));
            OnPropertyChangedWithValue(IsWatcherMode, nameof(IsWatcherMode));
            OnPropertyChangedWithValue(HasPendingProposal, nameof(HasPendingProposal));
            OnPropertyChangedWithValue(HasActiveTrade, nameof(HasActiveTrade));
            OnPropertyChangedWithValue(TradeHeaderText, nameof(TradeHeaderText));
            OnPropertyChangedWithValue(TradeInitiatorLineText, nameof(TradeInitiatorLineText));
            OnPropertyChangedWithValue(TradeTargetLineText, nameof(TradeTargetLineText));
            OnPropertyChangedWithValue(TradeAcceptButtonText, nameof(TradeAcceptButtonText));
            OnPropertyChangedWithValue(TradeResetButtonText, nameof(TradeResetButtonText));
            OnPropertyChangedWithValue(AcceptProposalText, nameof(AcceptProposalText));
            OnPropertyChangedWithValue(DeclineProposalText, nameof(DeclineProposalText));
            OnPropertyChangedWithValue(WatcherHintText, nameof(WatcherHintText));
            OnPropertyChangedWithValue(WatcherReplyText, nameof(WatcherReplyText));
            OnPropertyChangedWithValue(WatcherOfferGoldText, nameof(WatcherOfferGoldText));
            OnPropertyChangedWithValue(WatcherOfferTroopsText, nameof(WatcherOfferTroopsText));
            OnPropertyChangedWithValue(WatcherOfferItemText, nameof(WatcherOfferItemText));
            OnPropertyChangedWithValue(WatcherOfferShipText, nameof(WatcherOfferShipText));
            OnPropertyChangedWithValue(OptionGoldText, nameof(OptionGoldText));
            OnPropertyChangedWithValue(OptionTroopsText, nameof(OptionTroopsText));
            OnPropertyChangedWithValue(OptionItemText, nameof(OptionItemText));
            OnPropertyChangedWithValue(OptionShipText, nameof(OptionShipText));
            OnPropertyChangedWithValue(OptionDuelText, nameof(OptionDuelText));
            OnPropertyChangedWithValue(OptionRallyText, nameof(OptionRallyText));
            OnPropertyChangedWithValue(OptionSayText, nameof(OptionSayText));
            OnPropertyChangedWithValue(OptionAttackText, nameof(OptionAttackText));
            OnPropertyChangedWithValue(CloseTradeButtonText, nameof(CloseTradeButtonText));
            OnPropertyChangedWithValue(CloseButtonText, nameof(CloseButtonText));
        }

        [DataSourceProperty]
        public string TitleText => Loc.F("{0} ile Ticaret", "Trade with {0}", _partnerName);

        [DataSourceProperty]
        public string RoleIndicatorText => Loc.T("İki taraf da onayladığında takas gerçekleşir.", "Trade completes once both sides accept.");

        [DataSourceProperty]
        public string SpeakerHeaderText => _speakerHeaderText;

        [DataSourceProperty]
        public string CurrentSpeechText => _currentSpeechText;

        [DataSourceProperty]
        public string HistoryText => _historyLines.Count > 1
            ? string.Join("\n", _historyLines.GetRange(0, _historyLines.Count - 1))
            : Loc.T("Takas ve konuşma geçmişi burada görünür.", "Trade and dialog log appears here.");

        [DataSourceProperty]
        public bool IsInitiatorMode => _isInitiatorMode;

        [DataSourceProperty]
        public bool IsWatcherMode => !_isInitiatorMode;

        [DataSourceProperty]
        public bool HasPendingProposal => !_isInitiatorMode && !string.IsNullOrEmpty(_pendingProposalKey);

        [DataSourceProperty]
        public bool HasActiveTrade =>
            _initTradeGold > 0 || _initTradeTroopCount > 0 || _initTradeItemCount > 0 || _initTradeShipIndex >= 0 ||
            _targTradeGold > 0 || _targTradeTroopCount > 0 || _targTradeItemCount > 0 || _targTradeShipIndex >= 0;

        [DataSourceProperty]
        public string TradeHeaderText => HasActiveTrade
            ? Loc.T("Takas Masası", "Trade Table")
            : Loc.T("Tekliflerinizi aşağıya ekleyin; iki taraf da onayladığında takas tamamlanır.", "Place your offers below; both sides must accept to complete.");

        private string FormatSideTrade(string playerName, int gold, string troopId, int troopCount, string itemId, int itemCount, int shipIndex, string shipDesc, bool accepted)
        {
            var parts = new List<string>();
            if (gold > 0) parts.Add(Loc.F("{0} Altın", "{0} Gold", gold));
            string itemPart = ResolveItemDisplay(itemId, itemCount);
            if (!string.IsNullOrEmpty(itemPart)) parts.Add(itemPart);
            string troopPart = ResolveTroopDisplay(troopId, troopCount);
            if (!string.IsNullOrEmpty(troopPart)) parts.Add(troopPart);
            if (shipIndex >= 0)
            {
                parts.Add(string.IsNullOrWhiteSpace(shipDesc) ? Loc.F("Gemi #{0}", "Ship #{0}", shipIndex + 1) : Loc.F("Gemi: {0}", "Ship: {0}", shipDesc));
            }
            string offerDesc = parts.Count > 0 ? string.Join(" + ", parts) : Loc.T("Teklif yok", "No offer");
            string status = accepted ? Loc.T("[ONAYLADI ✓]", "[ACCEPTED ✓]") : Loc.T("[Bekliyor]", "[Waiting]");
            return $"{playerName}: {offerDesc}   {status}";
        }

        [DataSourceProperty]
        public string TradeInitiatorLineText => FormatSideTrade(_initiatorName, _initTradeGold, _initTradeTroopId, _initTradeTroopCount, _initTradeItemId, _initTradeItemCount, _initTradeShipIndex, _initTradeShipDesc, _initTradeAccepted);

        [DataSourceProperty]
        public string TradeTargetLineText => FormatSideTrade(_targetName, _targTradeGold, _targTradeTroopId, _targTradeTroopCount, _targTradeItemId, _targTradeItemCount, _targTradeShipIndex, _targTradeShipDesc, _targTradeAccepted);

        private bool IsLocalAccepted => _isInitiatorMode ? _initTradeAccepted : _targTradeAccepted;

        [DataSourceProperty]
        public string TradeAcceptButtonText => IsLocalAccepted
            ? Loc.T("Onayı Geri Çek", "Withdraw Accept")
            : Loc.T("Takası Onayla", "Accept Trade");

        [DataSourceProperty]
        public string TradeResetButtonText => Loc.T("Masayı Sıfırla", "Clear Trade");

        [DataSourceProperty]
        public string AcceptProposalText => _pendingProposalKey.StartsWith("rally")
            ? Loc.T("Harekatı Kabul Et", "Accept Rally")
            : Loc.T("Teklifi Kabul Et", "Accept Offer");

        [DataSourceProperty]
        public string DeclineProposalText => Loc.T("Reddet", "Decline");

        [DataSourceProperty]
        public string WatcherHintText => HasPendingProposal
            ? Loc.T("Size bir teklif yöneltildi. Yanıtınızı seçebilirsiniz:", "An offer was presented to you. Choose your response:")
            : Loc.T("Karşı tarafın hamlelerini canlı izliyorsunuz; siz de takasa teklif koyup yanıt verebilirsiniz.", "Following partner live; you may also place trade offers and reply.");

        [DataSourceProperty]
        public string WatcherOfferGoldText => Loc.T("Altın Teklif Et", "Offer Gold");

        [DataSourceProperty]
        public string WatcherOfferTroopsText => Loc.T("Asker Teklif Et", "Offer Troops");

        [DataSourceProperty]
        public string WatcherOfferItemText => Loc.T("Eşya Teklif Et", "Offer Item");

        [DataSourceProperty]
        public string WatcherOfferShipText => Loc.T("Gemi Teklif Et", "Offer Ship");

        [DataSourceProperty]
        public string WatcherReplyText => Loc.T("Sözle Yanıt Ver", "Send Spoken Reply");

        [DataSourceProperty]
        public string OptionGoldText => Loc.T("1. Altın", "1. Gold");

        [DataSourceProperty]
        public string OptionTroopsText => Loc.T("2. Asker", "2. Troops");

        [DataSourceProperty]
        public string OptionItemText => Loc.T("3. Eşya", "3. Item");

        [DataSourceProperty]
        public string OptionShipText => Loc.T("4. Gemi", "4. Ship");

        [DataSourceProperty]
        public string OptionDuelText => Loc.T("Dostluk Düellosu", "Friendly Duel");

        [DataSourceProperty]
        public string OptionRallyText => Loc.T("Ortak Harekat", "Call Joint Rally");

        [DataSourceProperty]
        public string OptionSayText => Loc.T("Özel Söz Söyle", "Speak Line");

        [DataSourceProperty]
        public string OptionAttackText => Loc.T("Savaş Aç", "Declare War");

        [DataSourceProperty]
        public string CloseTradeButtonText => Loc.T("Görüşmeye Dön", "Back to Conversation");

        [DataSourceProperty]
        public string CloseButtonText => Loc.T("Ayrıl", "Leave");

        public void ExecuteOptionGold()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("gold", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionTroops()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("troops", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionItem()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("item", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionShip()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("ship", _partnerPeerId, _sessionId);
        }

        public void ExecuteCloseTrade()
        {
            PlayerInteractionService.Instance.ExecuteCloseTradeOverlay();
            _onClose?.Invoke();
        }

        public void ExecuteOptionDuel()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("duel", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionRally()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("rally", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionSay()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("say", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionAttack()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("attack_player", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionTradeAccept()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("trade_accept", _partnerPeerId, _sessionId);
        }

        public void ExecuteOptionTradeReset()
        {
            PlayerInteractionService.Instance.ExecuteDialogAction("trade_reset", _partnerPeerId, _sessionId);
        }

        public void ExecuteAcceptProposal()
        {
            string key = _pendingProposalKey.StartsWith("rally") ? "accept_rally" : "accept_duel";
            SendResponse(key, Loc.T("Teklifini kabul ediyorum!", "I accept your proposal!"));
            _pendingProposalKey = "";
            RefreshAllProperties();
        }

        public void ExecuteDeclineProposal()
        {
            SendResponse("decline_duel", Loc.T("Bu sefer teklifini reddediyorum.", "I must decline this time."));
            _pendingProposalKey = "";
            RefreshAllProperties();
        }

        public void ExecuteWatcherReply()
        {
            P2PDialogManager.Instance.SuspendOverlayForInquiry();
            InformationManager.ShowTextInquiry(new TextInquiryData(
                titleText: Loc.T("Diyalog Yanıtı", "Dialog Reply"),
                text: Loc.F("'{0}' oyuncusuna canlı diyalog yanıtınızı yazın:", "Write your live dialog reply to '{0}':", _partnerName),
                isAffirmativeOptionShown: true,
                isNegativeOptionShown: true,
                affirmativeText: Loc.T("Gönder", "Send"),
                negativeText: Loc.T("Vazgeç", "Cancel"),
                affirmativeAction: (text) =>
                {
                    P2PDialogManager.Instance.ResumeOverlayAfterInquiry();
                    if (string.IsNullOrWhiteSpace(text)) return;
                    SendResponse("reply", text.Trim());
                },
                negativeAction: () =>
                {
                    P2PDialogManager.Instance.ResumeOverlayAfterInquiry();
                },
                shouldInputBeObfuscated: false,
                defaultInputText: Loc.T("Seni dinliyorum yoldaş!", "I hear you, comrade!")));
        }

        private void SendResponse(string optionKey, string text)
        {
            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);
            PlayerInteractionService.Instance.SendDialogPacket(new P2PDialogPacket
            {
                SessionId = _sessionId,
                InitiatorPeerId = _initiatorPeerId,
                TargetPeerId = _targetPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 4, // Response
                OptionKey = optionKey,
                SpeakerName = myName,
                DialogText = text
            });
        }

        public void ExecuteCloseDialog()
        {
            PlayerInteractionService.Instance.CloseActiveDialogLocalOnly();
            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            string myName = Hero.MainHero?.Name?.ToString() ?? Loc.F("Oyuncu #{0}", "Player #{0}", myPeerId);
            PlayerInteractionService.Instance.SendDialogPacket(new P2PDialogPacket
            {
                SessionId = _sessionId,
                InitiatorPeerId = _initiatorPeerId,
                TargetPeerId = _targetPeerId,
                SenderPeerId = myPeerId,
                SenderName = myName,
                Stage = 5, // Close
                OptionKey = "close",
                SpeakerName = myName,
                DialogText = Loc.T("Diyalog sonlandırıldı.", "Dialog ended.")
            });
            _onClose?.Invoke();
        }
    }

    public class P2PDialogManager
    {
        public static P2PDialogManager Instance { get; } = new P2PDialogManager();

        private GauntletLayer? _gauntletLayer;
        private GauntletMovieIdentifier? _movie;
        private P2PDialogVM? _dataSource;
        private ScreenBase? _parentScreen;
        private bool _subscribed;
        private string _activeSessionId = "";

        public void EnsureInitialized()
        {
            if (_subscribed) return;
            _subscribed = true;

            ModContext.Events.Subscribe<P2PDialogUpdatedEvent>(OnDialogUpdated);
            PlayerInteractionService.Instance.CloseDialogLocalRequested += CloseOverlay;
            PlayerInteractionService.Instance.SuspendDialogForInquiryRequested += SuspendOverlayForInquiry;
            PlayerInteractionService.Instance.ResumeDialogAfterInquiryRequested += ResumeOverlayAfterInquiry;
        }

        public void SuspendOverlayForInquiry()
        {
            try
            {
                if (_gauntletLayer != null)
                {
                    _gauntletLayer.InputRestrictions.ResetInputRestrictions();
                    _gauntletLayer.IsFocusLayer = false;
                    ScreenManager.SetSuspendLayer(_gauntletLayer, true);
                }
            }
            catch { }
        }

        public void ResumeOverlayAfterInquiry()
        {
            try
            {
                if (_gauntletLayer != null)
                {
                    ScreenManager.SetSuspendLayer(_gauntletLayer, false);
                    _gauntletLayer.IsFocusLayer = true;
                    _gauntletLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                    ScreenManager.TrySetFocus(_gauntletLayer);
                }
            }
            catch { }
        }

        private void OnDialogUpdated(P2PDialogUpdatedEvent evt)
        {
            var packet = evt.Packet;
            if (packet == null) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            if (myPeerId != packet.InitiatorPeerId && myPeerId != packet.TargetPeerId) return;

            if (packet.Stage == 5 || packet.Stage == 11)
            {
                if (_activeSessionId == packet.SessionId || _gauntletLayer != null)
                {
                    CloseOverlay();
                    if (packet.Stage == 5 && !string.IsNullOrWhiteSpace(packet.DialogText))
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            Loc.F("[Diyalog] {0}: {1}", "[Dialog] {0}: {1}", packet.SpeakerName, packet.DialogText),
                            Colors.Yellow));
                    }
                }
                return;
            }

            if (packet.Stage < 6)
            {
                return;
            }

            bool isInitiator = myPeerId == packet.InitiatorPeerId;
            if ((packet.Stage == 10 && _gauntletLayer == null) || _dataSource == null || _activeSessionId != packet.SessionId)
            {
                OpenOrUpdateOverlay(packet, isInitiator);
            }
            else
            {
                _dataSource.ApplyPacket(packet, isInitiator);
            }
        }

        private void OpenOrUpdateOverlay(P2PDialogPacket packet, bool isInitiator)
        {
            CloseOverlay();

            var topScreen = ScreenManager.TopScreen;
            if (topScreen == null) return;

            try
            {
                _activeSessionId = packet.SessionId ?? "";
                _parentScreen = topScreen;
                _dataSource = new P2PDialogVM(packet, isInitiator, CloseOverlay);

                try { UIResourceManager.LoadSpriteCategory("ui_group1"); } catch { }

                _gauntletLayer = new GauntletLayer("CalradiaConnectedDialog", 4600, false);
                _movie = _gauntletLayer.LoadMovie("CalradiaConnectedDialog", _dataSource);
                _gauntletLayer.IsFocusLayer = true;
                _gauntletLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                _parentScreen.AddLayer(_gauntletLayer);
                ScreenManager.TrySetFocus(_gauntletLayer);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] P2PDialog UI failed: {ex.Message}");
                CloseOverlay();
            }
        }

        public void CloseOverlay()
        {
            try
            {
                _activeSessionId = "";
                if (_gauntletLayer != null)
                {
                    if (_movie != null)
                    {
                        try { _gauntletLayer.ReleaseMovie(_movie); } catch { }
                        _movie = null;
                    }
                    try { _gauntletLayer.InputRestrictions.ResetInputRestrictions(); } catch { }
                    try { ScreenManager.SetSuspendLayer(_gauntletLayer, true); } catch { }
                    if (_parentScreen != null)
                    {
                        try { _parentScreen.RemoveLayer(_gauntletLayer); } catch { }
                    }
                    _gauntletLayer = null;
                }

                _dataSource?.OnFinalize();
                _dataSource = null;
                _parentScreen = null;
            }
            catch { }
        }
    }
}
