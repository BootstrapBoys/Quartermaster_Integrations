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
    /// Three relaxations, all for stackable items only: IgnoreCrafter drops the crafter fields,
    /// IgnoredStackTags drops the named custom-data keys (provenance stamps and the like), and
    /// IgnoreEpicLootComponents drops Epic Loot's MagicItemComponent - which Epic Loot also stamps onto
    /// materials, where it has no bearing on stacking. Quality, variant, world level, durability and
    /// every other custom-data entry must still match, so gear, Epic Loot world levels and real enchant
    /// data keep Quartermaster's rule untouched.
    /// </summary>
    public static class StackCrafterPatch
    {
        private static MethodInfo _itemId;

        /// <summary>Epic Loot's enchant payload; meaningless on the materials it also stamps.</summary>
        private const string EpicLootComponent = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";

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
                        (BridgePlugin.IgnoreCrafter != null && BridgePlugin.IgnoreCrafter.Value
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
            bool ignoreCrafter = BridgePlugin.IgnoreCrafter != null && BridgePlugin.IgnoreCrafter.Value;
            if (!ignoreCrafter && IgnoredTags().Count == 0 && !IgnoreComponents()) return true;
            if (!Mergeable(a, b, ignoreCrafter)) return true;

            __result = true;

            if (QIDebugger.Enabled)
            {
                string why = ignoreCrafter ? "crafter" : "ignored tags";
                QIDebugger.LogDebug(
                    $"[Stacks] merging '{a.m_shared.m_name}' x{a.m_stack} ({Crafter(a)}) with " +
                    $"x{b.m_stack} ({Crafter(b)}): identical apart from the {why}");
            }

            return false;
        }

        /// <summary>
        /// Mirrors Quartermaster's Stackable, relaxing exactly the fields the settings allow: the
        /// crafter when IgnoreCrafter is on, and any custom-data key listed in IgnoredStackTags.
        /// Everything else must still match, and something must actually differ or Quartermaster's own
        /// answer is left alone.
        /// </summary>
        private static bool Mergeable(ItemDrop.ItemData a, ItemDrop.ItemData b, bool ignoreCrafter)
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

            bool crafterSame = a.m_crafterID == b.m_crafterID && (a.m_crafterName ?? "") == (b.m_crafterName ?? "");

            // SameCustomData ignores the configured tags; Strict compares every entry.
            if (!SameCustomData(a.m_customData, b.m_customData, true)) return false;   // a real entry differs
            if (!crafterSame && !ignoreCrafter) return false;
            if (crafterSame && SameCustomData(a.m_customData, b.m_customData, false)) return false; // nothing to relax

            return true;
        }

        private static bool SameCustomData(Dictionary<string, string> left, Dictionary<string, string> right, bool ignoreTags)
        {
            HashSet<string> ignored = ignoreTags ? IgnoredTags() : new HashSet<string>();
            if (ignoreTags && IgnoreComponents()) ignored.Add(EpicLootComponent);

            List<string> leftKeys = left == null ? new List<string>() : new List<string>(left.Keys);
            List<string> rightKeys = right == null ? new List<string>() : new List<string>(right.Keys);

            foreach (string key in rightKeys)
            {
                if (!ignored.Contains(key) && !leftKeys.Contains(key)) return false;
            }

            foreach (string key in leftKeys)
            {
                if (ignored.Contains(key)) continue;
                if (right == null || !right.TryGetValue(key, out string value)) return false;
                if (value != left[key]) return false;
            }

            return true;
        }

        /// <summary>Whether Epic Loot's enchant payload is ignored on stackable items.</summary>
        private static bool IgnoreComponents()
        {
            return BridgePlugin.IgnoreEpicLootComponents != null && BridgePlugin.IgnoreEpicLootComponents.Value;
        }

        /// <summary>Custom-data keys the user considers cosmetic (comma separated in the config).</summary>
        private static HashSet<string> IgnoredTags()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            string raw = BridgePlugin.IgnoredStackTags != null ? BridgePlugin.IgnoredStackTags.Value : null;
            if (string.IsNullOrEmpty(raw)) return keys;

            foreach (string part in raw.Split(','))
            {
                string key = part.Trim();
                if (key.Length > 0) keys.Add(key);
            }

            return keys;
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
