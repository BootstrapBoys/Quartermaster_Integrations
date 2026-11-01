using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Registers a Quartermaster-backed inventory provider with Epic Loot, so enchantment costs,
    /// the sacrifice altar and the enchanting UI see what is in nearby storage instead of pockets
    /// only.
    ///
    /// This is not display-only: Epic Loot calls the provider's remove callbacks to actually take
    /// materials, so a wrong binding spends items. Counts come from Quartermaster's own
    /// CountAvailableNearPlayer and removals walk Quartermaster's CraftStores, which keeps the pull
    /// subject to CraftRange, the per-chest Crafting Supply flag and ward access.
    ///
    /// No save handler is registered: this provider can pay costs, but it does not accept items
    /// that Epic Loot wants to store, so nothing is ever moved into a chest from here.
    /// </summary>
    public static class EpicLootIntegration
    {
        // Stable id; Epic Loot lists it in its spend-only config and reports it in errors.
        public const string ProviderId = "bootstrapboys.quartermaster";

        public static bool IsReady { get; private set; }

        private static Type _apiType;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.EpicLootGUID, out PluginInfo info) ||
                info?.Instance == null)
            {
                return;
            }

            // Not compatible: Epic Loot sums every registered provider, and EpicLootContainerBridge already
            // reports Quartermaster storage - with both, every total doubles. Refuse outright.
            if (Chainloader.PluginInfos.ContainsKey(BridgePlugin.EpicLootContainerBridgeGUID))
            {
                BridgePlugin.Log?.LogWarning(
                    "[EpicLoot] Incompatible mod detected: EpicLootContainerBridge is installed and already " +
                    "reports Quartermaster storage to Epic Loot. Both together double every total, so this " +
                    "integration stays off - run one of the two.");
                return;
            }

            try
            {
                Assembly assembly = info.Instance.GetType().Assembly;
                Type api = assembly.GetType("EpicLoot.API");
                _apiType = api;
                MethodInfo register = api == null
                    ? null
                    : AccessTools.Method(api, "RegisterInventoryProvider", new[]
                    {
                        typeof(string),
                        typeof(Func<List<ItemDrop.ItemData>>),
                        typeof(Func<string, int>),
                        typeof(Func<string, int, int>),
                        typeof(Func<ItemDrop.ItemData, int, int>)
                    });

                if (register == null)
                {
                    QIDebugger.LogError(
                        "[EpicLoot] API.RegisterInventoryProvider(string, getItems, countItem, removeItem, removeExactItem) " +
                        "not found; storage is not offered to Epic Loot.");
                    return;
                }

                object result = register.Invoke(null, new object[]
                {
                    ProviderId,
                    (Func<List<ItemDrop.ItemData>>)GetStoredItems,
                    (Func<string, int>)CountStoredItem,
                    (Func<string, int, int>)RemoveStoredItem,
                    (Func<ItemDrop.ItemData, int, int>)RemoveExactStoredItem
                });

                IsReady = result is bool registered && registered;

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(IsReady
                        ? $"[EpicLoot] Registered inventory provider '{ProviderId}' (storage counts, item removal, no save handler)."
                        : $"[EpicLoot] API rejected inventory provider '{ProviderId}'.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to register the EpicLoot inventory provider", ex);
            }
        }

        /// <summary>
        /// Removes the provider again. Only matters when the plugin is unloaded or hot-reloaded:
        /// Epic Loot keeps the callbacks in a static list, so a stale registration would keep
        /// answering after this plugin is gone and every count would be added twice.
        /// </summary>
        public static void Unregister()
        {
            if (!IsReady || _apiType == null) return;

            try
            {
                MethodInfo unregister = AccessTools.Method(_apiType, "UnregisterInventoryProvider", new[] { typeof(string) });
                if (unregister != null)
                {
                    unregister.Invoke(null, new object[] { ProviderId });
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to unregister the EpicLoot inventory provider", ex);
            }
            finally
            {
                IsReady = false;
            }
        }

        private static List<ItemDrop.ItemData> GetStoredItems()
        {
            Player player = Player.m_localPlayer;
            return player == null
                ? new List<ItemDrop.ItemData>()
                : BridgePlugin.GetStorageItems(player);
        }

        private static int CountStoredItem(string sharedName)
        {
            Player player = Player.m_localPlayer;
            if (player == null || string.IsNullOrEmpty(sharedName)) return 0;

            // Storage only: Epic Loot adds the player's own inventory itself, so including pockets
            // here would count them twice.
            return BridgePlugin.GetQuartermasterStorageCount(player, sharedName);
        }

        private static int RemoveStoredItem(string sharedName, int amount)
        {
            Player player = Player.m_localPlayer;
            if (player == null) return 0;

            int removed = BridgePlugin.RemoveFromStorage(player, sharedName, amount);
            if (removed > 0)
            {
                QIDebugger.LogDebug($"[EpicLoot] Took {removed}/{amount}x '{sharedName}' from storage");
            }

            return removed;
        }

        private static int RemoveExactStoredItem(ItemDrop.ItemData item, int amount)
        {
            Player player = Player.m_localPlayer;
            if (player == null) return 0;

            int removed = BridgePlugin.RemoveExactFromStorage(player, item, amount);
            if (removed > 0)
            {
                QIDebugger.LogDebug($"[EpicLoot] Took {removed}/{amount}x '{item?.m_shared?.m_name}' (exact stack) from storage");
            }

            return removed;
        }
    }
}
