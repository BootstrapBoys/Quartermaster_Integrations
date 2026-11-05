using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Makes Quartermaster's chest dock honest about what it can actually do with the container in
    /// front of you.
    ///
    /// Two behaviours, one switch:
    ///  * "Chest Config" is hidden when there is nothing to configure - the chest is not accessible,
    ///    or its ZDO is not ours to write, so any settings would be silently dropped.
    ///  * "Sort Chest" still sorts any chest Quartermaster can reach but refuses because we do not own
    ///    it. Quartermaster normally hands those to its shared-chest handshake, which needs the owner
    ///    present; here the chest is claimed (exactly what vanilla does when a player opens it) and then
    ///    sorted, so the change carries our owner revision and sticks.
    ///
    /// Chests that are in use by another player are never forced - that is Quartermaster's call.
    ///
    /// Config: `4. Patches -> AnyChestDock` (default on).
    /// </summary>
    public static class ChestDockPatch
    {
        private const int ConfigButton = 0;

        private static Type _chestUi;
        private static FieldInfo _dockButtons;
        private static FieldInfo _openContainer;
        private static MethodInfo _accessible;
        private static MethodInfo _isUsable;
        private static MethodInfo _canWrite;
        private static MethodInfo _stackWithin;
        private static MethodInfo _sortInventory;

        private static float _nextNote;
        private static string _lastNote = "";

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Assembly qm = BridgePlugin.QuartermasterAssembly;
                _chestUi = qm?.GetType("Quartermaster.ChestUi");
                Type registry = qm?.GetType("Quartermaster.ContainerRegistry");
                Type native = qm?.GetType("Quartermaster.NativeStorageAccess");
                Type transfers = qm?.GetType("Quartermaster.InventoryTransfers");
                Type warehouse = qm?.GetType("Quartermaster.WarehouseService");

                _dockButtons = AccessTools.Field(_chestUi, "dockButtons");
                _openContainer = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
                _accessible = AccessTools.Method(registry, "Accessible", new[] { typeof(Container) });
                _isUsable = AccessTools.Method(registry, "IsUsable", new[] { typeof(Container), typeof(bool) });
                _canWrite = AccessTools.Method(native, "CanWrite", new[] { typeof(ZNetView) });
                _stackWithin = AccessTools.Method(transfers, "StackWithin", new[] { typeof(Inventory) });
                _sortInventory = AccessTools.Method(warehouse, "SortInventory",
                    new[] { typeof(Inventory), typeof(bool), typeof(bool) });

                MethodInfo layout = AccessTools.Method(_chestUi, "LayoutActions");
                MethodInfo sortChest = AccessTools.Method(_chestUi, "SortChest");

                bool complete = _dockButtons != null && _openContainer != null && _accessible != null &&
                                _isUsable != null && _canWrite != null && _stackWithin != null &&
                                _sortInventory != null && layout != null && sortChest != null;

                if (!complete)
                {
                    QIDebugger.LogError(
                        "[ChestDock] Bindings incomplete: buttons=" + (_dockButtons != null) +
                        ", open=" + (_openContainer != null) + ", accessible=" + (_accessible != null) +
                        ", usable=" + (_isUsable != null) + ", canWrite=" + (_canWrite != null) +
                        ", stackWithin=" + (_stackWithin != null) + ", sortInventory=" + (_sortInventory != null) +
                        ", layout=" + (layout != null) + ", sort=" + (sortChest != null));
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, layout, "Quartermaster.ChestUi.LayoutActions",
                    postfix: new HarmonyMethod(typeof(ChestDockPatch), nameof(LayoutActions_Postfix)));

                HarmonyPatchGuard.TryPatch(
                    harmony, sortChest, "Quartermaster.ChestUi.SortChest",
                    prefix: new HarmonyMethod(typeof(ChestDockPatch), nameof(SortChest_Prefix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[ChestDock] Chest Config hides on chests the player cannot write; Sort Chest works on any accessible chest.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[ChestDock] Initialization failed", ex);
            }
        }

        /// <summary>Runs after Quartermaster decided which dock buttons to show.</summary>
        public static void LayoutActions_Postfix()
        {
            if (BridgePlugin.AnyChestDock == null || !BridgePlugin.AnyChestDock.Value) return;

            try
            {
                Container c = OpenContainer();
                if (c == null) return;
                if (ConfigurableCached(c)) return;
                SetButtonActive(ConfigButton, false);
                Note(c);
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ChestDock] Could not adjust the chest dock", ex);
            }
        }

        /// <summary>
        /// Quartermaster refuses to sort a chest it cannot write; this lets the sort run anyway when the
        /// chest is reachable but owned by someone else. Everything else stays Quartermaster's decision.
        /// </summary>
        public static bool SortChest_Prefix()
        {
            if (BridgePlugin.AnyChestDock == null || !BridgePlugin.AnyChestDock.Value) return true;

            try
            {
                Container c = OpenContainer();
                if (c == null) return true;

                if ((bool)_isUsable.Invoke(null, new object[] { c, true })) return true;   // normal path
                if (!(bool)_accessible.Invoke(null, new object[] { c })) return true;      // not reachable at all
                if (c.IsInUse()) return true;                                              // someone is looking in it

                Inventory inventory = c.GetInventory();
                if (inventory == null) return true;

                // Ownership is a revision race the server arbitrates: claiming bumps our revision, the
                // server keeps the highest, and vanilla does the same the moment a player opens a chest.
                // Claiming first is what makes the sort stick while the current owner is online but away.
                ZNetView view = ContainerZdo.View(c);
                bool claimed = false;
                if (view != null && !view.IsOwner())
                {
                    view.ClaimOwnership();
                    claimed = view.IsOwner();
                }

                _stackWithin.Invoke(null, new object[] { inventory });
                _sortInventory.Invoke(null, new object[] { inventory, false, false });

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[ChestDock] Sorted '{ContainerZdo.Name(c)}' even though it is not ours to write" +
                        (claimed ? " (claimed it first)." : "."));
                }

                return false;   // Quartermaster's own handler would have bailed out
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ChestDock] Forced sort failed", ex);
                return true;
            }
        }

        private static int _cachedId;
        private static int _cachedFrame = -1;
        private static bool _cachedConfigurable;

        /// <summary>LayoutActions runs every frame; Accessible() can register and invoke CheckAccess.</summary>
        private static bool ConfigurableCached(Container c)
        {
            int id = c.GetInstanceID();
            int frame = Time.frameCount;

            if (id == _cachedId && frame == _cachedFrame) return _cachedConfigurable;

            bool value = Configurable(c);
            _cachedId = id;
            _cachedFrame = frame;
            _cachedConfigurable = value;
            return value;
        }

        private static bool Configurable(Container c)
        {
            if (c == null || c.GetInventory() == null) return false;
            if (!(bool)_accessible.Invoke(null, new object[] { c })) return false;

            ZNetView view = ContainerZdo.View(c);
            return view != null && (bool)_canWrite.Invoke(null, new object[] { view });
        }

        private static Container OpenContainer()
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || _openContainer == null) return null;
            return _openContainer.GetValue(gui) as Container;
        }

        private static void SetButtonActive(int index, bool active)
        {
            IList buttons = _dockButtons?.GetValue(null) as IList;
            if (buttons == null || buttons.Count <= index) return;

            Component button = buttons[index] as Component;
            if (button == null) return;

            if (button.gameObject.activeSelf != active) button.gameObject.SetActive(active);
        }

        /// <summary>One line per chest, not per frame.</summary>
        private static void Note(Container c)
        {
            if (!QIDebugger.Enabled) return;

            string name = ContainerZdo.Name(c);
            if (name == _lastNote && Time.unscaledTime < _nextNote) return;

            _lastNote = name;
            _nextNote = Time.unscaledTime + 10f;
            QIDebugger.LogDebug($"[ChestDock] '{name}' is not ours to configure - 'Chest Config' hidden.");
        }
    }
}
