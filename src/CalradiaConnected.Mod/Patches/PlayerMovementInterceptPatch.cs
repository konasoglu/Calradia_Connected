using System;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace CalradiaConnected.Mod.Patches
{
    /// <summary>
    /// Makes the server game window untouchable for map travel clicks so clicking the map on the server
    /// never moves the hidden cc_server_party.
    /// </summary>
    [HarmonyPatch(typeof(MapState), "ProcessTravel")]
    public static class ServerMapStateUntouchablePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (CalradiaConnectedSubModule.IsServerProcess)
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Removes the "Wait here for some time" ("Burada biraz zaman geçir") option from all settlements
    /// (towns, castles, villages) since campaign time flows continuously in Co-Op.
    /// </summary>
    [HarmonyPatch(typeof(PlayerTownVisitCampaignBehavior), "game_menu_wait_here_on_condition")]
    public static class RemoveSettlementWaitOptionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    public static class MoveOrderDebouncer
    {
        private static PlayerMoveOrderPacket? _pendingPacket;
        private static float _lastSendTime = 0f;
        private static float _lastLivePosSendTime = 0f;
        private static Vec2 _lastSentLivePos = Vec2.Invalid;
        private static float _lastSentSpeed = 0f;
        private static bool _lastSentAtSea = false;
        private const float MIN_SEND_INTERVAL = 0.05f; // 50ms throttle
        private const float LIVE_POS_INTERVAL = 0.20f; // 5 Hz live position & cargo speed sync
        private static bool _subscribed = false;

        public static void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;

            ModContext.Events.Subscribe<LocalPartyMoveRequestedEvent>(evt =>
            {
                if (!ModContext.IsClientConnected) return;
                if (Hero.MainHero == null || !Hero.MainHero.IsAlive || Hero.MainHero.IsPrisoner) return;

                var packet = new PlayerMoveOrderPacket
                {
                    PeerId = ModContext.Client?.LocalPeerId ?? 1001,
                    MoveType = evt.MoveType,
                    CurrentX = evt.CurrentX,
                    CurrentY = evt.CurrentY,
                    TargetSettlementId = evt.TargetSettlementId ?? string.Empty,
                    TargetPartyId = evt.TargetPartyId ?? string.Empty,
                    TargetX = evt.TargetX,
                    TargetY = evt.TargetY,
                    Speed = evt.Speed,
                    IsAtSea = evt.IsAtSea,
                    NavigationType = evt.NavigationType,
                    IsTargetingPort = evt.IsTargetingPort
                };

                EnqueueOrSend(packet);
            });
        }

        public static void EnqueueOrSend(PlayerMoveOrderPacket packet)
        {
            float now = Environment.TickCount / 1000f;
            if (now - _lastSendTime >= MIN_SEND_INTERVAL)
            {
                _lastSendTime = now;
                _pendingPacket = null;
                ModContext.Client?.Send(packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                // Queue trailing order so rapid clicks never drop the final intent
                _pendingPacket = packet;
            }
        }

        public static void TickPending()
        {
            float now = Environment.TickCount / 1000f;

            if (_pendingPacket != null && now - _lastSendTime >= MIN_SEND_INTERVAL)
            {
                _lastSendTime = now;
                var packetToSend = _pendingPacket;
                _pendingPacket = null;
                ModContext.Client?.Send(packetToSend, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            // Stream live position of local MainParty to the server while moving so server puppet is 100% visible & synced
            // NEVER stream live position while the local hero is a prisoner or dead (prevents ghost prisoner banner following the captor!).
            if (now - _lastLivePosSendTime >= LIVE_POS_INTERVAL &&
                TaleWorlds.CampaignSystem.Campaign.Current != null &&
                MobileParty.MainParty != null &&
                Hero.MainHero != null &&
                Hero.MainHero.IsAlive &&
                !Hero.MainHero.IsPrisoner)
            {
                var mainParty = MobileParty.MainParty;
                var curPos = mainParty.GetPosition2D;
                float curSpeed = mainParty.Speed;
                bool isAtSea = mainParty.IsCurrentlyAtSea;
                bool posChanged = !_lastSentLivePos.IsValid || (curPos - _lastSentLivePos).LengthSquared > 0.0020f;
                bool speedChanged = Math.Abs(curSpeed - _lastSentSpeed) > 0.05f;
                bool seaChanged = isAtSea != _lastSentAtSea;

                if (curPos.IsValid && (posChanged || speedChanged || seaChanged))
                {
                    _lastLivePosSendTime = now;
                    _lastSentLivePos = curPos;
                    _lastSentSpeed = curSpeed;
                    _lastSentAtSea = isAtSea;

                    var targetPt = mainParty.MoveTargetPoint.ToVec2();
                    var livePacket = new PlayerMoveOrderPacket
                    {
                        PeerId = ModContext.Client?.LocalPeerId ?? 1001,
                        MoveType = 3, // LivePositionSync
                        CurrentX = curPos.X,
                        CurrentY = curPos.Y,
                        TargetX = targetPt.IsValid ? targetPt.X : curPos.X,
                        TargetY = targetPt.IsValid ? targetPt.Y : curPos.Y,
                        Speed = curSpeed,
                        IsAtSea = isAtSea,
                        NavigationType = (byte)mainParty.DesiredAiNavigationType,
                        IsTargetingPort = false
                    };
                    ModContext.Client?.Send(livePacket, LiteNetLib.DeliveryMethod.Unreliable);
                }
            }
        }
    }

    [HarmonyPatch(typeof(MobileParty), "SetMoveGoToPoint")]
    public static class MoveGoToPointPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty __instance, CampaignVec2 point, MobileParty.NavigationType navigationType)
        {
            try
            {
                if (__instance == null) return true;

                // Server host dummy party is untouchable and must never move
                if (CalradiaConnectedSubModule.IsServerProcess &&
                    (__instance.IsMainParty || __instance.StringId == "cc_server_party"))
                {
                    return false;
                }

                if (!CalradiaConnectedSubModule.IsServerProcess &&
                    ModContext.IsClientConnected &&
                    __instance.IsMainParty)
                {
                    var cur = __instance.GetPosition2D;
                    MoveOrderDebouncer.EnsureSubscribed();
                    ModContext.Events.Publish(new LocalPartyMoveRequestedEvent
                    {
                        MoveType = 0, // GoToPoint
                        CurrentX = cur.X,
                        CurrentY = cur.Y,
                        TargetX = point.X,
                        TargetY = point.Y,
                        Speed = __instance.Speed,
                        IsAtSea = __instance.IsCurrentlyAtSea || navigationType == MobileParty.NavigationType.Naval,
                        NavigationType = (byte)navigationType
                    });
                }
            }
            catch (Exception) { }

            // Return true: Client-Side Prediction allows local party to smoothly begin pathing immediately
            return true;
        }
    }

    [HarmonyPatch(typeof(MobileParty), "SetMoveGoToSettlement")]
    public static class MoveGoToSettlementPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty __instance, Settlement settlement, MobileParty.NavigationType navigationType, bool isTargetingThePort)
        {
            try
            {
                if (__instance == null) return true;

                if (CalradiaConnectedSubModule.IsServerProcess &&
                    (__instance.IsMainParty || __instance.StringId == "cc_server_party"))
                {
                    return false;
                }

                if (!CalradiaConnectedSubModule.IsServerProcess &&
                    ModContext.IsClientConnected &&
                    __instance.IsMainParty &&
                    settlement != null)
                {
                    var cur = __instance.GetPosition2D;
                    var pos = (isTargetingThePort && settlement.HasPort && settlement.PortPosition.IsValid())
                        ? settlement.PortPosition.ToVec2()
                        : (settlement.GatePosition.IsValid() ? settlement.GatePosition.ToVec2() : settlement.GetPosition2D);
                    MoveOrderDebouncer.EnsureSubscribed();
                    ModContext.Events.Publish(new LocalPartyMoveRequestedEvent
                    {
                        MoveType = 1, // TargetSettlement
                        TargetSettlementId = settlement.StringId,
                        CurrentX = cur.X,
                        CurrentY = cur.Y,
                        TargetX = pos.X,
                        TargetY = pos.Y,
                        Speed = __instance.Speed,
                        IsAtSea = __instance.IsCurrentlyAtSea || isTargetingThePort || navigationType == MobileParty.NavigationType.Naval,
                        NavigationType = (byte)navigationType,
                        IsTargetingPort = isTargetingThePort
                    });
                }
            }
            catch (Exception) { }

            return true;
        }
    }

    [HarmonyPatch(typeof(MobileParty), "SetMoveEngageParty")]
    public static class MoveEngagePartyPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MobileParty __instance, MobileParty party, MobileParty.NavigationType navigationType)
        {
            try
            {
                if (__instance == null) return true;

                // Never allow server host party to move, and never allow any party to target cc_server_party
                if ((CalradiaConnectedSubModule.IsServerProcess && (__instance.IsMainParty || __instance.StringId == "cc_server_party")) ||
                    party?.StringId == "cc_server_party")
                {
                    return false;
                }

                if (!CalradiaConnectedSubModule.IsServerProcess &&
                    ModContext.IsClientConnected &&
                    __instance.IsMainParty &&
                    party != null)
                {
                    var cur = __instance.GetPosition2D;
                    var pos = party.GetPosition2D;
                    MoveOrderDebouncer.EnsureSubscribed();
                    ModContext.Events.Publish(new LocalPartyMoveRequestedEvent
                    {
                        MoveType = 2, // TargetParty
                        TargetPartyId = party.StringId,
                        CurrentX = cur.X,
                        CurrentY = cur.Y,
                        TargetX = pos.X,
                        TargetY = pos.Y,
                        Speed = __instance.Speed,
                        IsAtSea = __instance.IsCurrentlyAtSea || party.IsCurrentlyAtSea || navigationType == MobileParty.NavigationType.Naval,
                        NavigationType = (byte)navigationType
                    });
                }
            }
            catch (Exception) { }

            return true;
        }
    }
}
