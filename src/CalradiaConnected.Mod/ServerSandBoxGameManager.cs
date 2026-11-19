using System;
using SandBox;
using TaleWorldsCampaign = TaleWorlds.CampaignSystem.Campaign;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace CalradiaConnected.Mod
{
    public class ServerSandBoxGameManager : SandBoxGameManager
    {
        public ServerSandBoxGameManager() : base(() => new TaleWorldsCampaign(CampaignGameMode.Campaign, new TaleWorlds.CampaignSystem.AdvancedStartOptions.AdvancedStartOptionsData()))
        {
        }

        public override void OnLoadFinished()
        {
            try
            {
                // Dedicated Server: Initialize host entity with a neutral server identity
                if (Hero.MainHero != null)
                {
                    Hero.MainHero.SetName(new TaleWorlds.Localization.TextObject("Sunucu"), new TaleWorlds.Localization.TextObject("Sunucu"));
                    Hero.MainHero.StringId = "cc_server_host";
                }

                if (Clan.PlayerClan != null)
                {
                    Clan.PlayerClan.ChangeClanName(new TaleWorlds.Localization.TextObject("Sunucu"), new TaleWorlds.Localization.TextObject("Sunucu"));
                    Clan.PlayerClan.StringId = "cc_server_clan";
                }

                if (TaleWorldsCampaign.Current != null)
                {
                    // Keep TrueSight disabled so the server does not render hundreds of out-of-range parties
                    // and generated saves preserve normal Fog of War for joining clients.
                    TaleWorldsCampaign.Current.TrueSight = false;
                    TaleWorldsCampaign.Current.TimeControlMode = CalradiaConnected.Campaign.Time.CampaignTimeSyncBehavior.AuthoritativeSpeedMode;
                }

                if (PartyBase.MainParty?.MobileParty != null)
                {
                    var serverParty = PartyBase.MainParty.MobileParty;
                    serverParty.StringId = "cc_server_party";
                    serverParty.IsVisible = false;
                    serverParty.SetMoveModeHold();
                    serverParty.Ai?.SetDoNotMakeNewDecisions(true);
                    serverParty.IgnoreByOtherPartiesTill(CampaignTime.Never);
                    if (DefaultItems.Grain != null)
                    {
                        serverParty.ItemRoster.AddToCounts(DefaultItems.Grain, 10000);
                    }
                }

                // Dedicated Server: Skip intro video and character creation screens completely.
                // Push MapState directly into the newly generated sandbox campaign world.
                if (Game.Current?.GameStateManager != null)
                {
                    var mapState = Game.Current.GameStateManager.CreateState<MapState>();
                    Game.Current.GameStateManager.CleanAndPushState(mapState);
                    mapState?.OnLoadingFinished();
                }

                PartyBase.MainParty?.SetVisualAsDirty();
                CampaignEventDispatcher.Instance?.OnCharacterCreationIsOver();

                foreach (var settlement in Settlement.All)
                {
                    settlement.Party?.SetLevelMaskIsDirty();
                }

                IsLoaded = true;

                InformationManager.DisplayMessage(new InformationMessage(
                    "[Calradia-Connected] Yepyeni Sandbox sefer dünyası oluşturuldu ve harita hazırlandı!",
                    Colors.Green));

                // Request auto-save on the main game thread so clients can download it over FastDL
                CalradiaConnectedSubModule.RequestNewGameSave();
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ServerSandBoxGameManager OnLoadFinished hatası: {ex}");
                base.OnLoadFinished();
            }
        }
    }
}
