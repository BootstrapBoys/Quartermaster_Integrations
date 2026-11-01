using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Lets DropMoreLoot own stack sizes instead of fighting it.
    ///
    /// DropMoreLoot multiplies `SharedData.m_maxStackSize` in `ItemDrop.Awake`; Quartermaster overrides
    /// the same field through ItemStacks.Configure/Apply with its own absolute MaximumStackSize, so
    /// whichever runs last wins and DropMoreLoot's multiplier is erased in practice.
    ///
    /// With `DeferToDropMoreLootStacks` on (default) and DropMoreLoot's own stacking enabled,
    /// Quartermaster is told its stack override is off, so DropMoreLoot's multiplied value stands.
    /// Quartermaster's stack tab then says so, rather than showing a number that is not being applied.
    ///
    /// This is display/config only: no item, recipe or inventory amounts are touched here.
    /// </summary>
    public static class DropMoreLootIntegration
    {
        public static bool IsReady { get; private set; }
        public static string BindError { get; private set; }

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _multiplier;
        private static FieldInfo _tabField;
        private static FieldInfo _stackPendingField;

        /// <summary>True when DropMoreLoot is present, stacking is on, and the toggle says defer.</summary>
        public static bool Active
        {
            get
            {
                if (!IsReady || !BridgePlugin.Enabled) return false;
                if (BridgePlugin.DeferToDropMoreLootStacks == null || !BridgePlugin.DeferToDropMoreLootStacks.Value) return false;

                try
                {
                    return _enabled != null && _enabled.Value;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static int Multiplier
        {
            get
            {
                try
                {
                    return _multiplier != null ? _multiplier.Value : 1;
                }
                catch
                {
                    return 1;
                }
            }
        }

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.DropMoreLootGUID, out PluginInfo info) ||
                info?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly assembly = info.Instance.GetType().Assembly;
                Type main = assembly.GetType("DropMoreLoot.DropMoreLootMain");

                _enabled = AccessTools.Field(main, "enableStacking")?.GetValue(null) as ConfigEntry<bool>;
                _multiplier = AccessTools.Field(main, "itemStackMultiplier")?.GetValue(null) as ConfigEntry<int>;

                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type itemStacks = qm?.GetType("Quartermaster.ItemStacks");
                Type chestUi = qm?.GetType("Quartermaster.ChestUi");

                MethodInfo configure = itemStacks == null
                    ? null
                    : AccessTools.Method(itemStacks, "Configure", new[] { typeof(bool), typeof(int) });

                MethodInfo status = chestUi == null
                    ? null
                    : AccessTools.Method(chestUi, "StatusText", Type.EmptyTypes);

                _tabField = chestUi?.GetField("tab", BindingFlags.Static | BindingFlags.NonPublic);
                _stackPendingField = chestUi?.GetField("stackPending", BindingFlags.Static | BindingFlags.NonPublic);

                if (_enabled == null || _multiplier == null || configure == null || status == null ||
                    _tabField == null)
                {
                    BindError =
                        $"enableStacking={_enabled != null}, itemStackMultiplier={_multiplier != null}, " +
                        $"ItemStacks.Configure={configure != null}, ChestUi.StatusText={status != null}, tab={_tabField != null}";

                    QIDebugger.LogError("[DropMoreLoot] Bindings incomplete: " + BindError);
                    return;
                }

                bool stackPatch = HarmonyPatchGuard.TryPatch(
                    harmony, configure, "Quartermaster.ItemStacks.Configure",
                    prefix: new HarmonyMethod(typeof(DropMoreLootIntegration), nameof(Configure_Prefix)));

                bool textPatch = HarmonyPatchGuard.TryPatch(
                    harmony, status, "Quartermaster.ChestUi.StatusText",
                    postfix: new HarmonyMethod(typeof(DropMoreLootIntegration), nameof(StatusText_Postfix)));

                IsReady = stackPatch && textPatch;

                if (IsReady && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[DropMoreLoot] Stack sizes defer to DropMoreLoot (stacking={_enabled.Value}, x{_multiplier.Value}); " +
                        $"toggle is {(BridgePlugin.DeferToDropMoreLootStacks.Value ? "on" : "off")}.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[DropMoreLoot] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Forces Quartermaster's stack override off while DropMoreLoot owns stacking, so its
        /// multiplied max-stack value is the one that survives.
        /// </summary>
        public static void Configure_Prefix(ref bool active)
        {
            if (Active) active = false;
        }

        /// <summary>
        /// The ledger's stack tab shows Quartermaster's MaximumStackSize, which is not being applied
        /// while deferring. Say who is in charge instead of showing a number that does nothing.
        /// </summary>
        public static void StatusText_Postfix(ref string __result)
        {
            if (!Active) return;

            try
            {
                if (_tabField.GetValue(null) is int tab && tab != 1) return;
                if (_stackPendingField != null && _stackPendingField.GetValue(null) is bool pending && pending) return;

                __result = $"Stack sizes are managed by DropMoreLoot (x{Multiplier}); Quartermaster's stack override is off.";
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[DropMoreLoot] Could not write the stack-screen notice", ex);
            }
        }
    }
}
