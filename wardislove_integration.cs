using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Applies WardIsLove's chest permissions to Quartermaster storage.
    ///
    /// Quartermaster gates storage on the vanilla private-area check, and WardIsLove can be
    /// configured to block container use inside a ward even when that vanilla check would allow it
    /// (a ward with the guard-stone option off, or a player who is merely permitted to build). The
    /// result is a crafting window that offers materials from chests the player cannot open.
    ///
    /// Quartermaster answers "how much is in this container" through one method,
    /// InventoryTransfers.AvailableInContainer, for every container it counts or pulls from, so
    /// returning zero there covers both the displayed count and what a craft actually takes. This is
    /// deliberately a *filter*: it never adds storage, only removes containers the ward forbids.
    /// </summary>
    public static class WardIsLoveIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnableWardIsLove);

        // CheckAccess( playerID, point, radius, flash ) : bool - true when the player may use the
        // area. flash:false keeps the ward shield from pulsing during a background count.
        private static Func<long, Vector3, float, bool, bool> _checkAccess;

        // Wards change rarely but AvailableInContainer runs per container per frame while a crafting
        // window is open, so results are memoised for a short window instead of re-scanning wards.
        private const int CacheFrames = 30;
        private static readonly Dictionary<Container, bool> BlockedCache = new Dictionary<Container, bool>();
        private static int _cacheFrame = -1;
        private static float _lastLogTime;
        private static float _lastErrorTime;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.WardIsLoveGUID, out PluginInfo info) ||
                info?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly assembly = info.Instance.GetType().Assembly;

                // Range tying is independent of the permission filter, so bind it before the
                // CheckAccess lookup that may fail on a future WardIsLove layout.
                WardHostRange.Initialize(harmony, assembly);

                Type check = assembly.GetType("WardIsLove.Util.CustomCheck");
                MethodInfo checkAccess = check == null
                    ? null
                    : AccessTools.Method(check, "CheckAccess", new[]
                    {
                        typeof(long),
                        typeof(Vector3),
                        typeof(float),
                        typeof(bool)
                    });

                if (checkAccess == null || checkAccess.ReturnType != typeof(bool))
                {
                    QIDebugger.LogError(
                        "[WardIsLove] CustomCheck.CheckAccess(long, Vector3, float, bool) not found; " +
                        "ward permissions are not applied to storage counts.");
                    return;
                }

                _checkAccess = (Func<long, Vector3, float, bool, bool>)Delegate.CreateDelegate(
                    typeof(Func<long, Vector3, float, bool, bool>), checkAccess);

                Type transfers = BridgePlugin.QuartermasterAssembly?.GetType("Quartermaster.InventoryTransfers");
                MethodInfo available = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "AvailableInContainer", new[]
                    {
                        typeof(Container),
                        typeof(string),
                        typeof(int),
                        typeof(bool)
                    });

                if (available == null || available.ReturnType != typeof(int))
                {
                    QIDebugger.LogError(
                        "[WardIsLove] Quartermaster.InventoryTransfers.AvailableInContainer not found; " +
                        "ward permissions are not applied to storage counts.");
                    return;
                }

                _ready = HarmonyPatchGuard.TryPatch(
                    harmony,
                    available,
                    "Quartermaster.InventoryTransfers.AvailableInContainer",
                    prefix: new HarmonyMethod(typeof(WardIsLoveIntegration), nameof(AvailableInContainer_Prefix)));

                if (_ready && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug("[WardIsLove] Ward permissions now filter storage counts and pulls.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to initialize WardIsLoveIntegration", ex);
            }
        }

        /// <summary>
        /// True when WardIsLove (if present) allows this player to use the container. Always true
        /// when WardIsLove is absent, so callers do not need to branch.
        /// </summary>
        public static bool IsPermitted(Player player, Container container)
        {
            if (!IsReady || player == null || container == null) return true;
            return !IsBlocked(player, container);
        }

        /// <summary>
        /// Skips Quartermaster's own availability calculation for a container the local player is not
        /// permitted to use. Returning true leaves the original method (and its result) untouched.
        /// </summary>
        public static bool AvailableInContainer_Prefix(Container __0, ref int __result)
        {
            if (!BridgePlugin.Enabled || !IsReady || __0 == null) return true;

            Player player = Player.m_localPlayer;
            if (player == null) return true;

            if (!IsBlocked(player, __0)) return true;

            __result = 0;
            return false;
        }

        private static bool IsBlocked(Player player, Container container)
        {
            int frame = Time.frameCount;
            if (frame - _cacheFrame > CacheFrames)
            {
                BlockedCache.Clear();
                _cacheFrame = frame;
            }

            if (BlockedCache.TryGetValue(container, out bool cached)) return cached;

            bool blocked;
            try
            {
                blocked = !_checkAccess(player.GetPlayerID(), container.transform.position, 0f, false);
            }
            catch (Exception ex)
            {
                // Fail open: a broken ward query must not hide the player's own storage. Throttled
                // because this sits on the per-container counting path.
                if (Time.time - _lastErrorTime > 10f)
                {
                    _lastErrorTime = Time.time;
                    QIDebugger.LogError("[WardIsLove] CheckAccess failed; container is treated as permitted", ex);
                }

                blocked = false;
            }

            BlockedCache[container] = blocked;

            if (blocked && QIDebugger.Enabled && Time.time - _lastLogTime > 10f)
            {
                _lastLogTime = Time.time;
                QIDebugger.LogDebug(
                    $"[WardIsLove] Excluded '{container.m_name}' at {container.transform.position} from storage (ward does not permit this player).");
            }

            return blocked;
        }
    }
}
