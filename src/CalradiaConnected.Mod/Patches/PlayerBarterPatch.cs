using System;
using System.Collections.Generic;
using System.Reflection;
using CalradiaConnected.Campaign.Identity;
using CalradiaConnected.Campaign.Services;
using CalradiaConnected.Common.Localization;
using CalradiaConnected.Engine;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.ViewModelCollection.Barter;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace CalradiaConnected.Mod.Patches
{
    internal static class LiveBarterUIBridge
    {
        private static readonly MethodInfo? ChangeBarterableIsOfferedMethod =
            typeof(BarterVM).GetMethod("ChangeBarterableIsOffered", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo? OfferItemAddMethod =
            typeof(BarterVM).GetMethod("OfferItemAdd", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo? OfferItemRemoveMethod =
            typeof(BarterVM).GetMethod("OfferItemRemove", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo? RefreshResultBarMethod =
            typeof(BarterVM).GetMethod("RefreshResultBar", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo? RefreshCompatibilityMethod =
            typeof(BarterVM).GetMethod("RefreshCompatibility", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? BarterDataField =
            typeof(BarterVM).GetField("_barterData", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? BarterListField =
            typeof(BarterVM).GetField("_barterList", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? BarterableField =
            typeof(BarterItemVM).GetField("Barterable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SetPrisonerFreeHeroField =
            typeof(SetPrisonerFreeBarterable).GetField("_prisonerCharacter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TransferPrisonerHeroField =
            typeof(TransferPrisonerBarterable).GetField("_prisonerCharacter", BindingFlags.Instance | BindingFlags.NonPublic);

        private static WeakReference<BarterVM>? _activeVmRef;
        private static BarterData? _activeBarterData;
        private static bool _eventsHooked;

        public static void EnsureHooked()
        {
            if (_eventsHooked) return;
            _eventsHooked = true;
            PlayerInteractionService.Instance.PartnerBarterTokensUpdated += OnPartnerTokensUpdated;
            PlayerInteractionService.Instance.LiveBarterStatusChanged += OnLiveStatusChanged;
            PlayerInteractionService.Instance.LiveBarterCloseRequested += DetachActive;
        }

        public static void Attach(BarterVM vm, BarterData? data)
        {
            EnsureHooked();
            _activeVmRef = new WeakReference<BarterVM>(vm);
            _activeBarterData = data;
            RefreshOfferLabel();
        }

        public static void Detach(BarterVM vm)
        {
            if (_activeVmRef != null && _activeVmRef.TryGetTarget(out var current) && ReferenceEquals(current, vm))
            {
                DetachActive();
            }
        }

        private static void DetachActive()
        {
            _activeVmRef = null;
            _activeBarterData = null;
        }

        public static bool IsActivePlayerBarter(BarterVM? vm = null)
        {
            if (!ModContext.IsClientConnected || !PlayerInteractionService.Instance.IsLiveBarterOpen)
            {
                return false;
            }
            if (vm == null) return _activeVmRef != null && _activeVmRef.TryGetTarget(out _);
            return _activeVmRef != null && _activeVmRef.TryGetTarget(out var active) && ReferenceEquals(active, vm);
        }

        public static bool IsPartnerOwned(BarterItemVM? itemVm)
        {
            var barterable = GetBarterable(itemVm);
            return barterable?.OriginalOwner != null && barterable.OriginalOwner != Hero.MainHero;
        }

        public static Barterable? GetBarterable(BarterItemVM? itemVm) =>
            itemVm == null ? null : BarterableField?.GetValue(itemVm) as Barterable;

        public static string EncodeLocalOfferedTokens(BarterData? data)
        {
            data ??= _activeBarterData;
            if (data == null || Hero.MainHero == null) return string.Empty;
            return PlayerInteractionService.EncodeLordBarterTokens(Hero.MainHero, data.GetOfferedBarterables(), filterOwner: Hero.MainHero);
        }

        public static string EncodeLocalOfferedTokens(BarterVM vm) =>
            EncodeLocalOfferedTokens(BarterDataField?.GetValue(vm) as BarterData);

        public static void SyncLocalOfferIfChanged(BarterVM vm)
        {
            if (IsActivePlayerBarter(vm) && !PlayerInteractionService.Instance.IsApplyingRemoteBarterSync)
            {
                string tokens = EncodeLocalOfferedTokens(vm);
                PlayerInteractionService.Instance.NotifyLocalBarterOfferChanged(tokens);
            }
        }

        public static void ApplyLiveButtonState(BarterVM vm)
        {
            if (vm == null || !IsActivePlayerBarter(vm)) return;
            try
            {
                vm.OfferLbl = PlayerInteractionService.Instance.GetLiveBarterOfferButtonLabel();
                bool hasAny = !string.IsNullOrEmpty(PlayerInteractionService.Instance.LiveBarterLocalTokens)
                              || !string.IsNullOrEmpty(PlayerInteractionService.Instance.LiveBarterPartnerTokens)
                              || ((BarterDataField?.GetValue(vm) as BarterData)?.GetOfferedBarterables().Count ?? 0) > 0;
                vm.IsOfferDisabled = !hasAny;
                vm.ResultBarOffererPercentage = 50;
                vm.ResultBarOtherPercentage = 50;
            }
            catch { }
        }

        public static void RefreshOfferLabel()
        {
            if (_activeVmRef == null || !_activeVmRef.TryGetTarget(out var vm)) return;
            try
            {
                RefreshResultBarMethod?.Invoke(vm, null);
                ApplyLiveButtonState(vm);
            }
            catch { }
        }

        private static void OnLiveStatusChanged() => RefreshOfferLabel();

        private static void OnPartnerTokensUpdated(string partnerTokens) => ApplyPartnerTokensToActiveVM(partnerTokens);

        public static void ApplyPartnerTokensToActiveVM(string tokensCsv)
        {
            if (_activeVmRef == null || !_activeVmRef.TryGetTarget(out var vm)) return;
            if (BarterListField?.GetValue(vm) is not List<Dictionary<BarterGroup, MBBindingList<BarterItemVM>>> lists || lists.Count == 0)
            {
                return;
            }

            var desiredCounts = ParsePartnerOfferCounts(tokensCsv);
            var matchedKeys = new HashSet<string>(StringComparer.Ordinal);
            PlayerInteractionService.Instance.IsApplyingRemoteBarterSync = true;
            try
            {
                foreach (var groupDict in lists)
                {
                    if (groupDict == null) continue;
                    foreach (var list in groupDict.Values)
                    {
                        if (list == null) continue;
                        foreach (var itemVm in list)
                        {
                            var b = GetBarterable(itemVm);
                            if (b == null || b.OriginalOwner == Hero.MainHero) continue;

                            string? key = GetMatchKey(b);
                            int targetAmt = key != null && desiredCounts.TryGetValue(key, out int amt) ? amt : 0;
                            if (key != null && targetAmt > 0)
                            {
                                matchedKeys.Add(key);
                            }

                            int currentAmt = (itemVm.IsOffered || b.IsOffered) ? Math.Max(0, itemVm.CurrentOfferedAmount) : 0;
                            if (targetAmt > 0)
                            {
                                if (b is GoldBarterable)
                                {
                                    if (b.OriginalOwner != null && b.OriginalOwner.Gold < targetAmt)
                                    {
                                        b.OriginalOwner.Gold = targetAmt;
                                    }
                                    if (vm.LeftMaxGold < targetAmt)
                                    {
                                        vm.LeftMaxGold = targetAmt;
                                    }
                                    if (itemVm.TotalItemCount < targetAmt)
                                    {
                                        itemVm.TotalItemCount = targetAmt;
                                    }
                                }

                                int maxAllowed = itemVm.TotalItemCount > 0 ? itemVm.TotalItemCount : targetAmt;
                                int clamped = Math.Max(1, Math.Min(targetAmt, maxAllowed));

                                if (clamped > currentAmt)
                                {
                                    if (OfferItemAddMethod != null)
                                    {
                                        OfferItemAddMethod.Invoke(vm, new object[] { itemVm, clamped - currentAmt });
                                    }
                                    else
                                    {
                                        if (!b.IsOffered) ChangeBarterableIsOfferedMethod?.Invoke(vm, new object[] { b, true });
                                        if (itemVm.IsMultiple || b is GoldBarterable) itemVm.CurrentOfferedAmount = clamped;
                                    }
                                }
                                else if (clamped < currentAmt)
                                {
                                    if (OfferItemRemoveMethod != null)
                                    {
                                        OfferItemRemoveMethod.Invoke(vm, new object[] { itemVm, currentAmt - clamped });
                                    }
                                    else if (itemVm.IsMultiple || b is GoldBarterable)
                                    {
                                        itemVm.CurrentOfferedAmount = clamped;
                                    }
                                }

                                if (itemVm.IsMultiple || b is GoldBarterable)
                                {
                                    if (itemVm.CurrentOfferedAmount != clamped) itemVm.CurrentOfferedAmount = clamped;
                                    if (b.CurrentAmount != clamped) b.CurrentAmount = clamped;
                                }
                                RefreshCompatibilityMethod?.Invoke(vm, new object[] { itemVm, true });
                            }
                            else if (itemVm.IsOffered || b.IsOffered)
                            {
                                if (OfferItemRemoveMethod != null && currentAmt > 0)
                                {
                                    OfferItemRemoveMethod.Invoke(vm, new object[] { itemVm, currentAmt });
                                }
                                if (b.IsOffered)
                                {
                                    ChangeBarterableIsOfferedMethod?.Invoke(vm, new object[] { b, false });
                                }
                                RefreshCompatibilityMethod?.Invoke(vm, new object[] { itemVm, false });
                            }
                        }
                    }
                }

                // If partner offered an item not yet present in our stale local snapshot, display exact summary in chat
                if (desiredCounts.Count > matchedKeys.Count && !string.IsNullOrEmpty(tokensCsv))
                {
                    string partnerName = vm.LeftNameLbl ?? Loc.T("Oyuncu", "Partner");
                    string myName = Hero.MainHero?.Name?.ToString() ?? Loc.T("Siz", "You");
                    string summary = PlayerInteractionService.FormatLordBarterSummary(tokensCsv, perspectiveIsPartner: false, myName, partnerName);
                    InformationManager.DisplayMessage(new InformationMessage(
                        Loc.F("[Ticaret] Karşı teklif güncellendi: {0}", "[Trade] Partner offer updated: {0}", summary),
                        Colors.Cyan));
                }

                RefreshResultBarMethod?.Invoke(vm, null);
                ApplyLiveButtonState(vm);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] ApplyPartnerTokensToActiveVM error: {ex.Message}");
            }
            finally
            {
                PlayerInteractionService.Instance.IsApplyingRemoteBarterSync = false;
            }
        }

        private static Dictionary<string, int> ParsePartnerOfferCounts(string? tokensCsv)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(tokensCsv)) return result;

            foreach (string raw in tokensCsv!.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] parts = raw.Split(':');
                if (parts.Length < 3) continue;

                string kind = parts[0];
                if (kind == "G" && int.TryParse(parts[2], out int gold) && gold > 0)
                {
                    result["G"] = gold;
                }
                else if (kind == "T" && parts.Length >= 4 && int.TryParse(parts[3], out int amt) && amt > 0)
                {
                    string key = $"T:{parts[2]}";
                    result[key] = result.TryGetValue(key, out int prev) ? prev + amt : amt;
                }
                else if (kind == "F")
                {
                    result[$"F:{parts[2]}"] = 1;
                }
                else if (kind == "P")
                {
                    result[$"P:{parts[1]}:{parts[2]}"] = 1;
                }
                else if (kind == "R" || kind == "X")
                {
                    result[$"{kind}:{parts[2]}"] = 1;
                }
            }
            return result;
        }

        private static string? GetMatchKey(Barterable b)
        {
            if (b is GoldBarterable) return "G";
            if (b is ItemBarterable itemBarter && itemBarter.ItemRosterElement.EquipmentElement.Item != null)
            {
                var eq = itemBarter.ItemRosterElement.EquipmentElement;
                string itemKey = eq.ItemModifier != null ? $"{eq.Item.StringId}@{eq.ItemModifier.StringId}" : eq.Item.StringId;
                return $"T:{itemKey}";
            }
            if (b is FiefBarterable fief && fief.TargetSettlement != null) return $"F:{fief.TargetSettlement.StringId}";
            if (b is PeaceBarterable peace && peace.PeaceOfferingFaction != null && peace.OfferedFaction != null)
            {
                return $"P:{peace.PeaceOfferingFaction.StringId}:{peace.OfferedFaction.StringId}";
            }
            if (b is SetPrisonerFreeBarterable)
            {
                var p = SetPrisonerFreeHeroField?.GetValue(b) as Hero;
                return p != null ? $"R:{p.StringId}" : null;
            }
            if (b is TransferPrisonerBarterable)
            {
                var p = TransferPrisonerHeroField?.GetValue(b) as Hero;
                return p != null ? $"X:{p.StringId}" : null;
            }
            return null;
        }

        public static bool GuardPartnerEdit(BarterVM? vm, BarterItemVM? itemVm)
        {
            if (!IsActivePlayerBarter(vm) || PlayerInteractionService.Instance.IsApplyingRemoteBarterSync)
            {
                return true;
            }

            if (itemVm != null && IsPartnerOwned(itemVm))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Teklif] Karşı oyuncunun eşyalarını yalnızca kendisi masaya koyabilir!", "[Barter] Only your partner can modify their own side of the offer!"),
                    Colors.Yellow));
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Enables Bannerlord's native Lord Barter UI ("teklif sistemi") for player-to-player live simultaneous trade:
    /// both players keep BarterVM open together, see partner offers move in real time, and confirm mutually (2/2)
    /// before the server executes the atomic swap.
    /// </summary>
    [HarmonyPatch(typeof(BarterManager), nameof(BarterManager.CanPlayerBarterWithHero))]
    public static class PlayerBarterCooldownPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Hero hero, ref bool __result)
        {
            if (ModContext.IsClientConnected && hero != null && NetworkObjectRegistry.IsPlayerNetworkHero(hero))
            {
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(BarterManager), nameof(BarterManager.IsOfferAcceptable))]
    public static class PlayerBarterAcceptablePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterData args, Hero hero, PartyBase party, ref bool __result)
        {
            bool isPlayerTarget = (hero != null && NetworkObjectRegistry.IsPlayerNetworkHero(hero)) ||
                                  (args?.OtherHero != null && NetworkObjectRegistry.IsPlayerNetworkHero(args.OtherHero));

            if (ModContext.IsClientConnected && isPlayerTarget)
            {
                __result = (args != null && args.GetOfferedBarterables().Count > 0)
                           || !string.IsNullOrEmpty(PlayerInteractionService.Instance.LiveBarterLocalTokens)
                           || !string.IsNullOrEmpty(PlayerInteractionService.Instance.LiveBarterPartnerTokens);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(BarterManager), nameof(BarterManager.ApplyAndFinalizePlayerBarter))]
    public static class PlayerBarterFinalizeInterceptPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterManager __instance, Hero offererHero, Hero otherHero, BarterData barterData)
        {
            bool isPlayerTarget = otherHero != null && NetworkObjectRegistry.IsPlayerNetworkHero(otherHero);
            if (!ModContext.IsClientConnected || !isPlayerTarget)
            {
                return true;
            }

            if (PlayerInteractionService.Instance.IsLiveBarterOpen)
            {
                string localTokens = LiveBarterUIBridge.EncodeLocalOfferedTokens(barterData);
                PlayerInteractionService.Instance.ToggleLocalBarterAccept(localTokens);
                LiveBarterUIBridge.RefreshOfferLabel();
                return false;
            }

            PlayerInteractionService.Instance.InterceptAndSendLordBarterOffer(offererHero, otherHero, barterData);

            typeof(BarterManager)
                .GetProperty(nameof(BarterManager.LastBarterIsAccepted), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(__instance, false);
            __instance.Close();

            var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
            if (cm != null && cm.IsConversationInProgress)
            {
                cm.ContinueConversation();
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(BarterManager), nameof(BarterManager.CancelAndFinalizePlayerBarter))]
    public static class PlayerBarterCancelInterceptPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterManager __instance, Hero offererHero, Hero otherHero, BarterData barterData)
        {
            bool isPlayerTarget = otherHero != null && NetworkObjectRegistry.IsPlayerNetworkHero(otherHero);
            if (!ModContext.IsClientConnected || !isPlayerTarget)
            {
                return true;
            }

            PlayerInteractionService.Instance.NotifyLocalBarterCancelled();
            typeof(BarterManager)
                .GetProperty(nameof(BarterManager.LastBarterIsAccepted), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(__instance, false);
            __instance.Close();

            var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
            if (cm != null && cm.IsConversationInProgress)
            {
                cm.ContinueConversation();
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(BarterVM), MethodType.Constructor, new[] { typeof(BarterData) })]
    public static class BarterVMCtorPatch
    {
        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance, BarterData args)
        {
            if (ModContext.IsClientConnected && PlayerInteractionService.Instance.IsLiveBarterOpen)
            {
                LiveBarterUIBridge.Attach(__instance, args);
            }
        }
    }

    [HarmonyPatch(typeof(BarterVM), nameof(BarterVM.OnFinalize))]
    public static class BarterVMFinalizePatch
    {
        [HarmonyPrefix]
        public static void Prefix(BarterVM __instance)
        {
            if (LiveBarterUIBridge.IsActivePlayerBarter(__instance) && !PlayerInteractionService.Instance.IsApplyingRemoteBarterSync)
            {
                PlayerInteractionService.Instance.NotifyLocalBarterCancelled();
            }
            LiveBarterUIBridge.Detach(__instance);
        }
    }

    [HarmonyPatch(typeof(BarterVM), "TransferItem")]
    public static class BarterVMTransferItemGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterVM __instance, BarterItemVM item)
        {
            return LiveBarterUIBridge.GuardPartnerEdit(__instance, item);
        }

        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance)
        {
            LiveBarterUIBridge.SyncLocalOfferIfChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(BarterVM), "OfferItemAdd")]
    public static class BarterVMOfferAddGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterVM __instance, BarterItemVM barterItemVM)
        {
            return LiveBarterUIBridge.GuardPartnerEdit(__instance, barterItemVM);
        }

        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance)
        {
            LiveBarterUIBridge.SyncLocalOfferIfChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(BarterVM), "OfferItemRemove")]
    public static class BarterVMOfferRemoveGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterVM __instance, BarterItemVM barterItemVM)
        {
            return LiveBarterUIBridge.GuardPartnerEdit(__instance, barterItemVM);
        }

        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance)
        {
            LiveBarterUIBridge.SyncLocalOfferIfChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(BarterVM), "ExecuteTransferAll")]
    public static class BarterVMTransferAllGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterVM __instance, CharacterObject fromCharacter)
        {
            if (!LiveBarterUIBridge.IsActivePlayerBarter(__instance) || PlayerInteractionService.Instance.IsApplyingRemoteBarterSync)
            {
                return true;
            }
            if (fromCharacter != CharacterObject.PlayerCharacter)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    Loc.T("[Teklif] Karşı oyuncunun eşyalarını yalnızca kendisi masaya koyabilir!", "[Barter] Only your partner can modify their own side of the offer!"),
                    Colors.Yellow));
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class BarterVMLeftButtonsGuardPatch
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var vmType = typeof(BarterVM);
            string[] names =
            {
                "ExecuteTransferAllLeftFief",
                "ExecuteTransferAllLeftItem",
                "ExecuteTransferAllLeftPrisoner",
                "ExecuteTransferAllLeftOther",
                "ExecuteTransferAllGoldLeft",
                "ExecuteAutoBalance"
            };
            foreach (string name in names)
            {
                var method = AccessTools.Method(vmType, name);
                if (method != null) yield return method;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(BarterVM __instance)
        {
            if (!LiveBarterUIBridge.IsActivePlayerBarter(__instance) || PlayerInteractionService.Instance.IsApplyingRemoteBarterSync)
            {
                return true;
            }

            InformationManager.DisplayMessage(new InformationMessage(
                Loc.T("[Teklif] Canlı oyuncu takasında yalnızca kendi teklifinizi düzenleyebilirsiniz!", "[Barter] In live player barter you can only edit your own offer!"),
                Colors.Yellow));
            return false;
        }
    }

    [HarmonyPatch(typeof(BarterItemVM), nameof(BarterItemVM.ExecuteAddOffered))]
    public static class BarterItemAddGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterItemVM __instance) => LiveBarterUIBridge.GuardPartnerEdit(null, __instance);
    }

    [HarmonyPatch(typeof(BarterItemVM), nameof(BarterItemVM.ExecuteRemoveOffered))]
    public static class BarterItemRemoveGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterItemVM __instance) => LiveBarterUIBridge.GuardPartnerEdit(null, __instance);
    }

    [HarmonyPatch(typeof(BarterItemVM), nameof(BarterItemVM.ExecuteAction))]
    public static class BarterItemActionGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BarterItemVM __instance) => LiveBarterUIBridge.GuardPartnerEdit(null, __instance);
    }

    [HarmonyPatch(typeof(BarterVM), "SendOffer")]
    public static class BarterVMSendOfferSyncPatch
    {
        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance)
        {
            if (LiveBarterUIBridge.IsActivePlayerBarter(__instance) && !PlayerInteractionService.Instance.IsApplyingRemoteBarterSync)
            {
                string tokens = LiveBarterUIBridge.EncodeLocalOfferedTokens(__instance);
                PlayerInteractionService.Instance.NotifyLocalBarterOfferChanged(tokens);
            }
        }
    }

    [HarmonyPatch(typeof(BarterVM), "OnOfferedAmountChange")]
    public static class BarterVMAmountChangeSyncPatch
    {
        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance)
        {
            if (LiveBarterUIBridge.IsActivePlayerBarter(__instance) && !PlayerInteractionService.Instance.IsApplyingRemoteBarterSync)
            {
                string tokens = LiveBarterUIBridge.EncodeLocalOfferedTokens(__instance);
                PlayerInteractionService.Instance.NotifyLocalBarterOfferChanged(tokens);
            }
        }
    }

    [HarmonyPatch(typeof(BarterVM), nameof(BarterVM.ExecuteReset))]
    public static class BarterVMResetPreservePartnerPatch
    {
        [HarmonyPrefix]
        public static void Prefix(BarterVM __instance)
        {
            if (LiveBarterUIBridge.IsActivePlayerBarter(__instance))
            {
                PlayerInteractionService.Instance.IsApplyingRemoteBarterSync = true;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance)
        {
            if (!LiveBarterUIBridge.IsActivePlayerBarter(__instance)) return;

            PlayerInteractionService.Instance.IsApplyingRemoteBarterSync = false;
            string partnerTokens = PlayerInteractionService.Instance.LiveBarterPartnerTokens;
            if (!string.IsNullOrEmpty(partnerTokens))
            {
                LiveBarterUIBridge.ApplyPartnerTokensToActiveVM(partnerTokens);
            }
            PlayerInteractionService.Instance.NotifyLocalBarterOfferChanged(string.Empty);
        }
    }

    [HarmonyPatch(typeof(BarterVM), "RefreshResultBar")]
    public static class BarterVMOfferLabelPatch
    {
        [HarmonyPostfix]
        public static void Postfix(BarterVM __instance)
        {
            if (LiveBarterUIBridge.IsActivePlayerBarter(__instance))
            {
                LiveBarterUIBridge.ApplyLiveButtonState(__instance);
            }
        }
    }
}
