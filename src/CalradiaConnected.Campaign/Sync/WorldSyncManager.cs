using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using GameCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace CalradiaConnected.Campaign.Sync
{
    public class WorldSyncManager
    {
        private long _currentTick = 0;
        private float _lastSnapshotBroadcastTime = 0f;
        private const float SNAPSHOT_INTERVAL_SEC = 0.10f; // 10 Hz authoritative sync
        private const int MAX_PARTIES_PER_PACKET = 12; // ~550 bytes per UDP packet (strictly below LiteNetLib Unreliable MTU)
        private const int ROLLING_REFRESH_PER_TICK = 36;
        private const int WORLD_RECONCILE_PER_TICK = 18; // Reconciles all ~500 world AI parties every ~2.7s across all clients
        public const string DestroyedSettlementSentinel = "__DESTROYED__";
        public const float AiAreaOfInterestRadius = 68.0f; // Exceeds MaximumSeeingRange (60) for smooth boundary entry
        private const float AiAreaOfInterestRadiusSq = AiAreaOfInterestRadius * AiAreaOfInterestRadius;
        private const float ClientFallbackVisibilityRadius = 45.0f;

        private int _rollingCursor = 0;
        private int _worldReconcileCursor = 0;
        private float _clientConnectedSeconds = 0f;
        private float _clientStaleCheckTimer = 0f;
        private readonly Dictionary<string, Vec2> _lastKnownPositions = new Dictionary<string, Vec2>();
        private readonly Dictionary<string, Vec2> _lastKnownTargets = new Dictionary<string, Vec2>();
        private readonly Dictionary<string, string> _lastKnownSettlements = new Dictionary<string, string>();
        private readonly HashSet<string> _knownActiveServerAiIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _currentTickActiveAiIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _removedAiIdsBuffer = new List<string>(16);
        private readonly Dictionary<string, RemotePlayerMotionState> _remotePlayerMotion = new Dictionary<string, RemotePlayerMotionState>(StringComparer.Ordinal);
        private readonly Dictionary<string, RemoteAiMotionState> _remoteAiMotion = new Dictionary<string, RemoteAiMotionState>(StringComparer.Ordinal);
        private readonly List<string> _staleAiMotionKeysBuffer = new List<string>(32);
        private readonly ConcurrentDictionary<string, int> _requestedMissingAiParties = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private readonly List<Vec2> _playerPositionsBuffer = new List<Vec2>(16);
        private readonly List<PartySnapshotData> _outgoingBuffer = new List<PartySnapshotData>(64);
        private readonly List<PartySnapshotData> _reliableSettlementBuffer = new List<PartySnapshotData>(32);
        private readonly HashSet<string> _includedIdsBuffer = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _staleMotionKeysBuffer = new List<string>(8);
        private float _lastMissingHeroRequestTime = 0f;
        private static readonly ConcurrentDictionary<string, long> _recentlyDisconnectedParties =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> _serverGarrisonedParties =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> _destroyedAiParties =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> _suppressedStaleAiParties =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> _clientLastAiSyncTickMs =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        public static void MarkPartyGarrisoned(string? partyId)
        {
            if (!string.IsNullOrEmpty(partyId)) _serverGarrisonedParties[partyId!] = 1;
        }

        public static void ClearPartyGarrisoned(string? partyId)
        {
            if (!string.IsNullOrEmpty(partyId)) _serverGarrisonedParties.TryRemove(partyId!, out _);
        }

        public static void MarkClientAiPartySynced(string? partyId)
        {
            if (string.IsNullOrEmpty(partyId)) return;
            _clientLastAiSyncTickMs[partyId!] = Environment.TickCount;
            _suppressedStaleAiParties.TryRemove(partyId!, out _);
            _destroyedAiParties.TryRemove(partyId!, out _);
        }

        public static bool IsAiPartyHiddenByServer(string? partyId)
        {
            if (string.IsNullOrEmpty(partyId)) return false;
            return _serverGarrisonedParties.ContainsKey(partyId!)
                || _destroyedAiParties.ContainsKey(partyId!)
                || _suppressedStaleAiParties.ContainsKey(partyId!);
        }

        public static void ClearClientAiSyncState()
        {
            _serverGarrisonedParties.Clear();
            _destroyedAiParties.Clear();
            _suppressedStaleAiParties.Clear();
            _clientLastAiSyncTickMs.Clear();
        }

        public static void ApplyRemoteAiPartyDestroyed(MobileParty? party, string partyId)
        {
            if (!string.IsNullOrEmpty(partyId))
            {
                _destroyedAiParties[partyId] = 1;
                _serverGarrisonedParties.TryRemove(partyId, out _);
                _suppressedStaleAiParties.TryRemove(partyId, out _);
                _clientLastAiSyncTickMs.TryRemove(partyId, out _);
            }

            if (party == null || party.IsMainParty || NetworkObjectRegistry.IsPlayerNetworkParty(party))
            {
                return;
            }

            try
            {
                if (party.CurrentSettlement != null)
                {
                    try { LeaveSettlementAction.ApplyForParty(party); } catch { }
                }
                if (party.MapEvent != null && party.Party != null)
                {
                    try { party.Party.MapEventSide = null; } catch { }
                }
                party.SetMoveModeHold();
                party.IsVisible = false;
                party.IsInspected = false;
                party.Party?.SetVisualAsDirty();
                if (party.IsActive && !party.IsDisbanding)
                {
                    DestroyPartyAction.Apply(null, party);
                }
            }
            catch { }
        }

        public static void MarkPartyDisconnected(string? partyId, int durationMs = 8000)
        {
            if (string.IsNullOrEmpty(partyId)) return;
            _recentlyDisconnectedParties[partyId!] = Environment.TickCount + durationMs;
        }

        public static void ClearPartyDisconnected(string? partyId)
        {
            if (string.IsNullOrEmpty(partyId)) return;
            _recentlyDisconnectedParties.TryRemove(partyId!, out _);
        }

        public static bool IsRecentlyDisconnectedParty(string? partyId)
        {
            if (string.IsNullOrEmpty(partyId)) return false;
            if (!_recentlyDisconnectedParties.TryGetValue(partyId!, out long expiresAt)) return false;
            if ((expiresAt - Environment.TickCount) > 0) return true;
            _recentlyDisconnectedParties.TryRemove(partyId!, out _);
            return false;
        }

        private sealed class RemotePlayerMotionState
        {
            public Vec2 ServerPos;
            public Vec2 TargetPos;
            public float Speed;
            public Vec2 LastFramePos;
            public bool IsOnLand = true;
        }

        private sealed class RemoteAiMotionState
        {
            public Vec2 ServerPos;
            public Vec2 TargetPos;
            public Vec2 Bearing;
            public float Speed;
            public bool IsOnLand = true;
            public int LastUpdateTickMs;
        }

        public event Action? OnMissingRemotePlayerDetected;

        public void OnCampaignTick(float dt)
        {
            if (GameCampaign.Current == null) return;

            if (ModContext.IsServerRunning)
            {
                if ((ModContext.Server?.Sessions?.Count ?? 0) == 0)
                {
                    _lastSnapshotBroadcastTime = 0f;
                    return;
                }

                _lastSnapshotBroadcastTime += dt;
                if (_lastSnapshotBroadcastTime >= SNAPSHOT_INTERVAL_SEC)
                {
                    _lastSnapshotBroadcastTime = 0f;
                    _currentTick++;
                    BroadcastWorldSnapshot();
                }
            }
            else if (ModContext.IsClientConnected)
            {
                _clientConnectedSeconds += dt;
                InterpolateStalledRemotePlayers(dt);
                InterpolateRemoteAiParties(dt);
                _clientStaleCheckTimer += dt;
                if (_clientStaleCheckTimer >= 1.0f)
                {
                    _clientStaleCheckTimer = 0f;
                    ReconcileStaleNearbyAiParties();
                }
            }
            else if (_clientConnectedSeconds > 0f)
            {
                _clientConnectedSeconds = 0f;
                _clientStaleCheckTimer = 0f;
                _remoteAiMotion.Clear();
                _requestedMissingAiParties.Clear();
                ClearClientAiSyncState();
            }
        }

        private void InterpolateRemoteAiParties(float dt)
        {
            if (dt <= 0f || _remoteAiMotion.Count == 0 || GameCampaign.Current == null ||
                !GameCampaign.Current.GameStarted || GameCampaign.Current.SaveHandler?.IsSaving == true)
            {
                return;
            }

            var mainParty = MobileParty.MainParty;
            Vec2 mainPos = mainParty != null ? mainParty.GetPosition2D : Vec2.Invalid;
            float seeCutoffSq = mainParty != null
                ? GetClientVisibilityCutoff(mainParty) * GetClientVisibilityCutoff(mainParty)
                : ClientFallbackVisibilityRadius * ClientFallbackVisibilityRadius;

            int nowMs = Environment.TickCount;
            _staleAiMotionKeysBuffer.Clear();

            foreach (var kvp in _remoteAiMotion)
            {
                var st = kvp.Value;
                int ageMs = unchecked(nowMs - st.LastUpdateTickMs);
                if (ageMs > 6000 || IsAiPartyHiddenByServer(kvp.Key))
                {
                    _staleAiMotionKeysBuffer.Add(kvp.Key);
                    continue;
                }

                var party = NetworkObjectRegistry.Instance.ResolveParty(kvp.Key);
                if (party == null || !party.IsActive || party.IsMainParty || party.CurrentSettlement != null || party.MapEvent != null)
                {
                    _staleAiMotionKeysBuffer.Add(kvp.Key);
                    continue;
                }

                if (mainPos.IsValid && (st.ServerPos - mainPos).LengthSquared > seeCutoffSq * 1.35f)
                {
                    continue;
                }

                // Short bounded dead-reckoning (<= 0.25s) along server bearing/speed so 10 Hz snapshots glide at 60 FPS
                Vec2 desiredPos = st.ServerPos;
                if (st.Speed > 0.05f && ageMs > 0 && ageMs < 250 && st.Bearing.LengthSquared > 0.01f)
                {
                    float campaignDtScale = GameCampaign.Current.TimeControlMode == CampaignTimeControlMode.StoppableFastForward ||
                                            GameCampaign.Current.TimeControlMode == CampaignTimeControlMode.UnstoppableFastForward
                        ? 2.0f
                        : 1.0f;
                    float predSec = Math.Min(0.25f, (ageMs / 1000f) * campaignDtScale);
                    // Bannerlord party.Speed is map units per game-hour (~0.18 game-hours per real second at 1x)
                    Vec2 cand = st.ServerPos + st.Bearing * (st.Speed * 0.16f * predSec);
                    if ((st.TargetPos - st.ServerPos).LengthSquared > (cand - st.ServerPos).LengthSquared)
                    {
                        desiredPos = cand;
                    }
                }

                var curPos = party.GetPosition2D;
                if (!curPos.IsValid || !desiredPos.IsValid) continue;

                Vec2 driftVec = desiredPos - curPos;
                float drift = driftVec.Length;
                if (drift > 2.0f)
                {
                    party.Position = ResolveValidPosition(desiredPos, preferLand: st.IsOnLand);
                }
                else if (drift > 0.04f)
                {
                    float alpha = 1f - (float)Math.Exp(-10.0 * Math.Max(0.001f, dt));
                    Vec2 nextPos = Vec2.Lerp(curPos, desiredPos, alpha);
                    party.Position = ResolveValidPosition(nextPos, preferLand: st.IsOnLand);
                }
            }

            if (_staleAiMotionKeysBuffer.Count > 0)
            {
                for (int i = 0; i < _staleAiMotionKeysBuffer.Count; i++)
                {
                    _remoteAiMotion.Remove(_staleAiMotionKeysBuffer[i]);
                }
                _staleAiMotionKeysBuffer.Clear();
            }
        }

        private void ReconcileStaleNearbyAiParties()
        {
            var campaign = GameCampaign.Current;
            var mainParty = MobileParty.MainParty;
            var allParties = MobileParty.All;
            if (campaign == null || !campaign.GameStarted || campaign.SaveHandler?.IsSaving == true ||
                mainParty == null || allParties == null || _clientConnectedSeconds < 8.0f || _clientLastAiSyncTickMs.IsEmpty)
            {
                return;
            }

            var mainPos = mainParty.GetPosition2D;
            if (!mainPos.IsValid) return;

            const float checkRadiusSq = 60.0f * 60.0f;
            int nowMs = Environment.TickCount;

            for (int i = 0; i < allParties.Count; i++)
            {
                var p = allParties[i];
                if (p == null || !p.IsActive || p.IsMainParty || p.StringId == "cc_server_party") continue;
                if (p.IsGarrison || p.IsMilitia || p.CurrentSettlement != null) continue;
                if (NetworkObjectRegistry.IsPlayerNetworkParty(p)) continue;

                var pPos = p.GetPosition2D;
                if (!pPos.IsValid || (pPos - mainPos).LengthSquared > checkRadiusSq) continue;

                bool hasFreshSync = _clientLastAiSyncTickMs.TryGetValue(p.StringId, out long lastMs) &&
                                    unchecked(nowMs - (int)lastMs) <= 7500;
                if (!hasFreshSync)
                {
                    // Party is sitting inside our spotting radius locally (e.g. frozen at a town gate from initial save),
                    // yet the server hasn't confirmed it exists here in >7.5s -> suppress stale ghost until server updates it.
                    _suppressedStaleAiParties[p.StringId] = 1;
                    p.SetMoveModeHold();
                    if (p.IsVisible || p.IsInspected)
                    {
                        p.IsVisible = false;
                        p.IsInspected = false;
                        p.Party?.SetVisualAsDirty();
                    }
                }
            }
        }

        public void RemoveRemotePlayerState(string? partyId)
        {
            if (string.IsNullOrEmpty(partyId)) return;
            MarkPartyDisconnected(partyId);
            _remotePlayerMotion.Remove(partyId!);
            _lastKnownPositions.Remove(partyId!);
            _lastKnownTargets.Remove(partyId!);
            _lastKnownSettlements.Remove(partyId!);
        }

        public void UpdateRemotePlayerMotionHint(string partyId, Vec2 serverPos, Vec2 targetPos, float speed, bool isOnLand)
        {
            if (string.IsNullOrEmpty(partyId) || IsRecentlyDisconnectedParty(partyId)) return;
            if (!_remotePlayerMotion.TryGetValue(partyId, out var state))
            {
                state = new RemotePlayerMotionState();
                _remotePlayerMotion[partyId] = state;
            }
            if (serverPos.IsValid) state.ServerPos = serverPos;
            if (targetPos.IsValid) state.TargetPos = targetPos;
            if (speed > 0.01f) state.Speed = speed;
            else if ((state.TargetPos - state.ServerPos).LengthSquared <= 0.02f) state.Speed = 0f;
            state.IsOnLand = isOnLand;
        }

        private void InterpolateStalledRemotePlayers(float dt)
        {
            if (dt <= 0f || GameCampaign.Current == null) return;

            float campaignDt = GameCampaign.Current.CampaignDt;
            float effectiveDt = campaignDt > 0.0001f ? campaignDt : Math.Min(dt, 0.05f);

            _staleMotionKeysBuffer.Clear();
            foreach (var kvp in _remotePlayerMotion)
            {
                if (IsRecentlyDisconnectedParty(kvp.Key))
                {
                    _staleMotionKeysBuffer.Add(kvp.Key);
                    continue;
                }

                var party = NetworkObjectRegistry.Instance.ResolveParty(kvp.Key);
                if (party == null || !party.IsActive || party.IsMainParty)
                {
                    _staleMotionKeysBuffer.Add(kvp.Key);
                    continue;
                }

                if (party.CurrentSettlement != null)
                {
                    if (party.IsVisible)
                    {
                        party.IsVisible = false;
                        party.Party?.SetVisualAsDirty();
                    }
                    continue;
                }

                if (!party.IsVisible)
                {
                    party.IsVisible = true;
                    party.IsInspected = true;
                    party.Party?.SetVisualAsDirty();
                }

                var state = kvp.Value;
                var curPos = party.GetPosition2D;
                if (!curPos.IsValid || !state.ServerPos.IsValid) continue;

                float movedThisFrameSq = state.LastFramePos.IsValid ? (curPos - state.LastFramePos).LengthSquared : 0f;
                state.LastFramePos = curPos;

                Vec2 driftVec = state.ServerPos - curPos;
                float drift = driftVec.Length;

                // Only intervene if Bannerlord navmesh movement didn't step this frame while player should move/catch up
                if (drift > 0.25f && movedThisFrameSq < 0.00001f)
                {
                    float speed = state.Speed > 0.2f ? state.Speed : 4.5f;
                    float maxStep = Math.Max(speed * effectiveDt * 1.15f, drift * Math.Min(1f, dt * 6f));
                    float step = Math.Min(drift, maxStep);
                    Vec2 dir = driftVec / drift;
                    Vec2 nextPos = curPos + dir * step;
                    party.Position = new CampaignVec2(nextPos, isOnLand: state.IsOnLand);
                    state.LastFramePos = nextPos;
                }
            }

            if (_staleMotionKeysBuffer.Count > 0)
            {
                for (int i = 0; i < _staleMotionKeysBuffer.Count; i++)
                {
                    _remotePlayerMotion.Remove(_staleMotionKeysBuffer[i]);
                }
                _staleMotionKeysBuffer.Clear();
            }
        }

        private void BroadcastWorldSnapshot()
        {
            var server = ModContext.Server;
            if (server == null || !server.IsRunning || (server.Sessions?.Count ?? 0) == 0) return;

            var allParties = MobileParty.All;
            if (allParties == null || allParties.Count == 0) return;

            _outgoingBuffer.Clear();
            _reliableSettlementBuffer.Clear();
            _includedIdsBuffer.Clear();
            _playerPositionsBuffer.Clear();
            _currentTickActiveAiIds.Clear();

            int totalCount = allParties.Count;

            // Gather active connected player positions for Interest Management (AOI Culling)
            for (int i = 0; i < totalCount; i++)
            {
                var p = allParties[i];
                if (p != null && p.IsActive && !p.IsMainParty && p.StringId != "cc_server_party" &&
                    NetworkObjectRegistry.IsPlayerNetworkParty(p))
                {
                    var pPos = p.GetPosition2D;
                    if (pPos.IsValid)
                    {
                        _playerPositionsBuffer.Add(pPos);
                    }
                }
            }

            bool hasPlayerPositions = _playerPositionsBuffer.Count > 0;

            // 1. Collect all player parties + nearby AI parties within player spotting AOI whose state changed
            for (int i = 0; i < totalCount; i++)
            {
                var party = allParties[i];
                if (party == null || !party.IsActive || party.IsMainParty || party.StringId == "cc_server_party") continue;
                if (party.IsGarrison || party.IsMilitia) continue;

                string partyId = party.StringId;
                var pos = party.GetPosition2D;
                bool isPlayerParty = NetworkObjectRegistry.IsPlayerNetworkParty(party);
                if (!isPlayerParty)
                {
                    _currentTickActiveAiIds.Add(partyId);
                    _knownActiveServerAiIds.Add(partyId);
                }

                if (isPlayerParty &&
                    (party.LeaderHero == null ||
                     !party.LeaderHero.IsAlive ||
                     party.LeaderHero.IsPrisoner ||
                     (party.MemberRoster != null && party.MemberRoster.TotalManCount <= 0)))
                {
                    if (party.IsVisible)
                    {
                        party.IsVisible = false;
                        party.IsInspected = false;
                        party.Party?.SetVisualAsDirty();
                    }
                    continue;
                }

                if (!isPlayerParty && (!hasPlayerPositions || !IsWithinAnyPlayerAoi(pos, _playerPositionsBuffer, AiAreaOfInterestRadiusSq)))
                {
                    // Clear stale cache once on exit so re-entering player AOI triggers an immediate fresh sync
                    if (_lastKnownPositions.ContainsKey(partyId))
                    {
                        _lastKnownPositions.Remove(partyId);
                        _lastKnownTargets.Remove(partyId);
                        _lastKnownSettlements.Remove(partyId);
                    }
                    continue;
                }

                var target = party.MoveTargetPoint.ToVec2();
                if (!target.IsValid) target = pos;
                string settlementId = party.CurrentSettlement?.StringId ?? string.Empty;
                bool hasPrevSettlement = _lastKnownSettlements.TryGetValue(partyId, out var prevSettlement);

                bool shouldSend = isPlayerParty;
                bool settlementChanged = false;

                if (!hasPrevSettlement || !string.Equals(prevSettlement, settlementId, StringComparison.Ordinal))
                {
                    settlementChanged = true;
                    shouldSend = true;
                }
                else if (!shouldSend)
                {
                    bool hasPrevPos = _lastKnownPositions.TryGetValue(partyId, out var prevPos);
                    bool hasPrevTarget = _lastKnownTargets.TryGetValue(partyId, out var prevTarget);

                    if (!hasPrevPos || !hasPrevTarget)
                    {
                        shouldSend = true;
                    }
                    else if ((target - prevTarget).LengthSquared > 0.04f)
                    {
                        shouldSend = true;
                    }
                    else if ((pos - prevPos).LengthSquared > 0.0225f)
                    {
                        shouldSend = true;
                    }
                }

                if (shouldSend)
                {
                    _lastKnownPositions[partyId] = pos;
                    _lastKnownTargets[partyId] = target;
                    _lastKnownSettlements[partyId] = settlementId;
                    _includedIdsBuffer.Add(partyId);
                    var snap = BuildSnapshot(party, partyId, pos, target, settlementId, isPlayerParty);
                    if (settlementChanged)
                    {
                        _reliableSettlementBuffer.Add(snap);
                    }
                    else
                    {
                        _outgoingBuffer.Add(snap);
                    }
                }
            }

            // 2. Rolling background refresh strictly within player AOI (plus player parties)
            int checkedCount = 0;
            int addedRolling = 0;
            while (checkedCount < totalCount && addedRolling < ROLLING_REFRESH_PER_TICK)
            {
                if (_rollingCursor >= totalCount) _rollingCursor = 0;
                var party = allParties[_rollingCursor++];
                checkedCount++;

                if (party == null || !party.IsActive || party.IsMainParty || party.StringId == "cc_server_party") continue;
                if (party.IsGarrison || party.IsMilitia) continue;
                if (_includedIdsBuffer.Contains(party.StringId)) continue;

                var pos = party.GetPosition2D;
                bool isPlayerParty = NetworkObjectRegistry.IsPlayerNetworkParty(party);
                if (!isPlayerParty && (!hasPlayerPositions || !IsWithinAnyPlayerAoi(pos, _playerPositionsBuffer, AiAreaOfInterestRadiusSq)))
                {
                    continue;
                }

                var target = party.MoveTargetPoint.ToVec2();
                if (!target.IsValid) target = pos;
                string settlementId = party.CurrentSettlement?.StringId ?? string.Empty;

                _lastKnownPositions[party.StringId] = pos;
                _lastKnownTargets[party.StringId] = target;
                _lastKnownSettlements[party.StringId] = settlementId;
                _includedIdsBuffer.Add(party.StringId);
                addedRolling++;

                _outgoingBuffer.Add(BuildSnapshot(party, party.StringId, pos, target, settlementId, isPlayerParty));
            }

            // 2b. World-wide slow rolling reconciliation sweep (syncs ALL active AI parties' true position & settlement
            // across all clients every ~5s so distant caravans/lords never remain frozen at old gates when players travel!)
            int worldChecked = 0;
            int worldAdded = 0;
            while (worldChecked < totalCount && worldAdded < WORLD_RECONCILE_PER_TICK)
            {
                if (_worldReconcileCursor >= totalCount) _worldReconcileCursor = 0;
                var party = allParties[_worldReconcileCursor++];
                worldChecked++;

                if (party == null || !party.IsActive || party.IsMainParty || party.StringId == "cc_server_party") continue;
                if (party.IsGarrison || party.IsMilitia) continue;
                if (_includedIdsBuffer.Contains(party.StringId)) continue;
                if (NetworkObjectRegistry.IsPlayerNetworkParty(party)) continue;

                var pos = party.GetPosition2D;
                if (!pos.IsValid) continue;
                var target = party.MoveTargetPoint.ToVec2();
                if (!target.IsValid) target = pos;
                string settlementId = party.CurrentSettlement?.StringId ?? string.Empty;

                _includedIdsBuffer.Add(party.StringId);
                worldAdded++;
                _outgoingBuffer.Add(BuildSnapshot(party, party.StringId, pos, target, settlementId, isPlayerParty: false));
            }

            // 2c. Notify clients reliably whenever a previously active AI party was destroyed/disbanded on the server
            if (_knownActiveServerAiIds.Count > _currentTickActiveAiIds.Count)
            {
                _removedAiIdsBuffer.Clear();
                foreach (var prevId in _knownActiveServerAiIds)
                {
                    if (!_currentTickActiveAiIds.Contains(prevId))
                    {
                        _removedAiIdsBuffer.Add(prevId);
                    }
                }
                for (int r = 0; r < _removedAiIdsBuffer.Count; r++)
                {
                    string deadId = _removedAiIdsBuffer[r];
                    _knownActiveServerAiIds.Remove(deadId);
                    _lastKnownPositions.Remove(deadId);
                    _lastKnownTargets.Remove(deadId);
                    _lastKnownSettlements.Remove(deadId);
                    _reliableSettlementBuffer.Add(new PartySnapshotData
                    {
                        PartyId = deadId,
                        CurrentSettlementId = DestroyedSettlementSentinel,
                        IsVisible = false
                    });
                }
            }

            // 3. Broadcast settlement/destruction transitions reliably, and continuous movement updates in MTU-safe Unreliable chunks
            BroadcastSnapshotList(server, _reliableSettlementBuffer, LiteNetLib.DeliveryMethod.ReliableOrdered);
            BroadcastSnapshotList(server, _outgoingBuffer, LiteNetLib.DeliveryMethod.Unreliable);
        }

        private void BroadcastSnapshotList(Network.Transport.INetworkServer server, List<PartySnapshotData> list, LiteNetLib.DeliveryMethod method)
        {
            for (int offset = 0; offset < list.Count; offset += MAX_PARTIES_PER_PACKET)
            {
                int chunkCount = Math.Min(MAX_PARTIES_PER_PACKET, list.Count - offset);
                var chunkPacket = new WorldStateSnapshotPacket
                {
                    ServerTick = _currentTick,
                    Parties = list.GetRange(offset, chunkCount)
                };

                try
                {
                    server.Broadcast(chunkPacket, method);
                }
                catch (Exception)
                {
                    if (method != LiteNetLib.DeliveryMethod.ReliableOrdered)
                    {
                        try
                        {
                            server.Broadcast(chunkPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        }
                        catch (Exception) { }
                    }
                }
            }
        }

        public static bool IsWithinAnyPlayerAoi(Vec2 pos, IReadOnlyList<Vec2> playerPositions, float radiusSq)
        {
            if (!pos.IsValid || playerPositions == null || playerPositions.Count == 0) return false;
            for (int i = 0; i < playerPositions.Count; i++)
            {
                if ((pos - playerPositions[i]).LengthSquared <= radiusSq)
                {
                    return true;
                }
            }
            return false;
        }

        private static PartySnapshotData BuildSnapshot(
            MobileParty party,
            string partyId,
            Vec2 pos,
            Vec2 target,
            string settlementId,
            bool isPlayerParty)
        {
            var bearing = party.Bearing;
            float partySpeed = party.Speed;
            if (isPlayerParty && NetworkObjectRegistry.Instance.TryGetSyncedPartySpeed(partyId, out float syncedPlayerSpeed))
            {
                partySpeed = syncedPlayerSpeed;
            }

            return new PartySnapshotData
            {
                PartyId = partyId,
                PosX = pos.X,
                PosY = pos.Y,
                TargetX = target.X,
                TargetY = target.Y,
                Heading = bearing.X,
                HeadingY = bearing.Y,
                Speed = partySpeed,
                IsVisible = isPlayerParty ? party.IsVisible : false,
                CurrentSettlementId = settlementId,
                IsAtSea = party.IsCurrentlyAtSea,
                NavigationType = (byte)party.DesiredAiNavigationType
            };
        }

        public static CampaignVec2 ResolveValidPosition(Vec2 pos, bool preferLand, Settlement? fallbackSettlement = null)
        {
            if (pos.IsValid)
            {
                var preferred = new CampaignVec2(pos, isOnLand: preferLand);
                if (preferred.Face.IsValid()) return preferred;

                var alternate = new CampaignVec2(pos, isOnLand: !preferLand);
                if (alternate.Face.IsValid()) return alternate;
            }

            if (fallbackSettlement != null)
            {
                if (!preferLand && fallbackSettlement.HasPort && fallbackSettlement.PortPosition.IsValid())
                {
                    return fallbackSettlement.PortPosition;
                }
                if (fallbackSettlement.GatePosition.IsValid())
                {
                    return fallbackSettlement.GatePosition;
                }
                return new CampaignVec2(fallbackSettlement.GetPosition2D, isOnLand: true);
            }

            return new CampaignVec2(pos.IsValid ? pos : Vec2.Zero, isOnLand: preferLand);
        }

        public static void ApplyRemotePartySettlementState(MobileParty? party, Settlement? settlement, Vec2 worldPos, bool isAtSea)
        {
            if (party == null || !party.IsActive || party.IsMainParty || party.IsDisbanding) return;

            if (settlement != null)
            {
                MarkPartyGarrisoned(party.StringId);
                if (party.CurrentSettlement != settlement)
                {
                    try
                    {
                        if (!NetworkObjectRegistry.IsPlayerNetworkParty(party) && party.MapEvent != null && party.Party != null)
                        {
                            party.Party.MapEventSide = null;
                        }
                        party.SetMoveModeHold();
                        if (party.CurrentSettlement != null)
                        {
                            LeaveSettlementAction.ApplyForParty(party);
                        }
                        EnterSettlementAction.ApplyForParty(party, settlement);
                    }
                    catch (Exception ex)
                    {
                        TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ApplyRemotePartySettlementState enter error ({party.StringId} -> {settlement.StringId}): {ex.Message}");
                    }
                }

                try
                {
                    if (party.IsCurrentlyAtSea != isAtSea)
                    {
                        party.IsCurrentlyAtSea = isAtSea;
                        party.SetNavalVisualAsDirty();
                    }

                    party.SetMoveModeHold();
                    CampaignVec2 insidePos = isAtSea && settlement.HasPort && settlement.PortPosition.IsValid()
                        ? settlement.PortPosition
                        : (settlement.GatePosition.IsValid()
                            ? settlement.GatePosition
                            : new CampaignVec2(settlement.GetPosition2D, isOnLand: !isAtSea));

                    if ((party.GetPosition2D - insidePos.ToVec2()).LengthSquared > 0.25f)
                    {
                        party.Position = insidePos;
                    }
                    if (party.IsVisible)
                    {
                        party.IsVisible = false;
                        party.Party?.SetVisualAsDirty();
                    }
                }
                catch (Exception) { }
                return;
            }

            ClearPartyGarrisoned(party.StringId);
            if (party.CurrentSettlement != null)
            {
                try
                {
                    LeaveSettlementAction.ApplyForParty(party);
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ApplyRemotePartySettlementState leave error ({party.StringId}): {ex.Message}");
                }

                try
                {
                    if (party.IsCurrentlyAtSea != isAtSea)
                    {
                        party.IsCurrentlyAtSea = isAtSea;
                        party.SetNavalVisualAsDirty();
                    }
                    if (worldPos.IsValid)
                    {
                        party.Position = ResolveValidPosition(worldPos, preferLand: !isAtSea);
                    }
                    party.SetMoveModeHold();
                    if (NetworkObjectRegistry.IsPlayerNetworkParty(party))
                    {
                        party.IsVisible = true;
                        party.IsInspected = true;
                        party.Party?.SetVisualAsDirty();
                    }
                }
                catch (Exception) { }
            }
        }

        public void ApplyWorldSnapshot(WorldStateSnapshotPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                if (GameCampaign.Current == null || !GameCampaign.Current.GameStarted || GameCampaign.Current.SaveHandler?.IsSaving == true) return;

                var mainParty = MobileParty.MainParty;
                string? mainPartyId = mainParty?.StringId;
                string localPeerPartyId = $"cc_party_{ModContext.Client?.LocalPeerId ?? 1001}";

                foreach (var snap in packet.Parties)
                {
                    if (float.IsNaN(snap.PosX) || float.IsInfinity(snap.PosX) ||
                        float.IsNaN(snap.PosY) || float.IsInfinity(snap.PosY))
                    {
                        continue;
                    }

                    var serverPos = new Vec2(snap.PosX, snap.PosY);

                    bool isOnLand = !snap.IsAtSea;

                    // 1. Local Player's Main Party: Never overwrite local pathfinding unless extreme teleport (> 50 units)
                    bool isLocalPlayer = (mainParty != null && (snap.PartyId == mainPartyId || snap.PartyId == localPeerPartyId));
                    if (isLocalPlayer && mainParty != null)
                    {
                        var curPos = mainParty.GetPosition2D;
                        if ((serverPos - curPos).Length > 50.0f)
                        {
                            mainParty.Position = new CampaignVec2(serverPos, isOnLand: !mainParty.IsCurrentlyAtSea);
                        }
                        continue;
                    }

                    var party = IsRecentlyDisconnectedParty(snap.PartyId)
                        ? null
                        : NetworkObjectRegistry.Instance.ResolveParty(snap.PartyId);

                    // 1b. Handle destroyed/disbanded AI parties reliably
                    if (string.Equals(snap.CurrentSettlementId, DestroyedSettlementSentinel, StringComparison.Ordinal))
                    {
                        ApplyRemoteAiPartyDestroyed(party, snap.PartyId);
                        continue;
                    }

                    if (party == null || !party.IsActive || party.IsMainParty)
                    {
                        if (party == null &&
                            !IsRecentlyDisconnectedParty(snap.PartyId) &&
                            snap.PartyId.StartsWith(NetworkObjectRegistry.PartyPrefix, StringComparison.Ordinal))
                        {
                            float nowSec = Environment.TickCount / 1000f;
                            if (nowSec - _lastMissingHeroRequestTime > 3.0f)
                            {
                                _lastMissingHeroRequestTime = nowSec;
                                OnMissingRemotePlayerDetected?.Invoke();
                            }
                        }
                        else if (party == null &&
                                 !string.IsNullOrEmpty(snap.PartyId) &&
                                 snap.PartyId != "cc_server_party" &&
                                 !_destroyedAiParties.ContainsKey(snap.PartyId) &&
                                 !snap.PartyId.StartsWith(NetworkObjectRegistry.PartyPrefix, StringComparison.Ordinal))
                        {
                            // Newly spawned Server AI party (caravan/villager/bandit/lord) missing on this client -> request spawn metadata!
                            int nowMs = Environment.TickCount;
                            if (!_requestedMissingAiParties.TryGetValue(snap.PartyId, out int lastReqMs) ||
                                unchecked(nowMs - lastReqMs) > 6000)
                            {
                                _requestedMissingAiParties[snap.PartyId] = nowMs;
                                ModContext.Client?.Send(new AiPartySpawnSyncPacket
                                {
                                    PartyKind = 0, // RequestMissingFromServer
                                    PartyId = snap.PartyId
                                }, DeliveryMethod.ReliableOrdered);
                            }
                        }
                        continue;
                    }

                    bool isPlayerPartyPre = NetworkObjectRegistry.IsPlayerNetworkParty(party);
                    if (!isPlayerPartyPre)
                    {
                        MarkClientAiPartySynced(snap.PartyId);
                    }

                    if (party.IsCurrentlyAtSea != snap.IsAtSea)
                    {
                        try
                        {
                            party.IsCurrentlyAtSea = snap.IsAtSea;
                            party.SetNavalVisualAsDirty();
                            party.Party?.SetVisualAsDirty();
                        }
                        catch { }
                    }

                    // Skip overwriting position / settlement state of active PvP combatants while a duel mission is launching or running
                    if (Services.BattleSyncService.Instance.IsInActivePvpWithParty(party.StringId))
                    {
                        continue;
                    }

                    // 2. Synchronize Settlement Entry / Exit (Lords, Caravans, Villagers, Naval Ships, and Remote Players)
                    if (!string.IsNullOrEmpty(snap.CurrentSettlementId))
                    {
                        var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(snap.CurrentSettlementId);
                        if (settlement != null)
                        {
                            ApplyRemotePartySettlementState(party, settlement, serverPos, snap.IsAtSea);
                        }
                        continue;
                    }
                    else if (party.CurrentSettlement != null || _serverGarrisonedParties.ContainsKey(party.StringId))
                    {
                        ApplyRemotePartySettlementState(party, null, serverPos, snap.IsAtSea);
                    }

                    party.ForceAiNoPathMode = false;
                    bool isPlayerParty = NetworkObjectRegistry.IsPlayerNetworkParty(party);
                    var navType = Services.NavalSyncService.SanitizeNavigationType(party, snap.NavigationType, snap.IsAtSea);
                    if (isPlayerParty ||
                        navType == MobileParty.NavigationType.Default ||
                        navType == MobileParty.NavigationType.All)
                    {
                        party.SetLandNavigationAccess(true);
                    }
                    if (isPlayerParty)
                    {
                        bool isCapturedOrDead = party.LeaderHero == null ||
                                                !party.LeaderHero.IsAlive ||
                                                party.LeaderHero.IsPrisoner ||
                                                (party.MemberRoster != null && party.MemberRoster.TotalManCount <= 0);
                        if (isCapturedOrDead)
                        {
                            if (party.IsVisible || party.IsInspected)
                            {
                                party.IsVisible = false;
                                party.IsInspected = false;
                                party.Party?.SetVisualAsDirty();
                            }
                            party.SetMoveModeHold();
                            continue;
                        }

                        bool wasInvisible = !party.IsVisible;
                        party.IsVisible = true;
                        party.IsInspected = true;
                        party.Ai?.SetDoNotMakeNewDecisions(false);
                        party.Ai?.EnableAi();
                        if (wasInvisible)
                        {
                            party.Party?.SetVisualAsDirty();
                        }
                        if (snap.Speed > 0.05f)
                        {
                            NetworkObjectRegistry.Instance.SetSyncedPartySpeed(party.StringId, snap.Speed);
                        }
                    }
                    else if (mainParty != null)
                    {
                        // Server is authoritative for AI party decisions and speed on connected Clients
                        party.Ai?.SetDoNotMakeNewDecisions(true);
                        if (snap.Speed > 0.05f)
                        {
                            NetworkObjectRegistry.Instance.SetSyncedPartySpeed(party.StringId, snap.Speed);
                        }

                        // Enforce Fog of War for AI parties beyond the local player's spotting radius,
                        // and ensure parties INSIDE spotting range become visible immediately!
                        float clientSeeRange = GetClientVisibilityCutoff(mainParty);
                        var mainPos = mainParty.GetPosition2D;
                        if (mainPos.IsValid)
                        {
                            float distSq = (serverPos - mainPos).LengthSquared;
                            if (distSq > clientSeeRange * clientSeeRange)
                            {
                                if (party.IsVisible || party.IsInspected)
                                {
                                    party.IsVisible = false;
                                    party.IsInspected = false;
                                    party.Party?.SetVisualAsDirty();
                                }

                                if ((serverPos - party.GetPosition2D).LengthSquared > 4.0f)
                                {
                                    party.Position = ResolveValidPosition(serverPos, preferLand: isOnLand);
                                }
                                party.SetMoveModeHold();
                                _remoteAiMotion.Remove(party.StringId);
                                continue;
                            }
                            else
                            {
                                float spottingRange = Math.Max(14.0f, mainParty.SeeingRange);
                                if (distSq <= spottingRange * spottingRange && party.CurrentSettlement == null && !party.IsVisible)
                                {
                                    party.IsVisible = true;
                                    party.Party?.SetVisualAsDirty();
                                }
                            }
                        }
                    }

                    // 3. Smooth 60 FPS Authoritative Position & Navmesh Convergence
                    var curClientPos = party.GetPosition2D;
                    float drift = (serverPos - curClientPos).Length;

                    // Tight snap threshold (2.0 units) so caravans/lords/bandits match exact server location
                    if (drift > 2.0f)
                    {
                        party.Position = ResolveValidPosition(serverPos, preferLand: isOnLand);
                        party.Party?.SetVisualAsDirty();
                        curClientPos = serverPos;
                        drift = 0f;
                    }

                    bool hasValidTarget = !float.IsNaN(snap.TargetX) && !float.IsInfinity(snap.TargetX) &&
                                          !float.IsNaN(snap.TargetY) && !float.IsInfinity(snap.TargetY) &&
                                          (Math.Abs(snap.TargetX) > 0.01f || Math.Abs(snap.TargetY) > 0.01f);
                    var serverTarget = hasValidTarget ? new Vec2(snap.TargetX, snap.TargetY) : serverPos;

                    if (NetworkObjectRegistry.IsPlayerNetworkParty(party))
                    {
                        UpdateRemotePlayerMotionHint(party.StringId, serverPos, serverTarget, snap.Speed, isOnLand);
                    }
                    else
                    {
                        var bearing = new Vec2(snap.Heading, snap.HeadingY);
                        if (bearing.LengthSquared > 0.001f) bearing.Normalize();
                        else if ((serverTarget - serverPos).LengthSquared > 0.01f)
                        {
                            bearing = serverTarget - serverPos;
                            bearing.Normalize();
                        }

                        if (!_remoteAiMotion.TryGetValue(party.StringId, out var aiState))
                        {
                            aiState = new RemoteAiMotionState();
                            _remoteAiMotion[party.StringId] = aiState;
                        }
                        aiState.ServerPos = serverPos;
                        aiState.TargetPos = serverTarget;
                        aiState.Bearing = bearing;
                        aiState.Speed = snap.Speed;
                        aiState.IsOnLand = isOnLand;
                        aiState.LastUpdateTickMs = Environment.TickCount;
                    }

                    if ((serverTarget - serverPos).LengthSquared > 0.02f && snap.Speed > 0.01f)
                    {
                        var clientTarget = party.MoveTargetPoint.ToVec2();
                        if (!party.IsMoving || (clientTarget - serverTarget).LengthSquared > 0.16f)
                        {
                            party.SetMoveGoToPoint(new CampaignVec2(serverTarget, isOnLand: isOnLand), navType);
                        }
                    }
                    else if (drift > 0.30f)
                    {
                        var clientTarget = party.MoveTargetPoint.ToVec2();
                        if (!party.IsMoving || (clientTarget - serverPos).LengthSquared > 0.09f)
                        {
                            party.SetMoveGoToPoint(new CampaignVec2(serverPos, isOnLand: isOnLand), navType);
                        }
                    }
                    else if (drift <= 0.15f && snap.Speed < 0.01f && party.IsMoving)
                    {
                        party.SetMoveModeHold();
                    }
                }
            });
        }

        private static float GetClientVisibilityCutoff(MobileParty mainParty)
        {
            float range = mainParty != null ? mainParty.SeeingRange * 1.15f : ClientFallbackVisibilityRadius;
            if (range < 22.0f) range = 22.0f;
            if (range > 62.0f) range = 62.0f;
            return range;
        }

        /// <summary>
        /// Resets stale IsVisible/IsInspected flags on out-of-range AI parties (for example when loading
        /// an older save created before Fog of War was enforced) so only parties within spotting range render.
        /// </summary>
        public static int ResetOutOfRangeAiVisibility(MobileParty? mainParty)
        {
            ClearClientAiSyncState();
            var campaign = GameCampaign.Current;
            var allParties = MobileParty.All;
            if (campaign == null || allParties == null) return 0;

            campaign.TrueSight = false;

            Vec2 mainPos = mainParty != null ? mainParty.GetPosition2D : Vec2.Invalid;
            float cutoffSq = mainParty != null
                ? GetClientVisibilityCutoff(mainParty) * GetClientVisibilityCutoff(mainParty)
                : ClientFallbackVisibilityRadius * ClientFallbackVisibilityRadius;

            int hiddenCount = 0;
            for (int i = 0; i < allParties.Count; i++)
            {
                var p = allParties[i];
                if (p == null || !p.IsActive || p.IsMainParty || p.StringId == "cc_server_party") continue;
                if (NetworkObjectRegistry.IsPlayerNetworkParty(p)) continue;

                bool shouldHide = p.CurrentSettlement != null ||
                                  !mainPos.IsValid ||
                                  (p.GetPosition2D - mainPos).LengthSquared > cutoffSq;
                if (shouldHide && (p.IsVisible || p.IsInspected))
                {
                    p.IsVisible = false;
                    p.IsInspected = false;
                    p.Party?.SetVisualAsDirty();
                    hiddenCount++;
                }
            }

            return hiddenCount;
        }

        /// <summary>
        /// Streams a full authoritative snapshot of every active world party (caravans, villagers, bandits, lords,
        /// and players) directly to a newly joined peer via ReliableOrdered chunks so all NPC positions & settlement
        /// states match the Server from frame 1 (eliminating save-download drift).
        /// </summary>
        public void SendFullWorldSyncToPeer(int peerId)
        {
            if (!ModContext.IsServerRunning || peerId <= 0) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    var server = ModContext.Server;
                    var allParties = MobileParty.All;
                    if (server == null || !server.IsRunning || allParties == null ||
                        GameCampaign.Current == null || !GameCampaign.Current.GameStarted)
                    {
                        return;
                    }

                    var batch = new List<PartySnapshotData>(MAX_PARTIES_PER_PACKET);
                    for (int i = 0; i < allParties.Count; i++)
                    {
                        var p = allParties[i];
                        if (p == null || !p.IsActive || p.IsMainParty || p.StringId == "cc_server_party") continue;
                        if (p.IsGarrison || p.IsMilitia) continue;

                        var pos = p.GetPosition2D;
                        if (!pos.IsValid) continue;
                        var target = p.MoveTargetPoint.ToVec2();
                        if (!target.IsValid) target = pos;
                        string settlementId = p.CurrentSettlement?.StringId ?? string.Empty;
                        bool isPlayer = NetworkObjectRegistry.IsPlayerNetworkParty(p);

                        batch.Add(BuildSnapshot(p, p.StringId, pos, target, settlementId, isPlayer));
                        if (batch.Count >= MAX_PARTIES_PER_PACKET)
                        {
                            server.SendTo(peerId, new WorldStateSnapshotPacket
                            {
                                ServerTick = _currentTick,
                                Parties = new List<PartySnapshotData>(batch)
                            }, DeliveryMethod.ReliableOrdered);
                            batch.Clear();
                        }
                    }

                    if (batch.Count > 0)
                    {
                        server.SendTo(peerId, new WorldStateSnapshotPacket
                        {
                            ServerTick = _currentTick,
                            Parties = batch
                        }, DeliveryMethod.ReliableOrdered);
                    }
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] SendFullWorldSyncToPeer({peerId}) hatası: {ex.Message}");
                }
            });
        }

        public static AiPartySpawnSyncPacket? BuildAiPartySpawnPacket(MobileParty? party)
        {
            if (party == null || !party.IsActive || party.IsMainParty || party.StringId == "cc_server_party") return null;
            if (party.IsGarrison || party.IsMilitia || NetworkObjectRegistry.IsPlayerNetworkParty(party)) return null;

            var pos = party.GetPosition2D;
            if (!pos.IsValid) return null;

            var pkt = new AiPartySpawnSyncPacket
            {
                PartyId = party.StringId ?? string.Empty,
                PosX = pos.X,
                PosY = pos.Y,
                IsAtSea = party.IsCurrentlyAtSea,
                ClanId = party.ActualClan?.StringId ?? string.Empty,
                TroopCount = party.MemberRoster?.TotalManCount ?? 0
            };

            if (party.CaravanPartyComponent != null)
            {
                var c = party.CaravanPartyComponent;
                pkt.PartyKind = 1;
                pkt.OwnerHeroId = c.Owner?.StringId ?? string.Empty;
                pkt.LeaderHeroId = c.Leader?.StringId ?? string.Empty;
                pkt.SettlementId = c.HomeSettlement?.StringId ?? c.Settlement?.StringId ?? string.Empty;
                pkt.IsElite = c.IsElite;
                return pkt;
            }
            if (party.VillagerPartyComponent != null)
            {
                var v = party.VillagerPartyComponent;
                pkt.PartyKind = 2;
                pkt.SettlementId = v.Village?.Settlement?.StringId ?? v.HomeSettlement?.StringId ?? string.Empty;
                return pkt;
            }
            if (party.BanditPartyComponent != null)
            {
                var b = party.BanditPartyComponent;
                pkt.PartyKind = (byte)(b.Hideout != null ? 3 : 4);
                pkt.SettlementId = b.Hideout?.Settlement?.StringId ?? b.HomeSettlement?.StringId ?? string.Empty;
                pkt.IsBossParty = b.IsBossParty;
                pkt.TemplateId = party.ActualClan?.DefaultPartyTemplate?.StringId ?? string.Empty;
                return pkt;
            }
            if (party.LordPartyComponent != null)
            {
                var l = party.LordPartyComponent;
                pkt.PartyKind = 5;
                pkt.OwnerHeroId = l.Owner?.StringId ?? string.Empty;
                pkt.LeaderHeroId = l.Leader?.StringId ?? string.Empty;
                pkt.SettlementId = l.HomeSettlement?.StringId ?? string.Empty;
                return pkt;
            }

            return null;
        }

        public static void BroadcastAiPartySpawn(MobileParty? party)
        {
            if (!ModContext.IsServerRunning || party == null) return;
            var server = ModContext.Server;
            if (server == null || !server.IsRunning || (server.Sessions?.Count ?? 0) == 0) return;

            var pkt = BuildAiPartySpawnPacket(party);
            if (pkt != null)
            {
                server.Broadcast(pkt, DeliveryMethod.ReliableOrdered);
            }
        }

        public void HandleAiPartySpawnPacket(AiPartySpawnSyncPacket packet, int senderPeerId)
        {
            if (packet == null || string.IsNullOrEmpty(packet.PartyId)) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                try
                {
                    if (GameCampaign.Current == null || !GameCampaign.Current.GameStarted || GameCampaign.Current.SaveHandler?.IsSaving == true)
                    {
                        return;
                    }

                    if (ModContext.IsServerRunning)
                    {
                        if (packet.PartyKind != 0 || senderPeerId <= 0) return;

                        var party = NetworkObjectRegistry.Instance.ResolveParty(packet.PartyId);
                        if (party != null && party.IsActive)
                        {
                            var spawnPkt = BuildAiPartySpawnPacket(party);
                            if (spawnPkt != null)
                            {
                                ModContext.Server?.SendTo(senderPeerId, spawnPkt, DeliveryMethod.ReliableOrdered);
                            }
                        }
                        else
                        {
                            // Party no longer exists on server -> tell client to mark it destroyed
                            ModContext.Server?.SendTo(senderPeerId, new WorldStateSnapshotPacket
                            {
                                ServerTick = _currentTick,
                                Parties = new List<PartySnapshotData>
                                {
                                    new PartySnapshotData
                                    {
                                        PartyId = packet.PartyId,
                                        CurrentSettlementId = DestroyedSettlementSentinel,
                                        IsVisible = false
                                    }
                                }
                            }, DeliveryMethod.ReliableOrdered);
                        }
                        return;
                    }

                    // Client: spawn the exact Server-authoritative AI party locally if missing
                    if (packet.PartyKind == 0) return;

                    var existing = NetworkObjectRegistry.Instance.ResolveParty(packet.PartyId);
                    Vec2 pos = new Vec2(packet.PosX, packet.PosY);
                    CampaignVec2 spawnPos = ResolveValidPosition(pos, preferLand: !packet.IsAtSea);

                    if (existing != null && existing.IsActive)
                    {
                        existing.Position = spawnPos;
                        existing.Ai?.SetDoNotMakeNewDecisions(true);
                        MarkClientAiPartySynced(packet.PartyId);
                        return;
                    }

                    MobileParty? created = null;
                    switch (packet.PartyKind)
                    {
                        case 1: // Caravan
                        {
                            var owner = NetworkObjectRegistry.Instance.ResolveHero(packet.OwnerHeroId);
                            var leader = !string.IsNullOrEmpty(packet.LeaderHeroId)
                                ? NetworkObjectRegistry.Instance.ResolveHero(packet.LeaderHeroId)
                                : null;
                            var home = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId)
                                       ?? Settlement.All?.FirstOrDefault(s => s != null && s.IsTown);
                            if (owner != null && home != null)
                            {
                                var template = packet.IsElite
                                    ? (owner.Culture?.EliteCaravanPartyTemplates?.FirstOrDefault() ?? owner.Culture?.CaravanPartyTemplates?.FirstOrDefault())
                                    : owner.Culture?.CaravanPartyTemplates?.FirstOrDefault();
                                if (template != null)
                                {
                                    created = CaravanPartyComponent.CreateCaravanParty(owner, home, template, false, leader, null, packet.IsElite);
                                }
                            }
                            break;
                        }
                        case 2: // Villager
                        {
                            var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId);
                            if (settlement?.Village != null)
                            {
                                created = VillagerPartyComponent.CreateVillagerParty(packet.PartyId, settlement.Village);
                            }
                            break;
                        }
                        case 3: // Bandit (Hideout)
                        {
                            var clan = NetworkObjectRegistry.Instance.ResolveClan(packet.ClanId);
                            var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId);
                            var template = !string.IsNullOrEmpty(packet.TemplateId)
                                ? GameCampaign.Current.ObjectManager?.GetObject<PartyTemplateObject>(packet.TemplateId)
                                : clan?.DefaultPartyTemplate;
                            if (clan != null && settlement?.Hideout != null && template != null)
                            {
                                created = BanditPartyComponent.CreateBanditParty(packet.PartyId, clan, settlement.Hideout, packet.IsBossParty, template, spawnPos);
                            }
                            break;
                        }
                        case 4: // Looter
                        {
                            var clan = NetworkObjectRegistry.Instance.ResolveClan(packet.ClanId)
                                       ?? Clan.BanditFactions?.FirstOrDefault(c => c != null && c.StringId == "looters");
                            var settlement = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId)
                                             ?? Settlement.All?.FirstOrDefault(s => s != null && s.IsTown);
                            var template = !string.IsNullOrEmpty(packet.TemplateId)
                                ? GameCampaign.Current.ObjectManager?.GetObject<PartyTemplateObject>(packet.TemplateId)
                                : clan?.DefaultPartyTemplate;
                            if (clan != null && settlement != null && template != null)
                            {
                                created = BanditPartyComponent.CreateLooterParty(packet.PartyId, clan, settlement, packet.IsBossParty, template, spawnPos);
                            }
                            break;
                        }
                        case 5: // Lord
                        {
                            var owner = NetworkObjectRegistry.Instance.ResolveHero(packet.OwnerHeroId);
                            var leader = !string.IsNullOrEmpty(packet.LeaderHeroId)
                                ? NetworkObjectRegistry.Instance.ResolveHero(packet.LeaderHeroId)
                                : owner;
                            var home = NetworkObjectRegistry.Instance.ResolveSettlement(packet.SettlementId)
                                       ?? owner?.HomeSettlement
                                       ?? Settlement.All?.FirstOrDefault(s => s != null && s.IsTown);
                            if (owner != null && home != null)
                            {
                                created = LordPartyComponent.CreateLordParty(packet.PartyId, owner, spawnPos, 0.5f, home, leader);
                            }
                            break;
                        }
                    }

                    if (created != null)
                    {
                        created.StringId = packet.PartyId;
                        created.Position = spawnPos;
                        created.Ai?.SetDoNotMakeNewDecisions(true);
                        created.SetMoveModeHold();
                        MarkClientAiPartySynced(packet.PartyId);
                    }
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] HandleAiPartySpawnPacket({packet.PartyId}) hatası: {ex.Message}");
                }
            });
        }
    }
}
