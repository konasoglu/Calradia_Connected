using System;
using CalradiaConnected.Common.Packets;
using CalradiaConnected.Engine;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Missions.Arena
{
    public class ArenaDuelManager
    {
        private static ArenaDuelManager? _instance;
        public static ArenaDuelManager Instance => _instance ??= new ArenaDuelManager();

        public bool IsDuelActive { get; private set; }
        public int ChallengerPeerId { get; private set; }
        public int TargetPeerId { get; private set; }
        public Agent? ChallengerAgent { get; private set; }
        public Agent? TargetAgent { get; private set; }

        public int ChallengerScore { get; private set; }
        public int TargetScore { get; private set; }
        public int CurrentRound { get; private set; } = 1;
        public const int MAX_ROUNDS = 3;

        private Vec3 _spawnPos1 = new Vec3(50f, 50f, 0f);
        private Vec3 _spawnPos2 = new Vec3(70f, 70f, 0f);

        public void StartDuel(int challengerId, int targetId, Agent challenger, Agent target)
        {
            ChallengerPeerId = challengerId;
            TargetPeerId = targetId;
            ChallengerAgent = challenger;
            TargetAgent = target;

            ChallengerScore = 0;
            TargetScore = 0;
            CurrentRound = 1;
            IsDuelActive = true;

            // Record initial positions as arena spawn corners
            if (challenger != null && target != null)
            {
                _spawnPos1 = challenger.Position;
                _spawnPos2 = target.Position;
            }

            ResetDuelRoundPositions();

            InformationManager.DisplayMessage(new InformationMessage($"[Arena Düello] Düello başladı! Raunt 1/{MAX_ROUNDS}", Colors.Cyan));
        }

        public void StopDuel(string reason = "")
        {
            IsDuelActive = false;
            ChallengerAgent = null;
            TargetAgent = null;

            if (!string.IsNullOrEmpty(reason))
            {
                InformationManager.DisplayMessage(new InformationMessage($"[Arena Düello] Düello sona erdi: {reason}", Colors.Yellow));
            }
        }

        public bool IsDuelist(Agent? agent)
        {
            if (!IsDuelActive || agent == null) return false;
            return agent == ChallengerAgent || agent == TargetAgent;
        }

        public void InterceptDuelBlow(Agent victim, ref Blow blow)
        {
            if (!IsDuelActive || !IsDuelist(victim)) return;

            // 1 HP Safeguard: Never let fatal blow trigger TaleWorlds ragdoll destruction!
            if (blow.InflictedDamage >= victim.Health)
            {
                blow.InflictedDamage = 0;
                victim.Health = 1f;

                Agent winner = (victim == ChallengerAgent) ? TargetAgent! : ChallengerAgent!;
                OnDuelistDefeated(victim, winner);
            }
        }

        public void OnDuelistDefeated(Agent loser, Agent winner)
        {
            if (!IsDuelActive) return;

            bool challengerWon = (winner == ChallengerAgent);
            if (challengerWon)
                ChallengerScore++;
            else
                TargetScore++;

            int winnerPeerId = challengerWon ? ChallengerPeerId : TargetPeerId;

            InformationManager.DisplayMessage(new InformationMessage(
                $"[Arena Düello] Raunt {CurrentRound} bitti! Skor: {ChallengerScore} - {TargetScore}",
                Colors.Green));

            bool matchFinished = (ChallengerScore >= 2 || TargetScore >= 2 || CurrentRound >= MAX_ROUNDS);

            // Notify network peers of round result
            var resetPacket = new MissionDuelRoundResetPacket
            {
                RoundNumber = CurrentRound,
                WinnerPeerId = winnerPeerId,
                MatchFinished = matchFinished
            };

            if (ModContext.IsServerRunning)
            {
                ModContext.Server?.Broadcast(resetPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else if (ModContext.IsClientConnected)
            {
                ModContext.Client?.Send(resetPacket, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            if (matchFinished)
            {
                string matchWinnerName = (ChallengerScore > TargetScore) ? "Meydan Okuyan" : "Rakip";
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Arena Düello] MAÇ BİTTİ! Kazanan: {matchWinnerName} ({ChallengerScore} - {TargetScore})",
                    Colors.Magenta));
                StopDuel("Maç tamamlandı");
            }
            else
            {
                CurrentRound++;
                ResetDuelRoundPositions();
            }
        }

        public void ResetDuelRoundPositions()
        {
            if (!IsDuelActive) return;

            ModContext.Dispatcher.Enqueue(() =>
            {
                if (ChallengerAgent != null && ChallengerAgent.IsActive())
                {
                    ChallengerAgent.TeleportToPosition(_spawnPos1);
                    ChallengerAgent.Health = ChallengerAgent.HealthLimit;
                }

                if (TargetAgent != null && TargetAgent.IsActive())
                {
                    TargetAgent.TeleportToPosition(_spawnPos2);
                    TargetAgent.Health = TargetAgent.HealthLimit;
                }
            });
        }
    }
}

