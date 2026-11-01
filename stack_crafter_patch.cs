using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Quartermaster refuses to merge two stacks whose crafter differs, so core wood crafted at a bench
    /// will not stack with core wood you picked up. Vanilla does not care: Inventory.FindFreeStackItem
    /// compares name, quality and world level only. The extra rule is Quartermaster's own.
    ///
    /// With StackAcrossCrafters on, the comparison is repeated here without the crafter fields and the
    /// merge runs when that is the only difference. Deliberately narrow: stackable items only, and the
    /// quality, variant, world level, durability, cheat flag and custom data must all still match - so
    /// gear, Epic Loot world levels and enchant data keep Quartermaster's rule untouched.
    /// </summary>
    public static class StackCrafterPatch
    {
        private static MethodInfo _itemId;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Type transfers = BridgePlugin.QuartermasterAssembly?.GetType("Quartermaster.InventoryTransfers");
                MethodInfo stackable = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "Stackable", new[] { typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData) });

                _itemId = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "ItemId", new[] { typeof(ItemDrop.ItemData) });

                if (stackable == null || _itemId == null)
                {
                    QIDebugger.LogError(
                        $"[Stacks] Bindings incomplete: Stackable={stackable != null}, ItemId={_itemId != null}");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, stackable, "Quartermaster.InventoryTransfers.Stackable",
                    prefix: new HarmonyMethod(typeof(StackCrafterPatch), nameof(Stackable_Prefix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[Stacks] Merge-across-crafters " +
                        (BridgePlugin.StackAcrossCrafters != null && BridgePlugin.StackAcrossCrafters.Value
                            ? "enabled."
                            : "disabled: Quartermaster keeps crafted and looted stacks apart."));
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Stacks] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Returns false to skip Quartermaster's own comparison when the only difference is the crafter.
        /// </summary>
        public static bool Stackable_Prefix(ItemDrop.ItemData a, ItemDrop.ItemData b, ref bool __result)
        {
            if (BridgePlugin.StackAcrossCrafters == null || !BridgePlugin.StackAcrossCrafters.Value) return true;
            if (!Mergeable(a, b)) return true;

            __result = true;

            if (QIDebugger.Enabled)
            {
                QIDebugger.LogDebug(
                    $"[Stacks] merging '{a.m_shared.m_name}' x{a.m_stack} ({Crafter(a)}) with " +
                    $"x{b.m_stack} ({Crafter(b)}): identical apart from the crafter");
            }

            return false;
        }

        /// <summary>Mirrors Quartermaster's Stackable minus the crafter fields, and demands they differ.</summary>
        private static bool Mergeable(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            if (a?.m_shared == null || b?.m_shared == null) return false;
            if (a.m_shared.m_maxStackSize <= 1) return false;

            if (ItemId(a) != ItemId(b)) return false;
            if (!a.IsSameType(b)) return false;
            if (a.m_quality != b.m_quality) return false;
            if (a.m_variant != b.m_variant) return false;
            if (a.m_worldLevel != b.m_worldLevel) return false;
            if (a.m_cheated != b.m_cheated) return false;
            if ((a.m_shared.m_useDurability || b.m_shared.m_useDurability) && a.m_durability != b.m_durability) return false;
            if (!SameCustomData(a.m_customData, b.m_customData)) return false;

            return a.m_crafterID != b.m_crafterID || (a.m_crafterName ?? "") != (b.m_crafterName ?? "");
        }

        private static bool SameCustomData(Dictionary<string, string> left, Dictionary<string, string> right)
        {
            if ((left?.Count ?? 0) != (right?.Count ?? 0)) return false;
            if (left == null) return true;

            foreach (KeyValuePair<string, string> entry in left)
            {
                if (right == null || !right.TryGetValue(entry.Key, out string value) || value != entry.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ItemId(ItemDrop.ItemData item)
        {
            try
            {
                return _itemId?.Invoke(null, new object[] { item }) as string;
            }
            catch
            {
                return null;
            }
        }

        private static string Crafter(ItemDrop.ItemData item)
        {
            return string.IsNullOrEmpty(item.m_crafterName) ? "looted" : item.m_crafterName;
        }
    }
}
