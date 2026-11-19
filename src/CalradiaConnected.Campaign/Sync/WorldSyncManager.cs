using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
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
        private const int ROLLING_REFRESH_PER_TICK = 18;
        public const float AiAreaOfInterestRadius = 68.0f; // Exceeds MaximumSeeingRange (60) for smooth boundary entry
        private const float AiAreaOfInterestRadiusSq = AiAreaOfInterestRadius * AiAreaOfInterestRadius;
        private const float ClientFallbackVisibilityRadius = 45.0f;

        private int _rollingCursor = 0;
        private readonly Dictionary<string, Vec2> _lastKnownPositions = new Dictionary<string, Vec2>();
        private readonly Dictionary<string, Vec2> _lastKnownTargets = new Dictionary<string, Vec2>();
        private readonly Dictionary<string, string> _lastKnownSettlements = new Dictionary<string, string>();
        private readonly Dictionary<string, RemotePlayerMotionState> _remotePlayerMotion = new Dictionary<string, RemotePlayerMotionState>(StringComparer.Ordinal);
        private readonly List<Vec2> _playerPositionsBuffer = new List<Vec2>(16);
        private readonly List<PartySnapshotData> _outgoingBuffer = new List<PartySnapshotData>(64);
        private readonly List<PartySnapshotData> _reliableSettlementBuffer = new List<PartySnapshotData>(32);
        private readonly HashSet<string> _includedIdsBuffer = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _staleMotionKeysBuffer = new List<string>(8);
        private float _lastMissingHeroRequestTime = 0f;
        private static readonly ConcurrentDictionary<string, long> _recentlyDisconnectedParties =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

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
                InterpolateStalledRemotePlayers(dt);
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
                    else if ((pos - prevPos).LengthSquared > 0.0625f)
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

            // 3. Broadcast settlement state transitions reliably, and continuous movement updates in MTU-safe Unreliable chunks
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
                if (party.CurrentSettlement != settlement)
                {
                    try
                    {
                        if (!NetworkObjectRegistry.IsPlayerNetworkParty(party) && party.MapEvent != null && party.Party != null)
                        {
                            party.Party.MapEventSide = null;
                        }
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
                        continue;
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
                    else if (party.CurrentSettlement != null)
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
                        // Enforce Fog of War for AI parties beyond the local player's spotting radius
                        float clientSeeRange = GetClientVisibilityCutoff(mainParty);
                        var mainPos = mainParty.GetPosition2D;
                        if (mainPos.IsValid && (serverPos - mainPos).LengthSquared > clientSeeRange * clientSeeRange)
                        {
                            if (party.IsVisible || party.IsInspected)
                            {
                                party.IsVisible = false;
                                party.IsInspected = false;
                                party.Party?.SetVisualAsDirty();
                            }

                            if ((serverPos - party.GetPosition2D).LengthSquared > 36.0f)
                            {
                                party.Position = new CampaignVec2(serverPos, isOnLand: isOnLand);
                            }
                            continue;
                        }
                    }

                    // 3. Smooth 60 FPS Native Navmesh Movement
                    var curClientPos = party.GetPosition2D;
                    float drift = (serverPos - curClientPos).Length;

                    // Snap only if drift is large (e.g., initial sync or teleport)
                    if (drift > 5.0f)
                    {
                        party.Position = new CampaignVec2(serverPos, isOnLand: isOnLand);
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
    }
}
