using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Quartermaster_Integrations
{
    public static class AAACraftingIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnableAAACrafting);

        private static FieldInfo _cfcLoadedField;
        private static FieldInfo _maxCraftInventoryChangedField;
        private static FieldInfo _aaaCraftAmountField;
        private static FieldInfo _aaaInputField;
        private static FieldInfo _btnMinusField;
        private static FieldInfo _btnPlusField;
        private static Action _invalidatePerfCache;

        private static Button _cachedMinusBtn;
        private static Button _cachedPlusBtn;
        private static int _lastButtonScanFrame = -1;
        private static int _lastNotifyFrame = -1;

        public static void Initialize(Harmony harmony)
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.AAACraftingGUID, out PluginInfo aaaInfo) ||
                    aaaInfo?.Instance == null)
                {
                    return;
                }

                Assembly aaaAssembly = aaaInfo.Instance.GetType().Assembly;
                Type pluginType = aaaAssembly.GetType("AzuAntiArthriticCrafting.AzuAntiArthriticCraftingPlugin");
                Type utilitiesType = aaaAssembly.GetType("AzuAntiArthriticCrafting.Utilities.Utilities");
                Type aaaCraftType = aaaAssembly.GetType("AzuAntiArthriticCrafting.Patches.AAACraft");
                Type maxCraftAmountType = aaaAssembly.GetType("AzuAntiArthriticCrafting.MaxCraftAmount");
                Type perfCacheType = aaaAssembly.GetType("AzuAntiArthriticCrafting.Patches.RecipeListPerfCache");

                if (pluginType != null)
                {
                    _cfcLoadedField = AccessTools.Field(pluginType, "CfcLoaded");
                }

                if (maxCraftAmountType != null)
                {
                    _maxCraftInventoryChangedField = AccessTools.Field(maxCraftAmountType, "InventoryChanged");
                }

                if (aaaCraftType != null)
                {
                    _aaaCraftAmountField = AccessTools.Field(aaaCraftType, "amount");
                    _aaaInputField = AccessTools.Field(aaaCraftType, "inputAmount");
                    _btnMinusField = AccessTools.Field(aaaCraftType, "btnMinus");
                    _btnPlusField = AccessTools.Field(aaaCraftType, "btnPlus");
                }

                if (perfCacheType != null)
                {
                    MethodInfo invalidateMethod = AccessTools.Method(perfCacheType, "Invalidate");
                    if (invalidateMethod != null)
                    {
                        _invalidatePerfCache = (Action)Delegate.CreateDelegate(typeof(Action), invalidateMethod);
                    }
                }

                _ready = utilitiesType != null && aaaCraftType != null;
                if (!_ready)
                {
                    if (QIDebugger.Enabled)
                    {
                        QIDebugger.LogDebug("[AAA] Required types not found; integration disabled.");
                    }
                    return;
                }

                MethodInfo getCloseContainersMethod = AccessTools.Method(utilitiesType, "GetItemsInCloseContainers", new[] { typeof(string) });
                if (getCloseContainersMethod != null && getCloseContainersMethod.ReturnType == typeof(int))
                {
                    HarmonyPatchGuard.TryPatch(
                        harmony,
                        getCloseContainersMethod,
                        "AAA.Utilities.GetItemsInCloseContainers",
                        postfix: new HarmonyMethod(typeof(AAACraftingIntegration), nameof(GetItemsInCloseContainers_Postfix)));
                }
                else
                {
                    QIDebugger.LogError("[Patch] 'AAA.Utilities.GetItemsInCloseContainers' skipped: not found or does not return int.");
                }

                HarmonyMethod calcMaxPostfix = new HarmonyMethod(typeof(AAACraftingIntegration), nameof(CalculateMaxCraftAmount_Postfix));

                HarmonyPatchGuard.TryPatch(
                    harmony,
                    AccessTools.Method(aaaCraftType, "CalculateMaxCraftAmount"),
                    "AAA.AAACraft.CalculateMaxCraftAmount",
                    postfix: calcMaxPostfix);

                HarmonyPatchGuard.TryPatch(
                    harmony,
                    AccessTools.Method(utilitiesType, "CalculateMaxCraftAmount"),
                    "AAA.Utilities.CalculateMaxCraftAmount",
                    postfix: calcMaxPostfix);

                if (QIDebugger.Enabled) QIDebugger.LogDebug("AAACraftingIntegration initialized and patched.");
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to initialize AAACraftingIntegration", ex);
            }
        }

        public static bool TryGetCraftAmount(out int amount)
        {
            amount = 1;
            if (!IsReady) return false;

            if (_aaaInputField != null)
            {
                try
                {
                    if (_aaaInputField.GetValue(null) is TMP_InputField input && input != null &&
                        int.TryParse(input.text, out int parsed) && parsed > 0)
                    {
                        amount = parsed;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    QIDebugger.LogDebug("[AAA] Failed reading inputAmount", ex);
                }
            }

            if (_aaaCraftAmountField != null)
            {
                try
                {
                    int amt = (int)_aaaCraftAmountField.GetValue(null);
                    if (amt > 0)
                    {
                        amount = amt;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    QIDebugger.LogDebug("[AAA] Failed reading amount", ex);
                }
            }

            return false;
        }

        public static void EnableContainerCrafting()
        {
            if (!IsReady || _cfcLoadedField == null) return;
            try
            {
                _cfcLoadedField.SetValue(null, true);
                if (QIDebugger.Enabled) QIDebugger.LogDebug("Enabled CfcLoaded flag in AAA Crafting.");
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("Failed setting CfcLoaded", ex);
            }
        }

        public static void NotifyInventoryChanged()
        {
            if (!IsReady) return;

            // One craft moves stack after stack, so Inventory.Changed can fire dozens of times in a
            // single frame. AAA only needs its dirty flags raised once, and Invalidate() walks the
            // recipe list, so coalesce per frame.
            int frame = Time.frameCount;
            if (frame == _lastNotifyFrame) return;
            _lastNotifyFrame = frame;

            try
            {
                // Static-field writes through FieldInfo box their value; the frame guard above keeps
                // this to one write per frame no matter how many inventory changes a craft causes.
                _maxCraftInventoryChangedField?.SetValue(null, true);
                _invalidatePerfCache?.Invoke();
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[AAA] Failed notifying inventory change", ex);
            }
        }

        public static void ForceEnableBatchButtons(bool canCraftSingle, int currentAmount)
        {
            if (!IsReady) return;

            try
            {
                Button minus = _btnMinusField?.GetValue(null) as Button;
                Button plus = _btnPlusField?.GetValue(null) as Button;

                if (minus == null || plus == null)
                {
                    // Fallback for a renamed AAA field: scan by name, at most once a second. Without
                    // the throttle this walked the entire InventoryGui hierarchy every single frame,
                    // because a null field never satisfies the "found" condition above.
                    bool needsLookup = _cachedMinusBtn == null || _cachedPlusBtn == null ||
                                       _cachedMinusBtn.gameObject == null || _cachedPlusBtn.gameObject == null;

                    if (needsLookup && Time.frameCount - _lastButtonScanFrame >= 60)
                    {
                        _lastButtonScanFrame = Time.frameCount;
                        _cachedMinusBtn = null;
                        _cachedPlusBtn = null;

                        if (InventoryGui.instance != null)
                        {
                            Button[] buttons = InventoryGui.instance.GetComponentsInChildren<Button>(true);
                            for (int i = 0; i < buttons.Length; i++)
                            {
                                Button b = buttons[i];
                                if (b == null) continue;
                                string bName = b.name;
                                if (bName.IndexOf("Minus", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    _cachedMinusBtn = b;
                                }
                                else if (bName.IndexOf("Plus", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    _cachedPlusBtn = b;
                                }
                            }
                        }

                        if (QIDebugger.Enabled && (_cachedMinusBtn != null || _cachedPlusBtn != null))
                        {
                            QIDebugger.LogDebug("[AAA] btnMinus/btnPlus unresolved; using name-based fallback buttons.");
                        }
                    }

                    if (minus == null) minus = _cachedMinusBtn;
                    if (plus == null) plus = _cachedPlusBtn;
                }

                int batchCap = MultiCraftBridgeManager.GetBatchLimit();

                if (minus != null)
                {
                    // Always allow stepping back down. Gating the decrement on affordability is
                    // what trapped players at an unaffordable batch size (see the 2.1.6 entry).
                    minus.interactable = currentAmount > 1;
                }

                if (plus != null)
                {
                    plus.interactable = canCraftSingle && currentAmount < batchCap;
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[AAA] Failed updating batch buttons", ex);
            }
        }

        public static void GetItemsInCloseContainers_Postfix(string itemName, ref int __result)
        {
            if (!BridgePlugin.Enabled || !IsReady) return;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            // GetItemsInCloseContainers returns CONTAINER-ONLY counts: its callers add the player's
            // pocket count themselves (Utilities.GetAvailableItems does `pocket + GetItemsInClose...`).
            // So add our container count, never a pocket-inclusive total, or pockets are counted
            // twice in both the requirement display and AAA's max-craft.
            int qmCount = BridgePlugin.GetQuartermasterStorageCount(player, itemName);
            if (qmCount <= 0) return;

            long total = (long)__result + qmCount;
            __result = (int)Math.Min(int.MaxValue, total);
        }

        public static void CalculateMaxCraftAmount_Postfix(ref int __result)
        {
            if (!BridgePlugin.Enabled || !IsReady) return;

            InventoryGui gui = InventoryGui.instance;
            Player player = Player.m_localPlayer;
            if (gui == null || player == null || !InventoryGui.IsVisible()) return;

            (Recipe recipe, ItemDrop.ItemData upgradeItem) = InventoryGui_UpdateRecipe_BatchEnforcementPatch.GetSelectedRecipeData(gui);

            // If the selected recipe cannot be identified (renamed field or unknown pair type),
            // leave AAA's own answer alone. Reporting 0 here silently disables every recipe.
            if (recipe == null) return;

            // AAA returns 0 for "cannot craft at all": no recipe, or a missing/under-levelled
            // crafting station (Utilities.CalculateMaxCraftAmount checks station usability and
            // level before anything else). Never raise that back to a positive number.
            if (__result <= 0) return;

            bool noCost = player.NoCostCheat() ||
                          (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost));

            if (gui.InUpradeTab() || upgradeItem != null)
            {
                int nextQuality = upgradeItem != null ? upgradeItem.m_quality + 1 : 2;

                // One upgrade at a time when affordable, 0 when it is not (AAA floors at 1).
                __result = noCost || player.HaveRequirements(recipe, false, nextQuality)
                    ? Mathf.Max(__result, 1)
                    : 0;
                return;
            }

            int batchCap = MultiCraftBridgeManager.GetBatchLimit();

            if (noCost)
            {
                __result = batchCap;
                return;
            }

            // Raise AAA's figure rather than replacing it. AAA already counts containers through the
            // patched GetItemsInCloseContainers, and it applies rules we do not model (station level,
            // ItemData upgrade case). We only supply a bigger number when our count proves one, and
            // we clamp to AAA's own 100 batch ceiling.
            int calculatedMax = CalculateTrueMaxCraft(player, recipe);

            // AAA floors its own answer at 1, so an empty inventory still displays "1". Report 0 when
            // our count says nothing is craftable AND the game's own requirement check agrees: the
            // agreement matters because another craft-from-container mod can supply items our count
            // does not see, and showing 0 there would be wrong in the other direction.
            if (calculatedMax <= 0 && !player.HaveRequirements(recipe, false, 1))
            {
                __result = 0;
                if (QIDebugger.Enabled)
                {
                    string zeroName = recipe.m_item?.m_itemData?.m_shared?.m_name ?? "Unknown";
                    QIDebugger.LogDebug($"[CalculateMaxCraft] '{zeroName}' = 0 (nothing craftable)");
                }
                return;
            }

            __result = Mathf.Clamp(Mathf.Max(__result, calculatedMax), 1, batchCap);
            if (QIDebugger.Enabled)
            {
                string rName = recipe.m_item?.m_itemData?.m_shared?.m_name ?? "Unknown";
                QIDebugger.LogDebug($"[CalculateMaxCraft] '{rName}' = {__result} (Cap: {batchCap})");
            }
        }

        /// <summary>
        /// Largest number of crafts our own counts allow. "Require only one ingredient" recipes take
        /// the sum of what each ingredient affords; every other recipe takes the minimum. Both skip
        /// requirements we cannot evaluate (modded dummy tokens) and report the floor of 1 when that
        /// leaves nothing to judge, so the recipe stays usable (the 2.1.9 fix).
        /// </summary>
        private static int CalculateTrueMaxCraft(Player player, Recipe recipe)
        {
            if (player == null || recipe?.m_resources == null) return 0;

            bool anyEvaluated = false;
            int sum = 0;
            int minCrafts = int.MaxValue;

            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (req?.m_resItem == null) continue;

                int reqAmt = req.GetAmount(1);
                if (reqAmt <= 0) continue;

                string sharedName = req.m_resItem.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(sharedName) || !player.IsKnownMaterial(sharedName)) continue;

                anyEvaluated = true;
                int possible = BridgePlugin.GetTotalAvailable(player, sharedName) / reqAmt;

                sum += possible;
                if (possible < minCrafts)
                {
                    minCrafts = possible;
                }
            }

            if (!anyEvaluated) return 1;
            return recipe.m_requireOnlyOneIngredient ? sum : Mathf.Max(0, minCrafts);
        }
    }
}
