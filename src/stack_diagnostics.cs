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
            if (aCount != bCount)
            {
                Add("customData entries", aCount, bCount);
            }
            else if (a.m_customData != null)
            {
                foreach (KeyValuePair<string, string> pair in a.m_customData)
                {
                    b.m_customData.TryGetValue(pair.Key, out string other);
                    if (!string.Equals(pair.Value, other, StringComparison.Ordinal))
                    {
                        Add("customData " + pair.Key, pair.Value ?? "", other ?? "");
                    }
                }
            }

            if (text.Length == 0) text.Append("fields look equal - the difference is inside IsSameType");
            return text.ToString();
        }
    }
}
