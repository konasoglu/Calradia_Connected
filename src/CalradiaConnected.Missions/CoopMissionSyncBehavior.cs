using System;
using System.Collections.Generic;
using System.Reflection;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using LiteNetLib;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Missions
{
    /// <summary>
    /// Handles real-time Player-to-Player synchronization inside Bannerlord combat/arena missions:
    /// - Enforces dual "Hazır" (Ready) gate on synchronized PvP battles with AI paused until both players confirm
    /// - Strips AI brain from remote player hero agents & mounts so opponents never behave like bots
    /// - Streams local hero transform, look direction, movement flags, and combat swings at 30 Hz
    /// - Synchronizes real PvP blows/damage/KO between connected players
    /// - Provides live in-battle chat ([T]) and quick tactical comms ([Y]), plus [F6] Ready hotkey
    /// </summary>
    public sealed class CoopMissionSyncBehavior : MissionLogic
    {
        public static CoopMissionSyncBehavior? ActiveInstance { get; private set; }
        public static int PendingOpponentPeerId { get; set; } = 0;
        public static string PendingOpponentName { get; set; } = string.Empty;
        public static bool PendingIsNavalBattle { get; set; }

        private static PropertyInfo? _heroObjectProp;
        private static PropertyInfo? _heroStringIdProp;
        private static bool _reflectionInitAttempted;

        private sealed class RemoteAgentSyncState
        {
            public MissionAgentStatePacket? LastPacketRef;
            public float LastPacketReceivedTime;
            public Vec3 LastRawTargetPos;
            public bool HasInitializedTransform;
            public Vec3 SmoothedLookDir;
            public Vec2 SmoothedMountDir;
            public float SmoothedSpeed;
            public string LastUpperAction = string.Empty;
            public float LastUpperProgress;
            public string LastLowerAction = string.Empty;
            public float LastLowerProgress;
            public string LastMountAction = string.Empty;
            public float LastMountProgress;
            public sbyte LastMainHandSlot = -2;
            public sbyte LastOffHandSlot = -2;
            public float LastShieldSyncTime = -10f;
            public short LastObservedLocalShieldHp = -1;
            public bool KnockoutApplied;
            public bool MountKnockoutApplied;
            public float LocalHitReactHoldUntil = -10f;
            public float ForcedAttackAnimHoldUntil = -10f;
        }

        private readonly Dictionary<int, Agent> _remotePlayerAgents = new Dictionary<int, Agent>();
        private readonly Dictionary<int, MissionAgentStatePacket> _latestStates = new Dictionary<int, MissionAgentStatePacket>();
        private readonly Dictionary<int, MissionNavalShipStatePacket> _latestShipStates = new Dictionary<int, MissionNavalShipStatePacket>();
        private readonly Dictionary<int, string> _lastAppliedActions = new Dictionary<int, string>();
        private readonly Dictionary<int, RemoteAgentSyncState> _remoteSyncStates = new Dictionary<int, RemoteAgentSyncState>();
        private readonly NavalMissionBridge _navalBridge = new NavalMissionBridge();

        // Deterministic troop synchronization maps (owner-authoritative per side)
        private readonly Dictionary<ushort, Agent> _localTroopUnits = new Dictionary<ushort, Agent>();
        private readonly Dictionary<Agent, ushort> _localTroopIds = new Dictionary<Agent, ushort>();
        private readonly Dictionary<ushort, float> _localTroopDeadUntil = new Dictionary<ushort, float>();
        private readonly Dictionary<ushort, Agent> _remoteTroopUnits = new Dictionary<ushort, Agent>();
        private readonly Dictionary<Agent, ushort> _remoteTroopIds = new Dictionary<Agent, ushort>();
        private readonly Dictionary<ushort, TroopUnitSnapshot> _latestRemoteTroops = new Dictionary<ushort, TroopUnitSnapshot>();
        private float _troopSendAccumulator = 0f;
        private float _troopReindexAccumulator = 0f;
        private float _lastRemoteTroopPacketTime = -100f;
        private const float TROOP_SEND_INTERVAL = 0.085f; // ~12 Hz batched troop sync
        private const int TROOP_CHUNK_SIZE = 40;

        // Local authoritative hero state tracking for instant transition delivery & post-KO pulses
        private Agent? _localMainAgentRef;
        private string _lastSentUpperAction = string.Empty;
        private sbyte _lastSentMainSlot = -2;
        private sbyte _lastSentOffSlot = -2;
        private bool _lastSentHasMount = false;
        private byte _lastSentLifeState = 0;
        private float _localDeadPulseUntil = -1f;
        private float _lastShieldBlockBlowSentTime = -10f;

        // PvP battle end / single-TAB return tracking
        private int _cachedPvpOpponentPeerId = 0;
        private bool _missionResultResolved = false;
        private bool _missionResultLocalWon = false;
        private bool _missionEndReported = false;
        private bool _forceEndMissionRequested = false;

        private float _sendAccumulator = 0f;
        private const float SEND_INTERVAL = 0.025f; // 40 Hz for smooth animation & melee combat sync
        private bool _bannerShown = false;
        private bool _chatModalOpen = false;
        private bool _applyingNetworkBlow = false;

        private bool _isPvpReadyGateActive = false;
        private bool _combatUnlocked = false;
        private bool _localReadySent = false;
        private bool _sawDeploymentPhase = false;
        private float _missionElapsed = 0f;
        private bool _isRemoved = false;
        private bool _postUnlockVisibilityApplied = false;
        private float _visibilityCheckTimer = 0f;

        private readonly int _initialOpponentPeerId;
        private readonly string _initialOpponentName;
        private bool _isInitialized = false;
        private bool _loggedFirstRemoteStateApplied = false;

        public CoopMissionSyncBehavior(int opponentPeerId = 0, string? opponentName = null)
        {
            _initialOpponentPeerId = opponentPeerId;
            _initialOpponentName = opponentName ?? string.Empty;
        }

        public override void OnCreated()
        {
            base.OnCreated();
            InitializeBehaviorStateOnce();
        }

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            InitializeBehaviorStateOnce();
        }

        public void EnsureInitializedFromMission()
        {
            InitializeBehaviorStateOnce();
        }

        private void InitializeBehaviorStateOnce()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            if (_initialOpponentPeerId > 0)
            {
                PendingOpponentPeerId = _initialOpponentPeerId;
                if (!string.IsNullOrEmpty(_initialOpponentName))
                {
                    PendingOpponentName = _initialOpponentName;
                }
            }

            ActiveInstance = this;
            _remotePlayerAgents.Clear();
            _latestStates.Clear();
            _latestShipStates.Clear();
            _lastAppliedActions.Clear();
            _remoteSyncStates.Clear();
            _localTroopUnits.Clear();
            _localTroopIds.Clear();
            _localTroopDeadUntil.Clear();
            _remoteTroopUnits.Clear();
            _remoteTroopIds.Clear();
            _latestRemoteTroops.Clear();
            _navalBridge.Reset();
            _bannerShown = false;
            _chatModalOpen = false;
            _troopSendAccumulator = 0f;
            _troopReindexAccumulator = 0f;
            _lastRemoteTroopPacketTime = -100f;
            _localMainAgentRef = null;
            _lastSentUpperAction = string.Empty;
            _lastSentMainSlot = -2;
            _lastSentOffSlot = -2;
            _lastSentHasMount = false;
            _lastSentLifeState = 0;
            _localDeadPulseUntil = -1f;
            _lastShieldBlockBlowSentTime = -10f;
            _cachedPvpOpponentPeerId = PendingOpponentPeerId;
            _missionResultResolved = false;
            _missionResultLocalWon = false;
            _missionEndReported = false;
            _forceEndMissionRequested = false;

            _isPvpReadyGateActive = PendingOpponentPeerId > 0;
            _combatUnlocked = !_isPvpReadyGateActive;
            _localReadySent = false;
            _sawDeploymentPhase = false;
            _missionElapsed = 0f;
            _isRemoved = false;
            _postUnlockVisibilityApplied = false;
            _visibilityCheckTimer = 0f;
            _loggedPvpMissionDiagnostic = false;
            _loggedFirstRemoteStateApplied = false;

            ModContext.Events.Subscribe<PvpBattleStateChangedEvent>(OnPvpBattleStateChanged);

            try
            {
                TaleWorlds.Library.Debug.Print(
                    $"[Calradia-Connected PvP] CoopMissionSyncBehavior initialized: Scene={Mission?.SceneName ?? "null"}, OpponentPeerId={PendingOpponentPeerId}, ReadyGate={_isPvpReadyGateActive}, CombatUnlocked={_combatUnlocked}");
            }
            catch { }
        }

        public override void OnRemoveBehavior()
        {
            _isRemoved = true;
            ModContext.Events.Unsubscribe<PvpBattleStateChangedEvent>(OnPvpBattleStateChanged);
            if (ActiveInstance == this)
            {
                ActiveInstance = null;
                PendingOpponentPeerId = 0;
                PendingOpponentName = string.Empty;
                PendingIsNavalBattle = false;
            }
            _remotePlayerAgents.Clear();
            _latestStates.Clear();
            _latestShipStates.Clear();
            _lastAppliedActions.Clear();
            _remoteSyncStates.Clear();
            _localTroopUnits.Clear();
            _localTroopIds.Clear();
            _localTroopDeadUntil.Clear();
            _remoteTroopUnits.Clear();
            _remoteTroopIds.Clear();
            _latestRemoteTroops.Clear();
            base.OnRemoveBehavior();
        }

        public override void OnMissionResultReady(MissionResult missionResult)
        {
            base.OnMissionResultReady(missionResult);
            if (missionResult != null && missionResult.BattleResolved)
            {
                _missionResultResolved = true;
                _missionResultLocalWon = missionResult.PlayerVictory;
            }
        }

        public override void OnEndMissionInternal()
        {
            ReportPvpMissionEndIfResolved();
            RestoreMortalityOnAllSyncedAgents();

            base.OnEndMissionInternal();
            ModContext.Events.Unsubscribe<PvpBattleStateChangedEvent>(OnPvpBattleStateChanged);
            try
            {
                if (Mission != null && Mission.PauseAITick)
                {
                    Mission.PauseAITick = false;
                }
            }
            catch { }

            if (ActiveInstance == this)
            {
                ActiveInstance = null;
                PendingOpponentPeerId = 0;
                PendingOpponentName = string.Empty;
                PendingIsNavalBattle = false;
            }
            _latestShipStates.Clear();
            _latestRemoteTroops.Clear();
            _navalBridge.Reset();
        }

        private void ReportPvpMissionEndIfResolved()
        {
            if (_missionEndReported || ModContext.IsServerRunning || !ModContext.IsClientConnected) return;
            int oppPeerId = PendingOpponentPeerId > 0 ? PendingOpponentPeerId : _cachedPvpOpponentPeerId;
            if (oppPeerId <= 0 || !_combatUnlocked) return;

            bool resolved = _missionResultResolved;
            bool localWon = _missionResultLocalWon;

            try
            {
                if (!resolved && Mission?.MissionResult != null && Mission.MissionResult.BattleResolved)
                {
                    resolved = true;
                    localWon = Mission.MissionResult.PlayerVictory;
                }

                if (!resolved && Mission != null)
                {
                    var battleEnd = Mission.GetMissionBehavior<BattleEndLogic>();
                    if (battleEnd != null)
                    {
                        if (battleEnd.PlayerVictory)
                        {
                            resolved = true;
                            localWon = true;
                        }
                        else if (battleEnd.EnemyVictory)
                        {
                            resolved = true;
                            localWon = false;
                        }
                    }
                }

                if (!resolved && Mission != null)
                {
                    int playerActive = Mission.PlayerTeam?.ActiveAgents?.Count ?? 0;
                    int enemyActive = Mission.PlayerEnemyTeam?.ActiveAgents?.Count ?? 0;
                    if (playerActive > 0 && enemyActive == 0)
                    {
                        resolved = true;
                        localWon = true;
                    }
                    else if (playerActive == 0 && enemyActive > 0)
                    {
                        resolved = true;
                        localWon = false;
                    }
                }
            }
            catch { }

            if (!resolved) return;

            _missionEndReported = true;
            ModContext.Events.Publish(new PvpMissionEndedEvent
            {
                OpponentPeerId = oppPeerId,
                LocalWon = localWon,
                Resolved = true
            });
        }

        private void OnPvpBattleStateChanged(PvpBattleStateChangedEvent evt)
        {
            if (evt == null || _isRemoved || Mission == null || ActiveInstance != this) return;

            if (evt.Stage == 11 && evt.OpponentPeerId > 0)
            {
                PendingOpponentPeerId = evt.OpponentPeerId;
                _cachedPvpOpponentPeerId = evt.OpponentPeerId;
                PendingOpponentName = evt.OpponentName ?? string.Empty;
                _isPvpReadyGateActive = true;
            }
            else if (evt.Stage == 13) // PvpCombatBegin -> BOTH players are Ready!
            {
                if (_combatUnlocked) return;
                _combatUnlocked = true;
                UnlockCombatAndEnsureAgentsVisible();

                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("İki taraf da HAZIR! Savaş başladı!", "Both sides are READY! Battle begins!"),
                    Colors.Green));
            }
            else if (evt.Stage == 2 || evt.Stage == 3)
            {
                _combatUnlocked = true;
                RestoreMortalityOnAllSyncedAgents();
                if (evt.Stage == 3)
                {
                    _isPvpReadyGateActive = false;
                    PendingOpponentPeerId = 0;
                    PendingOpponentName = string.Empty;
                }
                UnlockCombatAndEnsureAgentsVisible();

                // Server confirmed PvP battle ended (e.g. opponent pressed TAB) -> end this mission automatically!
                if (evt.AttackerPeerId > 0 && evt.DefenderPeerId > 0)
                {
                    _missionEndReported = true;
                    _forceEndMissionRequested = true;
                    try { Mission?.EndMission(); } catch { }
                }
            }
        }

        private bool _loggedPvpMissionDiagnostic;

        private void LogPvpTeamDiagnostic(string phase)
        {
            try
            {
                if (Mission == null) return;
                int defCount = Mission.DefenderTeam?.ActiveAgents?.Count ?? 0;
                int atkCount = Mission.AttackerTeam?.ActiveAgents?.Count ?? 0;
                int plrCount = Mission.PlayerTeam?.ActiveAgents?.Count ?? 0;
                int enemyCount = Mission.PlayerEnemyTeam?.ActiveAgents?.Count ?? 0;
                TaleWorlds.Library.Debug.Print(
                    $"[Calradia-Connected PvP] {phase}: Teams={Mission.Teams?.Count ?? 0}, DefActive={defCount}, AtkActive={atkCount}, PlayerActive={plrCount}, EnemyActive={enemyCount}, BoundRemote={_remotePlayerAgents.Count}, CombatUnlocked={_combatUnlocked}, SawDeploy={_sawDeploymentPhase}");
            }
            catch { }
        }

        private void UnlockCombatAndEnsureAgentsVisible()
        {
            try
            {
                if (Mission != null)
                {
                    if (!Mission.IsDeploymentFinished)
                    {
                        try { Mission.GetMissionBehavior<DeploymentHandler>()?.FinishDeployment(); } catch { }
                    }
                    Mission.PauseAITick = false;
                    Mission.AllowAiTicking = true;
                    EnsureAllAgentsVisible();
                    _postUnlockVisibilityApplied = true;
                    LogPvpTeamDiagnostic("CombatUnlocked");
                }
            }
            catch { }
        }

        private void EnsureAllAgentsVisible()
        {
            if (Mission == null) return;
            try
            {
                foreach (var agent in Mission.Agents)
                {
                    if (agent == null || !agent.IsActive()) continue;
                    try
                    {
                        agent.SetRenderCheckEnabled(true);
                        agent.AgentVisuals?.SetVisible(true);
                    }
                    catch { }

                    if (agent.HasMount && agent.MountAgent != null && agent.MountAgent.IsActive())
                    {
                        try
                        {
                            agent.MountAgent.SetRenderCheckEnabled(true);
                            agent.MountAgent.AgentVisuals?.SetVisible(true);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public void MarkLocalReadyFromUi()
        {
            if (!_isPvpReadyGateActive || _localReadySent) return;
            _localReadySent = true;
            InformationManager.DisplayMessage(new InformationMessage(
                Loc.T("HAZIR sinyali gönderildi! Rakibin hazır olması bekleniyor...",
                      "READY confirmed! Waiting for opponent..."),
                Colors.Yellow));
        }

        private void TriggerLocalPvpReady()
        {
            if (!_isPvpReadyGateActive || _localReadySent) return;
            _localReadySent = true;
            ModContext.Events.Publish(new PvpBattleStateChangedEvent { Stage = 12 });
            InformationManager.DisplayMessage(new InformationMessage(
                Loc.T("HAZIR sinyali gönderildi! Rakibin hazır olması bekleniyor...",
                      "READY confirmed! Waiting for opponent..."),
                Colors.Yellow));
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            base.OnAgentBuild(agent, banner);
            TryBindRemotePlayerAgent(agent);
        }

        private static string? TryGetCharacterOrHeroStringId(Agent agent, out bool isPlayerHero)
        {
            isPlayerHero = false;
            if (agent.Character == null) return null;

            string? charId = agent.Character.StringId;
            if (!string.IsNullOrEmpty(charId) && charId.StartsWith("cc_hero_", StringComparison.Ordinal))
            {
                isPlayerHero = true;
                return charId;
            }

            try
            {
                if (!_reflectionInitAttempted)
                {
                    _reflectionInitAttempted = true;
                    _heroObjectProp = agent.Character.GetType().GetProperty("HeroObject", BindingFlags.Public | BindingFlags.Instance);
                }

                object? heroObj = _heroObjectProp?.GetValue(agent.Character);
                if (heroObj != null)
                {
                    _heroStringIdProp ??= heroObj.GetType().GetProperty("StringId", BindingFlags.Public | BindingFlags.Instance);
                    string? heroId = _heroStringIdProp?.GetValue(heroObj) as string;
                    if (!string.IsNullOrEmpty(heroId) && heroId!.StartsWith("cc_hero_", StringComparison.Ordinal))
                    {
                        isPlayerHero = true;
                        return heroId;
                    }
                }
            }
            catch { }

            return charId;
        }

        private static void StripRemoteAgentAi(Agent agent)
        {
            try
            {
                if (agent.Controller != AgentControllerType.None)
                {
                    agent.Controller = AgentControllerType.None;
                }
                // Keep AI paused = false when Controller == None so native action channel ticking & upper/lower blending never stall!
                agent.SetIsAIPaused(false);
                agent.DisableScriptedMovement();
                agent.DisableScriptedCombatMovement();
                agent.ClearTargetFrame();

                var mount = agent.MountAgent;
                if (mount != null && mount.IsActive())
                {
                    if (mount.Controller != AgentControllerType.None)
                    {
                        mount.Controller = AgentControllerType.None;
                    }
                    mount.SetIsAIPaused(false);
                    mount.DisableScriptedMovement();
                    mount.ClearTargetFrame();
                }
            }
            catch { }
        }

        private void ForceKnockoutOrKillAgent(Agent agent, bool isKilled)
        {
            if (agent == null || !agent.IsActive()) return;

            _applyingNetworkBlow = true;
            try
            {
                agent.SetMortalityState(Agent.MortalityState.Mortal);
                var blow = new Blow(agent.Index)
                {
                    DamageType = isKilled ? DamageTypes.Cut : DamageTypes.Blunt,
                    InflictedDamage = (int)Math.Ceiling(Math.Max(10f, agent.Health + 25f)),
                    BaseMagnitude = 40f,
                    VictimBodyPart = BoneBodyPartType.Chest,
                    GlobalPosition = agent.Position,
                    SwingDirection = Vec3.Forward,
                    BlowFlag = BlowFlags.NoSound
                };
                agent.Die(blow, Agent.KillInfo.Invalid);
            }
            catch
            {
                try
                {
                    agent.SetMortalityState(Agent.MortalityState.Mortal);
                    agent.FadeOut(hideInstantly: false, hideMount: false);
                }
                catch { }
            }
            finally
            {
                _applyingNetworkBlow = false;
            }
        }

        private void RestoreMortalityOnAllSyncedAgents()
        {
            try
            {
                foreach (var kvp in _remotePlayerAgents)
                {
                    var ag = kvp.Value;
                    if (ag != null && ag.IsActive())
                    {
                        ag.SetMortalityState(Agent.MortalityState.Mortal);
                        if (ag.HasMount && ag.MountAgent != null && ag.MountAgent.IsActive())
                        {
                            ag.MountAgent.SetMortalityState(Agent.MortalityState.Mortal);
                        }
                    }
                }
                foreach (var kvp in _remoteTroopUnits)
                {
                    var tr = kvp.Value;
                    if (tr != null && tr.IsActive())
                    {
                        tr.SetMortalityState(Agent.MortalityState.Mortal);
                        tr.DisableScriptedMovement();
                    }
                }
            }
            catch { }
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return 0f;
            return value > 0.99f ? 0.99f : value;
        }

        private static string ResolveHorseGaitActionName(MissionAgentStatePacket st, float remoteSpeed, Vec2 mountFace, out float speedScale)
        {
            speedScale = 1.0f;
            if (!string.IsNullOrEmpty(st.MountActionName))
            {
                return st.MountActionName;
            }

            if (remoteSpeed < 0.30f)
            {
                return "act_horse_idle_1";
            }

            var velXY = new Vec2(st.VelX, st.VelY);
            if (velXY.LengthSquared > 0.04f && mountFace.LengthSquared > 0.001f)
            {
                float forwardDot = (velXY.X * mountFace.X + velXY.Y * mountFace.Y) / velXY.Length;
                if (forwardDot < -0.35f)
                {
                    speedScale = Math.Max(0.75f, Math.Min(1.35f, remoteSpeed / 1.8f));
                    return "act_horse_backward_walk";
                }
            }

            if (remoteSpeed < 2.6f)
            {
                speedScale = Math.Max(0.70f, Math.Min(1.35f, remoteSpeed / 1.8f));
                return "act_horse_forward_walk";
            }
            if (remoteSpeed < 5.4f)
            {
                speedScale = Math.Max(0.80f, Math.Min(1.30f, remoteSpeed / 4.0f));
                return "act_horse_forward_trot";
            }
            if (remoteSpeed < 8.4f)
            {
                speedScale = Math.Max(0.85f, Math.Min(1.25f, remoteSpeed / 6.9f));
                return "act_horse_forward_canter";
            }
            speedScale = Math.Max(0.90f, Math.Min(1.35f, remoteSpeed / 10.2f));
            return "act_horse_forward_gallop_right_foot";
        }

        private static string ResolveHumanLocomotionActionName(Agent agent, MissionAgentStatePacket st, float remoteSpeed, Vec3 lookDir, out float speedScale)
        {
            speedScale = 1.0f;
            if (!string.IsNullOrEmpty(st.LowerActionName))
            {
                return st.LowerActionName;
            }

            if (remoteSpeed < 0.25f)
            {
                return string.Empty; // Let native bipedal idle stance play smoothly with held gear
            }

            bool isWalk = ((st.StateFlags & 4) != 0) || remoteSpeed < 3.2f;
            bool hasWeapon = agent.GetPrimaryWieldedItemIndex() != EquipmentIndex.None;
            bool hasShield = agent.GetOffhandWieldedItemIndex() != EquipmentIndex.None;

            Vec2 forward = new Vec2(lookDir.X, lookDir.Y);
            if (forward.LengthSquared < 0.001f)
            {
                forward = Vec2.Forward;
            }
            else
            {
                forward.Normalize();
            }
            Vec2 right = new Vec2(forward.Y, -forward.X);

            Vec2 velXY = new Vec2(st.VelX, st.VelY);
            if (velXY.LengthSquared < 0.02f)
            {
                velXY = new Vec2(st.MoveX, st.MoveY);
            }

            float fwdComp = velXY.X * forward.X + velXY.Y * forward.Y;
            float rightComp = velXY.X * right.X + velXY.Y * right.Y;

            speedScale = isWalk
                ? Math.Max(0.75f, Math.Min(1.35f, remoteSpeed / 2.2f))
                : Math.Max(0.80f, Math.Min(1.35f, remoteSpeed / 5.0f));

            if (Math.Abs(rightComp) > Math.Abs(fwdComp) * 1.15f)
            {
                if (rightComp > 0f)
                {
                    return isWalk ? "act_walk_right_unarmed" : "act_run_right_unarmed";
                }
                return isWalk ? "act_walk_left_unarmed" : "act_run_left_unarmed";
            }

            if (fwdComp < -0.25f)
            {
                if (isWalk) return "act_walk_backward_unarmed";
                if (hasWeapon && hasShield) return "act_run_backward_1h_with_shield";
                if (hasWeapon) return "act_run_backward_1h";
                return "act_run_backward_unarmed";
            }

            if (isWalk)
            {
                if (hasWeapon && hasShield) return "act_walk_forward_1h_with_shield";
                if (hasWeapon) return "act_walk_forward_1h";
                return "act_walk_forward_unarmed";
            }
            else
            {
                if (hasWeapon && hasShield) return "act_run_forward_1h_with_shield";
                if (hasWeapon) return "act_run_forward_1h";
                return "act_run_forward_unarmed";
            }
        }

        private static void ApplyActionChannelSynced(
            Agent target,
            int channelNo,
            string desiredAction,
            float desiredProgress,
            ref string lastAction,
            ref float lastProgress,
            float blendInPeriod,
            float actionSpeed = 1.0f)
        {
            if (string.IsNullOrEmpty(desiredAction))
            {
                if (!string.IsNullOrEmpty(lastAction))
                {
                    lastAction = string.Empty;
                    lastProgress = 0f;
                    try
                    {
                        var noneAct = ActionIndexCache.act_none;
                        target.SetActionChannel(channelNo, in noneAct, ignorePriority: true, blendInPeriod: blendInPeriod);
                    }
                    catch { }
                }
                return;
            }

            float clampedProgress = Clamp01(desiredProgress);
            bool actionChanged = !string.Equals(lastAction, desiredAction, StringComparison.Ordinal);
            bool actionRestarted = !actionChanged && clampedProgress < lastProgress - 0.25f;
            bool channelEnded = false;
            if (!actionChanged && !actionRestarted)
            {
                try
                {
                    var curAct = target.GetCurrentAction(channelNo);
                    channelEnded = curAct == ActionIndexCache.act_none || curAct.Index < 0;
                }
                catch { }
            }

            if (actionChanged || actionRestarted || channelEnded)
            {
                lastAction = desiredAction;
                lastProgress = clampedProgress;
                try
                {
                    var actionCache = ActionIndexCache.Create(desiredAction);
                    if (actionCache.Index >= 0)
                    {
                        // For Channel 1 upper-body combat swings/blocks/staggers, start fresh swings near 0 so fast attacks never skip their visible arc!
                        float startProg = (actionRestarted || channelEnded)
                            ? 0f
                            : (channelNo == 1 ? Math.Min(0.15f, clampedProgress) : clampedProgress);
                        target.SetActionChannel(
                            channelNo,
                            in actionCache,
                            ignorePriority: true,
                            actionSpeed: actionSpeed,
                            blendInPeriod: blendInPeriod,
                            startProgress: startProg);
                    }
                }
                catch { }
            }
            else
            {
                lastProgress = clampedProgress;
                if (Math.Abs(actionSpeed - 1.0f) > 0.04f)
                {
                    try { target.SetCurrentActionSpeed(channelNo, actionSpeed); } catch { }
                }
            }
        }

        private static void SyncWieldedWeapons(Agent agent, sbyte mainSlot, sbyte offSlot, RemoteAgentSyncState syncState)
        {
            bool instant = syncState.LastMainHandSlot == -2;
            var wieldType = instant
                ? Agent.WeaponWieldActionType.Instant
                : Agent.WeaponWieldActionType.WithAnimation;

            if (mainSlot >= -1 && mainSlot <= 3 && mainSlot != syncState.LastMainHandSlot)
            {
                syncState.LastMainHandSlot = mainSlot;
                try
                {
                    var curMain = agent.GetPrimaryWieldedItemIndex();
                    var desiredMain = mainSlot >= 0 ? (EquipmentIndex)mainSlot : EquipmentIndex.None;
                    if (curMain != desiredMain)
                    {
                        if (desiredMain == EquipmentIndex.None)
                        {
                            agent.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, wieldType);
                        }
                        else
                        {
                            agent.TryToWieldWeaponInSlot(desiredMain, wieldType, false);
                        }
                    }
                }
                catch { }
            }

            if (offSlot >= -1 && offSlot <= 3 && offSlot != syncState.LastOffHandSlot)
            {
                syncState.LastOffHandSlot = offSlot;
                try
                {
                    var curOff = agent.GetOffhandWieldedItemIndex();
                    var desiredOff = offSlot >= 0 ? (EquipmentIndex)offSlot : EquipmentIndex.None;
                    if (curOff != desiredOff)
                    {
                        if (desiredOff == EquipmentIndex.None)
                        {
                            agent.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, wieldType);
                        }
                        else
                        {
                            agent.TryToWieldWeaponInSlot(desiredOff, wieldType, false);
                        }
                    }
                }
                catch { }
            }
        }

        private static void ApplyRemoteMovementAndMountSync(
            Agent agent,
            Vec3 prevPos,
            Vec3 targetPos,
            Vec3 lookDir,
            MissionAgentStatePacket st,
            RemoteAgentSyncState syncState,
            float dt)
        {
            bool hasExtended = (st.StateFlags & 8) != 0;
            bool remoteHasMount = !hasExtended || (st.StateFlags & 1) != 0;
            float remoteSpeed = syncState.SmoothedSpeed;

            var moveFlags = (Agent.MovementControlFlag)st.MovementFlags;
            var moveInput = new Vec2(st.MoveX, st.MoveY);

            var mount = agent.MountAgent;
            if (mount != null && mount.IsActive())
            {
                // Dismount if the remote player dismounted on their client
                if (hasExtended && !remoteHasMount)
                {
                    try { agent.EventControlFlags |= Agent.EventControlFlag.Dismount; } catch { }
                }

                // Never inject Forward throttle or Attack/Defend MovementFlags onto a remote puppet (Attack/Defend
                // flags fight with SetActionChannel(1) and cancel visible swing/block animations!).
                agent.MovementInputVector = Vec2.Zero;
                agent.MovementFlags = Agent.MovementControlFlag.None;

                var desiredMountFace = new Vec2(st.MountLookX, st.MountLookY);
                if (desiredMountFace.LengthSquared <= 0.001f)
                {
                    var planarDelta = new Vec2(targetPos.X - prevPos.X, targetPos.Y - prevPos.Y);
                    if (planarDelta.LengthSquared > 0.0036f)
                    {
                        desiredMountFace = planarDelta;
                    }
                    else
                    {
                        desiredMountFace = new Vec2(lookDir.X, lookDir.Y);
                    }
                }

                if (desiredMountFace.LengthSquared > 0.001f)
                {
                    desiredMountFace.Normalize();
                    if (syncState.SmoothedMountDir.LengthSquared <= 0.001f)
                    {
                        syncState.SmoothedMountDir = desiredMountFace;
                    }
                    else
                    {
                        float turnAlpha = 1f - (float)Math.Exp(-14.0 * Math.Max(0.001f, dt));
                        Vec2 blendedMount = Vec2.Lerp(syncState.SmoothedMountDir, desiredMountFace, turnAlpha);
                        if (blendedMount.LengthSquared > 0.001f)
                        {
                            blendedMount.Normalize();
                            syncState.SmoothedMountDir = blendedMount;
                        }
                    }

                    Vec2 mountFace = syncState.SmoothedMountDir;
                    mount.LookDirection = new Vec3(mountFace.X, mountFace.Y, 0f);
                    mount.SetMovementDirection(in mountFace);
                }

                mount.MovementInputVector = Vec2.Zero;
                mount.MovementFlags = Agent.MovementControlFlag.None;

                string horseGait = ResolveHorseGaitActionName(st, remoteSpeed, syncState.SmoothedMountDir, out float horseSpeedScale);
                float horseProg = !string.IsNullOrEmpty(st.MountActionName) ? st.MountActionProgress : 0f;
                ApplyActionChannelSynced(mount, 0, horseGait, horseProg, ref syncState.LastMountAction, ref syncState.LastMountProgress, blendInPeriod: 0.14f, actionSpeed: horseSpeedScale);
            }
            else
            {
                // On-foot locomotion: keep MovementFlags clear of Attack/Defend masks so SetActionChannel(1)
                // plays remote attack swings, blocks, and hit-reactions without Bannerlord's native combat controller cancelling them!
                if (remoteSpeed < 0.20f && moveInput.LengthSquared < 0.01f)
                {
                    agent.MovementInputVector = Vec2.Zero;
                    agent.MovementFlags = Agent.MovementControlFlag.None;
                }
                else
                {
                    agent.MovementInputVector = moveInput;
                    agent.MovementFlags = moveFlags & Agent.MovementControlFlag.MoveMask;
                }

                if (hasExtended)
                {
                    try { agent.SetCrouchMode((st.StateFlags & 2) != 0); } catch { }
                }
            }
        }

        private void TryBindRemotePlayerAgent(Agent? agent)
        {
            if (agent == null || !agent.IsHuman || agent.Character == null || agent.IsMainAgent) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
            int peerId = 0;

            string? matchedId = TryGetCharacterOrHeroStringId(agent, out bool isCcHero);
            if (isCcHero && !string.IsNullOrEmpty(matchedId) && matchedId!.StartsWith("cc_hero_", StringComparison.Ordinal))
            {
                int.TryParse(matchedId.Substring("cc_hero_".Length), out peerId);
            }

            // Fallback in synchronized PvP battle when a template character StringId isn't cc_hero_*
            if (peerId <= 0 &&
                PendingOpponentPeerId > 0 &&
                agent.IsHero &&
                Agent.Main != null &&
                agent.Team != null &&
                Agent.Main.Team != null &&
                agent.Team.IsEnemyOf(Agent.Main.Team))
            {
                string agentName = agent.Name?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(PendingOpponentName) ||
                    string.Equals(agentName, PendingOpponentName, StringComparison.OrdinalIgnoreCase) ||
                    !_remotePlayerAgents.ContainsKey(PendingOpponentPeerId))
                {
                    peerId = PendingOpponentPeerId;
                }
            }

            if (peerId <= 0 || peerId == myPeerId) return;

            bool isNewBind = !_remotePlayerAgents.TryGetValue(peerId, out var existingAgent) || existingAgent != agent;
            _remotePlayerAgents[peerId] = agent;
            StripRemoteAgentAi(agent);

            if (isNewBind)
            {
                try
                {
                    var p = agent.Position;
                    TaleWorlds.Library.Debug.Print(
                        $"[Calradia-Connected PvP] BoundRemoteHero: PeerId={peerId}, Agent={agent.Name}, Team={agent.Team?.Side}, HasMount={agent.HasMount}, Pos=({p.x:F1},{p.y:F1},{p.z:F1})");
                }
                catch { }
            }
        }

        private void ScanAndBindRemoteAgents()
        {
            if (Mission == null) return;
            foreach (var agent in Mission.Agents)
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman || agent.IsMainAgent || agent.Character == null) continue;

                bool alreadyTracked = false;
                foreach (var kvp in _remotePlayerAgents)
                {
                    if (kvp.Value == agent)
                    {
                        alreadyTracked = true;
                        StripRemoteAgentAi(agent);
                        break;
                    }
                }

                if (!alreadyTracked)
                {
                    TryBindRemotePlayerAgent(agent);
                }
            }
        }

        public static void HandleRemoteStatePacket(MissionAgentStatePacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                var inst = ActiveInstance;
                if (inst == null) return;

                int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
                if (packet.PeerId == myPeerId) return;

                inst._latestStates[packet.PeerId] = packet;
            });
        }

        public static void HandleRemoteNavalShipStatePacket(MissionNavalShipStatePacket packet)
        {
            if (packet == null) return;
            ModContext.Dispatcher.Enqueue(() =>
            {
                var inst = ActiveInstance;
                if (inst == null) return;

                int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
                if (packet.PeerId == myPeerId) return;

                inst._latestShipStates[packet.PeerId] = packet;
            });
        }

        public static void HandleRemoteTroopsSyncPacket(MissionTroopsSyncPacket packet)
        {
            if (packet == null || packet.Units == null) return;
            ModContext.Dispatcher.Enqueue(() =>
            {
                var inst = ActiveInstance;
                if (inst == null) return;

                int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
                if (packet.SenderPeerId == myPeerId) return;

                inst._lastRemoteTroopPacketTime = inst._missionElapsed;
                for (int i = 0; i < packet.Units.Count; i++)
                {
                    var u = packet.Units[i];
                    if (u != null && u.UnitId > 0)
                    {
                        inst._latestRemoteTroops[u.UnitId] = u;
                    }
                }
            });
        }

        public static void HandleRemoteBlowPacket(MissionBlowSyncPacket packet)
        {
            if (packet == null) return;
            ModContext.Dispatcher.Enqueue(() =>
            {
                var inst = ActiveInstance;
                if (inst == null || inst.Mission == null) return;

                int myPeerId = ModContext.Client?.LocalPeerId ?? -1;

                // Authoritative kill confirmation from the owning client (hero, mount, or troop)
                if (packet.IsAuthoritativeKill)
                {
                    if (packet.HitTargetKind == 3 && packet.TargetTroopUnitId > 0)
                    {
                        if (inst._remoteTroopUnits.TryGetValue(packet.TargetTroopUnitId, out var remoteTroop) &&
                            remoteTroop != null && remoteTroop.IsActive())
                        {
                            inst.ForceKnockoutOrKillAgent(remoteTroop, isKilled: true);
                        }
                        return;
                    }

                    int deadOwnerPeerId = packet.VictimPeerId > 0 ? packet.VictimPeerId : packet.AttackerPeerId;
                    if (deadOwnerPeerId != myPeerId &&
                        inst._remotePlayerAgents.TryGetValue(deadOwnerPeerId, out var remoteHero) &&
                        remoteHero != null)
                    {
                        if (packet.HitTargetKind == 1)
                        {
                            if (remoteHero.HasMount && remoteHero.MountAgent != null && remoteHero.MountAgent.IsActive())
                            {
                                inst.ForceKnockoutOrKillAgent(remoteHero.MountAgent, isKilled: true);
                            }
                        }
                        else if (remoteHero.IsActive())
                        {
                            inst.ForceKnockoutOrKillAgent(remoteHero, isKilled: false);
                        }
                    }
                    return;
                }

                // Incoming damage directed at our local player hero, mount, shield, or one of our owned troops
                if (packet.VictimPeerId == myPeerId)
                {
                    if (packet.HitTargetKind == 3 && packet.TargetTroopUnitId > 0)
                    {
                        inst.ApplySyncedDamageToLocalTroop(packet);
                    }
                    else if (Agent.Main != null && Agent.Main.IsActive())
                    {
                        inst.ApplySyncedDamageToLocalMain(packet);
                    }
                }
            });
        }

        private void ApplySyncedDamageToLocalTroop(MissionBlowSyncPacket packet)
        {
            if (!_localTroopUnits.TryGetValue(packet.TargetTroopUnitId, out var troop) || troop == null || !troop.IsActive())
            {
                return;
            }
            if (packet.IsBlocked || packet.Damage <= 0.1f) return;

            _applyingNetworkBlow = true;
            try
            {
                float newHp = Math.Max(0f, troop.Health - packet.Damage);
                if (newHp <= 0.5f)
                {
                    ForceKnockoutOrKillAgent(troop, isKilled: true);
                    _localTroopDeadUntil[packet.TargetTroopUnitId] = _missionElapsed + 3.5f;
                    int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = myPeerId,
                        HitTargetKind = 3,
                        TargetTroopUnitId = packet.TargetTroopUnitId,
                        IsFatal = true,
                        IsAuthoritativeKill = true
                    }, DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    troop.Health = newHp;
                }
            }
            catch { }
            finally
            {
                _applyingNetworkBlow = false;
            }
        }

        private static AttackCollisionData BuildVisualHitCollisionData(Agent victim, Vec3 blowDir, BoneBodyPartType bodyPart, StrikeType strikeType, DamageTypes damageType)
        {
            Vec3 hitPos = victim.Position + new Vec3(0f, 0f, victim.IsMount ? 1.1f : 1.35f);
            return AttackCollisionData.GetAttackCollisionDataForDebugPurpose(
                _attackBlockedWithShield: false,
                _correctSideShieldBlock: false,
                _isAlternativeAttack: false,
                _isColliderAgent: true,
                _collidedWithShieldOnBack: false,
                _isMissile: false,
                _isMissileBlockedWithWeapon: false,
                _missileHasPhysics: false,
                _entityExists: true,
                _thrustTipHit: strikeType == StrikeType.Thrust,
                _missileGoneUnderWater: false,
                _missileGoneOutOfBorder: false,
                collisionResult: CombatCollisionResult.StrikeAgent,
                affectorWeaponSlotOrMissileIndex: -1,
                StrikeType: (int)strikeType,
                DamageType: (int)damageType,
                CollisionBoneIndex: 0,
                VictimHitBodyPart: bodyPart,
                AttackBoneIndex: 0,
                AttackDirection: Agent.UsageDirection.AttackRight,
                PhysicsMaterialIndex: 0,
                CollisionHitResultFlags: CombatHitResultFlags.NormalHit,
                AttackProgress: 0.5f,
                CollisionDistanceOnWeapon: 0.5f,
                AttackerStunPeriod: 0f,
                DefenderStunPeriod: 0.35f,
                MissileTotalDamage: 0f,
                MissileInitialSpeed: 0f,
                ChargeVelocity: 0f,
                FallSpeed: 0f,
                WeaponRotUp: Vec3.Up,
                _weaponBlowDir: blowDir,
                CollisionGlobalPosition: hitPos,
                MissileVelocity: Vec3.Zero,
                MissileStartingPosition: Vec3.Zero,
                VictimAgentCurVelocity: victim.Velocity,
                GroundNormal: Vec3.Up);
        }

        private void PlayRemoteAttackerSwingFromBlow(int attackerPeerId, string? actionName)
        {
            if (attackerPeerId <= 0 || string.IsNullOrEmpty(actionName)) return;
            if (!_remotePlayerAgents.TryGetValue(attackerPeerId, out var attacker) || attacker == null || !attacker.IsActive()) return;

            if (!_remoteSyncStates.TryGetValue(attackerPeerId, out var rs))
            {
                rs = new RemoteAgentSyncState();
                _remoteSyncStates[attackerPeerId] = rs;
            }

            rs.ForcedAttackAnimHoldUntil = _missionElapsed + 0.32f;
            if (!string.Equals(rs.LastUpperAction, actionName, StringComparison.Ordinal))
            {
                ApplyActionChannelSynced(attacker, 1, actionName!, 0f, ref rs.LastUpperAction, ref rs.LastUpperProgress, blendInPeriod: 0.04f);
            }
        }

        private void ApplySyncedDamageToLocalMain(MissionBlowSyncPacket packet)
        {
            var main = Agent.Main;
            if (main == null || !main.IsActive()) return;

            // Ensure Victim always visually sees the remote attacker's weapon swing animation on every hit!
            PlayRemoteAttackerSwingFromBlow(packet.AttackerPeerId, packet.AttackerActionName);

            _applyingNetworkBlow = true;
            try
            {
                // 1) Shield block damage -> deduct offhand shield durability authoritatively
                if (packet.HitTargetKind == 2 || packet.IsBlocked)
                {
                    short shieldDmg = packet.ShieldDamage > 0
                        ? packet.ShieldDamage
                        : (short)Math.Max(1, (int)Math.Ceiling(packet.Damage));
                    try
                    {
                        var offSlot = main.GetOffhandWieldedItemIndex();
                        if (offSlot != EquipmentIndex.None)
                        {
                            var offWeapon = main.Equipment[offSlot];
                            if (!offWeapon.IsEmpty && offWeapon.HitPoints > 0 && shieldDmg > 0)
                            {
                                short newShieldHp = (short)Math.Max(0, offWeapon.HitPoints - shieldDmg);
                                main.ChangeWeaponHitPoints(offSlot, newShieldHp);
                            }
                        }
                    }
                    catch { }

                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.T("Saldırı kalkanla bloklandı!", "Attack blocked with shield!"),
                        Colors.Cyan));
                    return;
                }

                var attacker = _remotePlayerAgents.TryGetValue(packet.AttackerPeerId, out var a) ? a : null;
                Vec3 swingDir = attacker != null
                    ? (main.Position - attacker.Position).NormalizedCopy()
                    : -main.LookDirection;
                if (swingDir.LengthSquared < 0.01f) swingDir = Vec3.Forward;

                BoneBodyPartType bodyPart = Enum.IsDefined(typeof(BoneBodyPartType), (sbyte)packet.VictimBodyPart)
                    ? (BoneBodyPartType)(sbyte)packet.VictimBodyPart
                    : BoneBodyPartType.Chest;
                StrikeType strikeType = packet.StrikeType == 1 ? StrikeType.Thrust : StrikeType.Swing;

                // 2) Mount damage -> register real visual hit on local MountAgent
                if (packet.HitTargetKind == 1)
                {
                    var mount = main.HasMount ? main.MountAgent : null;
                    if (mount == null || !mount.IsActive()) return;
                    float dmg = Math.Max(1f, packet.Damage);
                    float newMountHp = Math.Max(0f, mount.Health - dmg);
                    if (newMountHp <= 0.5f)
                    {
                        ForceKnockoutOrKillAgent(mount, isKilled: true);
                        int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
                        ModContext.Client?.Send(new MissionBlowSyncPacket
                        {
                            AttackerPeerId = myPeerId,
                            VictimPeerId = myPeerId,
                            HitTargetKind = 1,
                            IsFatal = true,
                            IsAuthoritativeKill = true
                        }, DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        int safeMountDmg = Math.Max(1, Math.Min((int)Math.Round(dmg), Math.Max(1, (int)Math.Floor(mount.Health) - 1)));
                        var mountBlow = new Blow(attacker?.Index ?? main.Index)
                        {
                            DamageType = DamageTypes.Cut,
                            StrikeType = strikeType,
                            AttackType = AgentAttackType.Standard,
                            InflictedDamage = safeMountDmg,
                            DamageCalculated = true,
                            BaseMagnitude = Math.Max(20f, dmg),
                            VictimBodyPart = BoneBodyPartType.Chest,
                            GlobalPosition = mount.Position + new Vec3(0f, 0f, 1.1f),
                            Direction = swingDir,
                            SwingDirection = swingDir
                        };
                        var mountCol = BuildVisualHitCollisionData(mount, swingDir, BoneBodyPartType.Chest, strikeType, DamageTypes.Cut);
                        mount.RegisterBlow(mountBlow, in mountCol);
                    }
                    return;
                }

                // 3) Human hero damage -> ALWAYS use RegisterBlow so Victim sees real flinch/stagger animation, blood, sound & red hit flash!
                // NEVER trust attacker's IsFatal or RemainingHealth (prevents all one-shots!).
                float inflicted = Math.Max(1f, packet.Damage);
                float newHp = Math.Max(0f, main.Health - inflicted);
                bool isLocalFatal = newHp <= 0.5f;
                int appliedDmg = isLocalFatal
                    ? (int)Math.Ceiling(main.Health + 15f)
                    : Math.Max(1, Math.Min((int)Math.Round(inflicted), Math.Max(1, (int)Math.Floor(main.Health) - 1)));

                var blow = new Blow(attacker?.Index ?? main.Index)
                {
                    DamageType = DamageTypes.Cut,
                    StrikeType = strikeType,
                    AttackType = AgentAttackType.Standard,
                    InflictedDamage = appliedDmg,
                    DamageCalculated = true,
                    BaseMagnitude = Math.Max(25f, inflicted),
                    DefenderStunPeriod = 0.32f,
                    VictimBodyPart = bodyPart,
                    GlobalPosition = main.Position + new Vec3(0f, 0f, 1.35f),
                    Direction = swingDir,
                    SwingDirection = swingDir
                };
                var col = BuildVisualHitCollisionData(main, swingDir, bodyPart, strikeType, DamageTypes.Cut);
                main.RegisterBlow(blow, in col);

                if (isLocalFatal)
                {
                    int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
                    _localDeadPulseUntil = _missionElapsed + 3.5f;
                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = myPeerId,
                        HitTargetKind = 0,
                        IsFatal = true,
                        IsAuthoritativeKill = true
                    }, DeliveryMethod.ReliableOrdered);
                }
            }
            catch
            {
            }
            finally
            {
                _applyingNetworkBlow = false;
            }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
            try
            {
                if (!ModContext.IsClientConnected || ModContext.IsServerRunning || affectedAgent == null) return;
                int myPeerId = ModContext.Client?.LocalPeerId ?? -1;
                if (myPeerId <= 0) return;

                if (affectedAgent == _localMainAgentRef || affectedAgent.IsMainAgent)
                {
                    _localDeadPulseUntil = _missionElapsed + 3.5f;
                    SendLocalAgentState(forceReliable: true);
                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = myPeerId,
                        HitTargetKind = 0,
                        IsFatal = true,
                        IsAuthoritativeKill = true
                    }, DeliveryMethod.ReliableOrdered);
                }
                else if (_localMainAgentRef != null && affectedAgent == _localMainAgentRef.MountAgent)
                {
                    SendLocalAgentState(forceReliable: true);
                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = myPeerId,
                        HitTargetKind = 1,
                        IsFatal = true,
                        IsAuthoritativeKill = true
                    }, DeliveryMethod.ReliableOrdered);
                }
                else if (_localTroopIds.TryGetValue(affectedAgent, out ushort localUnitId))
                {
                    _localTroopDeadUntil[localUnitId] = _missionElapsed + 3.5f;
                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = myPeerId,
                        HitTargetKind = 3,
                        TargetTroopUnitId = localUnitId,
                        IsFatal = true,
                        IsAuthoritativeKill = true
                    }, DeliveryMethod.ReliableOrdered);
                }
            }
            catch { }
        }

        private void TrySendShieldBlockDamage(Agent attacker, Agent victim, AttackCollisionData collisionData, float fallbackDamage)
        {
            if (_applyingNetworkBlow || !ModContext.IsClientConnected || ModContext.IsServerRunning) return;
            if (_isPvpReadyGateActive && !_combatUnlocked) return;
            if (attacker == null || victim == null || !attacker.IsMainAgent) return;
            if (!collisionData.AttackBlockedWithShield && !collisionData.MissileBlockedWithWeapon) return;
            if (_missionElapsed - _lastShieldBlockBlowSentTime < 0.06f) return;

            if (!TryGetPeerIdForAgent(victim, out int victimPeerId)) return;

            short shieldDmg = (short)Math.Max(8, (int)Math.Ceiling(fallbackDamage));
            try
            {
                var offSlot = victim.GetOffhandWieldedItemIndex();
                if (offSlot != EquipmentIndex.None && _remoteSyncStates.TryGetValue(victimPeerId, out var rs))
                {
                    short curLocalShieldHp = victim.Equipment[offSlot].HitPoints;
                    if (rs.LastObservedLocalShieldHp > curLocalShieldHp)
                    {
                        shieldDmg = (short)Math.Max(1, rs.LastObservedLocalShieldHp - curLocalShieldHp);
                    }
                    rs.LastObservedLocalShieldHp = curLocalShieldHp;
                }
            }
            catch { }

            _lastShieldBlockBlowSentTime = _missionElapsed;
            ModContext.Client?.Send(new MissionBlowSyncPacket
            {
                AttackerPeerId = ModContext.Client?.LocalPeerId ?? 1001,
                VictimPeerId = victimPeerId,
                Damage = 0f,
                RemainingHealth = victim.Health,
                IsFatal = false,
                IsBlocked = true,
                HitTargetKind = 2,
                ShieldDamage = shieldDmg,
                IsAuthoritativeKill = false
            }, DeliveryMethod.ReliableOrdered);
        }

        public override void OnMeleeHit(Agent attacker, Agent victim, bool isCanceled, AttackCollisionData collisionData)
        {
            base.OnMeleeHit(attacker, victim, isCanceled, collisionData);
            try
            {
                if (!isCanceled && collisionData.AttackBlockedWithShield)
                {
                    TrySendShieldBlockDamage(attacker, victim, collisionData, Math.Max(12f, collisionData.ChargeVelocity * 2f + 12f));
                }
            }
            catch { }
        }

        public override void OnMissileHit(Agent attacker, Agent victim, bool isCanceled, AttackCollisionData collisionData)
        {
            base.OnMissileHit(attacker, victim, isCanceled, collisionData);
            try
            {
                if (!isCanceled && (collisionData.AttackBlockedWithShield || collisionData.MissileBlockedWithWeapon))
                {
                    TrySendShieldBlockDamage(attacker, victim, collisionData, Math.Max(10f, collisionData.MissileTotalDamage));
                }
            }
            catch { }
        }

        public override void OnRegisterBlow(Agent attacker, Agent victim, TaleWorlds.Engine.WeakGameEntity realHitEntity, Blow b, ref AttackCollisionData collisionData, in MissionWeapon attackerWeapon)
        {
            base.OnRegisterBlow(attacker, victim, realHitEntity, b, ref collisionData, in attackerWeapon);

            try
            {
                if (_applyingNetworkBlow || !ModContext.IsClientConnected || ModContext.IsServerRunning) return;
                if (_isPvpReadyGateActive && !_combatUnlocked) return;
                if (attacker == null || victim == null) return;

                int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
                bool attackerIsLocalHero = attacker.IsMainAgent;
                bool attackerIsLocalTeam = attackerIsLocalHero || (Mission?.PlayerTeam != null && attacker.Team == Mission.PlayerTeam);
                if (!attackerIsLocalTeam) return;

                // 1) Shield block on remote player hero
                if (attackerIsLocalHero && (collisionData.AttackBlockedWithShield || collisionData.MissileBlockedWithWeapon) &&
                    TryGetPeerIdForAgent(victim, out int blockedPeerId))
                {
                    TrySendShieldBlockDamage(attacker, victim, collisionData, b.InflictedDamage > 0 ? b.InflictedDamage : b.BaseMagnitude * 0.6f);
                    return;
                }

                string atkActionName = string.Empty;
                try
                {
                    var atkAct = attacker.GetCurrentAction(1);
                    if (atkAct.Index >= 0 && atkAct != ActionIndexCache.act_none)
                    {
                        atkActionName = atkAct.GetName() ?? string.Empty;
                    }
                }
                catch { }

                // 2) Hit on remote player's mount
                if (victim.IsMount && victim.RiderAgent != null && TryGetPeerIdForAgent(victim.RiderAgent, out int riderPeerId))
                {
                    if (b.InflictedDamage <= 0) return;
                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = riderPeerId,
                        Damage = b.InflictedDamage,
                        RemainingHealth = Math.Max(1f, victim.Health),
                        IsFatal = false,
                        IsBlocked = false,
                        HitTargetKind = 1,
                        IsAuthoritativeKill = false,
                        AttackerActionName = atkActionName,
                        VictimBodyPart = (byte)b.VictimBodyPart,
                        StrikeType = (byte)b.StrikeType
                    }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                // 3) Hit on remote player hero (attacker NEVER sends IsFatal — victim is 100% authoritative for HP/KO!)
                if (TryGetPeerIdForAgent(victim, out int victimPeerId))
                {
                    if (b.InflictedDamage <= 0) return;

                    // Hold remote puppet's native hit-receiving flinch animation so ApplyRemoteAgentStates doesn't clear it with act_none!
                    if (!_remoteSyncStates.TryGetValue(victimPeerId, out var victimSync))
                    {
                        victimSync = new RemoteAgentSyncState();
                        _remoteSyncStates[victimPeerId] = victimSync;
                    }
                    victimSync.LocalHitReactHoldUntil = _missionElapsed + 0.38f;
                    try
                    {
                        if (!victim.IsInBeingStruckAction)
                        {
                            var stagger = ActionIndexCache.Create("act_stagger_backward");
                            if (stagger.Index >= 0)
                            {
                                victim.SetActionChannel(1, in stagger, ignorePriority: true, blendInPeriod: 0.05f);
                                victimSync.LastUpperAction = "act_stagger_backward";
                            }
                        }
                    }
                    catch { }

                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = victimPeerId,
                        Damage = b.InflictedDamage,
                        RemainingHealth = Math.Max(1f, victim.Health),
                        IsFatal = false,
                        IsBlocked = false,
                        HitTargetKind = 0,
                        IsAuthoritativeKill = false,
                        AttackerActionName = atkActionName,
                        VictimBodyPart = (byte)b.VictimBodyPart,
                        StrikeType = (byte)b.StrikeType
                    }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                // 4) Hit on a synced enemy non-hero troop -> route damage to troop's owning client
                int oppPeerId = PendingOpponentPeerId > 0 ? PendingOpponentPeerId : _cachedPvpOpponentPeerId;
                if (oppPeerId > 0 && b.InflictedDamage > 0 && _remoteTroopIds.TryGetValue(victim, out ushort remoteTroopUnitId))
                {
                    ModContext.Client?.Send(new MissionBlowSyncPacket
                    {
                        AttackerPeerId = myPeerId,
                        VictimPeerId = oppPeerId,
                        Damage = b.InflictedDamage,
                        RemainingHealth = Math.Max(1f, victim.Health),
                        IsFatal = false,
                        IsBlocked = collisionData.AttackBlockedWithShield,
                        HitTargetKind = 3,
                        TargetTroopUnitId = remoteTroopUnitId,
                        IsAuthoritativeKill = false
                    }, DeliveryMethod.ReliableOrdered);
                }
            }
            catch { }
        }

        private bool TryGetPeerIdForAgent(Agent agent, out int peerId)
        {
            peerId = 0;
            foreach (var kvp in _remotePlayerAgents)
            {
                if (kvp.Value == agent)
                {
                    peerId = kvp.Key;
                    return true;
                }
            }

            string? id = TryGetCharacterOrHeroStringId(agent, out bool isCcHero);
            if (isCcHero && !string.IsNullOrEmpty(id) && id!.StartsWith("cc_hero_", StringComparison.Ordinal))
            {
                return int.TryParse(id.Substring("cc_hero_".Length), out peerId) && peerId > 0;
            }
            return false;
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (!ModContext.IsClientConnected || ModContext.IsServerRunning || Mission == null) return;

            if (_forceEndMissionRequested)
            {
                try { Mission.EndMission(); } catch { }
                return;
            }

            _missionElapsed += dt;

            if (_isPvpReadyGateActive && !_combatUnlocked)
            {
                // Allow native SetupTeams / SpawnAgents to finish before locking AI tick
                if (_missionElapsed > 0.25f && Agent.Main != null && !Mission.PauseAITick)
                {
                    Mission.PauseAITick = true;
                }

                // Keep all spawned agents visible even if HideAgentsOfSide(0) hid Defender during attacker setup,
                // and emit one diagnostic snapshot once spawn & binding finish.
                if (_missionElapsed > 0.35f && Agent.Main != null && !_loggedPvpMissionDiagnostic)
                {
                    EnsureAllAgentsVisible();
                    LogPvpTeamDiagnostic("PreReadySpawnCheck");
                    _loggedPvpMissionDiagnostic = true;
                }

                if (!Mission.IsDeploymentFinished || Mission.Mode == MissionMode.Deployment)
                {
                    _sawDeploymentPhase = true;
                }

                if (!_localReadySent)
                {
                    bool f6Pressed = !_chatModalOpen &&
                                     (Input.IsKeyPressed(InputKey.F6) ||
                                      (Mission.InputManager != null && Mission.InputManager.IsKeyPressed(InputKey.F6)));
                    if (f6Pressed)
                    {
                        TriggerLocalPvpReady();
                    }
                }
            }
            else if (_combatUnlocked)
            {
                if (Mission.PauseAITick)
                {
                    Mission.PauseAITick = false;
                }

                _visibilityCheckTimer += dt;
                if (!_postUnlockVisibilityApplied || _visibilityCheckTimer >= 1.0f)
                {
                    _visibilityCheckTimer = 0f;
                    _postUnlockVisibilityApplied = true;
                    EnsureAllAgentsVisible();
                }
            }

            if (!_bannerShown && Agent.Main != null)
            {
                _bannerShown = true;
                ScanAndBindRemoteAgents();
                string opponentLabel = string.IsNullOrEmpty(PendingOpponentName) ? Loc.T("Rakip", "Opponent") : PendingOpponentName;
                string controlsHint = _isPvpReadyGateActive
                    ? Loc.F("'{0}' ile savaş alanına girdiniz! Birliklerinizi yerleştirip HAZIRIM butonuna basın. [T] Sohbet | [Y] Hızlı Mesaj",
                            "Entered battle with '{0}'! Deploy your troops and press READY to begin. [T] Chat | [Y] Quick Comms",
                            opponentLabel)
                    : (_remotePlayerAgents.Count > 0 || PendingOpponentPeerId > 0
                        ? Loc.F("'{0}' ile savaş başladı! [T] Sohbet | [Y] Hızlı Mesaj",
                                "Battle with '{0}' has begun! [T] Chat | [Y] Quick Comms",
                                opponentLabel)
                        : Loc.T("Savaş Sohbeti: [T] Mesaj Yaz | [Y] Hızlı Mesaj",
                                "Battle Chat: [T] Type Chat | [Y] Quick Comms"));
                InformationManager.DisplayMessage(new InformationMessage(controlsHint, Colors.Cyan));
            }

            if (Agent.Main != null)
            {
                _localMainAgentRef = Agent.Main;
            }

            HandleBattleChatHotkeys();
            ScanAndBindRemoteAgents();

            bool isPvpMission = PendingOpponentPeerId > 0 || _cachedPvpOpponentPeerId > 0 || _remotePlayerAgents.Count > 0;
            if (isPvpMission)
            {
                _troopReindexAccumulator += dt;
                if (_troopReindexAccumulator >= 0.50f)
                {
                    _troopReindexAccumulator = 0f;
                    RefreshDeterministicTroopIndices();
                }
            }

            bool hasImmediateTransition = HasImmediateLocalHeroTransition();
            _sendAccumulator += dt;
            if (hasImmediateTransition || _sendAccumulator >= SEND_INTERVAL)
            {
                _sendAccumulator = 0f;
                SendLocalAgentState(forceReliable: hasImmediateTransition);
            }

            ApplyRemoteAgentStates(dt);

            if (isPvpMission && _combatUnlocked)
            {
                _troopSendAccumulator += dt;
                if (_troopSendAccumulator >= TROOP_SEND_INTERVAL)
                {
                    _troopSendAccumulator = 0f;
                    SendLocalTroopsState();
                }
                ApplyRemoteTroopsStates(dt);
            }

            _navalBridge.Tick(Mission, dt, ModContext.Client?.LocalPeerId ?? 1001, _latestShipStates, _remotePlayerAgents);
        }

        private bool HasImmediateLocalHeroTransition()
        {
            var main = Agent.Main ?? _localMainAgentRef;
            if (main == null) return false;

            byte lifeState = !main.IsActive()
                ? (main.State == AgentState.Killed ? (byte)2 : (byte)1)
                : (byte)0;
            if (lifeState != _lastSentLifeState) return true;
            if (lifeState != 0) return false;

            try
            {
                var upperAction = main.GetCurrentAction(1);
                string upperName = upperAction.Index >= 0 && upperAction != ActionIndexCache.act_none
                    ? (upperAction.GetName() ?? string.Empty)
                    : string.Empty;
                if (!string.Equals(upperName, _lastSentUpperAction, StringComparison.Ordinal)) return true;

                bool hasActiveMount = main.HasMount && main.MountAgent != null && main.MountAgent.IsActive();
                if (hasActiveMount != _lastSentHasMount) return true;

                var mh = main.GetPrimaryWieldedItemIndex();
                sbyte mainSlot = mh == EquipmentIndex.None ? (sbyte)-1 : (sbyte)mh;
                var oh = main.GetOffhandWieldedItemIndex();
                sbyte offSlot = oh == EquipmentIndex.None ? (sbyte)-1 : (sbyte)oh;
                if (mainSlot != _lastSentMainSlot || offSlot != _lastSentOffSlot) return true;
            }
            catch { }

            return false;
        }

        private void SendLocalAgentState(bool forceReliable = false)
        {
            var main = Agent.Main ?? _localMainAgentRef;
            if (main == null) return;

            bool isActive = main.IsActive();
            byte lifeState = isActive
                ? (byte)0
                : (main.State == AgentState.Killed ? (byte)2 : (byte)1);

            if (!isActive)
            {
                if (_localDeadPulseUntil < 0f)
                {
                    _localDeadPulseUntil = _missionElapsed + 3.5f;
                }
                if (_missionElapsed > _localDeadPulseUntil && lifeState == _lastSentLifeState)
                {
                    return;
                }
            }

            var pos = main.Position;
            if (isActive &&
                (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) ||
                 (Math.Abs(pos.x) < 0.01f && Math.Abs(pos.y) < 0.01f && Math.Abs(pos.z) < 0.01f)))
            {
                return;
            }

            var mount = main.HasMount ? main.MountAgent : null;
            bool hasActiveMount = isActive && mount != null && mount.IsActive();
            var vel = hasActiveMount ? mount!.Velocity : (isActive ? main.Velocity : Vec3.Zero);
            if (isActive && vel.LengthSquared < 0.0001f)
            {
                try { vel = main.GetRealGlobalVelocity(); } catch { }
            }

            var look = main.LookDirection;
            var mountLook = hasActiveMount ? mount!.LookDirection : Vec3.Zero;
            var moveVec = isActive ? main.MovementInputVector : Vec2.Zero;

            string lowerName = string.Empty;
            float lowerProgress = 0f;
            string upperName = string.Empty;
            float upperProgress = 0f;
            int upperActionIdx = -1;
            byte combatDir = 0;
            if (isActive)
            {
                var lowerAction = main.GetCurrentAction(0);
                lowerProgress = lowerAction.Index >= 0 ? main.GetCurrentActionProgress(0) : 0f;
                lowerName = lowerAction.Index >= 0 && lowerAction != ActionIndexCache.act_none
                    ? (lowerAction.GetName() ?? string.Empty)
                    : string.Empty;

                var upperAction = main.GetCurrentAction(1);
                upperActionIdx = upperAction.Index;
                upperProgress = upperAction.Index >= 0 ? main.GetCurrentActionProgress(1) : 0f;
                upperName = upperAction.Index >= 0 && upperAction != ActionIndexCache.act_none
                    ? (upperAction.GetName() ?? string.Empty)
                    : string.Empty;
                combatDir = (byte)main.GetCurrentActionDirection(1);
            }

            string mountActionName = string.Empty;
            float mountProgress = 0f;
            float mountHealth = -1f;
            float mountMaxHealth = 100f;
            if (mount != null)
            {
                mountMaxHealth = mount.HealthLimit > 1f ? mount.HealthLimit : 100f;
                mountHealth = mount.IsActive() ? mount.Health : 0f;
                if (hasActiveMount)
                {
                    var mountAction = mount.GetCurrentAction(0);
                    if (mountAction.Index >= 0 && mountAction != ActionIndexCache.act_none)
                    {
                        mountActionName = mountAction.GetName() ?? string.Empty;
                        mountProgress = mount.GetCurrentActionProgress(0);
                    }
                }
            }

            sbyte mainHandSlot = -1;
            sbyte offHandSlot = -1;
            short shieldHp = -1;
            short shieldMaxHp = 0;
            if (isActive)
            {
                try
                {
                    var mh = main.GetPrimaryWieldedItemIndex();
                    mainHandSlot = mh == EquipmentIndex.None ? (sbyte)-1 : (sbyte)mh;
                    var oh = main.GetOffhandWieldedItemIndex();
                    offHandSlot = oh == EquipmentIndex.None ? (sbyte)-1 : (sbyte)oh;
                    if (oh != EquipmentIndex.None)
                    {
                        var offWeapon = main.Equipment[oh];
                        if (!offWeapon.IsEmpty && offWeapon.CurrentUsageItem != null && offWeapon.CurrentUsageItem.IsShield)
                        {
                            shieldHp = offWeapon.HitPoints;
                            shieldMaxHp = offWeapon.ModifiedMaxHitPoints;
                        }
                    }
                }
                catch { }
            }

            byte stateFlags = 8; // bit3 = ExtendedAnimPayload
            if (hasActiveMount) stateFlags |= 1;
            if (isActive && main.CrouchMode) stateFlags |= 2;
            if (isActive && main.WalkMode) stateFlags |= 4;

            bool lifeChanged = lifeState != _lastSentLifeState;
            bool actionOrGearChanged =
                !string.Equals(upperName, _lastSentUpperAction, StringComparison.Ordinal) ||
                mainHandSlot != _lastSentMainSlot ||
                offHandSlot != _lastSentOffSlot ||
                hasActiveMount != _lastSentHasMount;

            _lastSentLifeState = lifeState;
            _lastSentUpperAction = upperName;
            _lastSentMainSlot = mainHandSlot;
            _lastSentOffSlot = offHandSlot;
            _lastSentHasMount = hasActiveMount;

            var packet = new MissionAgentStatePacket
            {
                PeerId = ModContext.Client?.LocalPeerId ?? 1001,
                HeroId = main.Character?.StringId ?? string.Empty,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                LookDirX = look.x,
                LookDirY = look.y,
                LookDirZ = look.z,
                MoveX = moveVec.X,
                MoveY = moveVec.Y,
                Health = isActive ? main.Health : 0f,
                MaxHealth = main.HealthLimit > 1f ? main.HealthLimit : 100f,
                ActionIndex = upperActionIdx,
                ActionName = upperName,
                MovementFlags = isActive ? (int)main.MovementFlags : 0,
                CombatDirection = combatDir,
                VelX = vel.x,
                VelY = vel.y,
                VelZ = vel.z,
                MountLookX = mountLook.x,
                MountLookY = mountLook.y,
                LowerActionName = lowerName,
                LowerActionProgress = lowerProgress,
                UpperActionProgress = upperProgress,
                MountActionName = mountActionName,
                MountActionProgress = mountProgress,
                MainHandSlot = mainHandSlot,
                OffHandSlot = offHandSlot,
                StateFlags = stateFlags,
                MountHealth = mountHealth,
                MountMaxHealth = mountMaxHealth,
                ShieldHitPoints = shieldHp,
                ShieldMaxHitPoints = shieldMaxHp,
                AgentLifeState = lifeState
            };

            DeliveryMethod method = lifeChanged
                ? DeliveryMethod.ReliableOrdered
                : ((forceReliable || actionOrGearChanged) ? DeliveryMethod.ReliableSequenced : DeliveryMethod.Unreliable);

            ModContext.Client?.Send(packet, method);
        }

        private void ApplyRemoteAgentStates(float dt)
        {
            foreach (var kvp in _latestStates)
            {
                if (!_remotePlayerAgents.TryGetValue(kvp.Key, out var agent) || agent == null)
                {
                    continue;
                }

                var st = kvp.Value;
                if (!_remoteSyncStates.TryGetValue(kvp.Key, out var syncState))
                {
                    syncState = new RemoteAgentSyncState();
                    _remoteSyncStates[kvp.Key] = syncState;
                }

                // 1) Authoritative Knockout / Death sync FIRST — ensures nobody stays standing while knocked out on the other screen!
                if (st.AgentLifeState != 0 || st.Health <= 0.1f)
                {
                    if (agent.IsActive() && !syncState.KnockoutApplied)
                    {
                        syncState.KnockoutApplied = true;
                        ForceKnockoutOrKillAgent(agent, isKilled: st.AgentLifeState == 2);
                    }
                    continue;
                }

                if (!agent.IsActive())
                {
                    continue;
                }

                StripRemoteAgentAi(agent);

                if (float.IsNaN(st.PosX) || float.IsNaN(st.PosY) || float.IsNaN(st.PosZ) ||
                    float.IsInfinity(st.PosX) || float.IsInfinity(st.PosY) || float.IsInfinity(st.PosZ) ||
                    (Math.Abs(st.PosX) < 0.01f && Math.Abs(st.PosY) < 0.01f && Math.Abs(st.PosZ) < 0.01f))
                {
                    continue;
                }

                if (_combatUnlocked || Mission.IsDeploymentFinished)
                {
                    try
                    {
                        agent.SetRenderCheckEnabled(true);
                        agent.AgentVisuals?.SetVisible(true);
                        if (agent.HasMount && agent.MountAgent != null && agent.MountAgent.IsActive())
                        {
                            agent.MountAgent.SetRenderCheckEnabled(true);
                            agent.MountAgent.AgentVisuals?.SetVisible(true);
                        }
                    }
                    catch { }
                }

                var rawTargetPos = new Vec3(st.PosX, st.PosY, st.PosZ);
                var curPos = agent.Position;

                if (!ReferenceEquals(syncState.LastPacketRef, st))
                {
                    float packetDt = syncState.HasInitializedTransform
                        ? Math.Max(0.015f, _missionElapsed - syncState.LastPacketReceivedTime)
                        : SEND_INTERVAL;
                    float reportedVelSpeed = new Vec2(st.VelX, st.VelY).Length;
                    float derivedSpeed = syncState.HasInitializedTransform
                        ? new Vec2(rawTargetPos.X - syncState.LastRawTargetPos.X, rawTargetPos.Y - syncState.LastRawTargetPos.Y).Length / packetDt
                        : 0f;
                    float measuredSpeed = reportedVelSpeed > 0.05f ? reportedVelSpeed : derivedSpeed;
                    syncState.SmoothedSpeed = syncState.HasInitializedTransform
                        ? (syncState.SmoothedSpeed * 0.45f + measuredSpeed * 0.55f)
                        : measuredSpeed;

                    syncState.LastPacketRef = st;
                    syncState.LastPacketReceivedTime = _missionElapsed;
                    syncState.LastRawTargetPos = rawTargetPos;
                }

                float rawAge = Math.Max(0f, _missionElapsed - syncState.LastPacketReceivedTime);

                // 2) Authoritative Hero HP, Mount HP & Shield HP mirroring + Immortal lock until owner confirms KO
                try
                {
                    if (rawAge < 10.0f)
                    {
                        if (agent.CurrentMortalityState != Agent.MortalityState.Immortal)
                        {
                            agent.SetMortalityState(Agent.MortalityState.Immortal);
                        }
                        if (st.MaxHealth > 1f && Math.Abs(agent.HealthLimit - st.MaxHealth) > 0.5f)
                        {
                            agent.BaseHealthLimit = st.MaxHealth;
                            agent.HealthLimit = st.MaxHealth;
                        }
                        float targetHp = Math.Max(1f, Math.Min(agent.HealthLimit, st.Health));
                        if (Math.Abs(agent.Health - targetHp) > 0.5f)
                        {
                            agent.Health = targetHp;
                        }
                    }
                    else if (agent.CurrentMortalityState == Agent.MortalityState.Immortal)
                    {
                        agent.SetMortalityState(Agent.MortalityState.Mortal);
                    }

                    // Mount HP & KO sync
                    var mount = agent.HasMount ? agent.MountAgent : null;
                    if (mount != null && mount.IsActive() && st.MountHealth >= 0f)
                    {
                        if (st.MountHealth <= 0.1f)
                        {
                            if (!syncState.MountKnockoutApplied)
                            {
                                syncState.MountKnockoutApplied = true;
                                ForceKnockoutOrKillAgent(mount, isKilled: true);
                            }
                        }
                        else if (rawAge < 10.0f)
                        {
                            if (mount.CurrentMortalityState != Agent.MortalityState.Immortal)
                            {
                                mount.SetMortalityState(Agent.MortalityState.Immortal);
                            }
                            if (st.MountMaxHealth > 1f && Math.Abs(mount.HealthLimit - st.MountMaxHealth) > 0.5f)
                            {
                                mount.BaseHealthLimit = st.MountMaxHealth;
                                mount.HealthLimit = st.MountMaxHealth;
                            }
                            float targetMountHp = Math.Max(1f, Math.Min(mount.HealthLimit, st.MountHealth));
                            if (Math.Abs(mount.Health - targetMountHp) > 0.5f)
                            {
                                mount.Health = targetMountHp;
                            }
                        }
                    }

                    // Shield durability sync
                    if (st.ShieldHitPoints >= 0)
                    {
                        var offSlot = agent.GetOffhandWieldedItemIndex();
                        if (offSlot != EquipmentIndex.None)
                        {
                            var offWeapon = agent.Equipment[offSlot];
                            if (!offWeapon.IsEmpty && offWeapon.CurrentUsageItem != null && offWeapon.CurrentUsageItem.IsShield)
                            {
                                if (offWeapon.HitPoints != st.ShieldHitPoints && _missionElapsed - syncState.LastShieldSyncTime >= 0.08f)
                                {
                                    syncState.LastShieldSyncTime = _missionElapsed;
                                    agent.ChangeWeaponHitPoints(offSlot, st.ShieldHitPoints);
                                }
                                syncState.LastObservedLocalShieldHp = st.ShieldHitPoints;
                            }
                        }
                    }
                }
                catch { }

                if (rawAge > 0.18f)
                {
                    syncState.SmoothedSpeed *= (float)Math.Exp(-8.0 * Math.Max(0.001f, dt));
                }
                float packetAge = rawAge > 0.18f ? 0f : Math.Min(0.10f, rawAge);
                Vec3 predictedTarget = rawTargetPos + new Vec3(st.VelX, st.VelY, st.VelZ) * packetAge;
                float dist = (predictedTarget - curPos).Length;

                if (!_loggedFirstRemoteStateApplied)
                {
                    _loggedFirstRemoteStateApplied = true;
                    try
                    {
                        TaleWorlds.Library.Debug.Print(
                            $"[Calradia-Connected PvP] FirstRemoteStateApplied: PeerId={kvp.Key}, Pos=({st.PosX:F1},{st.PosY:F1},{st.PosZ:F1}), HP={st.Health:F0}/{st.MaxHealth:F0}, MountHP={st.MountHealth:F0}, ShieldHP={st.ShieldHitPoints}, Dist={dist:F2}");
                    }
                    catch { }
                }

                if (!syncState.HasInitializedTransform || dist > 4.5f)
                {
                    syncState.HasInitializedTransform = true;
                    try { agent.TeleportToPosition(rawTargetPos); } catch { }
                }
                else if (dist > 0.02f || syncState.SmoothedSpeed > 0.15f)
                {
                    float posAlpha = 1f - (float)Math.Exp(-16.0 * Math.Max(0.001f, dt));
                    Vec3 blended = Vec3.Lerp(curPos, predictedTarget, posAlpha);
                    try { agent.TeleportToPosition(blended); } catch { }
                }

                var rawLookDir = new Vec3(st.LookDirX, st.LookDirY, st.LookDirZ);
                if (rawLookDir.LengthSquared > 0.01f)
                {
                    rawLookDir.Normalize();
                    if (syncState.SmoothedLookDir.LengthSquared <= 0.01f)
                    {
                        syncState.SmoothedLookDir = rawLookDir;
                    }
                    else
                    {
                        float lookAlpha = 1f - (float)Math.Exp(-18.0 * Math.Max(0.001f, dt));
                        Vec3 blendedLook = Vec3.Lerp(syncState.SmoothedLookDir, rawLookDir, lookAlpha);
                        if (blendedLook.LengthSquared > 0.01f)
                        {
                            blendedLook.Normalize();
                            syncState.SmoothedLookDir = blendedLook;
                        }
                    }
                    agent.LookDirection = syncState.SmoothedLookDir;
                }

                ApplyRemoteMovementAndMountSync(agent, curPos, predictedTarget, syncState.SmoothedLookDir, st, syncState, dt);

                if ((st.StateFlags & 8) != 0)
                {
                    SyncWieldedWeapons(agent, st.MainHandSlot, st.OffHandSlot, syncState);

                    if (agent.HasMount && agent.MountAgent != null && agent.MountAgent.IsActive())
                    {
                        ApplyActionChannelSynced(agent, 0, st.LowerActionName, st.LowerActionProgress, ref syncState.LastLowerAction, ref syncState.LastLowerProgress, blendInPeriod: 0.14f);
                    }
                    else
                    {
                        string footLowerAction = ResolveHumanLocomotionActionName(agent, st, syncState.SmoothedSpeed, syncState.SmoothedLookDir, out float humanSpeedScale);
                        float footProg = !string.IsNullOrEmpty(st.LowerActionName) ? st.LowerActionProgress : 0f;
                        ApplyActionChannelSynced(agent, 0, footLowerAction, footProg, ref syncState.LastLowerAction, ref syncState.LastLowerProgress, blendInPeriod: 0.14f, actionSpeed: humanSpeedScale);
                    }
                }

                // Preserve local hit-receiving stagger/flinch or freshly triggered blow swing if incoming state has empty upper action!
                bool holdUpperAnim = string.IsNullOrEmpty(st.ActionName) &&
                                     (_missionElapsed < syncState.LocalHitReactHoldUntil ||
                                      _missionElapsed < syncState.ForcedAttackAnimHoldUntil ||
                                      agent.IsInBeingStruckAction);
                if (!holdUpperAnim)
                {
                    ApplyActionChannelSynced(agent, 1, st.ActionName, st.UpperActionProgress, ref syncState.LastUpperAction, ref syncState.LastUpperProgress, blendInPeriod: 0.05f);
                }
                _lastAppliedActions[kvp.Key] = syncState.LastUpperAction;
            }
        }

        private void RefreshDeterministicTroopIndices()
        {
            if (Mission == null) return;
            try
            {
                IndexTeamTroops(Mission.PlayerTeam, _localTroopUnits, _localTroopIds);
                IndexTeamTroops(Mission.PlayerEnemyTeam, _remoteTroopUnits, _remoteTroopIds);
            }
            catch { }
        }

        private void IndexTeamTroops(Team? team, Dictionary<ushort, Agent> idToAgent, Dictionary<Agent, ushort> agentToId)
        {
            if (team == null || Mission == null) return;

            var unindexed = new List<Agent>();
            foreach (var ag in Mission.Agents)
            {
                if (ag == null || !ag.IsHuman || ag.IsMainAgent || ag.Team != team) continue;
                if (TryGetPeerIdForAgent(ag, out _)) continue;
                if (agentToId.ContainsKey(ag)) continue;
                unindexed.Add(ag);
            }

            if (unindexed.Count == 0) return;

            unindexed.Sort((a, b) =>
            {
                string idA = a.Character?.StringId ?? string.Empty;
                string idB = b.Character?.StringId ?? string.Empty;
                int cmp = string.CompareOrdinal(idA, idB);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });

            ushort nextId = (ushort)(idToAgent.Count + 1);
            for (int i = 0; i < unindexed.Count && nextId < ushort.MaxValue; i++)
            {
                var ag = unindexed[i];
                idToAgent[nextId] = ag;
                agentToId[ag] = nextId;
                nextId++;
            }
        }

        private void SendLocalTroopsState()
        {
            if (Mission == null || _localTroopUnits.Count == 0) return;

            int myPeerId = ModContext.Client?.LocalPeerId ?? 1001;
            byte teamSide = Mission.PlayerTeam != null && Mission.PlayerTeam.Side == BattleSideEnum.Attacker ? (byte)1 : (byte)0;

            var batch = new List<TroopUnitSnapshot>(TROOP_CHUNK_SIZE);
            foreach (var kvp in _localTroopUnits)
            {
                ushort unitId = kvp.Key;
                var ag = kvp.Value;
                if (ag == null) continue;

                bool alive = ag.IsActive();
                if (!alive)
                {
                    if (!_localTroopDeadUntil.TryGetValue(unitId, out float until))
                    {
                        until = _missionElapsed + 3.5f;
                        _localTroopDeadUntil[unitId] = until;
                    }
                    if (_missionElapsed > until) continue;
                }

                var pos = ag.Position;
                var look = ag.LookDirection;
                float yaw = (float)Math.Atan2(look.y, look.x);
                if (yaw < 0f) yaw += (float)(Math.PI * 2.0);
                byte yawByte = (byte)(Math.Min(255, Math.Max(0, (int)(yaw / (Math.PI * 2.0) * 256.0))));

                byte flags = 0;
                if (alive) flags |= 1;
                if (alive && ag.HasMount) flags |= 2;
                if (alive && ag.Velocity.LengthSquared > 0.25f) flags |= 4;
                if (!alive && ag.State == AgentState.Unconscious) flags |= 32;

                batch.Add(new TroopUnitSnapshot
                {
                    UnitId = unitId,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    YawByte = yawByte,
                    Health = alive ? (short)Math.Max(1, Math.Min(short.MaxValue, (int)Math.Ceiling(ag.Health))) : (short)0,
                    MaxHealth = (short)Math.Max(1, Math.Min(short.MaxValue, (int)Math.Ceiling(ag.HealthLimit))),
                    Flags = flags
                });

                if (batch.Count >= TROOP_CHUNK_SIZE)
                {
                    ModContext.Client?.Send(new MissionTroopsSyncPacket
                    {
                        SenderPeerId = myPeerId,
                        TeamSide = teamSide,
                        Units = batch
                    }, DeliveryMethod.Unreliable);
                    batch = new List<TroopUnitSnapshot>(TROOP_CHUNK_SIZE);
                }
            }

            if (batch.Count > 0)
            {
                ModContext.Client?.Send(new MissionTroopsSyncPacket
                {
                    SenderPeerId = myPeerId,
                    TeamSide = teamSide,
                    Units = batch
                }, DeliveryMethod.Unreliable);
            }
        }

        private void ApplyRemoteTroopsStates(float dt)
        {
            if (Mission == null || _remoteTroopUnits.Count == 0) return;

            bool troopStreamFresh = (_missionElapsed - _lastRemoteTroopPacketTime) < 2.5f;
            if (!troopStreamFresh)
            {
                foreach (var kvp in _remoteTroopUnits)
                {
                    var tr = kvp.Value;
                    if (tr != null && tr.IsActive() && tr.CurrentMortalityState == Agent.MortalityState.Immortal)
                    {
                        try
                        {
                            tr.SetMortalityState(Agent.MortalityState.Mortal);
                            tr.DisableScriptedMovement();
                        }
                        catch { }
                    }
                }
                return;
            }

            foreach (var kvp in _latestRemoteTroops)
            {
                if (!_remoteTroopUnits.TryGetValue(kvp.Key, out var unit) || unit == null) continue;
                var snap = kvp.Value;

                bool ownerAlive = (snap.Flags & 1) != 0 && snap.Health > 0;
                if (!ownerAlive)
                {
                    if (unit.IsActive())
                    {
                        bool unconscious = (snap.Flags & 32) != 0;
                        ForceKnockoutOrKillAgent(unit, isKilled: !unconscious);
                    }
                    continue;
                }

                if (!unit.IsActive()) continue;

                try
                {
                    if (unit.CurrentMortalityState != Agent.MortalityState.Immortal)
                    {
                        unit.SetMortalityState(Agent.MortalityState.Immortal);
                    }
                    if (snap.MaxHealth > 1 && Math.Abs(unit.HealthLimit - snap.MaxHealth) > 1f)
                    {
                        unit.BaseHealthLimit = snap.MaxHealth;
                        unit.HealthLimit = snap.MaxHealth;
                    }
                    float desiredHp = Math.Max(1f, Math.Min(unit.HealthLimit, snap.Health));
                    if (Math.Abs(unit.Health - desiredHp) > 1f)
                    {
                        unit.Health = desiredHp;
                    }

                    var targetPos = new Vec3(snap.PosX, snap.PosY, snap.PosZ);
                    if (float.IsNaN(targetPos.x) || float.IsNaN(targetPos.y) || float.IsNaN(targetPos.z)) continue;

                    float dist = (targetPos - unit.Position).Length;
                    if (dist > 4.5f)
                    {
                        unit.DisableScriptedMovement();
                        unit.TeleportToPosition(targetPos);
                    }
                    else if (dist > 1.35f && Mission.Scene != null)
                    {
                        var wp = new TaleWorlds.Engine.WorldPosition(Mission.Scene, targetPos);
                        unit.SetScriptedPosition(ref wp, addHumanLikeDelay: false, Agent.AIScriptedFrameFlags.None);
                    }
                    else
                    {
                        // Inside melee combat radius -> let native AI fight/swing/block with full animations!
                        unit.DisableScriptedMovement();
                    }
                }
                catch { }
            }
        }

        private void HandleBattleChatHotkeys()
        {
            if (_chatModalOpen) return;

            if (Input.IsKeyPressed(InputKey.T))
            {
                OpenBattleChatInput();
            }
            else if (Input.IsKeyPressed(InputKey.Y))
            {
                OpenQuickCommsMenu();
            }
        }

        private void OpenBattleChatInput()
        {
            _chatModalOpen = true;
            InformationManager.ShowTextInquiry(new TextInquiryData(
                titleText: Loc.T("Savaş Alanı Sohbeti", "Battlefield Chat"),
                text: Loc.T("Karşı oyuncuya veya tüm oyunculara mesajınızı yazın:", "Type a message to your opponent or all players:"),
                isAffirmativeOptionShown: true,
                isNegativeOptionShown: true,
                affirmativeText: Loc.T("Gönder", "Send"),
                negativeText: Loc.T("İptal", "Cancel"),
                affirmativeAction: (txt) =>
                {
                    _chatModalOpen = false;
                    SendBattleChat(txt);
                },
                negativeAction: () => { _chatModalOpen = false; }),
                pauseGameActiveState: false);
        }

        private void OpenQuickCommsMenu()
        {
            _chatModalOpen = true;
            var phrases = new List<InquiryElement>
            {
                new InquiryElement(Loc.T("Teslim ol, canını bağışlayayım!", "Surrender and I shall spare your life!"), Loc.T("Teslim ol, canını bağışlayayım!", "Surrender and I shall spare your life!"), null),
                new InquiryElement(Loc.T("Karşıma çık, teke tek dövüşelim!", "Face me in single combat!"), Loc.T("Karşıma çık, teke tek dövüşelim!", "Face me in single combat!"), null),
                new InquiryElement(Loc.T("Güzel hamle! İyi savaşıyorsun!", "Well struck! You fight well!"), Loc.T("Güzel hamle! İyi savaşıyorsun!", "Well struck! You fight well!"), null),
                new InquiryElement(Loc.T("Geri çekilin / Ateşkes!", "Fall back / Ceasefire!"), Loc.T("Geri çekilin / Ateşkes!", "Fall back / Ceasefire!"), null),
                new InquiryElement(Loc.T("Saldırın! Zafere kadar!", "Charge! Fight to victory!"), Loc.T("Saldırın! Zafere kadar!", "Charge! Fight to victory!"), null)
            };

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                titleText: Loc.T("Hızlı Muharebe Mesajı", "Quick Battle Comms"),
                descriptionText: Loc.T("Göndermek istediğiniz mesajı seçin:", "Choose a quick message to send:"),
                inquiryElements: phrases,
                isExitShown: true,
                minSelectableOptionCount: 1,
                maxSelectableOptionCount: 1,
                affirmativeText: Loc.T("Gönder", "Send"),
                negativeText: Loc.T("Kapat", "Close"),
                affirmativeAction: (sel) =>
                {
                    _chatModalOpen = false;
                    if (sel != null && sel.Count > 0 && sel[0].Identifier is string msg)
                    {
                        SendBattleChat(msg);
                    }
                },
                negativeAction: (_) => { _chatModalOpen = false; }),
                pauseGameActiveState: false,
                prioritize: true);
        }

        private static void SendBattleChat(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || !ModContext.IsClientConnected) return;

            string senderName = Agent.Main?.Name?.ToString()
                                ?? ModContext.Client?.LocalPeerId.ToString()
                                ?? Loc.T("Oyuncu", "Player");

            var packet = new ChatMessagePacket
            {
                SenderPeerId = ModContext.Client?.LocalPeerId ?? 1001,
                SenderName = senderName,
                Message = text!.Trim(),
                IsSystem = false,
                Channel = 1
            };

            ModContext.Client?.Send(packet, DeliveryMethod.ReliableOrdered);
            InformationManager.DisplayMessage(new InformationMessage(
                $"[{Loc.T("Savaş", "Battle")} - {senderName}]: {packet.Message}",
                Colors.Cyan));
        }
    }
}
