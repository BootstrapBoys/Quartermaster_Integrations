using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Debug-only: when Quartermaster refuses to merge two stacks of the same item, say which field
    /// stopped it.
    ///
    /// "Sort Chest" runs InventoryTransfers.StackWithin, which merges only when Stackable(a, b) is
    /// true. That test is deliberately strict - quality, variant, world level, crafter, the cheated
    /// flag, durability and every custom-data entry must match - so two stacks that look identical in
    /// the UI can still be refused. Without this the only symptom is "sort did not stack them".
    ///
    /// Nothing here changes behaviour; it reads the same fields Stackable reads and reports the
    /// difference once per item per ten seconds.
    /// </summary>
    public static class StackDiagnostics
    {
        private static readonly Dictionary<string, float> LastLogged = new Dictionary<string, float>();
        private const float Throttle = 10f;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Type transfers = BridgePlugin.QuartermasterAssembly?.GetType("Quartermaster.InventoryTransfers");
                System.Reflection.MethodInfo stackable = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "Stackable", new[] { typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData) });

                if (stackable == null)
                {
                    QIDebugger.LogError("[Stacks] InventoryTransfers.Stackable(ItemData, ItemData) not found; merge diagnostics off.");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, stackable, "Quartermaster.InventoryTransfers.Stackable",
                    postfix: new HarmonyMethod(typeof(StackDiagnostics), nameof(Stackable_Postfix)));

                // StackWithin calls Stackable for every pair, so a refusal it cannot merge always
                // reaches the log above. This second line proves the pass ran at all and names what
                // was left as separate stacks, even when the pair refused for a reason we cannot see.
                System.Reflection.MethodInfo stackWithin = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "StackWithin", new[] { typeof(Inventory) });

                if (stackWithin != null)
                {
                    HarmonyPatchGuard.TryPatch(
                        harmony, stackWithin, "Quartermaster.InventoryTransfers.StackWithin",
                        postfix: new HarmonyMethod(typeof(StackDiagnostics), nameof(StackWithin_Postfix)));
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Stacks] Initialization failed", ex);
            }
        }

        public static void Stackable_Postfix(ItemDrop.ItemData a, ItemDrop.ItemData b, ref bool __result)
        {
            if (__result || !QIDebugger.Enabled) return;
            if (a?.m_shared == null || b?.m_shared == null) return;

            // Only interesting when the two stacks are the same item and would visibly fit together.
            if (!string.Equals(a.m_shared.m_name, b.m_shared.m_name, StringComparison.Ordinal)) return;

            try
            {
                string differences = Describe(a, b);
                if (differences.Length == 0) return;

                float now = UnityEngine.Time.time;
                if (LastLogged.TryGetValue(a.m_shared.m_name, out float last) && now - last < Throttle) return;
                LastLogged[a.m_shared.m_name] = now;

                QIDebugger.LogDebug($"[Stacks] '{a.m_shared.m_name}' not merged: {differences}");
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[Stacks] Could not describe a refused merge", ex);
            }
        }

        /// <summary>
        /// After Quartermaster stacks a chest, report every item that still occupies more than one
        /// stack - the sort ran, and these are the names it could not merge.
        /// </summary>
        public static void StackWithin_Postfix(Inventory __0)
        {
            if (!QIDebugger.Enabled || __0 == null) return;

            try
            {
                Dictionary<string, int> stacks = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (ItemDrop.ItemData item in __0.GetAllItems())
                {
                    if (item?.m_shared == null) continue;
                    stacks.TryGetValue(item.m_shared.m_name, out int count);
                    stacks[item.m_shared.m_name] = count + 1;
                }

                float now = UnityEngine.Time.time;
                foreach (KeyValuePair<string, int> pair in stacks)
                {
                    if (pair.Value < 2) continue;
                    if (LastLogged.TryGetValue("sort:" + pair.Key, out float last) && now - last < Throttle) continue;
                    LastLogged["sort:" + pair.Key] = now;
                    QIDebugger.LogDebug($"[Stacks] after sorting, '{pair.Key}' still has {pair.Value} stacks");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[Stacks] Could not summarise the sort", ex);
            }
        }

        private static string Describe(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            StringBuilder text = new StringBuilder();

            void Add(string label, object left, object right)
            {
                if (Equals(left, right)) return;
                if (text.Length > 0) text.Append(", ");
                text.Append(label).Append(' ').Append(left).Append(" vs ").Append(right);
            }

            Add("quality", a.m_quality, b.m_quality);
            Add("variant", a.m_variant, b.m_variant);
            Add("worldLevel", a.m_worldLevel, b.m_worldLevel);
            Add("crafterId", a.m_crafterID, b.m_crafterID);
            Add("crafter", a.m_crafterName ?? "", b.m_crafterName ?? "");
            Add("cheated", a.m_cheated, b.m_cheated);

            if (a.m_shared.m_useDurability) Add("durability", a.m_durability, b.m_durability);

            int aCount = a.m_customData?.Count ?? 0;
            int bCount = b.m_customData?.Count ?? 0;
            if (aCount != bCount || (a.m_customData != null && b.m_customData != null))
            {
                HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
                if (a.m_customData != null) foreach (string key in a.m_customData.Keys) keys.Add(key);
                if (b.m_customData != null) foreach (string key in b.m_customData.Keys) keys.Add(key);

                foreach (string key in keys)
                {
                    string left = null, right = null;
                    bool hasLeft = a.m_customData != null && a.m_customData.TryGetValue(key, out left);
                    bool hasRight = b.m_customData != null && b.m_customData.TryGetValue(key, out right);
                    if (hasLeft == hasRight && string.Equals(left, right, StringComparison.Ordinal)) continue;

                    // Name the entry itself: a plain material carrying custom data is usually Epic
                    // Loot or a mod tag, and the key says which.
                    Add("customData " + key,
                        hasLeft ? left ?? "" : "<absent>",
                        hasRight ? right ?? "" : "<absent>");
                }
            }

            if (text.Length == 0) text.Append("fields look equal - the difference is inside IsSameType");
            return text.ToString();
        }
    }
}
