using System;
using System.Linq;
using System.Reflection;
using CalradiaConnected.Campaign.Identity;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace CalradiaConnected.Campaign.Services
{
    /// <summary>
    /// Creates and maintains the special minor-faction style "Developer" clan (similar to "Gizli El" / Hidden Hand)
    /// containing strictly a single 25-year-old male hero named "konasoglu" with fair skin, brown beard,
    /// high level (35), elite skills, and a powerful party in Calradia.
    /// </summary>
    public sealed class DeveloperClanService
    {
        private static DeveloperClanService? _instance;
        public static DeveloperClanService Instance => _instance ??= new DeveloperClanService();

        public const string DeveloperClanId = "cc_clan_developer";
        public const string KonasogluHeroId = "cc_hero_konasoglu";
        public const string KonasogluPartyId = "cc_party_konasoglu";

        public void EnsureDeveloperClanExists()
        {
            try
            {
                var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                if (campaign == null) return;

                var culture = campaign.ObjectManager.GetObject<CultureObject>("empire")
                              ?? campaign.Factions.FirstOrDefault()?.Culture;
                if (culture == null) return;

                var homeSettlement = campaign.ObjectManager.GetObject<Settlement>("town_EW2")
                                     ?? campaign.ObjectManager.GetObject<Settlement>("town_ES1")
                                     ?? Settlement.All.FirstOrDefault(s => s.IsTown && s.Culture == culture)
                                     ?? Settlement.All.FirstOrDefault(s => s.IsTown);
                if (homeSettlement == null) return;

                // 1. Resolve or create the "Developer" Clan (Gizli El / Minor Faction style)
                var clan = campaign.CampaignObjectManager.Find<Clan>(DeveloperClanId)
                           ?? Clan.All.FirstOrDefault(c => c != null && c.StringId == DeveloperClanId)
                           ?? Clan.CreateClan(DeveloperClanId);

                var clanName = new TextObject("Developer");
                clan.ChangeClanName(clanName, clanName);
                clan.Culture = culture;
                clan.Banner = new Banner("11.149.128.1528.1528.764.764.1.0.0.434.128.116.480.480.764.764.1.0.0");
                clan.SetInitialHomeSettlement(homeSettlement);

                var defaultPt = culture.DefaultPartyTemplate
                                ?? campaign.ObjectManager.GetObject<PartyTemplateObject>("kingdom_hero_party_empire_template");
                if (defaultPt != null)
                {
                    typeof(Clan).GetField("_defaultPartyTemplate", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, defaultPt);
                }

                // Set minor faction / Gizli El style properties via reflection while keeping _minorFactionCharacterTemplates empty
                typeof(Clan).GetField("<IsMinorFaction>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, true);
                typeof(Clan).GetField("<IsMafia>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, true);
                typeof(Clan).GetField("<Color>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, 0xFF1E1B4Bu);
                typeof(Clan).GetField("<Color2>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, 0xFFF59E0Bu);
                var clanDescription = new TextObject(
                    "Calradia'nın bitmek bilmeyen savaşları lordları topraklarından uzak tutarken, halkın yönetimi kendilerini özgürlük savunucusu olarak tanıtan oligarkların eline geçmiştir. Özgürlük vaatleriyle halkı kendilerine bağımlı kılan bu düzene karşı Konasoglu önderliğinde bir isyan başlamıştır.\n\n" +
                    "Developer Klanı, gerçek özgürlüğün hiçbir hükümdara veya efendiye bağlı olmadan yaşamak olduğuna inanır. Ne kralların ne de sahte özgürlükçülerin boyunduruğunu kabul ederler. Amaçları, herkesin kendi kaderini tayin edebildiği özgür bir Calradia kurmaktır.");
                typeof(Clan).GetField("<EncyclopediaText>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, clanDescription);

                if (clan.BasicTroop == null && culture.EliteBasicTroop != null)
                {
                    clan.BasicTroop = culture.EliteBasicTroop;
                }
                else if (clan.BasicTroop == null && culture.BasicTroop != null)
                {
                    clan.BasicTroop = culture.BasicTroop;
                }

                if (clan.Renown < 3500f)
                {
                    clan.AddRenown(3500f - clan.Renown, false);
                }

                // 2. Resolve or create the sole hero "konasoglu" (25 years old, Male, Light Skin, Brown Beard)
                var hero = campaign.CampaignObjectManager.Find<Hero>(KonasogluHeroId)
                           ?? campaign.AliveHeroes.FirstOrDefault(h => h != null && h.StringId == KonasogluHeroId);

                if (hero == null)
                {
                    var template = culture.LordTemplates?.FirstOrDefault(t => t != null && !t.IsFemale && !t.StringId.StartsWith("spc_", StringComparison.OrdinalIgnoreCase))
                                   ?? culture.LordTemplates?.FirstOrDefault(t => t != null && !t.IsFemale)
                                   ?? CharacterObject.PlayerCharacter;

                    hero = HeroCreator.CreateSpecialHero(template, homeSettlement, clan, null, 25);
                    hero.StringId = KonasogluHeroId;
                }

                ApplyKonasogluAppearanceAndStats(hero, clan, culture, homeSettlement);
                clan.SetLeader(hero);
                clan.CalculateMidSettlement();

                // Ensure strictly ONLY "konasoglu" is in the Developer clan (remove any unintended extra members)
                if (clan.Heroes != null && clan.Heroes.Count > 1)
                {
                    var extras = clan.Heroes.Where(h => h != null && h != hero && h.StringId != KonasogluHeroId).ToList();
                    foreach (var extra in extras)
                    {
                        try
                        {
                            extra.Clan = null;
                        }
                        catch { }
                    }
                }

                // 3. Ensure konasoglu has an active elite party on the campaign map
                var party = hero.PartyBelongedTo
                            ?? campaign.CampaignObjectManager.Find<MobileParty>(KonasogluPartyId)
                            ?? MobileParty.All.FirstOrDefault(p => p != null && p.StringId == KonasogluPartyId);

                if (party == null)
                {
                    CampaignVec2 spawnPos = homeSettlement.GatePosition.IsValid()
                        ? homeSettlement.GatePosition
                        : new CampaignVec2(homeSettlement.GetPosition2D, isOnLand: true);

                    var lordCompCtor = typeof(LordPartyComponent).GetConstructor(
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        null,
                        new[] { typeof(Hero), typeof(Hero), typeof(LordPartyComponent.InitializationArgs) },
                        null);

                    if (lordCompCtor != null)
                    {
                        var comp = (PartyComponent)lordCompCtor.Invoke(new object?[] { hero, hero, null });
                        party = MobileParty.CreateParty(KonasogluPartyId, comp);
                        party.StringId = KonasogluPartyId;
                        party.AddElementToMemberRoster(hero.CharacterObject, 1, insertAtFront: true);
                        party.InitializeMobilePartyAtPosition(spawnPos);
                    }
                    else
                    {
                        party = LordPartyComponent.CreateLordParty(KonasogluPartyId, hero, spawnPos, 1f, homeSettlement, hero);
                        party.StringId = KonasogluPartyId;
                    }

                    PopulateKonasogluEliteTroops(party, campaign, culture);
                }

                party.IsVisible = party.CurrentSettlement == null;
                party.IsInspected = true;
                party.Party?.SetVisualAsDirty();
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] DeveloperClanService hatası: {ex.Message}");
            }
        }

        private static void ApplyKonasogluAppearanceAndStats(Hero hero, Clan clan, CultureObject culture, Settlement homeSettlement)
        {
            var heroName = new TextObject("konasoglu");
            hero.SetName(heroName, heroName);
            hero.IsFemale = false;
            hero.Culture = culture;
            hero.Clan = clan;
            hero.IsMinorFactionHero = true;
            hero.BornSettlement = homeSettlement;
            hero.UpdateHomeSettlement();
            hero.SetBirthDay(CampaignTime.YearsFromNow(-25f));
            hero.ChangeState(Hero.CharacterStates.Active);
            hero.EncyclopediaText = new TextObject(
                "Calradia'nın bitmek bilmeyen savaşları lordları topraklarından uzak tutarken, halkı sahte özgürlük vaatleriyle kendilerine bağımlı kılan oligarkların düzenine karşı baş kaldıran büyük isyanın 25 yaşındaki önderidir.\n\n" +
                "Developer Klanı'nın kurucusu ve tek lideri olan Konasoglu, gerçek özgürlüğün hiçbir hükümdara veya efendiye bağlı olmadan yaşamak olduğuna inanır. Ne kralların ne de sahte özgürlükçülerin boyunduruğunu kabul eder; tek amacı, herkesin kendi kaderini tayin edebildiği özgür bir Calradia kurmaktır.");
            try { hero.SetHasMet(); } catch { }

            if (hero.Gold < 500000)
            {
                hero.Gold = 500000;
            }

            // Fair/light skin tone (0005), chestnut-brown hair & full brown beard (AC, beard=13, hair=4D), age 25
            var staticBody = new StaticBodyProperties(
                0x0005AC06C000134DUL,
                0x59A4894CAAA8A4A6UL,
                0xBC7262A866999764UL,
                0x79AD7D8A99877A3AUL,
                0x007886030768E986UL,
                0x0000000000000000UL,
                0x0000000000000000UL,
                0x0000000000FC1142UL);
            hero.StaticBodyProperties = staticBody;
            hero.Weight = 0.45f;
            hero.Build = 0.70f;

            // High level (Level 35) & Elite Skills/Attributes
            hero.Level = 35;

            if (hero.HeroDeveloper != null)
            {
                try
                {
                    foreach (var attr in Attributes.All)
                    {
                        if (attr == null) continue;
                        int current = hero.GetAttributeValue(attr);
                        if (current < 10)
                        {
                            hero.HeroDeveloper.AddAttribute(attr, 10 - current, checkUnspentPoints: false);
                        }
                    }

                    foreach (var skill in Skills.All)
                    {
                        if (skill == null) continue;
                        if (hero.GetSkillValue(skill) < 280)
                        {
                            hero.HeroDeveloper.SetInitialSkillLevel(skill, 280);
                        }
                    }
                }
                catch { }
            }

            try
            {
                hero.SetTraitLevel(DefaultTraits.Valor, 2);
                hero.SetTraitLevel(DefaultTraits.Honor, 2);
                hero.SetTraitLevel(DefaultTraits.Generosity, 2);
                hero.SetTraitLevel(DefaultTraits.Calculating, 2);
                hero.SetTraitLevel(DefaultTraits.Mercy, 1);
            }
            catch { }

            // Equip high-tier lord battle gear & warhorse
            var objMgr = TaleWorlds.CampaignSystem.Campaign.Current?.ObjectManager;
            if (objMgr != null && hero.BattleEquipment != null)
            {
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Weapon0, "empire_sword_6_t5", "vlandia_sword_5_t5", "empire_sword_5_t4");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Weapon1, "Bound_horsemans_kite_shield", "fortified_kite_shield", "heavy_heater_shield");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Weapon2, "noble_long_bow", "composite_steppe_bow", "hunting_bow");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Weapon3, "bodkin_arrows_a", "piercing_arrows", "default_arrows");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Head, "imperial_jeweled_helmet", "closed_decorated_goggled_helmet", "ironlame_nasalhelm_over_imperial_coif");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Cape, "imperial_studded_strip_shoulders", "pauldrons_with_cape", "decorated_imperial_shoulder");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Body, "imperial_scale_armor", "lamellar_with_scale_skirt", "imperial_lamellar_over_leather");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Gloves, "imperial_decorated_gauntlets", "lamellar_plate_gauntlets", "reinforced_padded_mitten");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.Leg, "lamellar_plate_boots", "decorated_imperial_boots", "plated_strip_boots");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.ArmorItemEndSlot, "noble_horse_imperial", "t2_empire_horse", "charger");
                SetEquipmentSlotIfFound(objMgr, hero.BattleEquipment, EquipmentIndex.HorseHarness, "imperial_scale_barding", "half_scale_barding", "chain_barding");
            }
        }

        private static void SetEquipmentSlotIfFound(TaleWorlds.ObjectSystem.MBObjectManager objMgr, Equipment eq, EquipmentIndex slot, params string[] itemIds)
        {
            foreach (var id in itemIds)
            {
                var item = objMgr.GetObject<ItemObject>(id);
                if (item != null)
                {
                    eq[slot] = new EquipmentElement(item);
                    return;
                }
            }
        }

        private static void PopulateKonasogluEliteTroops(MobileParty party, TaleWorlds.CampaignSystem.Campaign campaign, CultureObject culture)
        {
            if (party?.MemberRoster == null) return;

            var cataphract = campaign.ObjectManager.GetObject<CharacterObject>("imperial_elite_cataphract")
                             ?? culture.EliteBasicTroop;
            var legionary = campaign.ObjectManager.GetObject<CharacterObject>("imperial_legionary")
                            ?? culture.BasicTroop;
            var palatine = campaign.ObjectManager.GetObject<CharacterObject>("imperial_palatine_guard")
                           ?? culture.BasicTroop;

            if (cataphract != null) party.MemberRoster.AddToCounts(cataphract, 50);
            if (legionary != null) party.MemberRoster.AddToCounts(legionary, 40);
            if (palatine != null) party.MemberRoster.AddToCounts(palatine, 30);

            if (DefaultItems.Grain != null)
            {
                party.ItemRoster.AddToCounts(DefaultItems.Grain, 100);
            }
            if (DefaultItems.Meat != null)
            {
                party.ItemRoster.AddToCounts(DefaultItems.Meat, 50);
            }
        }
    }
}
