using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// ExtraSlots protection is delegated to Quartermaster rather than reimplemented.
    ///
    /// Quartermaster's WarehouseService.SortInventory already takes a preserveEquipped flag and, when
    /// set, marks every external slot cell as immovable via ExternalSlotCompatibility
    /// (QM 0.1.66, WarehouseService.SortInventory and ExternalSlotCompatibility). Its own Sort button
    /// calls it with preserveEquipped: true, and StoreAllInOpenChest skips protected slots outright.
    ///
    /// What we add is the piece Quartermaster cannot guarantee: its ExternalSlotCompatibility only
    /// knows about ExtraSlots if its own reflection probe succeeded. These two postfixes answer
    /// "is this cell / this item an extra slot" from ExtraSlots directly, so a probe failure in
    /// Quartermaster cannot cost the player their slot contents.
    ///
    /// The earlier approach - removing extra-slot items from the inventory list, clamping m_height,
    /// sorting, then putting them back - is gone. It duplicated protection Quartermaster already
    /// performs and mutated the live item list mid-sort, which is how items could end up sharing a
    /// grid cell or vanish if the restore threw.
    /// </summary>
    public static class ExtraSlotsIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnableExtraSlots);

        private static Func<int> _getInventoryHeightPlayer;
        private static Func<Vector2i, bool> _isGridPositionASlot;
        private static Func<ItemDrop.ItemData, bool> _isItemInSlot;

        public static void Initialize(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.ExtraSlotsGUID, out PluginInfo esInfo) ||
                esInfo?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly esAssembly = esInfo.Instance.GetType().Assembly;
                Type slotsType = esAssembly.GetType("ExtraSlots.Slots");
                if (slotsType == null)
                {
                    QIDebugger.LogError("[ExtraSlots] ExtraSlots.Slots not found; slot protection is unavailable.");
                    return;
                }

                PropertyInfo heightProp = AccessTools.Property(slotsType, "InventoryHeightPlayer");
                if (heightProp?.GetGetMethod() != null)
                {
                    _getInventoryHeightPlayer = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), heightProp.GetGetMethod());
                }

                MethodInfo isGridMethod = AccessTools.Method(slotsType, "IsGridPositionASlot", new[] { typeof(Vector2i) });
                if (isGridMethod != null)
                {
                    _isGridPositionASlot = (Func<Vector2i, bool>)Delegate.CreateDelegate(typeof(Func<Vector2i, bool>), isGridMethod);
                }

                MethodInfo isItemMethod = AccessTools.Method(slotsType, "IsItemInSlot", new[] { typeof(ItemDrop.ItemData) });
                if (isItemMethod != null)
                {
                    _isItemInSlot = (Func<ItemDrop.ItemData, bool>)Delegate.CreateDelegate(typeof(Func<ItemDrop.ItemData, bool>), isItemMethod);
                }

                _ready = _getInventoryHeightPlayer != null;
                if (!_ready) return;

                ApplyQuartermasterPatches(harmony);

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug("ExtraSlotsIntegration initialized and patched Quartermaster slot protection.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to initialize ExtraSlotsIntegration", ex);
            }
        }

        private static void ApplyQuartermasterPatches(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.QuartermasterGUID, out PluginInfo qmInfo) ||
                qmInfo?.Instance == null)
            {
                return;
            }

            Assembly qmAssembly = qmInfo.Instance.GetType().Assembly;
            Type extCompatType = qmAssembly.GetType("Quartermaster.ExternalSlotCompatibility");
            if (extCompatType == null)
            {
                QIDebugger.LogError("[ExtraSlots] Quartermaster.ExternalSlotCompatibility not found; slot protection is unavailable.");
                return;
            }

            MethodInfo isProtectedCoords = AccessTools.Method(extCompatType, "IsProtectedPlayerSlot", new[] { typeof(int), typeof(int) });
            if (isProtectedCoords != null)
            {
                HarmonyPatchGuard.TryPatch(
                    harmony,
                    isProtectedCoords,
                    "Quartermaster.ExternalSlotCompatibility.IsProtectedPlayerSlot(int,int)",
                    postfix: new HarmonyMethod(typeof(ExtraSlotsIntegration), nameof(IsProtectedPlayerSlot_Coords_Postfix)));
            }
            else
            {
                QIDebugger.LogError("[Patch] 'ExternalSlotCompatibility.IsProtectedPlayerSlot(int,int)' skipped: target method not found.");
            }

            MethodInfo isProtectedItem = AccessTools.Method(extCompatType, "IsProtectedPlayerSlot", new[] { typeof(ItemDrop.ItemData) });
            if (isProtectedItem != null)
            {
                HarmonyPatchGuard.TryPatch(
                    harmony,
                    isProtectedItem,
                    "Quartermaster.ExternalSlotCompatibility.IsProtectedPlayerSlot(ItemData)",
                    postfix: new HarmonyMethod(typeof(ExtraSlotsIntegration), nameof(IsProtectedPlayerSlot_Item_Postfix)));
            }
            else
            {
                QIDebugger.LogError("[Patch] 'ExternalSlotCompatibility.IsProtectedPlayerSlot(ItemData)' skipped: target method not found.");
            }
        }

        public static int GetPlayerRegularInventoryHeight()
        {
            if (!IsReady || _getInventoryHeightPlayer == null) return 4;
            try
            {
                return _getInventoryHeightPlayer();
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ExtraSlots] Failed reading InventoryHeightPlayer", ex);
                return 4;
            }
        }

        public static bool IsExtraSlotPosition(int x, int y)
        {
            if (!IsReady) return false;
            try
            {
                if (_isGridPositionASlot != null)
                {
                    return _isGridPositionASlot(new Vector2i(x, y));
                }
                return y >= GetPlayerRegularInventoryHeight();
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ExtraSlots] Failed checking grid position", ex);
                return false;
            }
        }

        public static bool IsItemInExtraSlot(ItemDrop.ItemData item)
        {
            if (!IsReady || item == null) return false;
            try
            {
                if (_isItemInSlot != null)
                {
                    return _isItemInSlot(item);
                }
                return item.m_gridPos.y >= GetPlayerRegularInventoryHeight();
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ExtraSlots] Failed checking item slot", ex);
                return false;
            }
        }

        public static void IsProtectedPlayerSlot_Coords_Postfix(int x, int y, ref bool __result)
        {
            if (__result) return;
            if (!BridgePlugin.Enabled) return;

            if (IsExtraSlotPosition(x, y))
            {
                __result = true;
            }
        }

        public static void IsProtectedPlayerSlot_Item_Postfix(ItemDrop.ItemData item, ref bool __result)
        {
            if (__result || item == null) return;
            if (!BridgePlugin.Enabled) return;

            if (IsItemInExtraSlot(item))
            {
                __result = true;
            }
        }
    }
}
