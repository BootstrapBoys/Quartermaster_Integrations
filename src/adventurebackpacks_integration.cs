using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Quartermaster_Integrations
{
    public static class AdventureBackpacksIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnableAdventureBackpacks);

        private static Func<bool> _isBackpackOpen;
        private static FieldInfo _currentContainerField;
        private static readonly List<Button> CachedQmSortButtons = new List<Button>();
        private static int _lastButtonScanFrame = -1;

        // GetComponentInParent walks the hierarchy, so cache the answer per container per frame
        // instead of doing it twice per container on every cache rebuild.
        private static readonly Dictionary<int, bool> ParentPlayerCache = new Dictionary<int, bool>();
        private static int _parentCacheFrame = -1;

        // Tracks when Quartermaster is executing a sort or store action so AdventureBackpacks'
        // Container.TakeAll / MoveAll hooks cannot dump the backpack into the player's main inventory
        internal static bool InQuartermasterSortOrStore;

        public sealed class BackpackIsolationState
        {
            internal InventoryGui Gui;
            internal Container DetachedBackpackProxy;
        }

        public static void Initialize(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.AdventureBackpacksGUID, out PluginInfo abInfo) ||
                abInfo?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly abAssembly = abInfo.Instance.GetType().Assembly;
                Type abApiType = abAssembly.GetType("AdventureBackpacks.API.ABAPI");
                if (abApiType != null)
                {
                    MethodInfo isOpenMethod = AccessTools.Method(abApiType, "IsBackpackOpen");
                    if (isOpenMethod != null && isOpenMethod.ReturnType == typeof(bool) && isOpenMethod.GetParameters().Length == 0)
                    {
                        _isBackpackOpen = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), isOpenMethod);
                    }
                }

                _currentContainerField = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");

                _ready = abApiType != null;
                if (!_ready) return;

                ApplyPatches(harmony);

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug("AdventureBackpacksIntegration initialized and patched Quartermaster.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to initialize AdventureBackpacksIntegration", ex);
            }
        }

        private static void ApplyPatches(Harmony harmony)
        {
            // 1. Guard Container.TakeAll on AB_BackpackProxy so Quartermaster never triggers MoveAll
            //    from the backpack into the player inventory. The return type is not something we can
            //    compile against, so pick the prefix variant that matches what shipped.
            MethodInfo containerTakeAll = AccessTools.Method(typeof(Container), "TakeAll");
            if (containerTakeAll != null)
            {
                HarmonyMethod takeAllPrefix = containerTakeAll.ReturnType == typeof(bool)
                    ? new HarmonyMethod(typeof(AdventureBackpacksIntegration), nameof(Container_TakeAll_Prefix))
                    {
                        priority = Priority.First
                    }
                    : new HarmonyMethod(typeof(AdventureBackpacksIntegration), nameof(Container_TakeAll_Prefix_NoResult))
                    {
                        priority = Priority.First
                    };

                HarmonyPatchGuard.TryPatch(harmony, containerTakeAll, "Container.TakeAll", prefix: takeAllPrefix);
            }
            else
            {
                QIDebugger.LogError("[Patch] 'Container.TakeAll' skipped: target method not found.");
            }

            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.QuartermasterGUID, out PluginInfo qmInfo) ||
                qmInfo?.Instance == null)
            {
                return;
            }

            Assembly qmAssembly = qmInfo.Instance.GetType().Assembly;
            Type warehouseType = qmAssembly.GetType("Quartermaster.WarehouseService");
            if (warehouseType != null)
            {
                // 2. Isolate the backpack proxy during SortInventory so sorting the player inventory
                //    never touches the backpack
                MethodInfo sortMethod = AccessTools.Method(warehouseType, "SortInventory");
                if (sortMethod != null)
                {
                    HarmonyPatchGuard.TryPatch(
                        harmony,
                        sortMethod,
                        "Quartermaster.WarehouseService.SortInventory",
                        prefix: new HarmonyMethod(typeof(AdventureBackpacksIntegration), nameof(SortInventory_Prefix)),
                        finalizer: new HarmonyMethod(typeof(AdventureBackpacksIntegration), nameof(SortInventory_Finalizer)));
                }

                // 3. Keep Quartermaster's "store all in open chest" from treating the backpack proxy
                //    as a target chest. Signature in 0.1.66 is
                //    StoreAllInOpenChest(Player, Container) : int - note the Player first parameter.
                MethodInfo storeAllMethod = AccessTools.Method(
                    warehouseType,
                    "StoreAllInOpenChest",
                    new[] { typeof(Player), typeof(Container) });

                if (storeAllMethod != null && storeAllMethod.ReturnType == typeof(int))
                {
                    HarmonyPatchGuard.TryPatch(
                        harmony,
                        storeAllMethod,
                        "Quartermaster.WarehouseService.StoreAllInOpenChest",
                        prefix: new HarmonyMethod(typeof(AdventureBackpacksIntegration), nameof(BlockBackpackContainerOp_Prefix)));
                }
                else
                {
                    QIDebugger.LogError("[Patch] 'Quartermaster.WarehouseService.StoreAllInOpenChest' skipped: expected (Player, Container) returning int.");
                }
            }
        }

        public static bool IsBackpackOpen()
        {
            if (!IsReady) return false;
            try
            {
                if (_isBackpackOpen != null && _isBackpackOpen()) return true;
                Container current = GetCurrentContainer(InventoryGui.instance);
                return IsBackpackProxyContainer(current);
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[AB] Failed checking whether a backpack is open", ex);
                return false;
            }
        }

        // Matches AdventureBackpacks.Patches.ContainerPatches.IsBackpackProxy. Name checks are cheap;
        // the parent lookup is cached per frame because this runs for every container in the scan.
        public static bool IsBackpackProxyContainer(Container container)
        {
            if (!IsReady || container == null) return false;

            GameObject go = container.gameObject;
            if (go != null)
            {
                string objName = go.name;
                if (objName.StartsWith("AB_BackpackProxy", StringComparison.Ordinal) ||
                    objName.Equals("Player(Clone)", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            Player localPlayer = Player.m_localPlayer;
            if (localPlayer != null && container.transform.parent == localPlayer.transform)
            {
                return true;
            }

            return HasPlayerParent(container);
        }

        private static bool HasPlayerParent(Container container)
        {
            int frame = Time.frameCount;
            if (frame != _parentCacheFrame)
            {
                ParentPlayerCache.Clear();
                _parentCacheFrame = frame;
            }

            int id = container.GetInstanceID();
            if (ParentPlayerCache.TryGetValue(id, out bool cached))
            {
                return cached;
            }

            bool hasPlayerParent = container.GetComponentInParent<Player>() != null;
            ParentPlayerCache[id] = hasPlayerParent;
            return hasPlayerParent;
        }

        public static Container GetCurrentContainer(InventoryGui gui)
        {
            if (gui == null || _currentContainerField == null) return null;
            return _currentContainerField.GetValue(gui) as Container;
        }

        public static void SetCurrentContainer(InventoryGui gui, Container container)
        {
            if (gui == null || _currentContainerField == null) return;
            _currentContainerField.SetValue(gui, container);
        }

        // Prevents Container.TakeAll from emptying the backpack into player inventory during a
        // Quartermaster sort (used only when TakeAll returns bool).
        public static bool Container_TakeAll_Prefix(Container __instance, ref bool __result)
        {
            if (!InQuartermasterSortOrStore) return true;
            if (!BridgePlugin.Enabled || !IsReady) return true;

            if (IsBackpackProxyContainer(__instance))
            {
                __result = false;
                return false;
            }

            return true;
        }

        // Same isolation for a void-returning TakeAll: skipping the original method is the effect we
        // want, and there is no return value to set.
        public static bool Container_TakeAll_Prefix_NoResult(Container __instance)
        {
            if (!InQuartermasterSortOrStore) return true;
            if (!BridgePlugin.Enabled || !IsReady) return true;

            return !IsBackpackProxyContainer(__instance);
        }

        // Isolates the open backpack proxy while Quartermaster sorts the player's main inventory
        public static void SortInventory_Prefix(Inventory inventory, out BackpackIsolationState __state)
        {
            __state = null;
            if (!IsReady || inventory == null) return;
            if (!BridgePlugin.Enabled || !IsReady) return;

            InQuartermasterSortOrStore = true;

            InventoryGui gui = InventoryGui.instance;
            Player localPlayer = Player.m_localPlayer;
            if (gui == null || localPlayer == null) return;

            // When sorting the player's main inventory while the backpack is open, temporarily detach
            // m_currentContainer so Quartermaster's sort logic cannot touch or pull from the proxy
            if (inventory == localPlayer.GetInventory())
            {
                Container current = GetCurrentContainer(gui);
                if (IsBackpackProxyContainer(current))
                {
                    __state = new BackpackIsolationState
                    {
                        Gui = gui,
                        DetachedBackpackProxy = current
                    };
                    SetCurrentContainer(gui, null);
                }
            }
        }

        public static Exception SortInventory_Finalizer(BackpackIsolationState __state, Exception __exception)
        {
            InQuartermasterSortOrStore = false;

            try
            {
                if (__state?.Gui != null && __state.DetachedBackpackProxy != null)
                {
                    SetCurrentContainer(__state.Gui, __state.DetachedBackpackProxy);
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[AB] Failed restoring m_currentContainer after Quartermaster sort", ex);
            }

            return __exception;
        }

        public static bool BlockBackpackContainerOp_Prefix(Player player, Container container, ref int __result)
        {
            if (!BridgePlugin.Enabled || !IsReady) return true;

            if (IsBackpackProxyContainer(container))
            {
                __result = 0;
                return false;
            }

            return true;
        }

        // Keeps Quartermaster's Sort Inventory button visible when the AdventureBackpacks window is open
        public static void EnsureSortButtonVisible(InventoryGui gui)
        {
            if (gui == null || gui.m_player == null) return;

            if (Time.frameCount - _lastButtonScanFrame > 60 || CachedQmSortButtons.Count == 0)
            {
                _lastButtonScanFrame = Time.frameCount;
                CachedQmSortButtons.Clear();

                // Scan both player panel and container panel for Quartermaster's Sort button(s)
                Button[] playerButtons = gui.m_player.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < playerButtons.Length; i++)
                {
                    Button b = playerButtons[i];
                    if (b != null && b.name.IndexOf("Sort", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        CachedQmSortButtons.Add(b);
                    }
                }

                if (gui.m_container != null)
                {
                    Button[] containerButtons = gui.m_container.GetComponentsInChildren<Button>(true);
                    for (int i = 0; i < containerButtons.Length; i++)
                    {
                        Button b = containerButtons[i];
                        if (b != null && b.name.IndexOf("Sort", StringComparison.OrdinalIgnoreCase) >= 0 && !CachedQmSortButtons.Contains(b))
                        {
                            CachedQmSortButtons.Add(b);
                        }
                    }
                }
            }

            for (int i = 0; i < CachedQmSortButtons.Count; i++)
            {
                Button btn = CachedQmSortButtons[i];
                if (btn != null && btn.gameObject != null && !btn.gameObject.activeSelf)
                {
                    btn.gameObject.SetActive(true);
                }
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Update")]
    public static class InventoryGui_Update_AdventureBackpacksSortButtonPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(InventoryGui __instance)
        {
            if (!BridgePlugin.Enabled || !AdventureBackpacksIntegration.IsReady) return;
            if (!AdventureBackpacksIntegration.IsReady || !InventoryGui.IsVisible()) return;

            if (AdventureBackpacksIntegration.IsBackpackOpen())
            {
                AdventureBackpacksIntegration.EnsureSortButtonVisible(__instance);
            }
        }
    }
}
