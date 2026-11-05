using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Treats "Receive matching items" as a precondition for a chest counting as craft supply.
    ///
    /// Quartermaster's CanSupply() only asks whether the chest can be read (accessible, valid view, not in
    /// use); AcceptStorage is not consulted anywhere on the supply path. So a chest explicitly taken out
    /// of the storage system is still counted in the recipe panel and offered as craft supply.
    ///
    /// With this on, CanSupply() reports false for a chest whose AcceptStorage is off. That single
    /// predicate is what CraftStorageAccess.Nearby() filters on, so the chest drops out of the counts and
    /// out of the pull validation (Run/SourcesValid) at the same time.
    /// </summary>
    public static class CraftSupplyPatch
    {
        private static MethodInfo _getSettings;
        private static FieldInfo _acceptStorage;
        private static readonly Dictionary<int, float> LastLogged = new Dictionary<int, float>();
        private const float Throttle = 60f;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type storage = qm?.GetType("Quartermaster.CraftStorageAccess");
                Type registry = qm?.GetType("Quartermaster.ContainerRegistry");

                MethodInfo canSupply = storage == null
                    ? null
                    : AccessTools.Method(storage, "CanSupply", new[] { typeof(Container) });

                _getSettings = registry == null
                    ? null
                    : AccessTools.Method(registry, "GetSettings", new[] { typeof(Container) });

                _acceptStorage = _getSettings?.ReturnType?.GetField(
                    "AcceptStorage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (canSupply == null || _getSettings == null || _acceptStorage == null)
                {
                    QIDebugger.LogError(
                        $"[Supply] Bindings incomplete: CanSupply={canSupply != null}, " +
                        $"GetSettings={_getSettings != null}, AcceptStorage={_acceptStorage != null}");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, canSupply, "Quartermaster.CraftStorageAccess.CanSupply",
                    prefix: new HarmonyMethod(typeof(CraftSupplyPatch), nameof(CanSupply_Prefix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[Supply] Chests with 'Receive matching items' off are not counted as craft supply.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Supply] Initialization failed", ex);
            }
        }

        public static bool CanSupply_Prefix(Container chest, ref bool __result)
        {
            if (BridgePlugin.SupplyRequiresAccept == null || !BridgePlugin.SupplyRequiresAccept.Value) return true;
            if (chest == null) return true;

            try
            {
                object settings = _getSettings.Invoke(null, new object[] { chest });
                if (settings == null) return true;

                if ((bool)_acceptStorage.GetValue(settings)) return true;

                __result = false;

                if (QIDebugger.Enabled)
                {
                    int id = chest.GetInstanceID();
                    float now = Time.time;
                    if (!LastLogged.TryGetValue(id, out float last) || now - last > Throttle)
                    {
                        LastLogged[id] = now;
                        QIDebugger.LogDebug(
                            $"[Supply] '{chest.m_name}' is not counted: 'Receive matching items' is off.");
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[Supply] Could not read the chest settings", ex);
                return true;
            }
        }
    }
}
