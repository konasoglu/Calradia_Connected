using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using LiteNetLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Missions
{
    /// <summary>
    /// War Sails (NavalDLC v1.2.8) Phase 3:
    /// Optional zero-hard-dependency reflection bridge for synchronizing player-controlled ships inside
    /// NavalMissions without breaking builds or launches when NavalDLC is not installed/enabled.
    /// </summary>
    internal sealed class NavalMissionBridge
    {
        private static bool _typesResolved;
        private static Type? _navalShipsLogicType;
        private static Type? _missionShipType;
        private static Type? _shipControllerEnumType;
        private static object? _controllerNoneValue;

        private static PropertyInfo? _playerControlledShipProp;
        private static PropertyInfo? _allShipsProp;

        private static PropertyInfo? _globalFrameProp;
        private static PropertyInfo? _isRemovedProp;
        private static PropertyInfo? _isSunkProp;
        private static PropertyInfo? _isSinkingProp;
        private static PropertyInfo? _isPlayerShipProp;
        private static PropertyInfo? _captainProp;
        private static PropertyInfo? _teamProp;
        private static PropertyInfo? _hitPointsProp;
        private static PropertyInfo? _sailHitPointsProp;
        private static PropertyInfo? _sailTargetSettingProp;
        private static PropertyInfo? _visualRudderRotationProp;
        private static PropertyInfo? _shipOriginProp;
        private static PropertyInfo? _physicsProp;
        private static PropertyInfo? _linearVelocityProp;
        private static PropertyInfo? _gameEntityProp;

        private static MethodInfo? _setControllerMethod;
        private static MethodInfo? _setAnchorFrameMethod;

        private MissionBehavior? _cachedShipsLogic;
        private bool _searchedMission;
        private float _shipSendAccumulator;
        private const float SHIP_SEND_INTERVAL = 0.05f; // 20 Hz

        private static void EnsureReflectionInitialized()
        {
            if (_typesResolved) return;
            _typesResolved = true;

            try
            {
                Assembly? navalAsm = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(asm.GetName().Name, "NavalDLC", StringComparison.OrdinalIgnoreCase))
                    {
                        navalAsm = asm;
                        break;
                    }
                }
                if (navalAsm == null) return;

                _navalShipsLogicType = navalAsm.GetType("NavalDLC.Missions.MissionLogics.NavalShipsLogic");
                _missionShipType = navalAsm.GetType("NavalDLC.Missions.Objects.MissionShip");
                _shipControllerEnumType = navalAsm.GetType("NavalDLC.Missions.ShipControl.ShipControllerType");
                var navalPhysicsType = navalAsm.GetType("NavalDLC.Missions.Objects.NavalPhysics");

                if (_shipControllerEnumType != null && _shipControllerEnumType.IsEnum)
                {
                    _controllerNoneValue = Enum.ToObject(_shipControllerEnumType, 0);
                }

                if (_navalShipsLogicType != null)
                {
                    _playerControlledShipProp = _navalShipsLogicType.GetProperty("PlayerControlledShip", BindingFlags.Public | BindingFlags.Instance);
                    _allShipsProp = _navalShipsLogicType.GetProperty("AllShips", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_missionShipType != null)
                {
                    _globalFrameProp = _missionShipType.GetProperty("GlobalFrame", BindingFlags.Public | BindingFlags.Instance);
                    _isRemovedProp = _missionShipType.GetProperty("IsRemoved", BindingFlags.Public | BindingFlags.Instance);
                    _isSunkProp = _missionShipType.GetProperty("IsSunk", BindingFlags.Public | BindingFlags.Instance);
                    _isSinkingProp = _missionShipType.GetProperty("IsSinking", BindingFlags.Public | BindingFlags.Instance);
                    _isPlayerShipProp = _missionShipType.GetProperty("IsPlayerShip", BindingFlags.Public | BindingFlags.Instance);
                    _captainProp = _missionShipType.GetProperty("Captain", BindingFlags.Public | BindingFlags.Instance);
                    _teamProp = _missionShipType.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                    _hitPointsProp = _missionShipType.GetProperty("HitPoints", BindingFlags.Public | BindingFlags.Instance);
                    _sailHitPointsProp = _missionShipType.GetProperty("SailHitPoints", BindingFlags.Public | BindingFlags.Instance);
                    _sailTargetSettingProp = _missionShipType.GetProperty("SailTargetSetting", BindingFlags.Public | BindingFlags.Instance);
                    _visualRudderRotationProp = _missionShipType.GetProperty("VisualRudderRotation", BindingFlags.Public | BindingFlags.Instance);
                    _shipOriginProp = _missionShipType.GetProperty("ShipOrigin", BindingFlags.Public | BindingFlags.Instance);
                    _physicsProp = _missionShipType.GetProperty("Physics", BindingFlags.Public | BindingFlags.Instance);
                    _gameEntityProp = _missionShipType.GetProperty("GameEntity", BindingFlags.Public | BindingFlags.Instance);

                    _setControllerMethod = _missionShipType.GetMethod("SetController", BindingFlags.Public | BindingFlags.Instance);
                    _setAnchorFrameMethod = _missionShipType.GetMethod("SetAnchorFrame", BindingFlags.Public | BindingFlags.Instance);
                }

                if (navalPhysicsType != null)
                {
                    _linearVelocityProp = navalPhysicsType.GetProperty("LinearVelocity", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            catch { }
        }

        public void Reset()
        {
            _cachedShipsLogic = null;
            _searchedMission = false;
            _shipSendAccumulator = 0f;
        }

        public bool IsNavalMissionActive(Mission? mission)
        {
            if (mission == null) return false;
            EnsureReflectionInitialized();
            if (_navalShipsLogicType == null) return false;

            if (_cachedShipsLogic != null) return true;
            if (_searchedMission) return false;

            _searchedMission = true;
            foreach (var behavior in mission.MissionBehaviors)
            {
                if (behavior != null && _navalShipsLogicType.IsInstanceOfType(behavior))
                {
                    _cachedShipsLogic = behavior;
                    return true;
                }
            }
            return false;
        }

        public void Tick(
            Mission? mission,
            float dt,
            int localPeerId,
            Dictionary<int, MissionNavalShipStatePacket> latestShipStates,
            Dictionary<int, Agent> remotePlayerAgents)
        {
            if (!IsNavalMissionActive(mission) || _cachedShipsLogic == null || _missionShipType == null) return;

            _shipSendAccumulator += dt;
            if (_shipSendAccumulator >= SHIP_SEND_INTERVAL)
            {
                _shipSendAccumulator = 0f;
                SendLocalShipState(localPeerId);
            }

            ApplyRemoteShipStates(mission!, dt, latestShipStates, remotePlayerAgents);
        }

        private void SendLocalShipState(int localPeerId)
        {
            try
            {
                object? localShip = _playerControlledShipProp?.GetValue(_cachedShipsLogic);
                if (localShip == null) return;
                if ((bool?)_isRemovedProp?.GetValue(localShip) == true) return;

                if (_globalFrameProp?.GetValue(localShip) is not MatrixFrame frame) return;

                Vec3 vel = Vec3.Zero;
                object? physics = _physicsProp?.GetValue(localShip);
                if (physics != null && _linearVelocityProp?.GetValue(physics) is Vec3 linVel)
                {
                    vel = linVel;
                }

                var origin = frame.origin;
                var fwd = frame.rotation.f;
                float hp = _hitPointsProp?.GetValue(localShip) is float h ? h : 100f;
                float sailHp = _sailHitPointsProp?.GetValue(localShip) is float sh ? sh : 100f;
                float sailSet = _sailTargetSettingProp?.GetValue(localShip) is float ss ? ss : 0f;
                float rudder = _visualRudderRotationProp?.GetValue(localShip) is float rr ? rr : 0f;
                bool isSinking = (bool?)_isSinkingProp?.GetValue(localShip) == true || (bool?)_isSunkProp?.GetValue(localShip) == true;

                string hullId = string.Empty;
                if (_shipOriginProp?.GetValue(localShip) is IShipOrigin shipOrigin)
                {
                    hullId = shipOrigin.Hull?.StringId ?? string.Empty;
                }

                var packet = new MissionNavalShipStatePacket
                {
                    PeerId = localPeerId,
                    ShipIndex = 0,
                    HullId = hullId,
                    PosX = origin.x,
                    PosY = origin.y,
                    PosZ = origin.z,
                    DirX = fwd.x,
                    DirY = fwd.y,
                    VelX = vel.x,
                    VelY = vel.y,
                    HitPoints = hp,
                    SailHitPoints = sailHp,
                    SailSetting = sailSet,
                    RudderRotation = rudder,
                    IsAnchored = false,
                    IsSinking = isSinking
                };

                ModContext.Client?.Send(packet, DeliveryMethod.Unreliable);
            }
            catch { }
        }

        private void ApplyRemoteShipStates(
            Mission mission,
            float dt,
            Dictionary<int, MissionNavalShipStatePacket> latestShipStates,
            Dictionary<int, Agent> remotePlayerAgents)
        {
            if (latestShipStates.Count == 0) return;

            try
            {
                if (_allShipsProp?.GetValue(_cachedShipsLogic) is not IEnumerable allShips) return;

                var shipList = new List<object>();
                foreach (var s in allShips)
                {
                    if (s != null && (bool?)_isRemovedProp?.GetValue(s) != true)
                    {
                        shipList.Add(s);
                    }
                }
                if (shipList.Count == 0) return;

                var localTeam = Agent.Main?.Team ?? mission.PlayerTeam;

                foreach (var kvp in latestShipStates)
                {
                    int peerId = kvp.Key;
                    var st = kvp.Value;

                    object? targetShip = FindShipForRemotePeer(shipList, peerId, st.HullId, localTeam, remotePlayerAgents);
                    if (targetShip == null) continue;

                    // Strip AI ship controller so enemy ship strictly mirrors the human opponent
                    if (_setControllerMethod != null && _controllerNoneValue != null)
                    {
                        try { _setControllerMethod.Invoke(targetShip, new object[] { _controllerNoneValue, false }); } catch { }
                    }

                    if (_globalFrameProp?.GetValue(targetShip) is not MatrixFrame curFrame) continue;

                    Vec2 targetPos2 = new Vec2(st.PosX, st.PosY);
                    Vec2 curPos2 = curFrame.origin.AsVec2;
                    float dist = (targetPos2 - curPos2).Length;

                    Vec2 dir2 = new Vec2(st.DirX, st.DirY);
                    if (dir2.LengthSquared < 0.0001f)
                    {
                        dir2 = curFrame.rotation.f.AsVec2;
                    }
                    if (dir2.LengthSquared > 0.0001f)
                    {
                        dir2.Normalize();
                    }

                    if (dist > 22f && _gameEntityProp?.GetValue(targetShip) is WeakGameEntity shipEntity && shipEntity.IsValid)
                    {
                        MatrixFrame teleportFrame = curFrame;
                        teleportFrame.origin = new Vec3(st.PosX, st.PosY, st.PosZ);
                        if (dir2.LengthSquared > 0.0001f)
                        {
                            Vec3 f = new Vec3(dir2.X, dir2.Y, 0f).NormalizedCopy();
                            Vec3 u = Vec3.Up;
                            Vec3 s = Vec3.CrossProduct(u, f).NormalizedCopy();
                            u = Vec3.CrossProduct(f, s).NormalizedCopy();
                            teleportFrame.rotation = new Mat3(s, f, u);
                        }
                        shipEntity.SetGlobalFrame(in teleportFrame, isTeleportation: true);
                    }
                    else if (_setAnchorFrameMethod != null && dir2.LengthSquared > 0.0001f)
                    {
                        Vec2 blendedPos = dist > 0.05f
                            ? Vec2.Lerp(curPos2, targetPos2, Math.Min(1f, dt * 10f))
                            : targetPos2;
                        object[] args = new object[] { blendedPos, dir2, 6.0f };
                        try { _setAnchorFrameMethod.Invoke(targetShip, args); } catch { }
                    }
                }
            }
            catch { }
        }

        private static object? FindShipForRemotePeer(
            List<object> ships,
            int peerId,
            string hullId,
            Team? localTeam,
            Dictionary<int, Agent> remotePlayerAgents)
        {
            remotePlayerAgents.TryGetValue(peerId, out var remoteAgent);

            if (remoteAgent != null)
            {
                foreach (var s in ships)
                {
                    if (_captainProp?.GetValue(s) is Agent cap && cap == remoteAgent)
                    {
                        return s;
                    }
                }
            }

            // Fallback: pick enemy team's first ship (preferring matching hullId) when captain isn't mounted yet
            object? enemyFallback = null;
            foreach (var s in ships)
            {
                if ((bool?)_isPlayerShipProp?.GetValue(s) == true) continue;
                var team = _teamProp?.GetValue(s) as Team;
                if (localTeam != null && team != null && !team.IsEnemyOf(localTeam)) continue;

                if (!string.IsNullOrEmpty(hullId) &&
                    _shipOriginProp?.GetValue(s) is IShipOrigin so &&
                    string.Equals(so.Hull?.StringId, hullId, StringComparison.Ordinal))
                {
                    return s;
                }

                enemyFallback ??= s;
            }

            return enemyFallback;
        }
    }
}
