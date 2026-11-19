using System;
using System.Reflection;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace CalradiaConnected.Campaign.Time
{
    public class CampaignTimeSyncBehavior : CampaignBehaviorBase
    {
        public static CampaignTimeSyncBehavior? Instance { get; private set; }

        /// <summary>
        /// Uses UnstoppablePlay / UnstoppableFastForward / Stop so the dedicated server clock
        /// keeps advancing even when the hidden host party is stationary.
        /// </summary>
        public static CampaignTimeControlMode AuthoritativeSpeedMode { get; private set; } = CampaignTimeControlMode.UnstoppablePlay;
        public static bool PersistentWorldTime { get; set; } = true;

        private float _timeSinceLastBroadcast = 0f;
        private const float BROADCAST_INTERVAL_SEC = 0.5f;
        private long _serverTickCount = 0;

        private static readonly FieldInfo? _campaignTimeTicksField = AccessTools.Field(typeof(CampaignTime), "_numTicks");
        private static readonly PropertyInfo? _mapTimeTrackerProp = AccessTools.Property(typeof(TaleWorlds.CampaignSystem.Campaign), "MapTimeTracker");
        private static readonly FieldInfo? _mapTimeTrackerBackingField = AccessTools.Field(typeof(TaleWorlds.CampaignSystem.Campaign), "<MapTimeTracker>k__BackingField");
        private static FieldInfo? _trackerNumTicksField;

        public CampaignTimeSyncBehavior()
        {
            Instance = this;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnCampaignTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Transient network synchronization; no persistence needed
        }

        /// <summary>
        /// Exclusively called by ServerIpcHost when the Server Manager sends SET_TIME 0/1/2.
        /// </summary>
        public static void SetAuthoritativeSpeedFromServerManager(int mode)
        {
            AuthoritativeSpeedMode = mode switch
            {
                0 => CampaignTimeControlMode.Stop,
                2 => CampaignTimeControlMode.UnstoppableFastForward,
                3 => CampaignTimeControlMode.UnstoppableFastForward,
                _ => CampaignTimeControlMode.UnstoppablePlay
            };

            if (TaleWorlds.CampaignSystem.Campaign.Current != null)
            {
                TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode = GetEffectiveServerTimeMode();
            }

            Instance?.BroadcastServerTime();
        }

        public static CampaignTimeControlMode GetEffectiveServerTimeMode()
        {
            if (ModContext.IsServerRunning && !PersistentWorldTime && (ModContext.Server?.Sessions?.Count ?? 0) == 0)
            {
                return CampaignTimeControlMode.Stop;
            }
            return AuthoritativeSpeedMode;
        }

        public void SetDesiredSpeed(CampaignTimeControlMode mode)
        {
            if (mode == CampaignTimeControlMode.StoppablePlay)
                mode = CampaignTimeControlMode.UnstoppablePlay;
            else if (mode == CampaignTimeControlMode.StoppableFastForward)
                mode = CampaignTimeControlMode.UnstoppableFastForward;

            AuthoritativeSpeedMode = mode;
            if (TaleWorlds.CampaignSystem.Campaign.Current != null)
            {
                TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode = AuthoritativeSpeedMode;
            }
        }

        public static long GetRawCampaignTicks()
        {
            try
            {
                var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                if (campaign != null)
                {
                    var tracker = _mapTimeTrackerProp?.GetValue(campaign) ?? _mapTimeTrackerBackingField?.GetValue(campaign);
                    if (tracker != null)
                    {
                        _trackerNumTicksField ??= AccessTools.Field(tracker.GetType(), "_numTicks");
                        if (_trackerNumTicksField != null)
                        {
                            return (long)_trackerNumTicksField.GetValue(tracker)!;
                        }
                    }
                }

                if (_campaignTimeTicksField != null)
                {
                    return (long)_campaignTimeTicksField.GetValue(CampaignTime.Now)!;
                }
            }
            catch { }

            return (long)CampaignTime.Now.ToMilliseconds;
        }

        private void OnCampaignTick(float dt)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

            var desired = GetEffectiveServerTimeMode();
            if (TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode != desired)
            {
                TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode = desired;
            }
        }

        /// <summary>
        /// Called every frame from CalradiaConnectedSubModule.OnApplicationTick(realDt) with real wall-clock delta time.
        /// Guarantees time synchronization broadcasts even when campaign time is paused (Stop) or MainParty is holding.
        /// </summary>
        public void OnRealTick(float realDt)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

            var desired = GetEffectiveServerTimeMode();
            if (TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode != desired)
            {
                TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode = desired;
            }

            if (ModContext.IsServerRunning && (ModContext.Server?.Sessions?.Count ?? 0) > 0)
            {
                _timeSinceLastBroadcast += realDt;
                if (_timeSinceLastBroadcast >= BROADCAST_INTERVAL_SEC)
                {
                    _timeSinceLastBroadcast = 0f;
                    _serverTickCount++;
                    BroadcastServerTime();
                }
            }
        }

        public void BroadcastServerTime()
        {
            var server = ModContext.Server;
            if (server == null || !server.IsRunning || TaleWorlds.CampaignSystem.Campaign.Current == null) return;

            byte speedCode = AuthoritativeSpeedMode switch
            {
                CampaignTimeControlMode.Stop => (byte)0,
                CampaignTimeControlMode.FastForwardStop => (byte)0,
                CampaignTimeControlMode.UnstoppableFastForward => (byte)2,
                CampaignTimeControlMode.StoppableFastForward => (byte)2,
                _ => (byte)1
            };

            long nowTicks = GetRawCampaignTicks();

            var packet = new CampaignTimeSpeedPacket
            {
                SpeedMode = speedCode,
                ServerTick = _serverTickCount,
                ServerCampaignTicks = nowTicks
            };

            server.Broadcast(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void HandleServerTimeSpeedPacket(CampaignTimeSpeedPacket packet)
        {
            ModContext.Dispatcher.Enqueue(() =>
            {
                if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;

                // 1. Speed Mode sync (strictly UnstoppablePlay / UnstoppableFastForward / Stop)
                AuthoritativeSpeedMode = packet.SpeedMode switch
                {
                    0 => CampaignTimeControlMode.Stop,
                    2 => CampaignTimeControlMode.UnstoppableFastForward,
                    _ => CampaignTimeControlMode.UnstoppablePlay
                };

                TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode = AuthoritativeSpeedMode;

                // 2. Absolute World Time Drift Compensation (reconcile on any drift > 100 ticks)
                if (packet.ServerCampaignTicks > 0)
                {
                    long localTicks = GetRawCampaignTicks();
                    long drift = Math.Abs(packet.ServerCampaignTicks - localTicks);

                    if (drift > 100)
                    {
                        ReconcileLocalClock(packet.ServerCampaignTicks);
                    }
                }
            });
        }

        private static void ReconcileLocalClock(long targetTicks)
        {
            try
            {
                var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                if (campaign == null) return;

                // Always resolve tracker from current active Campaign instance (never cache stale campaign tracker)
                var tracker = _mapTimeTrackerProp?.GetValue(campaign) ?? _mapTimeTrackerBackingField?.GetValue(campaign);
                if (tracker != null)
                {
                    _trackerNumTicksField ??= AccessTools.Field(tracker.GetType(), "_numTicks");
                    _trackerNumTicksField?.SetValue(tracker, targetTicks);
                }
            }
            catch (Exception)
            {
                // Fallback gracefully
            }
        }
    }
}
