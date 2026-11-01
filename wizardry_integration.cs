using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Lets Wizardry's crafting tables reach base storage the way a workbench in the middle of the
    /// base does.
    ///
    /// The tables (WizardTable, ArcaneForge, WeavingLoom, PotionCauldron) are ordinary vanilla
    /// CraftingStation pieces - Wizardry patches nothing in the crafting path - so Quartermaster
    /// already handles them, but it measures storage from the *player* with CraftRange. A table
    /// placed away from the chest wall therefore shows `4 (0)` and craft refuses, even though the
    /// materials are part of the same base.
    ///
    /// While the current station is one of Wizardry's, containers within Quartermaster's BaseRange of
    /// the station are counted in addition to those within CraftRange of the player. Range only:
    /// CanSupply, the per-chest Crafting Supply flag and ward permissions still decide what counts.
    ///
    /// Runs at lower Harmony priority than the ward rule, so a ward bubble still wins when both apply.
    /// </summary>
    public static class WizardryIntegration
    {
        public static bool IsReady { get; private set; }
        public static string BindError { get; private set; }

        private static readonly string[] StationFields =
        {
            "WizardTable",
            "ArcaneForge",
            "WeavingLoom",
            "PotionCauldron"
        };

        private static readonly List<FieldInfo> StationRefs = new List<FieldInfo>();
        private static readonly HashSet<string> StationNames = new HashSet<string>(StringComparer.Ordinal);

        private static List<Container> _allContainers;
        private static Func<Container, bool> _canSupply;
        private static Func<Container, bool> _craftingSupply;
        private static ConfigEntryBridge _craftRange;
        private static ConfigEntryBridge _baseRange;

        private static List<Container> _cachedSources;
        private static int _cachedSourcesFrame = -1;
        private static float _lastLogTime;

        public static bool Active => IsReady && BridgePlugin.Enabled;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.WizardryGUID, out PluginInfo info) ||
                info?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly wiz = info.Instance.GetType().Assembly;
                Type plugin = wiz.GetType("WarfareWizardry.WizardryPlugin");

                StationRefs.Clear();
                foreach (string field in StationFields)
                {
                    FieldInfo reference = plugin?.GetField(
                        field,
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    if (reference != null) StationRefs.Add(reference);
                }

                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type registry = qm?.GetType("Quartermaster.ContainerRegistry");
                Type transfers = qm?.GetType("Quartermaster.CraftStorageAccess");
                Type pluginType = qm?.GetType("Quartermaster.Plugin");
                Type sharedCrafting = qm?.GetType("Quartermaster.SharedCrafting");

                _allContainers = AccessTools.Field(registry, "All")?.GetValue(null) as List<Container>;

                MethodInfo canSupply = AccessTools.Method(transfers, "CanSupply", new[] { typeof(Container) });
                MethodInfo getSettings = AccessTools.Method(registry, "GetSettings", new[] { typeof(Container) });

                _craftRange = ConfigEntryBridge.For(AccessTools.Field(pluginType, "CraftRange")?.GetValue(null));
                _baseRange = ConfigEntryBridge.For(AccessTools.Field(pluginType, "Range")?.GetValue(null));

                FieldInfo supplyField = getSettings?.ReturnType.GetField(
                    "CraftingSupply",
                    BindingFlags.Public | BindingFlags.Instance);

                MethodInfo nearby = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "Nearby", new[] { typeof(Player) });

                MethodInfo sourcesValid = sharedCrafting == null
                    ? null
                    : AccessTools.Method(sharedCrafting, "SourcesValid", new[] { typeof(Player), typeof(Container[]) });

                if (StationRefs.Count == 0 || _allContainers == null || canSupply == null || supplyField == null ||
                    _craftRange == null || _baseRange == null || nearby == null || sourcesValid == null)
                {
                    BindError =
                        $"stations={StationRefs.Count}, All={_allContainers != null}, CanSupply={canSupply != null}, " +
                        $"CraftingSupply={supplyField != null}, CraftRange={_craftRange != null}, " +
                        $"BaseRange={_baseRange != null}, Nearby={nearby != null}, SourcesValid={sourcesValid != null}";

                    QIDebugger.LogError("[Wizardry] Bindings incomplete: " + BindError);
                    return;
                }

                _canSupply = (Func<Container, bool>)Delegate.CreateDelegate(typeof(Func<Container, bool>), canSupply);

                _craftingSupply = container =>
                    (bool)supplyField.GetValue(getSettings.Invoke(null, new object[] { container }));

                bool counted = HarmonyPatchGuard.TryPatch(
                    harmony, nearby, "Quartermaster.CraftStorageAccess.Nearby (Wizardry)",
                    prefix: new HarmonyMethod(typeof(WizardryIntegration), nameof(Nearby_Prefix))
                    {
                        priority = (int)Priority.LowerThanNormal
                    });

                bool validated = HarmonyPatchGuard.TryPatch(
                    harmony, sourcesValid, "Quartermaster.SharedCrafting.SourcesValid (Wizardry)",
                    prefix: new HarmonyMethod(typeof(WizardryIntegration), nameof(SourcesValid_Prefix))
                    {
                        priority = (int)Priority.LowerThanNormal
                    });

                IsReady = counted && validated;

                if (IsReady && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug("[Wizardry] Base-range storage enabled at Wizardry crafting tables.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Wizardry] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Adds containers within BaseRange of the Wizardry station being used, on top of what
        /// Quartermaster would count around the player. Returns true (and does nothing) when the
        /// current station is not one of Wizardry's.
        /// </summary>
        public static bool Nearby_Prefix(Player player, ref IEnumerable<Container> __result)
        {
            if (!Active || player == null) return true;

            CraftingStation station = player.GetCurrentCraftingStation();
            if (!IsWizardryStation(station)) return true;

            int frame = Time.frameCount;
            if (frame == _cachedSourcesFrame && _cachedSources != null)
            {
                __result = _cachedSources;
                return false;
            }

            Vector3 playerPosition = player.transform.position;
            Vector3 stationPosition = station.transform.position;
            float craftSq = _craftRange.Value * _craftRange.Value;
            float baseSq = _baseRange.Value * _baseRange.Value;

            List<Container> sources = new List<Container>();
            foreach (Container container in _allContainers)
            {
                if (container == null) continue;

                try
                {
                    if (!_canSupply(container) || !_craftingSupply(container)) continue;

                    Vector3 position = container.transform.position;
                    bool nearPlayer = (position - playerPosition).sqrMagnitude <= craftSq;
                    bool nearStation = (position - stationPosition).sqrMagnitude <= baseSq;

                    if (!nearPlayer && !nearStation) continue;
                    if (!WardIsLoveIntegration.IsPermitted(player, container)) continue;

                    sources.Add(container);
                }
                catch (Exception ex)
                {
                    QIDebugger.LogDebug("[Wizardry] Skipped a container while filtering", ex);
                }
            }

            sources.Sort((a, b) =>
            {
                float da = (a.transform.position - playerPosition).sqrMagnitude;
                float db = (b.transform.position - playerPosition).sqrMagnitude;
                return da != db ? da.CompareTo(db) : a.GetInstanceID().CompareTo(b.GetInstanceID());
            });

            if (QIDebugger.Enabled && Time.time - _lastLogTime > 10f)
            {
                _lastLogTime = Time.time;
                QIDebugger.LogDebug(
                    $"[Wizardry] '{station.m_name}': {sources.Count} container(s) reachable (CraftRange + BaseRange of the table).");
            }

            __result = sources;
            _cachedSources = sources;
            _cachedSourcesFrame = frame;
            return false;
        }

        /// <summary>Mirrors <see cref="Nearby_Prefix"/> so the craft can take what the window promised.</summary>
        public static bool SourcesValid_Prefix(Player player, Container[] sources, ref bool __result)
        {
            if (!Active || player == null || sources == null) return true;

            CraftingStation station = player.GetCurrentCraftingStation();
            if (!IsWizardryStation(station)) return true;

            Vector3 playerPosition = player.transform.position;
            Vector3 stationPosition = station.transform.position;
            float craftSq = _craftRange.Value * _craftRange.Value;
            float baseSq = _baseRange.Value * _baseRange.Value;

            __result = sources.All(container =>
            {
                if (container == null) return false;

                try
                {
                    if (!_canSupply(container) || !_craftingSupply(container)) return false;

                    Vector3 position = container.transform.position;
                    if ((position - playerPosition).sqrMagnitude > craftSq &&
                        (position - stationPosition).sqrMagnitude > baseSq)
                    {
                        return false;
                    }

                    return WardIsLoveIntegration.IsPermitted(player, container);
                }
                catch
                {
                    return false;
                }
            });

            return false;
        }

        /// <summary>
        /// Compares by station name against Wizardry's four station objects. Read lazily because
        /// Wizardry assigns them during its own registration, which can run after this plugin loads.
        /// </summary>
        private static bool IsWizardryStation(CraftingStation station)
        {
            if (station == null) return false;

            // Re-read every time: Wizardry fills its four station fields during registration, which
            // can finish after this plugin loads, and only some of them exist on a given save. Four
            // field reads per craft is nothing next to locking in a partial list.
            ResolveStationNames();
            return StationNames.Count > 0 && StationNames.Contains(station.m_name);
        }

        private static void ResolveStationNames()
        {
            int before = StationNames.Count;

            foreach (FieldInfo field in StationRefs)
            {
                try
                {
                    if (!(field.GetValue(null) is CraftingStation reference) || reference == null) continue;
                    if (!string.IsNullOrEmpty(reference.m_name)) StationNames.Add(reference.m_name);
                }
                catch
                {
                    // Registration not finished yet; the next craft retries.
                }
            }

            if (QIDebugger.Enabled && StationNames.Count > before)
            {
                QIDebugger.LogDebug($"[Wizardry] Stations ({StationNames.Count}/{StationRefs.Count}): " +
                                    string.Join(", ", StationNames));
            }
        }

        /// <summary>
        /// Reads a Quartermaster ConfigEntry&lt;float&gt; without binding to its generic type at
        /// compile time, so the audit can prove the field and the value still flows through.
        /// </summary>
        private sealed class ConfigEntryBridge
        {
            private readonly object _entry;
            private readonly PropertyInfo _value;

            private ConfigEntryBridge(object entry, PropertyInfo value)
            {
                _entry = entry;
                _value = value;
            }

            public float Value => _value != null ? (float)_value.GetValue(_entry, null) : 0f;

            public static ConfigEntryBridge For(object entry)
            {
                if (entry == null) return null;

                PropertyInfo value = entry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                return value == null ? null : new ConfigEntryBridge(entry, value);
            }
        }
    }
}
