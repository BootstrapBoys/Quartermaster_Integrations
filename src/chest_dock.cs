using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Makes Quartermaster's chest dock match what the open container can actually do.
    ///
    /// Three behaviours, one switch (`4. Patches -> ContainerFixes`, default on):
    ///  * "Chest Config" is hidden where the settings cannot take effect - the container is not
    ///    accessible, or its ZDO is not ours to write, so Quartermaster would drop them silently.
    ///  * "Sort Chest" works on any accessible container. Quartermaster hands containers it does not own
    ///    to a shared-chest handshake that needs the owner present; here the chest is claimed first
    ///    (exactly what vanilla does when a player opens it) and then sorted, so the change carries our
    ///    owner revision and sticks.
    ///  * "Deposit All" works on any accessible container, not only Quartermaster deposit chests. For an
    ///    ordinary chest it stores everything the way the vanilla chest does (hotbar and equipment kept),
    ///    claiming the container first when its owner is away.
    ///
    /// A container another player has open is never forced - that is Quartermaster's call.
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
        private static MethodInfo _depositAll;
        private static MethodInfo _getSettings;
        private static FieldInfo _deposit;
        private static MethodInfo _storeAll;

        /// <summary>Settings object whose Deposit flag the layout prefix raised for one call.</summary>
        private static object _forcedDeposit;

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
                _storeAll = AccessTools.Method(warehouse, "StoreAllInOpenChest",
                    new[] { typeof(Player), typeof(Container) });

                _getSettings = AccessTools.Method(registry, "GetSettings", new[] { typeof(Container) });
                _deposit = _getSettings?.ReturnType?.GetField(
                    "Deposit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo layout = AccessTools.Method(_chestUi, "LayoutActions");
                MethodInfo sortChest = AccessTools.Method(_chestUi, "SortChest");
                _depositAll = AccessTools.Method(_chestUi, "DepositAll");

                bool complete = _dockButtons != null && _openContainer != null && _accessible != null &&
                                _isUsable != null && _canWrite != null && _stackWithin != null &&
                                _sortInventory != null && _storeAll != null && _getSettings != null &&
                                _deposit != null && layout != null && sortChest != null && _depositAll != null;

                if (!complete)
                {
                    QIDebugger.LogError(
                        "[ChestDock] Bindings incomplete: buttons=" + (_dockButtons != null) +
                        ", open=" + (_openContainer != null) + ", accessible=" + (_accessible != null) +
                        ", usable=" + (_isUsable != null) + ", canWrite=" + (_canWrite != null) +
                        ", stackWithin=" + (_stackWithin != null) + ", sortInventory=" + (_sortInventory != null) +
                        ", storeAll=" + (_storeAll != null) + ", settings=" + (_getSettings != null) +
                        ", deposit=" + (_deposit != null) + ", layout=" + (layout != null) +
                        ", sort=" + (sortChest != null) + ", depositAll=" + (_depositAll != null));
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, layout, "Quartermaster.ChestUi.LayoutActions",
                    prefix: new HarmonyMethod(typeof(ChestDockPatch), nameof(LayoutActions_Prefix)),
                    postfix: new HarmonyMethod(typeof(ChestDockPatch), nameof(LayoutActions_Postfix)));

                HarmonyPatchGuard.TryPatch(
                    harmony, sortChest, "Quartermaster.ChestUi.SortChest",
                    prefix: new HarmonyMethod(typeof(ChestDockPatch), nameof(SortChest_Prefix)));

                HarmonyPatchGuard.TryPatch(
                    harmony, _depositAll, "Quartermaster.ChestUi.DepositAll",
                    prefix: new HarmonyMethod(typeof(ChestDockPatch), nameof(DepositAll_Prefix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[ChestDock] Container fixes: Chest Config only where it can save; Sort Chest and " +
                        "Deposit All work on any accessible container.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[ChestDock] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Quartermaster only draws "Deposit All" for its own deposit chests. Raise the flag for the
        /// duration of one layout pass so the button is laid out like the others; the postfix puts it back
        /// before anything else reads the settings.
        /// </summary>
        public static void LayoutActions_Prefix()
        {
            // If a previous pass threw before its postfix ran, put the flag back before raising it again.
            if (_forcedDeposit != null) RestoreDepositFlag();
            if (!Enabled()) return;

            try
            {
                Container c = OpenContainer();
                if (c == null) return;
                if (!(bool)_accessible.Invoke(null, new object[] { c })) return;

                object settings = _getSettings.Invoke(null, new object[] { c });
                if (settings == null) return;
                if ((bool)_deposit.GetValue(settings)) return;   // a real deposit chest: untouched

                _deposit.SetValue(settings, true);
                _forcedDeposit = settings;
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ChestDock] Could not show the deposit button", ex);
            }
        }

        /// <summary>Runs after Quartermaster decided which dock buttons to show.</summary>
        public static void LayoutActions_Postfix()
        {
            RestoreDepositFlag();
            if (!Enabled()) return;

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
        /// Quartermaster refuses to sort a container it cannot write; this lets the sort run anyway when
        /// the container is reachable but owned by someone else.
        /// </summary>
        public static bool SortChest_Prefix()
        {
            if (!Enabled()) return true;

            try
            {
                Container c = OpenContainer();
                if (c == null) return true;

                if ((bool)_isUsable.Invoke(null, new object[] { c, true })) return true;   // normal path
                if (!(bool)_accessible.Invoke(null, new object[] { c })) return true;      // not reachable
                if (c.IsInUse()) return true;                                              // someone is in it

                Inventory inventory = c.GetInventory();
                if (inventory == null) return true;

                bool claimed = ClaimIfAway(c);

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

        /// <summary>
        /// Quartermaster's Deposit All only acts on its own deposit chests. For any other open container,
        /// store everything the way a vanilla chest would - hotbar and equipment kept - after claiming the
        /// container if its owner is away.
        /// </summary>
        public static bool DepositAll_Prefix()
        {
            if (!Enabled()) return true;

            try
            {
                Container c = OpenContainer();
                if (c == null) return true;

                object settings = _getSettings.Invoke(null, new object[] { c });
                if (settings != null && (bool)_deposit.GetValue(settings)) return true;   // QM's own path
                if (!(bool)_accessible.Invoke(null, new object[] { c })) return true;     // not reachable
                if (c.IsInUse()) return true;                                             // someone is in it

                Player player = Player.m_localPlayer;
                if (player == null) return true;

                bool claimed = ClaimIfAway(c);

                if (!(bool)_isUsable.Invoke(null, new object[] { c, true }))
                {
                    player.Message(MessageHud.MessageType.Center,
                        "Nothing deposited: '" + ContainerZdo.Name(c) + "' is not available to write.");
                    return false;
                }

                int count = (int)_storeAll.Invoke(null, new object[] { player, c });

                player.Message(MessageHud.MessageType.Center,
                    count > 0
                        ? "Deposited " + count + " items (hotbar and equipment kept)"
                        : "Nothing to deposit");

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[ChestDock] Stored {count} item(s) in '{ContainerZdo.Name(c)}' " +
                        (claimed ? "(claimed it first)." : "(not a deposit chest)."));
                }

                return false;
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ChestDock] Deposit All fallback failed", ex);
                return true;
            }
        }

        private static bool Enabled()
        {
            return BridgePlugin.ContainerFixes != null && BridgePlugin.ContainerFixes.Value;
        }

        private static void RestoreDepositFlag()
        {
            if (_forcedDeposit == null) return;

            try
            {
                _deposit.SetValue(_forcedDeposit, false);
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[ChestDock] Could not restore the deposit flag", ex);
            }

            _forcedDeposit = null;
        }

        /// <summary>
        /// Ownership is a revision race the server arbitrates: claiming bumps our revision, the server
        /// keeps the highest, and vanilla does exactly this when a player opens a chest. Only containers
        /// whose owner is not a connected player are claimed.
        /// </summary>
        private static bool ClaimIfAway(Container c)
        {
            ZNetView view = ContainerZdo.View(c);
            if (view == null || !view.IsValid() || view.IsOwner()) return false;
            if (ContainerZdo.OwnerIsConnectedPlayer(view)) return false;

            view.ClaimOwnership();
            return view.IsOwner();
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
