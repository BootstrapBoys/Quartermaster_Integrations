using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Quartermaster_Integrations
{
    public enum RequirementDisplayMode
    {
        CombinedTotal,
        SplitInventoryAndStorage,
        TotalWithSplit
    }

    /// <summary>
    /// Applies a single Harmony patch and reports failures individually. Patching methods that live
    /// in another mod means guessing at private signatures; without a per-patch guard one mismatch
    /// silently aborts every remaining patch in the same Initialize call.
    /// </summary>
    internal static class HarmonyPatchGuard
    {
        internal static bool TryPatch(
            Harmony harmony,
            MethodBase target,
            string label,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod finalizer = null)
        {
            if (harmony == null) return false;

            if (target == null)
            {
                QIDebugger.LogError($"[Patch] '{label}' skipped: target method not found.");
                return false;
            }

            try
            {
                // The 5-argument overload is [Obsolete(..., error: true)] in HarmonyX 2.x.
                harmony.Patch(target, prefix, postfix, null, finalizer, null);
                if (QIDebugger.Enabled) QIDebugger.LogDebug($"[Patch] '{label}' applied.");
                return true;
            }
            catch (Exception ex)
            {
                QIDebugger.LogError($"[Patch] '{label}' failed to apply.", ex);
                return false;
            }
        }
    }

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(QuartermasterGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(MLUIGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(AAACraftingGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ExtraSlotsGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(AdventureBackpacksGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(AAABuildMenuGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(EpicLootGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PlanBuildGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(WardIsLoveGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(DropMoreLootGUID, BepInDependency.DependencyFlags.SoftDependency)]
    public class BridgePlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.bootstrapboys.quartermaster_integrations";
        public const string PluginName = "Quartermaster Integrations";

        // Keep in sync with <Version> in QuarterMaster_Integrations.csproj, resources/manifest.json
        // and the top entry of resources/CHANGELOG.md. Awake() warns if the built assembly disagrees.
        //
        // Versioning: bug fixes bump the patch digit (2.4.2 -> 2.4.3); adding an integration bumps the
        // minor and resets the patch (2.4.3 -> 2.5.0). Every bump gets a changelog entry.
        public const string PluginVersion = "2.7.6";

        public const string QuartermasterGUID = "local.valheim.quartermaster";
        public const string MLUIGUID = "shudnal.MyLittleUI";
        public const string AAACraftingGUID = "Azumatt.AzuAntiArthriticCrafting";
        public const string ExtraSlotsGUID = "shudnal.ExtraSlots";
        public const string AdventureBackpacksGUID = "vapok.mods.adventurebackpacks";
        public const string AAABuildMenuGUID = "Azumatt.AAABuildMenu";
        public const string EpicLootGUID = "randyknapp.mods.epicloot";
        public const string EpicLootContainerBridgeGUID = "KlownKiller.EpicLootContainerBridge";
        public const string PlanBuildGUID = "marcopogo.PlanBuild";
        public const string WardIsLoveGUID = "Azumatt.WardIsLove";
        public const string DropMoreLootGUID = "TastyChickenLegs.DropMoreLoot";

        private readonly Harmony _harmony = new Harmony(PluginGUID);
        internal static ManualLogSource Log;

        public static ConfigEntry<bool> ModEnabled;
        public static ConfigEntry<RequirementDisplayMode> DisplayMode;
        public static ConfigEntry<bool> DeferToOtherFormatting;

        /// <summary>WardIsLove only: use the ward's radius as the crafting/storage range while inside it.</summary>
        public static ConfigEntry<bool> WardHostRange;
        public static ConfigEntry<WardOwnershipMode> WardOwnership;

        /// <summary>DropMoreLoot only: let its stack multiplier win instead of MaximumStackSize.</summary>
        public static ConfigEntry<bool> DeferToDropMoreLootStacks;
        public static ConfigEntry<bool> ForgetRemovesEntry;
        public static ConfigEntry<bool> IgnoreCrafter;
        public static ConfigEntry<string> IgnoredStackTags;
        public static ConfigEntry<bool> MergePocketsOnSort;
        public static ConfigEntry<bool> EnableAAABuildMenu;
        public static ConfigEntry<bool> EnableAAACrafting;
        public static ConfigEntry<bool> EnableMyLittleUI;
        public static ConfigEntry<bool> EnableExtraSlots;
        public static ConfigEntry<bool> EnableAdventureBackpacks;
        public static ConfigEntry<bool> EnableEpicLoot;
        public static ConfigEntry<bool> EnablePlanBuild;
        public static ConfigEntry<bool> EnableWardIsLove;
        public static ConfigEntry<bool> EnableDropMoreLoot;
        public static ConfigEntry<Color> SufficientResourceColor;
        public static ConfigEntry<Color> InsufficientFlashBrightColor;
        public static ConfigEntry<Color> InsufficientFlashDarkColor;
        public static ConfigEntry<float> FlashSpeed;
        public static ConfigEntry<bool> DebugMode;

        public static string SufficientColorHex = "ADE8E6FF";

        // Quartermaster's own nearby-storage query. Binding it means our counts are computed by the
        // same rules that decide what can actually be spent at craft time: CraftRange, the per-chest
        // Crafting Supply flag, the global CraftFromContainers toggle, ward/in-use checks and
        // world-level matching. Rolling our own container scan produced numbers Quartermaster would
        // not honour.
        private const int AnyQuality = -1;
        private const bool MatchWorldLevel = false;
        private static Func<string, int, bool, int> _countAvailableNearPlayer;

        // Quartermaster's own list of spendable containers. The integrations that must actually move
        // items (EpicLoot, PlanBuild) and the WardIsLove permission filter walk this, so they follow
        // the same CraftRange/supply/ward rules as the counts.
        private static Func<Player, bool, IEnumerable<Container>> _craftStores;

        // Per-frame memo of the storage container list; shared empty list keeps the unbound path
        // allocation-free.
        private static readonly List<Container> EmptyStorageContainers = new List<Container>();
        private static List<Container> _cachedContainers;
        private static int _cachedContainersFrame = -1;

        // Kept so an integration can reach a Quartermaster type that is not bound as a delegate.
        internal static Assembly QuartermasterAssembly;

        /// <summary>True when Quartermaster's counting entry point bound successfully.</summary>
        public static bool HasStorageCountBinding => _countAvailableNearPlayer != null;

        /// <summary>True when Quartermaster's spendable-container list bound successfully.</summary>
        public static bool HasStorageContainerBinding => _craftStores != null;

        // Per-frame unified caches for zero-GC lookups
        private static int _cachedFrame = -1;
        private static bool _cacheDirty;
        private static readonly Dictionary<string, int> CachedStorageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> CachedPocketCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        private void Awake()
        {
            Log = Logger;

            ModEnabled = Config.Bind(
                "1. General",
                "Enabled",
                true,
                ConfigOrder.Description("Enable or disable the Quartermaster Integrations mod.", 100));

            EnableAAABuildMenu = Config.Bind(
                "2. Debug",
                "EnableAAABuildMenu",
                true,
                "AAA Build Menu integration. Turn this off to leave that mod's behaviour alone - its pinned recipe tracker counts Quartermaster storage.");
            EnableAAACrafting = Config.Bind(
                "2. Debug",
                "EnableAAACrafting",
                true,
                "AAA Crafting integration. Turn this off to leave that mod's behaviour alone - cluster crafting pulls from Quartermaster storage.");
            EnableMyLittleUI = Config.Bind(
                "2. Debug",
                "EnableMyLittleUI",
                true,
                "My Little UI integration. Turn this off to leave that mod's behaviour alone - requirement totals are shown in its tooltip layout.");
            EnableExtraSlots = Config.Bind(
                "2. Debug",
                "EnableExtraSlots",
                true,
                "ExtraSlots integration. Turn this off to leave that mod's behaviour alone - its extra rows are counted and protected from the storage adapter.");
            EnableAdventureBackpacks = Config.Bind(
                "2. Debug",
                "EnableAdventureBackpacks",
                true,
                "Adventure Backpacks integration. Turn this off to leave that mod's behaviour alone - its backpack is excluded from Quartermaster storage and store-all.");
            EnableEpicLoot = Config.Bind(
                "2. Debug",
                "EnableEpicLoot",
                true,
                "Epic Loot integration. Turn this off to leave that mod's behaviour alone - enchanting and sacrifice costs can be paid from storage.");
            EnablePlanBuild = Config.Bind(
                "2. Debug",
                "EnablePlanBuild",
                true,
                "PlanBuild integration. Turn this off to leave that mod's behaviour alone - plan costs and hold-to-add use storage.");
            EnableWardIsLove = Config.Bind(
                "2. Debug",
                "EnableWardIsLove",
                true,
                "WardIsLove integration. Turn this off to leave that mod's behaviour alone - chests inside a ward you are not permitted on are filtered out.");
            EnableDropMoreLoot = Config.Bind(
                "2. Debug",
                "EnableDropMoreLoot",
                true,
                "DropMoreLoot integration. Turn this off to leave that mod's behaviour alone - its stack multiplier is respected instead of Quartermaster's.");

            DebugMode = Config.Bind(
                "2. Debug",
                "DebugMode",
                false,
                "Enable detailed debug logging to file and console.");

            DisplayMode = Config.Bind(
                "3. Visuals",
                "RequirementDisplayMode",
                RequirementDisplayMode.CombinedTotal,
                "Format for crafting requirement amounts:\n" +
                "CombinedTotal = 10 (16) [Req (Total)]\n" +
                "SplitInventoryAndStorage = 10 (4+12) [Req (Inv+QM)]\n" +
                "TotalWithSplit = 10 (16 [4+12])");

            SufficientResourceColor = Config.Bind(
                "3. Visuals",
                "SufficientColor",
                new Color(0.68f, 0.91f, 0.90f, 1f),
                "Text color for available resource counts when you have enough components.");

            InsufficientFlashBrightColor = Config.Bind(
                "3. Visuals",
                "InsufficientFlashBrightColor",
                new Color(1f, 0.22f, 0.22f, 1f),
                "Primary bright red color when flashing insufficient crafting requirements.");

            InsufficientFlashDarkColor = Config.Bind(
                "3. Visuals",
                "InsufficientFlashDarkColor",
                new Color(0.45f, 0.02f, 0.02f, 1f),
                "Secondary dark red color when flashing insufficient crafting requirements.");

            FlashSpeed = Config.Bind(
                "3. Visuals",
                "FlashSpeed",
                4.0f,
                new ConfigDescription("Flashing speed when lacking components to craft.", new AcceptableValueRange<float>(1f, 12f)));

            DeferToOtherFormatting = Config.Bind(
                "4. Patches",
                "DeferToOtherFormatting",
                false,
                "Leave the requirement amount text alone when another mod (AAA Crafting, AdventureBackpacks) " +
                "has already formatted it. Vanilla's own text is a bare number, so this only triggers when " +
                "something else has decorated the row. Enable it if you prefer their 'have/need' display.");

            DeferToDropMoreLootStacks = Config.Bind(
                "4. Patches",
                "DeferToDropMoreLootStacks",
                true,
                "DropMoreLoot only. When DropMoreLoot's own stacking is enabled, its stack multiplier " +
                "becomes the authority and Quartermaster stops applying MaximumStackSize - the ledger's " +
                "stack tab says so. Turn this off to keep Quartermaster's stack override, which replaces " +
                "DropMoreLoot's multiplied value.");




            ForgetRemovesEntry = Config.Bind(
                "4. Patches",
                "ForgetRemovesEntry",
                false,
                "Quartermaster only. Its Forget button does not delete a chest entry - it moves the item " +
                "to the chest's ignore list, where it keeps showing as '(ignored)' and keeps refusing " +
                "storage. With this on, Forget and Forget All remove the entry from the chest outright, " +
                "so the row disappears and putting a sample back teaches the chest again.");

            IgnoreCrafter = Config.Bind(
                "4. Patches",
                "IgnoreCrafter",
                false,
                "Quartermaster only. It refuses to merge two stacks whose crafter differs, so core wood " +
                "crafted at a bench will not stack with core wood you picked up - vanilla ignores the " +
                "crafter entirely. With this on, materials merge when the crafter is the only difference: " +
                "same item, quality, variant, world level, durability, cheat flag and custom data. Gear, " +
                "enchanted and world-levelled items keep Quartermaster's rule.");

            IgnoredStackTags = Config.Bind(
                "4. Patches",
                "IgnoredStackTags",
                "ap_prov",
                "Quartermaster only. Comma-separated custom-data keys to ignore when deciding whether two " +
                "stackable items are identical. Empty compares every entry. Only affects items that can " +
                "stack; Epic Loot's enchant data is never a sensible entry here. Default 'ap_prov' is the " +
                "provenance stamp some admin panels write onto items, which otherwise keeps stamped and " +
                "unstamped materials apart.");

            MergePocketsOnSort = Config.Bind(
                "4. Patches",
                "MergePocketsOnSort",
                true,
                "Quartermaster only. Its inventory sort only rearranges slots, and its merge routine runs " +
                "for chests but never for your pockets - so sorting your inventory leaves two stacks of the " +
                "same item separate. With this on, identical pocket stacks are combined first (honouring " +
                "IgnoreCrafter and IgnoredStackTags), equipped items are left alone, then Quartermaster " +
                "arranges the result.");



            WardHostRange = Config.Bind(
                "5. Ward",
                "WardHostRange",
                true,
                "WardIsLove only. While you are inside one of its wards, that ward's radius replaces " +
                "Quartermaster's CraftRange for the crafting counts, the max-craft number and which chests " +
                "a craft may pull from - the ward bubble and your storage range become the same thing. " +
                "Outside a ward nothing changes. Turn this off to always use Quartermaster's CraftRange.");

            WardOwnership = Config.Bind(
                "5. Ward",
                "WardOwnership",
                WardOwnershipMode.IfOwnerOffline,
                "WardIsLove only. Quartermaster asks a container's owner to hand the ZDO over and claims it " +
                "after three unanswered requests, so a permitted visitor can take the ward owner's chests. " +
                "Never = never request or claim (the owner keeps every chest, but deposits and machine " +
                "collection by a visitor fail); IfOwnerOffline = skip while the recorded owner is connected, " +
                "claim only when that owner is not online, so deposits and machine output still work; " +
                "Always = Quartermaster's normal request/claim everywhere.");

            UpdateCachedColors();
            SufficientResourceColor.SettingChanged += (_, __) => UpdateCachedColors();

            QIDebugger.Initialize();
            LogVersionConsistency();
            QIDebugger.LogUiSignatures();
            BindQuartermasterStorageQuery();

            // Initialize soft-dependency integrations from their dedicated files
            MLUIIntegration.Initialize();
            AAACraftingIntegration.Initialize(_harmony);
            ExtraSlotsIntegration.Initialize(_harmony);
            AdventureBackpacksIntegration.Initialize(_harmony);
            AAABuildMenuIntegration.Initialize(_harmony);
            EpicLootIntegration.Initialize(_harmony);
            PlanBuildIntegration.Initialize(_harmony);
            WardIsLoveIntegration.Initialize(_harmony);
            DropMoreLootIntegration.Initialize(_harmony);
            StackDiagnostics.Initialize(_harmony);
            RoutingDiagnostics.Initialize(_harmony);
            ForgetOverride.Initialize(_harmony);
            StackCrafterPatch.Initialize(_harmony);
            CraftRepaintPatch.Initialize(_harmony);
            PocketMerge.Initialize(_harmony);
            WardOwnershipSkip.Initialize(_harmony);

            // Soft-dependency integrations set up their own patches; container-content invalidation
            // comes from Inventory.Changed (Container.Load ends in Inventory.Load, which calls
            // Changed()), so there is no Container.Load patch to add.
            _harmony.PatchAll();

            // Logged after the integrations so readiness-derived values (BatchLimit, per-mod
            // readiness) are final; reading them mid-initialisation reported false negatives.
            QIDebugger.LogConfigSnapshot();
            QIDebugger.LogPatchInventory();
            QIDebugger.LogIntegrationSnapshot();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded (MLUI: {MLUIIntegration.IsReady}, AAA: {AAACraftingIntegration.IsReady}, ExtraSlots: {ExtraSlotsIntegration.IsReady}, Backpacks: {AdventureBackpacksIntegration.IsReady}, BuildMenu: {AAABuildMenuIntegration.IsReady}, EpicLoot: {EpicLootIntegration.IsReady}, PlanBuild: {PlanBuildIntegration.IsReady}, WardIsLove: {WardIsLoveIntegration.IsReady}, DropMoreLoot: {DropMoreLootIntegration.IsReady}, QM storage query: {_countAvailableNearPlayer != null}, QM storages: {_craftStores != null}).");
        }

        private void Start()
        {
            AAACraftingIntegration.EnableContainerCrafting();
        }

        private void OnDestroy()
        {
            EpicLootIntegration.Unregister();
            _harmony?.UnpatchSelf();
        }

        // Debug-only heartbeat: proves ward entry/exit without needing a crafting window open. The
        // range code itself is demand-driven, so a plain walk-through would otherwise leave no trace.
        private float _nextWardProbe;

        private void Update()
        {
            if (!QIDebugger.Enabled) return;
            if (Time.time < _nextWardProbe) return;

            _nextWardProbe = Time.time + 0.5f;
            Quartermaster_Integrations.WardHostRange.LogState();
        }

        public static bool Enabled => ModEnabled == null || ModEnabled.Value;

        /// <summary>True unless the integration's own switch under 2. Debug is off.</summary>
        public static bool IntegrationOn(ConfigEntry<bool> toggle) => toggle == null || toggle.Value;

        private void LogVersionConsistency()
        {
            try
            {
                // assembly_valheim declares a global-namespace `Version` type, which shadows
                // System.Version inside this namespace; qualify it explicitly.
                System.Version assemblyVersion = typeof(BridgePlugin).Assembly.GetName().Version;
                if (assemblyVersion == null) return;

                string shortVersion = assemblyVersion.ToString(3);
                if (!string.Equals(shortVersion, PluginVersion, StringComparison.Ordinal))
                {
                    Log?.LogWarning(
                        $"Assembly version ({shortVersion}) does not match PluginVersion ({PluginVersion}). " +
                        "Update <Version> in QuarterMaster_Integrations.csproj, resources/manifest.json and PluginVersion together.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("Version consistency check failed", ex);
            }
        }

        /// <summary>
        /// Binds Quartermaster.WarehouseService.CountAvailableNearPlayer(string, int, bool) : int, the
        /// same call Quartermaster uses to answer "how much of this is available in nearby storage".
        /// </summary>
        private void BindQuartermasterStorageQuery()
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(QuartermasterGUID, out PluginInfo qmInfo) || qmInfo?.Instance == null)
                {
                    QIDebugger.LogError("[QM] Quartermaster is not loaded; storage counts are unavailable.");
                    return;
                }

                Assembly qmAssembly = qmInfo.Instance.GetType().Assembly;
                QuartermasterAssembly = qmAssembly;
                Type warehouseType = qmAssembly.GetType("Quartermaster.WarehouseService");
                if (warehouseType == null)
                {
                    QIDebugger.LogError("[QM] Quartermaster.WarehouseService not found; storage counts are unavailable.");
                    return;
                }

                MethodInfo query = AccessTools.Method(
                    warehouseType,
                    "CountAvailableNearPlayer",
                    new[] { typeof(string), typeof(int), typeof(bool) });

                if (query == null || query.ReturnType != typeof(int))
                {
                    QIDebugger.LogError("[QM] WarehouseService.CountAvailableNearPlayer(string, int, bool) : int not found. " +
                             "Storage counts are unavailable; this mod probably needs updating for this Quartermaster version.");
                    return;
                }

                _countAvailableNearPlayer = (Func<string, int, bool, int>)Delegate.CreateDelegate(
                    typeof(Func<string, int, bool, int>), query);

                MethodInfo stores = AccessTools.Method(
                    warehouseType,
                    "CraftStores",
                    new[] { typeof(Player), typeof(bool) });

                if (stores != null && stores.ReturnType == typeof(IEnumerable<Container>))
                {
                    _craftStores = (Func<Player, bool, IEnumerable<Container>>)Delegate.CreateDelegate(
                        typeof(Func<Player, bool, IEnumerable<Container>>), stores);
                }
                else
                {
                    QIDebugger.LogError("[QM] WarehouseService.CraftStores(Player, bool) : IEnumerable<Container> not found. " +
                             "Item-moving integrations (EpicLoot, PlanBuild) and the WardIsLove filter stay inactive.");
                }

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug("[QM] Bound WarehouseService.CountAvailableNearPlayer.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to bind Quartermaster's nearby-storage query", ex);
            }
        }

        private static void EnsureFrameCacheUpdated(Player player)
        {
            int currentFrame = Time.frameCount;
            if (currentFrame == _cachedFrame && !_cacheDirty) return;

            _cachedFrame = currentFrame;
            _cacheDirty = false;
            CachedStorageCounts.Clear();
            CachedPocketCounts.Clear();

            if (player == null) return;

            Inventory pInv = player.GetInventory();
            if (pInv == null) return;

            List<ItemDrop.ItemData> pItems = pInv.GetAllItems();
            for (int i = 0; i < pItems.Count; i++)
            {
                ItemDrop.ItemData item = pItems[i];
                if (item?.m_shared?.m_name == null) continue;

                string name = item.m_shared.m_name;
                CachedPocketCounts.TryGetValue(name, out int current);
                CachedPocketCounts[name] = current + item.m_stack;
            }
        }

        public static int GetQuartermasterStorageCount(Player player, string itemName)
        {
            if (!Enabled) return 0;
            if (player == null || string.IsNullOrEmpty(itemName)) return 0;

            EnsureFrameCacheUpdated(player);
            return GetCachedStorageCount(itemName);
        }

        public static void GetItemCounts(Player player, string itemName, out int pocketCount, out int qmCount, out int totalAvailable)
        {
            if (player == null || string.IsNullOrEmpty(itemName))
            {
                pocketCount = 0;
                qmCount = 0;
                totalAvailable = 0;
                return;
            }

            EnsureFrameCacheUpdated(player);
            CachedPocketCounts.TryGetValue(itemName, out pocketCount);

            // Enabled is checked here rather than inside GetCachedStorageCount so a disabled mod
            // never touches Quartermaster at all.
            qmCount = Enabled ? GetCachedStorageCount(itemName) : 0;
            totalAvailable = pocketCount + qmCount;
        }

        public static int GetTotalAvailable(Player player, string itemName)
        {
            GetItemCounts(player, itemName, out _, out _, out int total);
            return total;
        }

        private static int GetCachedStorageCount(string itemName)
        {
            if (CachedStorageCounts.TryGetValue(itemName, out int cached)) return cached;

            int resolved = QueryQuartermasterStorage(itemName);
            CachedStorageCounts[itemName] = resolved;
            return resolved;
        }

        private static int QueryQuartermasterStorage(string itemName)
        {
            if (_countAvailableNearPlayer == null) return 0;

            try
            {
                return _countAvailableNearPlayer(itemName, AnyQuality, MatchWorldLevel);
            }
            catch (Exception ex)
            {
                // Fail closed: showing only pocket counts is what vanilla shows, and is better than
                // advertising storage Quartermaster will refuse to pull from.
                QIDebugger.LogDebug($"[QM] CountAvailableNearPlayer failed for '{itemName}'", ex);
                return 0;
            }
        }

        /// <summary>
        /// Marks the per-frame caches stale so the next consumer rebuilds them immediately.
        /// </summary>
        public static void MarkCacheDirty()
        {
            _cacheDirty = true;
            _cachedContainersFrame = -1;
        }

        /// <summary>
        /// Quartermaster's spendable containers for this player. Never null, so callers can always
        /// iterate; an unavailable binding yields an empty list and the feature degrades to pockets.
        /// </summary>
        public static List<Container> GetStorageContainers(Player player)
        {
            if (!Enabled || player == null || _craftStores == null) return EmptyStorageContainers;

            int frame = Time.frameCount;
            if (_cachedContainers != null && _cachedContainersFrame == frame) return _cachedContainers;

            List<Container> containers = new List<Container>();
            try
            {
                foreach (Container container in _craftStores(player, false))
                {
                    if (container != null) containers.Add(container);
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[QM] CraftStores failed; treating storage as empty", ex);
                containers.Clear();
            }

            // Memoised for the frame: CraftStores sorts and filters every chest, and Epic Loot asks
            // for the item list and then removes from it in the same frame.
            _cachedContainers = containers;
            _cachedContainersFrame = frame;
            return containers;
        }

        /// <summary>
        /// Live ItemData instances held in storage. Callers must treat these as borrowed: moving or
        /// removing an item invalidates the list returned by the container inventory.
        /// </summary>
        public static List<ItemDrop.ItemData> GetStorageItems(Player player)
        {
            List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>();
            foreach (Container container in GetStorageContainers(player))
            {
                Inventory inventory = container.GetInventory();
                if (inventory != null) items.AddRange(inventory.GetAllItems());
            }

            return items;
        }

        /// <summary>
        /// Removes up to <paramref name="amount"/> items matching a shared name from Quartermaster
        /// storage and reports how many actually left. Iterates containers in Quartermaster's own
        /// order so the materials come from the same chests its counts advertise.
        /// </summary>
        public static int RemoveFromStorage(Player player, string sharedName, int amount)
        {
            if (!Enabled || player == null || string.IsNullOrEmpty(sharedName) || amount <= 0) return 0;

            int removed = 0;
            foreach (Container container in GetStorageContainers(player))
            {
                if (removed >= amount) break;

                Inventory inventory = container.GetInventory();
                if (inventory == null) continue;

                // GetAllItems hands back the live list and RemoveItem mutates it, so walk backwards:
                // a full take removes the element at the current index, which backwards iteration
                // already accounts for.
                List<ItemDrop.ItemData> items = inventory.GetAllItems();
                for (int i = items.Count - 1; i >= 0 && removed < amount; i--)
                {
                    ItemDrop.ItemData item = items[i];
                    if (item?.m_shared == null ||
                        !string.Equals(item.m_shared.m_name, sharedName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int take = Math.Min(item.m_stack, amount - removed);
                    if (inventory.RemoveItem(item, take)) removed += take;
                }
            }

            if (removed > 0)
            {
                MarkCacheDirty();
                QIDebugger.LogDebug($"[QM] Removed {removed}x '{sharedName}' from storage");
            }

            return removed;
        }

        /// <summary>
        /// Removes from one specific stack, matched by instance identity so a quality or world-level
        /// variant is never substituted for the item the caller asked about.
        /// </summary>
        public static int RemoveExactFromStorage(Player player, ItemDrop.ItemData target, int amount)
        {
            if (!Enabled || player == null || target == null || amount <= 0) return 0;

            foreach (Container container in GetStorageContainers(player))
            {
                Inventory inventory = container.GetInventory();
                if (inventory == null || !inventory.GetAllItems().Contains(target)) continue;

                int take = Math.Min(target.m_stack, amount);
                if (!inventory.RemoveItem(target, take)) return 0;

                MarkCacheDirty();
                QIDebugger.LogDebug($"[QM] Removed {take}x '{target.m_shared?.m_name}' (exact stack) from storage");
                return take;
            }

            return 0;
        }

        private static void UpdateCachedColors()
        {
            SufficientColorHex = ColorUtility.ToHtmlStringRGBA(
                SufficientResourceColor != null ? SufficientResourceColor.Value : new Color(0.68f, 0.91f, 0.90f, 1f));
        }

    }

    public static class MultiCraftBridgeManager
    {
        private static int _cachedAmountFrame = -1;
        private static int _cachedAmount = 1;

        public static int GetBatchLimit() => AAACraftingIntegration.IsReady ? 100 : 99;

        public static int GetTargetCraftAmount()
        {
            if (!BridgePlugin.Enabled) return 1;

            // Called once per requirement row per frame; memoize so the reflection reads inside
            // TryGetCraftAmount run at most once per frame instead of once per row.
            int frame = Time.frameCount;
            if (frame == _cachedAmountFrame) return _cachedAmount;

            _cachedAmountFrame = frame;
            _cachedAmount = ResolveTargetCraftAmount();
            return _cachedAmount;
        }

        private static int ResolveTargetCraftAmount()
        {
            if (InventoryGui.instance != null && InventoryGui.instance.InUpradeTab())
            {
                return 1;
            }

            int batchCap = GetBatchLimit();

            if (AAACraftingIntegration.IsReady && AAACraftingIntegration.TryGetCraftAmount(out int aaaAmt))
            {
                return Mathf.Clamp(aaaAmt, 1, batchCap);
            }

            if (MLUIIntegration.IsReady && MLUIIntegration.TryGetCraftAmount(out int mluiAmt))
            {
                return Mathf.Clamp(mluiAmt, 1, batchCap);
            }

            return 1;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "SetupRequirement")]
    public static class InventoryGui_SetupRequirement_QuartermasterBridgePatch
    {
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter(new[] { BridgePlugin.MLUIGUID, BridgePlugin.AAACraftingGUID, BridgePlugin.QuartermasterGUID })]
        public static void Postfix(
            Transform elementRoot,
            Piece.Requirement req,
            Player player,
            bool craft,
            int quality,
            int craftMultiplier)
        {
            if (!BridgePlugin.Enabled) return;
            if (elementRoot == null || player == null || req?.m_resItem == null) return;

            int baseReqAmount = req.GetAmount(quality);
            if (baseReqAmount <= 0) return;

            TMP_Text amountText = elementRoot.Find("res_amount")?.GetComponent<TMP_Text>();
            if (amountText == null) return;

            // AAA Crafting and AdventureBackpacks both rewrite this row. Vanilla's own text is a bare
            // number; if the existing text is anything else, someone else formatted it, and
            // DeferToOtherFormatting lets their display win instead of being overwritten here.
            if (BridgePlugin.DeferToOtherFormatting != null && BridgePlugin.DeferToOtherFormatting.Value &&
                !string.IsNullOrEmpty(amountText.text) &&
                !int.TryParse(amountText.text.Trim(), out _))
            {
                return;
            }

            string itemName = req.m_resItem.m_itemData?.m_shared?.m_name;
            if (string.IsNullOrEmpty(itemName)) return;

            bool isUpgrading = InventoryGui.instance != null && InventoryGui.instance.InUpradeTab();

            // The game hands the craft amount to SetupRequirement itself (sixth parameter), so use it
            // when it carries a batch. Builds where that parameter is always 1 fall back to reading
            // AAA Crafting / My Little UI directly.
            int effectiveMultiplier = (craft && !isUpgrading) ? craftMultiplier : 1;
            if (craft && !isUpgrading && effectiveMultiplier <= 1)
            {
                effectiveMultiplier = MultiCraftBridgeManager.GetTargetCraftAmount();
            }

            int scaledRequiredAmount = baseReqAmount * effectiveMultiplier;

            BridgePlugin.GetItemCounts(player, itemName, out int pocketCount, out int qmCount, out int totalAvailable);

            bool noCost = player.NoCostCheat() ||
                          (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost));
            bool hasEnough = noCost || totalAvailable >= scaledRequiredAmount;

            if (!amountText.enableAutoSizing)
            {
                amountText.enableAutoSizing = true;
                amountText.fontSizeMin = 9f;
                amountText.fontSizeMax = 14f;
                amountText.textWrappingMode = TextWrappingModes.NoWrap;
                amountText.overflowMode = TextOverflowModes.Overflow;
            }

            string availabilitySegment = FormatAvailability(pocketCount, qmCount, totalAvailable);

            if (hasEnough)
            {
                amountText.color = Color.white;
                amountText.SetText($"{scaledRequiredAmount} <color=#{BridgePlugin.SufficientColorHex}>{availabilitySegment}</color>");
            }
            else
            {
                amountText.color = Color.white;
                amountText.SetText($"<color=#{FlashHex()}>{scaledRequiredAmount} {availabilitySegment}</color>");
            }
        }

        private static float _flashTick = float.NaN;
        private static string _flashHex = "FF3838FF";

        /// <summary>
        /// One HTML colour per animation tick rather than one per requirement row per frame. The
        /// flash advances at FlashSpeed*3 Hz, so every row drawn in the same frame shares a colour;
        /// recomputing it per row was building the same string six times a frame.
        /// </summary>
        private static string FlashHex()
        {
            float speed = BridgePlugin.FlashSpeed != null ? BridgePlugin.FlashSpeed.Value : 4f;
            float tick = Mathf.Floor(Time.unscaledTime * speed * 3f);
            if (tick == _flashTick) return _flashHex;

            _flashTick = tick;

            float t = Mathf.PingPong(tick / 3f, 1f);
            Color bright = BridgePlugin.InsufficientFlashBrightColor != null
                ? BridgePlugin.InsufficientFlashBrightColor.Value
                : new Color(1f, 0.22f, 0.22f, 1f);
            Color dark = BridgePlugin.InsufficientFlashDarkColor != null
                ? BridgePlugin.InsufficientFlashDarkColor.Value
                : new Color(0.45f, 0.02f, 0.02f, 1f);

            _flashHex = ColorUtility.ToHtmlStringRGBA(Color.Lerp(bright, dark, t));
            return _flashHex;
        }

        private static string FormatAvailability(int invCount, int qmCount, int totalAvailable)
        {
            RequirementDisplayMode mode = BridgePlugin.DisplayMode != null
                ? BridgePlugin.DisplayMode.Value
                : RequirementDisplayMode.CombinedTotal;

            switch (mode)
            {
                case RequirementDisplayMode.SplitInventoryAndStorage:
                    return $"({invCount.ToString(CultureInfo.InvariantCulture)}+{qmCount.ToString(CultureInfo.InvariantCulture)})";

                case RequirementDisplayMode.TotalWithSplit:
                    return $"({totalAvailable.ToString(CultureInfo.InvariantCulture)} [{invCount.ToString(CultureInfo.InvariantCulture)}+{qmCount.ToString(CultureInfo.InvariantCulture)}])";

                case RequirementDisplayMode.CombinedTotal:
                default:
                    return $"({totalAvailable.ToString(CultureInfo.InvariantCulture)})";
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui))]
    public static class InventoryGui_UpdateRecipe_BatchEnforcementPatch
    {
        private static readonly FieldInfo SelectedRecipeField = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        private static readonly FieldInfo CraftTimerField = AccessTools.Field(typeof(InventoryGui), "m_craftTimer");

        private static Type _resolvedPairType;
        private static PropertyInfo _recipeProp;
        private static PropertyInfo _itemDataProp;

        public static (Recipe recipe, ItemDrop.ItemData itemData) GetSelectedRecipeData(InventoryGui gui)
        {
            if (gui == null || SelectedRecipeField == null) return (null, null);

            object pairObj = SelectedRecipeField.GetValue(gui);
            if (pairObj == null) return (null, null);

            Type pairType = pairObj.GetType();
            if (_resolvedPairType != pairType || _recipeProp == null || _itemDataProp == null)
            {
                PropertyInfo recipeProp = AccessTools.Property(pairType, "Recipe");
                PropertyInfo itemDataProp = AccessTools.Property(pairType, "ItemData");

                if (recipeProp == null || itemDataProp == null)
                {
                    // Deliberately do not cache a failed lookup: latching it here would permanently
                    // disable max-craft and batch enforcement for the rest of the session.
                    if (QIDebugger.Enabled)
                    {
                        QIDebugger.LogDebug(
                            $"[GetSelectedRecipeData] Unresolved members on '{pairType.FullName}' " +
                            $"(Recipe found: {recipeProp != null}, ItemData found: {itemDataProp != null}).");
                    }
                    return (null, null);
                }

                _resolvedPairType = pairType;
                _recipeProp = recipeProp;
                _itemDataProp = itemDataProp;
            }

            Recipe r = _recipeProp.GetValue(pairObj, null) as Recipe;
            ItemDrop.ItemData item = _itemDataProp.GetValue(pairObj, null) as ItemDrop.ItemData;
            return (r, item);
        }

        public static float GetCraftTimer(InventoryGui gui)
        {
            if (gui == null || CraftTimerField == null) return -1f;
            return (float)CraftTimerField.GetValue(gui);
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(InventoryGui), "UpdateRecipe", new[] { typeof(Player), typeof(float) })
                ?? AccessTools.Method(typeof(InventoryGui), "UpdateRecipe", new[] { typeof(Player) });
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter(new[] { BridgePlugin.AAACraftingGUID, BridgePlugin.MLUIGUID })]
        [HarmonyPostfix]
        public static void Postfix(InventoryGui __instance, Player player)
        {
            if (!BridgePlugin.Enabled) return;
            if (__instance == null || player == null || !InventoryGui.IsVisible()) return;

            CraftRepaintPatch.FlushIfIdle(__instance, GetCraftTimer(__instance));

            (Recipe recipe, ItemDrop.ItemData upgradeItem) = GetSelectedRecipeData(__instance);
            if (recipe == null || __instance.m_craftButton == null || GetCraftTimer(__instance) >= 0f) return;

            bool isUpgrading = __instance.InUpradeTab() || upgradeItem != null;
            bool noCost = player.NoCostCheat() || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost));

            if (QIDebugger.Enabled)
            {
                QIDebugger.LogCraftingSnapshot(
                    recipe,
                    player,
                    isUpgrading ? 1 : MultiCraftBridgeManager.GetTargetCraftAmount(),
                    isUpgrading);
            }

            if (isUpgrading)
            {
                int nextQuality = upgradeItem != null ? upgradeItem.m_quality + 1 : 2;
                bool canUpgrade = noCost || player.HaveRequirements(recipe, false, nextQuality);
                if (canUpgrade)
                {
                    __instance.m_craftButton.interactable = true;
                }
                return;
            }

            int multiplier = MultiCraftBridgeManager.GetTargetCraftAmount();

            // Never derive "can craft" from m_craftButton.interactable: this postfix writes that
            // very field, so reading it compares our previous decision against itself and the
            // button latches enabled after the resources are gone.
            //
            // Quartermaster transpiles Player.HaveRequirements so its ingredient counting goes
            // through WarehouseService, which means this single check already covers pockets,
            // qualifying chests and the CraftFromContainers toggle. Adding our own count on top
            // produced false positives for chests Quartermaster refuses to pull from.
            bool canCraftSingle = noCost || player.HaveRequirements(recipe, false, 1);

            if (canCraftSingle)
            {
                // The amount-aware overload; also Quartermaster-transpiled, so a batch is only
                // advertised as craftable when the storage rules agree.
                bool canBatch = multiplier <= 1 || player.HaveRequirements(recipe, false, 1, multiplier);
                __instance.m_craftButton.interactable = canBatch;
            }
            else
            {
                __instance.m_craftButton.interactable = false;
            }

            AAACraftingIntegration.ForceEnableBatchButtons(canCraftSingle, multiplier);
        }
    }

    [HarmonyPatch]
    public static class ContainerStateChangedWatchers
    {
        [HarmonyPatch(typeof(Inventory), "Changed")]
        [HarmonyPostfix]
        public static void InventoryChangedPostfix()
        {
            // Inventory.Changed fires for every container in the world on every item move, so
            // clearing the caches unconditionally made a distant smelter force a cache rebuild and
            // a AAA recipe-list invalidation per tick. The caches only feed the crafting UI, so
            // only mark them dirty while it is open.
            if (!InventoryGui.IsVisible()) return;

            BridgePlugin.MarkCacheDirty();
            AAACraftingIntegration.NotifyInventoryChanged();
        }
    }
}
