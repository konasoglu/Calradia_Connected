namespace CalradiaConnected.Engine.Events
{
    /// <summary>
    /// Published by movement Harmony patches when the local player issues a move order on the campaign map.
    /// </summary>
    public sealed class LocalPartyMoveRequestedEvent
    {
        /// <summary>
        /// 0 = GoToPoint, 1 = GoToSettlement, 2 = EngageParty, 3 = LivePositionSync
        /// </summary>
        public byte MoveType { get; set; }
        public string? TargetSettlementId { get; set; }
        public string? TargetPartyId { get; set; }
        public float CurrentX { get; set; }
        public float CurrentY { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public float Speed { get; set; }
        public bool IsAtSea { get; set; }
        public byte NavigationType { get; set; } // 0=None, 1=Default, 2=Naval, 3=All
        public bool IsTargetingPort { get; set; }
    }

    /// <summary>
    /// Published when the local player finishes the Sandbox character & clan creation flow.
    /// </summary>
    public sealed class LocalCharacterCreationCompletedEvent
    {
        public int PeerId { get; set; }
        public string Username { get; set; } = string.Empty;
    }

    /// <summary>
    /// Published when a player encounter is intercepted on the campaign map.
    /// </summary>
    public sealed class PlayerEncounterInterceptedEvent
    {
        public string AttackerPartyId { get; set; } = string.Empty;
        public string DefenderPartyId { get; set; } = string.Empty;
        public string? SettlementId { get; set; }
    }

    /// <summary>
    /// Published when the local player recruits a volunteer troop from a settlement notable.
    /// </summary>
    public sealed class LocalVolunteerRecruitedEvent
    {
        public string SettlementId { get; set; } = string.Empty;
        public string NotableId { get; set; } = string.Empty;
        public int SlotIndex { get; set; }
        public string TroopId { get; set; } = string.Empty;
        public int Cost { get; set; }
    }

    /// <summary>
    /// Published when the local player enters or leaves a settlement or completes a market transaction.
    /// </summary>
    public sealed class LocalSettlementActionEvent
    {
        /// <summary>
        /// 1 = Enter, 2 = Leave, 3 = Trade
        /// </summary>
        public byte ActionType { get; set; }
        public string SettlementId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public int Count { get; set; }
        public int GoldDelta { get; set; }
    }

    /// <summary>
    /// Published when the local player's troop or item roster changes and needs synchronization.
    /// </summary>
    public sealed class LocalRosterChangedEvent
    {
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Published when a siege is started or lifted on the campaign map.
    /// </summary>
    public sealed class SiegeStateChangedEvent
    {
        public string SettlementId { get; set; } = string.Empty;
        public string BesiegerPartyId { get; set; } = string.Empty;
        public bool IsUnderSiege { get; set; }
        public float Progress { get; set; }
    }

    /// <summary>
    /// Published when a player initiates or receives a synchronized P2P Campaign Map Dialog.
    /// </summary>
    public sealed class P2PDialogUpdatedEvent
    {
        public Common.Packets.P2PDialogPacket Packet { get; set; } = new Common.Packets.P2PDialogPacket();
    }

    /// <summary>
    /// Published on clients when a hero execution / permadeath event occurs.
    /// </summary>
    public sealed class HeroExecutionReceivedEvent
    {
        public Common.Packets.HeroExecutionPacket Packet { get; set; } = new Common.Packets.HeroExecutionPacket();
    }

    /// <summary>
    /// Published across Campaign and Missions layers when a synchronized PvP battle changes stage
    /// (11 = Start/OpenMission, 13 = BothReady/CombatBegin, 14 = ReadyStatusUpdate, 2/3 = BattleEnded).
    /// </summary>
    public sealed class PvpBattleStateChangedEvent
    {
        public byte Stage { get; set; }
        public int AttackerPeerId { get; set; }
        public int DefenderPeerId { get; set; }
        public int OpponentPeerId { get; set; }
        public string OpponentName { get; set; } = string.Empty;
        public bool AttackerReady { get; set; }
        public bool DefenderReady { get; set; }
    }

    /// <summary>
    /// Published by CoopMissionSyncBehavior when a synchronized PvP battle mission ends (e.g., player presses TAB
    /// after battle resolution) so BattleSyncService immediately broadcasts the authoritative result and closes
    /// the opponent's mission without requiring both players to manually press TAB.
    /// </summary>
    public sealed class PvpMissionEndedEvent
    {
        public int OpponentPeerId { get; set; }
        public bool LocalWon { get; set; }
        public bool Resolved { get; set; }
    }
}
