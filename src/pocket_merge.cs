using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Merges identical stacks in the player's own inventory when it is sorted.
    ///
    /// Quartermaster's sort only rearranges grid positions, and its merge routine
    /// (InventoryTransfers.StackWithin) is called for chests - never for pockets. So sorting your
    /// inventory leaves two stacks of the same iron where they were, which reads as the sort failing.
    ///
    /// This runs before the sort when the target inventory is the local player's: pairs that
    /// Quartermaster itself would merge (which includes the IgnoreCrafter and IgnoredStackTags
    /// relaxations) are combined, equipped items are left alone, and then Quartermaster arranges the
    /// compacted result.
    ///
    /// Config: `4. Patches -> MergePocketsOnSort` (default on).
    /// </summary>
    public static class PocketMerge
    {
        private static MethodInfo _stackable;
        private static MethodInfo _changed;
        private static int _lastFrame = -1;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Type warehouse = BridgePlugin.QuartermasterAssembly?.GetType("Quartermaster.WarehouseService");
                Type transfers = BridgePlugin.QuartermasterAssembly?.GetType("Quartermaster.InventoryTransfers");

                MethodInfo sort = warehouse == null
                    ? null
                    : AccessTools.Method(warehouse, "SortInventory",
                        new[] { typeof(Inventory), typeof(bool), typeof(bool) });

                _stackable = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "Stackable", new[] { typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData) });

                _changed = AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) });

                if (sort == null || _stackable == null)
                {
                    QIDebugger.LogError(
                        $"[Pockets] Bindings incomplete: SortInventory={sort != null}, Stackable={_stackable != null}");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, sort, "Quartermaster.WarehouseService.SortInventory",
                    prefix: new HarmonyMethod(typeof(PocketMerge), nameof(SortInventory_Prefix)));

                if (QIDebugger.Enabled) QIDebugger.LogDebug("[Pockets] Sorting the player inventory merges identical stacks first.");
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Pockets] Initialization failed", ex);
            }
        }

        public static void SortInventory_Prefix(Inventory inventory)
        {
            if (BridgePlugin.MergePocketsOnSort == null || !BridgePlugin.MergePocketsOnSort.Value) return;

            Player player = Player.m_localPlayer;
            if (player == null || inventory == null || inventory != player.GetInventory()) return;
            if (Time.frameCount == _lastFrame) return;   // one pass per frame, whatever calls the sort

            _lastFrame = Time.frameCount;

            try
            {
                int merged = Merge(player, inventory);
                if (merged > 0 && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug($"[Pockets] Merged {merged} pocket stack(s) before sorting.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[Pockets] Could not merge pocket stacks", ex);
            }
        }

        private static int Merge(Player player, Inventory inventory)
        {
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            int merged = 0;

            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData target = items[i];
                if (target?.m_shared == null || player.IsItemEquiped(target)) continue;

                for (int j = items.Count - 1; j > i; j--)
                {
                    ItemDrop.ItemData source = items[j];
                    if (source?.m_shared == null || player.IsItemEquiped(source)) continue;

                    bool stackable;
                    try
                    {
                        stackable = (bool)_stackable.Invoke(null, new object[] { target, source });
                    }
                    catch
                    {
                        continue;
                    }

                    if (!stackable) continue;

                    int room = Math.Max(0, target.m_shared.m_maxStackSize - target.m_stack);
                    int moved = Math.Min(source.m_stack, room);
                    if (moved <= 0) continue;

                    target.m_stack += moved;
                    source.m_stack -= moved;
                    merged++;

                    if (source.m_stack <= 0)
                    {
                        inventory.RemoveItem(source);
                        items = inventory.GetAllItems();
                        j = items.Count;   // list changed; restart the inner scan
                        if (i >= items.Count) break;
                        target = items[i];
                        if (target?.m_shared == null || player.IsItemEquiped(target)) break;
                    }
                }
            }

            if (merged > 0)
            {
                // Changed() is private; it is what syncs the grid and the ZDO after item mutations.
                try
                {
                    _changed?.Invoke(inventory, new object[] { false, false });
                }
                catch
                {
                    // The sort still runs; a missed notification only delays the refresh.
                }
            }

            return merged;
        }
    }
}
