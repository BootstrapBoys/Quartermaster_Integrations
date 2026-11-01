using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// AAABuildMenu's pinned-recipe tracker renders have/need from the player's pockets only:
    /// PinnedRecipeTracker.CountItem sums inv.GetAllItems(). Postfixing that single method gives
    /// pinned build rows Quartermaster storage-inclusive counts, matching the crafting window.
    ///
    /// The value is display-only - both call sites compare it against row.ResNeeds to choose a
    /// colour - so nothing here can change what a build actually spends.
    /// </summary>
    public static class AAABuildMenuIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnableAAABuildMenu);

        // Last logged total per item, so the debug log records changes rather than every refresh.
        private static readonly Dictionary<string, int> LastLoggedTotal = new Dictionary<string, int>(StringComparer.Ordinal);

        public static void Initialize(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.AAABuildMenuGUID, out PluginInfo info) ||
                info?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly assembly = info.Instance.GetType().Assembly;
                Type trackerType = assembly.GetType("AAABuildMenu.PinnedRecipeTracker");
                if (trackerType == null)
                {
                    QIDebugger.LogError("[AAABuildMenu] PinnedRecipeTracker not found; pinned counts stay pocket-only.");
                    return;
                }

                MethodInfo countItem = AccessTools.Method(
                    trackerType,
                    "CountItem",
                    new[] { typeof(Inventory), typeof(string) });

                if (countItem == null || countItem.ReturnType != typeof(int))
                {
                    QIDebugger.LogError(
                        "[Patch] 'AAABuildMenu.PinnedRecipeTracker.CountItem' skipped: " +
                        "expected (Inventory, string) returning int.");
                    return;
                }

                _ready = HarmonyPatchGuard.TryPatch(
                    harmony,
                    countItem,
                    "AAABuildMenu.PinnedRecipeTracker.CountItem",
                    postfix: new HarmonyMethod(typeof(AAABuildMenuIntegration), nameof(CountItem_Postfix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug("AAABuildMenuIntegration initialized and patched pinned-recipe counts.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to initialize AAABuildMenuIntegration", ex);
            }
        }

        public static void CountItem_Postfix(Inventory inv, string sharedName, ref int __result)
        {
            if (!BridgePlugin.Enabled) return;

            Player player = Player.m_localPlayer;
            if (player == null || inv == null || string.IsNullOrEmpty(sharedName)) return;

            // Only the player's own inventory gains storage. A container inventory already counts
            // what it holds, and adding nearby storage on top of that would double-count it.
            if (!ReferenceEquals(inv, player.GetInventory())) return;

            int pockets = __result;
            int storage = BridgePlugin.GetQuartermasterStorageCount(player, sharedName);

            if (QIDebugger.Enabled)
            {
                // Log on change only: RefreshCounts runs whenever the pinned panel updates, so a line
                // per call would bury everything else in the log.
                int total = pockets + storage;
                if (!LastLoggedTotal.TryGetValue(sharedName, out int previous) || previous != total)
                {
                    LastLoggedTotal[sharedName] = total;
                    QIDebugger.LogDebug(
                        $"[AAABuildMenu] '{sharedName}': pockets {pockets} + storage {storage} = {total}");
                }
            }

            if (storage > 0)
            {
                __result = pockets + storage;
            }
        }
    }
}
