using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Keeps Quartermaster's chest dock off the inventory slots when another mod grows the player grid.
    ///
    /// Quartermaster positions its dock from the panel it parents to: the player panel normally, or
    /// InventorySlots' "InventorySlots_CustomSlotPanel" when that mod is present (ExternalSlotCompatibility).
    /// Neither accounts for rows added by a different inventory mod, so the dock - and the Sort Inventory
    /// button with it - can end up sitting on the new bottom row.
    ///
    /// This measures instead of assuming: InventoryGrid.m_elements holds a RectTransform per slot, so the
    /// dock is only moved when its rectangle actually overlaps one, and then only as far as the lowest
    /// overlapping cell. If nothing overlaps, nothing moves. Quartermaster's own Sort Inventory offsets
    /// (ledger - Interface) still apply on top and remain the manual answer.
    ///
    /// Config: `4. Patches -> KeepDockOffSlots` (default on).
    /// </summary>
    public static class DockClearancePatch
    {
        private const int SortInventoryButton = 3;
        private const float Gap = 4f;

        private static FieldInfo _dockButtons;
        private static FieldInfo _elements;
        private static int _lastFrame = -1;
        private static float _nextNote;

        private static readonly Vector3[] DockCorners = new Vector3[4];
        private static readonly Vector3[] CellCorners = new Vector3[4];

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type chestUi = qm?.GetType("Quartermaster.ChestUi");

                _dockButtons = AccessTools.Field(chestUi, "dockButtons");
                _elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");

                MethodInfo layout = AccessTools.Method(chestUi, "LayoutActions");

                if (layout == null || _dockButtons == null || _elements == null)
                {
                    QIDebugger.LogError(
                        $"[Dock] Bindings incomplete: LayoutActions={layout != null}, " +
                        $"dockButtons={_dockButtons != null}, elements={_elements != null}");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, layout, "Quartermaster.ChestUi.LayoutActions",
                    postfix: new HarmonyMethod(typeof(DockClearancePatch), nameof(LayoutActions_Postfix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[Dock] The chest dock is moved down when it overlaps an inventory slot (expanded grids).");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Dock] Initialization failed", ex);
            }
        }

        public static void LayoutActions_Postfix()
        {
            if (BridgePlugin.KeepDockOffSlots == null || !BridgePlugin.KeepDockOffSlots.Value) return;
            if (Time.frameCount == _lastFrame) return;      // LayoutActions can run more than once per frame
            _lastFrame = Time.frameCount;

            try
            {
                InventoryGui gui = InventoryGui.instance;
                if (gui == null || !InventoryGui.IsVisible() || gui.m_playerGrid == null) return;

                IList buttons = _dockButtons.GetValue(null) as IList;
                if (buttons == null || buttons.Count <= SortInventoryButton) return;

                Button sort = buttons[SortInventoryButton] as Button;
                if (sort == null || !sort.gameObject.activeInHierarchy) return;

                RectTransform dock = sort.transform.parent as RectTransform;
                if (dock == null || !dock.gameObject.activeInHierarchy) return;

                // Only the player-side dock: with a container open, the dock belongs to the chest panel.
                if (gui.m_container != null && gui.m_container.gameObject.activeInHierarchy &&
                    dock.IsChildOf(gui.m_container))
                {
                    return;
                }

                float dockMinX, dockMaxX, dockMinY, dockMaxY, lowestCellBottom;
                if (!DockBounds(dock, out dockMinX, out dockMaxX, out dockMinY, out dockMaxY)) return;
                if (!LowestOverlappingCell(gui, dockMinX, dockMaxX, dockMinY, dockMaxY, out lowestCellBottom)) return;

                float shift = dockMaxY - lowestCellBottom + Gap;
                if (shift <= 0.5f) return;

                dock.position -= new Vector3(0f, shift, 0f);

                if (QIDebugger.Enabled && Time.unscaledTime >= _nextNote)
                {
                    _nextNote = Time.unscaledTime + 30f;
                    QIDebugger.LogDebug(
                        $"[Dock] Moved the chest dock down {shift:0} px so it clears the inventory row.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[Dock] Could not measure the dock against the inventory", ex);
            }
        }

        private static bool DockBounds(RectTransform dock, out float minX, out float maxX, out float minY, out float maxY)
        {
            dock.GetWorldCorners(DockCorners);
            minX = Mathf.Min(DockCorners[0].x, DockCorners[2].x);
            maxX = Mathf.Max(DockCorners[0].x, DockCorners[2].x);
            minY = Mathf.Min(DockCorners[0].y, DockCorners[2].y);
            maxY = Mathf.Max(DockCorners[0].y, DockCorners[2].y);
            return maxX > minX && maxY > minY;
        }

        /// <summary>Bottom edge of the lowest slot the dock covers, in world units.</summary>
        private static bool LowestOverlappingCell(
            InventoryGui gui, float dockMinX, float dockMaxX, float dockMinY, float dockMaxY,
            out float lowestBottom)
        {
            lowestBottom = float.MaxValue;

            IList cells = _elements.GetValue(gui.m_playerGrid) as IList;
            if (cells == null) return false;

            bool hit = false;
            foreach (object entry in cells)
            {
                Component cell = entry as Component;
                if (cell == null || !cell.gameObject.activeInHierarchy) continue;

                RectTransform rect = cell.transform as RectTransform;
                if (rect == null || rect.rect.width <= 0f || rect.rect.height <= 0f) continue;

                rect.GetWorldCorners(CellCorners);
                float cellMinX = Mathf.Min(CellCorners[0].x, CellCorners[2].x);
                float cellMaxX = Mathf.Max(CellCorners[0].x, CellCorners[2].x);
                float cellMinY = Mathf.Min(CellCorners[0].y, CellCorners[2].y);
                float cellMaxY = Mathf.Max(CellCorners[0].y, CellCorners[2].y);

                if (cellMaxX <= dockMinX || cellMinX >= dockMaxX) continue;
                if (cellMaxY <= dockMinY || cellMinY >= dockMaxY) continue;

                hit = true;
                if (cellMinY < lowestBottom) lowestBottom = cellMinY;
            }

            return hit;
        }
    }
}
