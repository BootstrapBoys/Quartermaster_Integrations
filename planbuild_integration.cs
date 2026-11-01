using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Lets PlanBuild plan pieces be placed using materials from Quartermaster storage.
    ///
    /// PlanBuild asks a single question ("which inventories belong to this player?") through
    /// PlanPiece.GetInventories, and both of its consumers - HasAllResources for the hover text and
    /// AddAllMaterials for "hold use" - walk that list. Appending one adapter therefore covers both
    /// the count and the consumption, and nothing here can spend items PlanBuild would not have
    /// spent itself.
    ///
    /// PlanPiece.IInventory lives in an assembly that is only a soft dependency, so the adapter is a
    /// DispatchProxy created against the interface's runtime type rather than a compiled class.
    /// </summary>
    public static class PlanBuildIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnablePlanBuild);

        private static Type _inventoryInterface;
        private static MethodInfo _proxyFactory;
        private static object _adapter;
        private static float _lastLogTime;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.PlanBuildGUID, out PluginInfo info) ||
                info?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly assembly = info.Instance.GetType().Assembly;
                Type planPiece = assembly.GetType("PlanBuild.Plans.PlanPiece") ?? FindPlanPiece(assembly);

                _inventoryInterface = planPiece?.GetNestedType("IInventory", BindingFlags.Public);
                MethodInfo getInventories = planPiece == null
                    ? null
                    : AccessTools.Method(planPiece, "GetInventories", new[] { typeof(Humanoid) });

                if (_inventoryInterface == null || getInventories == null)
                {
                    QIDebugger.LogError(
                        "[PlanBuild] PlanPiece.GetInventories or its IInventory interface was not found; " +
                        "plan costs stay pocket-only.");
                    return;
                }

                _proxyFactory = typeof(DispatchProxy).GetMethod(
                    "Create",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);

                if (_proxyFactory == null || !TryCreateAdapter())
                {
                    QIDebugger.LogError(
                        "[PlanBuild] Could not create an IInventory adapter on this runtime; plan costs stay pocket-only.");
                    return;
                }

                _ready = HarmonyPatchGuard.TryPatch(
                    harmony,
                    getInventories,
                    "PlanBuild.Plans.PlanPiece.GetInventories",
                    postfix: new HarmonyMethod(typeof(PlanBuildIntegration), nameof(GetInventories_Postfix)));

                if (_ready && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug("[PlanBuild] Added the Quartermaster storage adapter to plan inventories.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to initialize PlanBuildIntegration", ex);
            }
        }

        /// <summary>
        /// Namespace-independent fallback: PlanBuild's type layout has moved before, so match on the
        /// shape (a PlanPiece type carrying an IInventory interface) rather than the full name only.
        /// </summary>
        private static Type FindPlanPiece(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            foreach (Type type in types)
            {
                if (type != null &&
                    type.Name == "PlanPiece" &&
                    type.GetNestedType("IInventory", BindingFlags.Public) != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool TryCreateAdapter()
        {
            try
            {
                _adapter = _proxyFactory
                    .MakeGenericMethod(_inventoryInterface, typeof(StorageInventoryProxy))
                    .Invoke(null, null);

                return _adapter != null;
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[PlanBuild] DispatchProxy.Create failed for PlanPiece.IInventory", ex);
                return false;
            }
        }

        /// <summary>
        /// Runs after PlanBuild builds its list (which is the player's inventory plus whatever its
        /// own compat patches added). The adapter is only added for the local player, so NPC or
        /// other-humanoid contexts keep pocket-only costs.
        /// </summary>
        public static void GetInventories_Postfix(Humanoid player, object __result)
        {
            if (!BridgePlugin.Enabled || !IsReady || _adapter == null) return;

            Player localPlayer = Player.m_localPlayer;
            if (localPlayer == null || !ReferenceEquals(player, localPlayer)) return;

            if (!(__result is IList inventories)) return;
            if (inventories.Contains(_adapter)) return;

            inventories.Add(_adapter);

            // GetInventories runs on every hover and interact, so keep the trace to one line per
            // ten seconds instead of one per call.
            if (QIDebugger.Enabled && Time.time - _lastLogTime > 10f)
            {
                _lastLogTime = Time.time;
                QIDebugger.LogDebug(
                    $"[PlanBuild] Plan costs include {BridgePlugin.GetStorageContainers(localPlayer).Count} storage container(s).");
            }
        }

        /// <summary>
        /// PlanBuild's inventory contract, implemented against Quartermaster storage. Public and
        /// parameterless because DispatchProxy requires both.
        /// </summary>
        public class StorageInventoryProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                try
                {
                    switch (targetMethod.Name)
                    {
                        case "CountItems":
                            return Count(args[0] as string);

                        case "HaveItem":
                            return Count(args[0] as string) > 0;

                        case "RemoveItem":
                            Remove(args[0] as string, args[1] is int amount ? amount : 0);
                            return null;

                        default:
                            // Anything PlanBuild adds to the interface later is treated as absent
                            // rather than throwing inside the proxy.
                            return DefaultValue(targetMethod.ReturnType);
                    }
                }
                catch (Exception ex)
                {
                    QIDebugger.LogError($"[PlanBuild] Inventory adapter failed in {targetMethod.Name}", ex);
                    return DefaultValue(targetMethod.ReturnType);
                }
            }

            private static int Count(string sharedName)
            {
                Player player = Player.m_localPlayer;
                if (player == null || string.IsNullOrEmpty(sharedName)) return 0;

                // Storage only: PlanBuild's own StandardInventory already covers the pockets.
                return BridgePlugin.GetQuartermasterStorageCount(player, sharedName);
            }

            private static void Remove(string sharedName, int amount)
            {
                Player player = Player.m_localPlayer;
                if (player == null) return;

                int removed = BridgePlugin.RemoveFromStorage(player, sharedName, amount);
                if (removed > 0)
                {
                    QIDebugger.LogDebug($"[PlanBuild] Took {removed}/{amount}x '{sharedName}' from storage");
                }
            }

            private static object DefaultValue(Type type)
            {
                if (type == typeof(void)) return null;
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }
        }
    }
}
