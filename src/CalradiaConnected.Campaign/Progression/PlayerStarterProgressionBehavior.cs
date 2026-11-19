#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Common.Config;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Engine;
using CalradiaConnected.Engine.Events;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace CalradiaConnected.Campaign.Progression
{
    public class PlayerStarterProgressionBehavior : CampaignBehaviorBase
    {
        public static PlayerStarterProgressionBehavior? Instance { get; private set; }

        public int StartingLevel { get; set; } = 1;
        public int StartingFocusPoints { get; set; } = 5;
        public int StartingAttributePoints { get; set; } = 2;
        public int StartingAllSkillsLevel { get; set; } = 0;
        public float CharacterXpMultiplier { get; set; } = 1.0f;
        public float SkillXpMultiplier { get; set; } = 1.0f;
        public int StartingGold { get; set; } = 1000;
        public int StartingTroopsPreset { get; set; } = 0;
        public int StartingEquipmentPreset { get; set; } = 0;
        public float StartingRenown { get; set; } = 0f;
        public float StartingInfluence { get; set; } = 0f;
        public bool StartingFoodSupplies { get; set; } = true;

        private bool _progressionApplied = false;

        public PlayerStarterProgressionBehavior()
        {
            Instance = this;
            if (!ServerRuntimeSettings.Current.HasReceivedServerSync)
            {
                ServerRuntimeSettings.Current.LoadFromDisk();
            }
            SyncFromRuntimeSettings();
        }

        public void SyncFromRuntimeSettings()
        {
            var s = ServerRuntimeSettings.Current;
            StartingLevel = s.StartingLevel;
            StartingFocusPoints = s.StartingFocusPoints;
            StartingAttributePoints = s.StartingAttributePoints;
            StartingAllSkillsLevel = s.StartingAllSkillsLevel;
            CharacterXpMultiplier = s.CharacterXpMultiplier;
            SkillXpMultiplier = s.SkillXpMultiplier;
            StartingGold = s.StartingGold;
            StartingRenown = s.StartingRenown;
            StartingInfluence = s.StartingInfluence;
            StartingEquipmentPreset = s.StartingEquipmentPreset;
            StartingTroopsPreset = s.StartingTroopsPreset;
            StartingFoodSupplies = s.StartingFoodSupplies;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_progressionApplied", ref _progressionApplied);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            SyncFromRuntimeSettings();
            ServerGameRulesService.Instance.ApplyActiveRulesToCampaign();
        }

        private void OnHourlyTick()
        {
            if (_progressionApplied || ModContext.IsServerRunning) return;

            var hero = Hero.MainHero;
            if (hero == null || hero.Clan == null || MobileParty.MainParty == null) return;
            if (string.Equals(hero.StringId, "cc_server_host", StringComparison.Ordinal)) return;

            string key = GetHeroStarterRecordKey(hero);
            if (HasAppliedRecordOnDisk(key))
            {
                _progressionApplied = true;
                return;
            }

            ApplyProgression(hero, markApplied: true);
        }

        public void ApplyStarterForNewCharacter(Hero hero)
        {
            if (hero == null || ModContext.IsServerRunning) return;
            SyncFromRuntimeSettings();
            ApplyProgression(hero, markApplied: true);
        }

        public void ResetLocalProgressionAndReapply()
        {
            ClearAppliedRecordsOnDisk();
            _progressionApplied = false;
            SyncFromRuntimeSettings();

            if (!ModContext.IsServerRunning && Hero.MainHero != null && MobileParty.MainParty != null &&
                !string.Equals(Hero.MainHero.StringId, "cc_server_host", StringComparison.Ordinal))
            {
                ApplyProgression(Hero.MainHero, markApplied: true);
            }
        }

        public void ApplyProgression(Hero hero, bool markApplied = true)
        {
            if (hero == null) return;
            SyncFromRuntimeSettings();

            var clan = hero.Clan;
            var party = MobileParty.MainParty;

            // 1. Gold (exact top-up without double adding)
            if (StartingGold > 0 && hero.Gold < StartingGold)
            {
                hero.ChangeHeroGold(StartingGold - hero.Gold);
            }

            // 2. Renown (uses AddRenown so Clan Tier automatically upgrades!)
            if (clan != null)
            {
                if (StartingRenown > 0f && clan.Renown < StartingRenown)
                {
                    float delta = StartingRenown - clan.Renown;
                    clan.AddRenown(delta, true);
                }

                if (StartingInfluence > 0f && clan.Influence < StartingInfluence)
                {
                    clan.Influence = StartingInfluence;
                }
            }

            // 3. Starting All Skills Floor (0 / 25 / 50 / 100 / 150)
            if (hero.HeroDeveloper != null && StartingAllSkillsLevel > 0)
            {
                try
                {
                    foreach (var skill in Skills.All)
                    {
                        if (skill == null) continue;
                        int currentVal = hero.GetSkillValue(skill);
                        if (currentVal < StartingAllSkillsLevel)
                        {
                            hero.HeroDeveloper.SetInitialSkillLevel(skill, StartingAllSkillsLevel);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] StartingAllSkillsLevel error: {ex.Message}");
                }
            }

            // 4. Starting Level (1..30)
            if (hero.HeroDeveloper != null && StartingLevel > hero.Level)
            {
                try
                {
                    hero.HeroDeveloper.SetInitialLevel(StartingLevel);
                    int requiredXp = hero.HeroDeveloper.GetXpRequiredForLevel(StartingLevel);
                    if (hero.HeroDeveloper.TotalXp < requiredXp)
                    {
                        var gainRawXp = typeof(TaleWorlds.CampaignSystem.CharacterDevelopment.HeroDeveloper)
                            .GetMethod("GainRawXp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        gainRawXp?.Invoke(hero.HeroDeveloper, new object[] { (float)(requiredXp - hero.HeroDeveloper.TotalXp + 1), false });
                    }
                    if (hero.Level < StartingLevel)
                    {
                        hero.Level = StartingLevel;
                    }
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"[Calradia-Connected] StartingLevel error: {ex.Message}");
                }
            }

            // 5. Focus & Attribute Points
            if (hero.HeroDeveloper != null)
            {
                if (hero.HeroDeveloper.UnspentFocusPoints < StartingFocusPoints)
                {
                    hero.HeroDeveloper.UnspentFocusPoints = StartingFocusPoints;
                }
                if (hero.HeroDeveloper.UnspentAttributePoints < StartingAttributePoints)
                {
                    hero.HeroDeveloper.UnspentAttributePoints = StartingAttributePoints;
                }
            }

            // 6. Starting Equipment Preset (0=Vanilla, 1=Recruit T2-3, 2=Knight T4-5, 3=Royal Elite T6, 4=Merchant Caravan)
            if (StartingEquipmentPreset > 0)
            {
                ApplyEquipmentPreset(hero, party, StartingEquipmentPreset);
            }

            // 7. Starting Troops Preset (0=Solo, 1=10 Basic, 2=20 Basic + 5 Noble, 3=35 Basic + 15 Elite)
            int troopsAdded = 0;
            if (party != null && party.MemberRoster != null && StartingTroopsPreset > 0)
            {
                troopsAdded = ApplyTroopsPreset(hero, party, StartingTroopsPreset);
            }

            // 8. Starting Food Supplies
            if (party != null && party.ItemRoster != null && StartingFoodSupplies)
            {
                ApplyStarterFoodSupplies(party);
            }

            if (markApplied)
            {
                _progressionApplied = true;
                RecordAppliedOnDisk(GetHeroStarterRecordKey(hero));
            }

            PartyBase.MainParty?.SetVisualAsDirty();
            PartyBase.MainParty?.SetLevelMaskIsDirty();

            ModContext.Events.Publish(new LocalRosterChangedEvent
            {
                Reason = "StarterProgression"
            });

            InformationManager.DisplayMessage(new InformationMessage(
                Loc.F(
                    "[Calradia-Connected] Başlangıç paketi uygulandı: Seviye {0}, {1:N0} Dinar, +{2} Asker, {3:0.#} Nam, {4:0.#} Nüfuz.",
                    "[Calradia-Connected] Starting package applied: Level {0}, {1:N0} Denars, +{2} Troops, {3:0.#} Renown, {4:0.#} Influence.",
                    hero.Level, hero.Gold, troopsAdded, clan?.Renown ?? 0f, clan?.Influence ?? 0f),
                Colors.Green));
        }

        private static int ApplyTroopsPreset(Hero hero, MobileParty party, int preset)
        {
            var culture = hero.Culture ?? hero.Clan?.Culture;
            var basicTroop = culture?.BasicTroop;
            var nobleTroop = culture?.EliteBasicTroop ?? basicTroop;
            if (basicTroop == null) return 0;

            // For tier 3 preset, try picking an upgraded elite troop along the noble upgrade path
            CharacterObject eliteTroop = nobleTroop ?? basicTroop;
            if (preset >= 3 && nobleTroop != null)
            {
                var upgraded = GetUpgradedTroop(nobleTroop, 2);
                if (upgraded != null) eliteTroop = upgraded;
            }
            else if (preset == 2 && nobleTroop != null)
            {
                var upgraded = GetUpgradedTroop(nobleTroop, 1);
                if (upgraded != null) nobleTroop = upgraded;
            }

            int targetBasic = 0;
            int targetElite = 0;
            switch (preset)
            {
                case 1:
                    targetBasic = 10;
                    break;
                case 2:
                    targetBasic = 20;
                    targetElite = 5;
                    break;
                case 3:
                    targetBasic = 35;
                    targetElite = 15;
                    break;
            }

            int totalDesired = targetBasic + targetElite;
            int currentRegulars = party.MemberRoster.TotalRegulars;
            if (currentRegulars >= totalDesired) return 0;

            int added = 0;
            if (targetBasic > 0)
            {
                int needBasic = Math.Max(0, targetBasic - currentRegulars);
                if (needBasic > 0)
                {
                    party.MemberRoster.AddToCounts(basicTroop, needBasic);
                    added += needBasic;
                }
            }

            if (targetElite > 0 && eliteTroop != null)
            {
                int remaining = Math.Max(0, totalDesired - party.MemberRoster.TotalRegulars);
                int toAdd = Math.Min(targetElite, remaining);
                if (toAdd > 0)
                {
                    party.MemberRoster.AddToCounts(eliteTroop, toAdd);
                    added += toAdd;
                }
            }

            return added;
        }

        private static CharacterObject? GetUpgradedTroop(CharacterObject start, int steps)
        {
            var current = start;
            for (int i = 0; i < steps; i++)
            {
                if (current?.UpgradeTargets == null || current.UpgradeTargets.Length == 0) break;
                current = current.UpgradeTargets[0];
            }
            return current;
        }

        private static void ApplyEquipmentPreset(Hero hero, MobileParty? party, int preset)
        {
            try
            {
                var eq = hero.BattleEquipment;
                if (eq == null) return;

                var culture = hero.Culture ?? hero.Clan?.Culture;
                var allItems = Items.All?.Where(i => i != null && !i.IsCraftedByPlayer && !i.NotMerchandise).ToList();
                if (allItems == null || allItems.Count == 0) return;

                if (preset == 4)
                {
                    // Merchant Caravan Kit: Tier 2-3 armor + sword, 16x Mules/Sumpter Horses, trade goods & 50x Grain
                    EquipBestMatch(eq, EquipmentIndex.Body, allItems, ItemObject.ItemTypeEnum.BodyArmor, 2, 3, culture);
                    EquipBestMatch(eq, EquipmentIndex.Weapon0, allItems, ItemObject.ItemTypeEnum.OneHandedWeapon, 2, 3, culture);
                    EquipBestMatch(eq, EquipmentIndex.Horse, allItems, ItemObject.ItemTypeEnum.Horse, 1, 2, culture);
                    EquipBestMatch(eq, EquipmentIndex.HorseHarness, allItems, ItemObject.ItemTypeEnum.HorseHarness, 1, 2, culture);

                    if (party?.ItemRoster != null)
                    {
                        AddItemByIdOrCategory(party, allItems, new[] { "mule", "sumpter_horse" }, 16);
                        AddItemByIdOrCategory(party, allItems, new[] { "oil" }, 10);
                        AddItemByIdOrCategory(party, allItems, new[] { "linen" }, 10);
                        AddItemByIdOrCategory(party, allItems, new[] { "wine" }, 10);
                        AddItemByIdOrCategory(party, allItems, new[] { "beer" }, 10);
                        if (DefaultItems.Grain != null)
                        {
                            party.ItemRoster.AddToCounts(DefaultItems.Grain, 50);
                        }
                    }
                    return;
                }

                int minTier = preset == 1 ? 1 : (preset == 2 ? 3 : 5);
                int maxTier = preset == 1 ? 3 : (preset == 2 ? 5 : 6);

                EquipBestMatch(eq, EquipmentIndex.Head, allItems, ItemObject.ItemTypeEnum.HeadArmor, minTier, maxTier, culture);
                EquipBestMatch(eq, EquipmentIndex.Body, allItems, ItemObject.ItemTypeEnum.BodyArmor, minTier, maxTier, culture);
                EquipBestMatch(eq, EquipmentIndex.Cape, allItems, ItemObject.ItemTypeEnum.Cape, minTier, maxTier, culture);
                EquipBestMatch(eq, EquipmentIndex.Gloves, allItems, ItemObject.ItemTypeEnum.HandArmor, minTier, maxTier, culture);
                EquipBestMatch(eq, EquipmentIndex.Leg, allItems, ItemObject.ItemTypeEnum.LegArmor, minTier, maxTier, culture);
                EquipBestMatch(eq, EquipmentIndex.Horse, allItems, ItemObject.ItemTypeEnum.Horse, minTier, maxTier, culture);
                EquipBestMatch(eq, EquipmentIndex.HorseHarness, allItems, ItemObject.ItemTypeEnum.HorseHarness, minTier, maxTier, culture);

                EquipBestMatch(eq, EquipmentIndex.Weapon0, allItems, ItemObject.ItemTypeEnum.OneHandedWeapon, minTier, maxTier, culture);
                EquipBestMatch(eq, EquipmentIndex.Weapon1, allItems, ItemObject.ItemTypeEnum.Shield, minTier, maxTier, culture);

                if (preset == 1)
                {
                    EquipBestMatch(eq, EquipmentIndex.Weapon2, allItems, ItemObject.ItemTypeEnum.Bow, minTier, maxTier, culture);
                    EquipBestMatch(eq, EquipmentIndex.Weapon3, allItems, ItemObject.ItemTypeEnum.Arrows, minTier, maxTier, culture);
                }
                else if (preset == 2)
                {
                    EquipBestMatch(eq, EquipmentIndex.Weapon2, allItems, ItemObject.ItemTypeEnum.Polearm, minTier, maxTier, culture);
                    EquipBestMatch(eq, EquipmentIndex.Weapon3, allItems, ItemObject.ItemTypeEnum.Thrown, minTier, maxTier, culture);
                }
                else // preset == 3 Royal Elite
                {
                    EquipBestMatch(eq, EquipmentIndex.Weapon2, allItems, ItemObject.ItemTypeEnum.Bow, minTier, maxTier, culture);
                    EquipBestMatch(eq, EquipmentIndex.Weapon3, allItems, ItemObject.ItemTypeEnum.Arrows, minTier, maxTier, culture);
                }
            }
            catch (Exception ex)
            {
                Debug.PrintError($"[Calradia-Connected] ApplyEquipmentPreset error: {ex.Message}");
            }
        }

        private static void EquipBestMatch(
            Equipment eq,
            EquipmentIndex slot,
            List<ItemObject> pool,
            ItemObject.ItemTypeEnum itemType,
            int minTier,
            int maxTier,
            CultureObject? preferredCulture)
        {
            var candidates = pool.Where(i => i.ItemType == itemType && !i.IsCivilian).ToList();
            if (candidates.Count == 0)
            {
                candidates = pool.Where(i => i.ItemType == itemType).ToList();
            }
            if (candidates.Count == 0) return;

            var tierMatches = candidates.Where(i => (int)i.Tier >= minTier && (int)i.Tier <= maxTier).ToList();
            if (tierMatches.Count == 0) tierMatches = candidates;

            ItemObject? chosen = null;
            if (preferredCulture != null)
            {
                chosen = tierMatches
                    .Where(i => i.Culture == preferredCulture)
                    .OrderByDescending(i => (int)i.Tier)
                    .ThenByDescending(i => i.Value)
                    .FirstOrDefault();
            }

            chosen ??= tierMatches
                .OrderByDescending(i => (int)i.Tier)
                .ThenByDescending(i => i.Value)
                .FirstOrDefault();

            if (chosen != null)
            {
                eq[slot] = new EquipmentElement(chosen);
            }
        }

        private static void AddItemByIdOrCategory(MobileParty party, List<ItemObject> pool, string[] ids, int count)
        {
            var objMgr = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager;
            foreach (var id in ids)
            {
                var item = objMgr?.GetObject<ItemObject>(id)
                    ?? pool.FirstOrDefault(i => string.Equals(i.StringId, id, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    party.ItemRoster.AddToCounts(item, count);
                    return;
                }
            }
        }

        private static void ApplyStarterFoodSupplies(MobileParty party)
        {
            try
            {
                if (DefaultItems.Grain != null && party.ItemRoster.GetItemNumber(DefaultItems.Grain) < 30)
                {
                    party.ItemRoster.AddToCounts(DefaultItems.Grain, 30 - party.ItemRoster.GetItemNumber(DefaultItems.Grain));
                }

                var objMgr = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager;
                if (objMgr == null) return;

                foreach (var foodId in new[] { "butter", "cheese", "meat", "fish" })
                {
                    var food = objMgr.GetObject<ItemObject>(foodId);
                    if (food != null && party.ItemRoster.GetItemNumber(food) < 10)
                    {
                        party.ItemRoster.AddToCounts(food, 10 - party.ItemRoster.GetItemNumber(food));
                    }
                }
            }
            catch { }
        }

        private static string GetHeroStarterRecordKey(Hero hero)
        {
            int peerId = ModContext.Client?.LocalPeerId ?? 0;
            return $"{peerId}:{hero.StringId}";
        }

        private static string GetPrimaryAppliedRecordPath()
        {
            foreach (var dir in ServerRuntimeSettings.GetCandidateConfigDirs())
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "starter_applied.json");
                }
                catch { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord", "CalradiaConnectedData", "starter_applied.json");
        }

        private static bool HasAppliedRecordOnDisk(string key)
        {
            try
            {
                foreach (var dir in ServerRuntimeSettings.GetCandidateConfigDirs())
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir, "starter_applied.json");
                    if (!File.Exists(p)) continue;
                    var list = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(p));
                    if (list != null && list.Contains(key)) return true;
                }
            }
            catch { }
            return false;
        }

        private static void RecordAppliedOnDisk(string key)
        {
            try
            {
                string path = GetPrimaryAppliedRecordPath();
                var list = File.Exists(path)
                    ? (JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path)) ?? new List<string>())
                    : new List<string>();
                if (!list.Contains(key))
                {
                    list.Add(key);
                    File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
                }
            }
            catch { }
        }

        public static void ClearAppliedRecordsOnDisk()
        {
            foreach (var dir in ServerRuntimeSettings.GetCandidateConfigDirs())
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string p = Path.Combine(dir, "starter_applied.json");
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }
        }
    }
}
