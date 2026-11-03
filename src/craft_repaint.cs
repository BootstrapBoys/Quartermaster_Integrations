using System;
using System.Reflection;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Repaints the crafting panel once after each craft.
    ///
    /// InventoryGui builds its recipe list from HaveRequirements and only rebuilds it on specific
    /// events, so after crafting consumes materials the list can keep the tint it was painted with -
    /// every recipe looking unaffordable while the craft button, which reads live counts, still works.
    /// DoCrafting marks a repaint pending; the next idle frame invokes the private
    /// UpdateCraftingPanel(false) with the count cache invalidated, so the list reflects the new
    /// inventory without touching anything while a craft timer is still running.
    /// </summary>
    public static class CraftRepaintPatch
    {
        private static bool _pending;
        private static MethodInfo _updatePanel;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                MethodInfo doCrafting = AccessTools.Method(typeof(InventoryGui), "DoCrafting", new[] { typeof(Player) });
                _updatePanel = AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel", new[] { typeof(bool) });

                if (doCrafting == null || _updatePanel == null)
                {
                    QIDebugger.LogError(
                        $"[Craft] Repaint bindings incomplete: DoCrafting={doCrafting != null}, " +
                        $"UpdateCraftingPanel={_updatePanel != null}");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, doCrafting, "InventoryGui.DoCrafting",
                    postfix: new HarmonyMethod(typeof(CraftRepaintPatch), nameof(DoCrafting_Postfix)));

                if (QIDebugger.Enabled) QIDebugger.LogDebug("[Craft] Recipe list repaints after each craft.");
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Craft] Repaint initialization failed", ex);
            }
        }

        /// <summary>Marks the list stale; the flush happens when the craft timer is idle.</summary>
        public static void DoCrafting_Postfix()
        {
            _pending = true;
        }

        /// <summary>Called from the per-frame crafting postfix; never repaints mid-craft.</summary>
        public static void FlushIfIdle(InventoryGui gui, float craftTimer)
        {
            if (!_pending || gui == null || craftTimer >= 0f || _updatePanel == null) return;

            _pending = false;
            BridgePlugin.MarkCacheDirty();

            try
            {
                _updatePanel.Invoke(gui, new object[] { false });
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[Craft] Could not repaint the recipe list", ex);
            }
        }
    }
}
