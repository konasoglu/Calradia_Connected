using System;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace CalradiaConnected.Mod.UI
{
    public class PvpReadyVM : ViewModel
    {
        private bool _localReady;
        private bool _opponentReady;
        private string _opponentName = string.Empty;

        public PvpReadyVM()
        {
            SyncFromService();
        }

        public void SyncFromService()
        {
            var svc = BattleSyncService.Instance;
            _opponentName = string.IsNullOrEmpty(svc.ActivePvpOpponentName)
                ? Loc.T("Rakip", "Opponent")
                : svc.ActivePvpOpponentName;
            _localReady = svc.LocalIsAttackerInPvp ? svc.AttackerReady : svc.DefenderReady;
            _opponentReady = svc.LocalIsAttackerInPvp ? svc.DefenderReady : svc.AttackerReady;

            OnPropertyChangedWithValue(TitleText, nameof(TitleText));
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
            OnPropertyChangedWithValue(ReadyButtonText, nameof(ReadyButtonText));
            OnPropertyChangedWithValue(WaitingHintText, nameof(WaitingHintText));
            OnPropertyChangedWithValue(CanClickReady, nameof(CanClickReady));
            OnPropertyChangedWithValue(IsWaitingForOpponent, nameof(IsWaitingForOpponent));
        }

        [DataSourceProperty]
        public string TitleText => Loc.F("Savaş Hazırlığı — {0}", "Battle Preparation — {0}", _opponentName);

        [DataSourceProperty]
        public string StatusText
        {
            get
            {
                string me = _localReady ? Loc.T("HAZIR ✓", "READY ✓") : Loc.T("Bekleniyor...", "Waiting...");
                string opp = _opponentReady ? Loc.T("HAZIR ✓", "READY ✓") : Loc.T("Bekleniyor...", "Waiting...");
                return Loc.F("Sen: {0}   |   {1}: {2}", "You: {0}   |   {1}: {2}", me, _opponentName, opp);
            }
        }

        [DataSourceProperty]
        public string ReadyButtonText => Loc.T("HAZIRIM", "READY");

        [DataSourceProperty]
        public string WaitingHintText => Loc.T("HAZIR ONAYLANDI ✓ — Rakibin hazır olması bekleniyor...", "READY CONFIRMED ✓ — Waiting for opponent...");

        [DataSourceProperty]
        public bool CanClickReady => !_localReady;

        [DataSourceProperty]
        public bool IsWaitingForOpponent => _localReady;

        public void ExecuteReady()
        {
            CalradiaConnected.Missions.CoopMissionSyncBehavior.ActiveInstance?.MarkLocalReadyFromUi();
            BattleSyncService.Instance.SendPvpReadySignal();
            SyncFromService();
        }
    }

    public class PvpReadyOverlayManager
    {
        public static PvpReadyOverlayManager Instance { get; } = new PvpReadyOverlayManager();

        private GauntletLayer? _gauntletLayer;
        private GauntletMovieIdentifier? _movie;
        private PvpReadyVM? _dataSource;
        private ScreenBase? _parentScreen;
        private bool _subscribed;
        private bool _hasInputRestrictions;

        public void EnsureInitialized()
        {
            if (_subscribed) return;
            _subscribed = true;
            ModContext.Events.Subscribe<PvpBattleStateChangedEvent>(OnPvpStateChanged);
        }

        private void OnPvpStateChanged(PvpBattleStateChangedEvent evt)
        {
            if (evt == null) return;

            if (evt.Stage == 13 || evt.Stage == 2 || evt.Stage == 3)
            {
                CloseOverlay();
                return;
            }

            if (_dataSource != null)
            {
                _dataSource.SyncFromService();
            }
        }

        public void SyncFromService()
        {
            EnsureInitialized();
            TickAttachIfNeeded(TaleWorlds.MountAndBlade.Mission.Current);
        }

        public void TickAttachIfNeeded(Mission? mission)
        {
            var svc = BattleSyncService.Instance;
            bool shouldShow = mission != null &&
                              mission.CurrentState == Mission.State.Continuing &&
                              svc.ActivePvpOpponentPeerId > 0 &&
                              !svc.PvpCombatStarted;

            if (!shouldShow)
            {
                if (_gauntletLayer != null)
                {
                    CloseOverlay();
                }
                return;
            }

            bool localReady = svc.LocalIsAttackerInPvp ? svc.AttackerReady : svc.DefenderReady;
            bool inDeployment = mission != null && (mission.Mode == MissionMode.Deployment || !mission.IsDeploymentFinished);

            // During active Order of Battle deployment before local Ready, let native Order of Battle "HAZIR VER (PvP)" button own input without overlap.
            if (inDeployment && !localReady)
            {
                if (_gauntletLayer != null)
                {
                    CloseOverlay();
                }
                return;
            }

            var topScreen = ScreenManager.TopScreen;
            if (topScreen == null) return;

            // Wait until MapScreen / LoadingScreen has transitioned out to MissionScreen
            string screenName = topScreen.GetType().Name;
            if (screenName.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0 ||
                screenName.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            if (_gauntletLayer == null || _parentScreen != topScreen)
            {
                OpenOverlay(topScreen, !localReady);
            }
            else
            {
                _dataSource?.SyncFromService();
                ApplyInputMode(!localReady);
            }

            if (!localReady && _gauntletLayer != null &&
                (_gauntletLayer.Input.IsKeyPressed(InputKey.F6) || Input.IsKeyPressed(InputKey.F6)))
            {
                _dataSource?.ExecuteReady();
            }
        }

        private void ApplyInputMode(bool needsButtonClick)
        {
            if (_gauntletLayer == null) return;
            try
            {
                if (needsButtonClick && !_hasInputRestrictions)
                {
                    _gauntletLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                    _hasInputRestrictions = true;
                }
                else if (!needsButtonClick && _hasInputRestrictions)
                {
                    _gauntletLayer.InputRestrictions.ResetInputRestrictions();
                    _hasInputRestrictions = false;
                }
            }
            catch { }
        }

        private void OpenOverlay(ScreenBase targetScreen, bool needsButtonClick)
        {
            CloseOverlay();

            try
            {
                _parentScreen = targetScreen;
                _dataSource = new PvpReadyVM();

                try { UIResourceManager.LoadSpriteCategory("ui_group1"); } catch { }

                _gauntletLayer = new GauntletLayer("CalradiaConnectedPvpReady", 4500, false);
                _movie = _gauntletLayer.LoadMovie("CalradiaConnectedPvpReady", _dataSource);
                _parentScreen.AddLayer(_gauntletLayer);
                ApplyInputMode(needsButtonClick);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] PvpReadyOverlay UI failed: {ex.Message}");
                CloseOverlay();
            }
        }

        public void CloseOverlay()
        {
            try
            {
                if (_gauntletLayer != null)
                {
                    if (_hasInputRestrictions)
                    {
                        try { _gauntletLayer.InputRestrictions.ResetInputRestrictions(); } catch { }
                        _hasInputRestrictions = false;
                    }
                    if (_movie != null)
                    {
                        try { _gauntletLayer.ReleaseMovie(_movie); } catch { }
                        _movie = null;
                    }
                    if (_parentScreen != null)
                    {
                        try { _parentScreen.RemoveLayer(_gauntletLayer); } catch { }
                    }
                    _gauntletLayer = null;
                }

                _dataSource?.OnFinalize();
                _dataSource = null;
                _parentScreen = null;
                _hasInputRestrictions = false;
            }
            catch { }
        }
    }
}
