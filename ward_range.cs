using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Makes a WardIsLove ward the range authority for a Quartermaster base that sits inside it.
    ///
    /// Three rules, all keyed off the ward bubble rather than a radius from the player:
    ///  1. A Deposit Chest inside a ward hosts its network over that ward's bubble. The bubble is the
    ///     BaseRange for that network, so a chest at the bubble's edge is out of range the moment the
    ///     player steps outside it, however close Quartermaster's numbers would allow.
    ///  2. While the player stands inside a ward, only chests inside that same bubble count for
    ///     crafting - Quartermaster's CraftRange does not reach past the bubble.
    ///  3. A Deposit Chest outside every ward keeps Quartermaster's BaseRange and CraftRange exactly
    ///     as configured.
    ///
    /// Seams, all in Quartermaster:
    ///  * BaseNetwork.Contains / CanTransfer - network membership, which also drives which chests a
    ///    base owns and whether the player is "in range" of it
    ///  * RebuildNetworks                   - merge deposit chests that share a ward into one network
    ///  * CraftStorageAccess.Nearby         - the source list behind counts, max-craft and CraftStores
    ///  * SharedCrafting.SourcesValid       - the validation a craft must pass before it pulls
    /// </summary>
    public static class WardHostRange
    {
        public static bool IsReady { get; private set; }
        public static string BindError { get; private set; }

        private struct Bubble
        {
            public Vector3 Centre;
            public float Radius;
        }

        private static ConfigEntry<float> _craftRange;
        private static List<Container> _allContainers;
        private static Func<Container, bool> _canSupply;
        private static Func<Container, bool> _craftingSupply;
        private static FieldInfo _allAreas;
        private static Type _wardType;
        private static MethodInfo _getWardRadius;

        private static Type _baseNetworkType;
        private static MethodInfo _networkFor;
        private static MethodInfo _rebuildNetworks;
        private static FieldInfo _hubsField;
        private static FieldInfo _chestsField;
        private static FieldInfo _machinesField;
        private static FieldInfo _networksField;

        // Every WardIsLove ward, rebuilt once per frame and shared by all reads below.
        private static readonly List<Bubble> Wards = new List<Bubble>();
        private static int _wardFrame = -1;

        // Which ward a network hangs off; networks are few but Contains is called per container.
        private static readonly Dictionary<object, int> NetworkWard = new Dictionary<object, int>();

        private static List<Container> _cachedSources;
        private static int _cachedSourcesFrame = -1;
        private static float _lastLogTime;

        public static bool Active =>
            IsReady &&
            BridgePlugin.Enabled &&
            BridgePlugin.WardHostRange != null &&
            BridgePlugin.WardHostRange.Value;

        public static void Initialize(Harmony harmony, Assembly wardIsLoveAssembly)
        {
            if (!BridgePlugin.Enabled || harmony == null || wardIsLoveAssembly == null) return;

            try
            {
                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type pluginType = qm?.GetType("Quartermaster.Plugin");
                Type transfers = qm?.GetType("Quartermaster.CraftStorageAccess");
                Type registry = qm?.GetType("Quartermaster.ContainerRegistry");
                Type sharedCrafting = qm?.GetType("Quartermaster.SharedCrafting");

                _craftRange = AccessTools.Field(pluginType, "CraftRange")?.GetValue(null) as ConfigEntry<float>;
                _allContainers = AccessTools.Field(registry, "All")?.GetValue(null) as List<Container>;

                MethodInfo canSupply = AccessTools.Method(transfers, "CanSupply", new[] { typeof(Container) });
                MethodInfo getSettings = AccessTools.Method(registry, "GetSettings", new[] { typeof(Container) });

                _allAreas = AccessTools.Field(typeof(PrivateArea), "m_allAreas");
                _wardType = wardIsLoveAssembly.GetType("WardIsLove.Util.WardMonoscript");
                _getWardRadius = _wardType == null
                    ? null
                    : AccessTools.Method(
                        wardIsLoveAssembly.GetType("WardIsLove.Extensions.WardMonoscriptExt"),
                        "GetWardRadius",
                        new[] { _wardType });

                MethodInfo nearby = transfers == null
                    ? null
                    : AccessTools.Method(transfers, "Nearby", new[] { typeof(Player) });

                MethodInfo sourcesValid = sharedCrafting == null
                    ? null
                    : AccessTools.Method(sharedCrafting, "SourcesValid", new[] { typeof(Player), typeof(Container[]) });

                FieldInfo supplyField = getSettings?.ReturnType.GetField(
                    "CraftingSupply",
                    BindingFlags.Public | BindingFlags.Instance);

                ResolveNetworkTypes(qm);

                MethodInfo contains = _baseNetworkType == null
                    ? null
                    : AccessTools.Method(_baseNetworkType, "Contains", new[] { typeof(Vector3) });

                MethodInfo canTransfer = _baseNetworkType == null
                    ? null
                    : AccessTools.Method(_baseNetworkType, "CanTransfer", new[] { typeof(Vector3), typeof(Vector3) });

                if (_craftRange == null || _allContainers == null || canSupply == null || supplyField == null ||
                    _allAreas == null || _wardType == null || _getWardRadius == null ||
                    nearby == null || sourcesValid == null ||
                    contains == null || canTransfer == null || _networkFor == null || _networksField == null)
                {
                    BindError =
                        $"CraftRange={_craftRange != null}, ContainerRegistry.All={_allContainers != null}, " +
                        $"CanSupply={canSupply != null}, CraftingSupply={supplyField != null}, " +
                        $"m_allAreas={_allAreas != null}, WardMonoscript={_wardType != null}, " +
                        $"GetWardRadius={_getWardRadius != null}, Nearby={nearby != null}, " +
                        $"SourcesValid={sourcesValid != null}, Contains={contains != null}, " +
                        $"CanTransfer={canTransfer != null}, Network={_networkFor != null}, " +
                        $"Networks={_networksField != null}";

                    QIDebugger.LogError("[WardHostRange] Bindings incomplete: " + BindError);
                    return;
                }

                _canSupply = (Func<Container, bool>)Delegate.CreateDelegate(typeof(Func<Container, bool>), canSupply);

                // GetSettings returns an internal struct, so the supply flag is read through the
                // struct's own field rather than a typed delegate.
                _craftingSupply = container =>
                    (bool)supplyField.GetValue(getSettings.Invoke(null, new object[] { container }));

                bool counted = HarmonyPatchGuard.TryPatch(
                    harmony, nearby, "Quartermaster.CraftStorageAccess.Nearby",
                    prefix: new HarmonyMethod(typeof(WardHostRange), nameof(Nearby_Prefix)));

                bool validated = HarmonyPatchGuard.TryPatch(
                    harmony, sourcesValid, "Quartermaster.SharedCrafting.SourcesValid",
                    prefix: new HarmonyMethod(typeof(WardHostRange), nameof(SourcesValid_Prefix)));

                bool contained = HarmonyPatchGuard.TryPatch(
                    harmony, contains, "Quartermaster.BaseNetwork.Contains",
                    postfix: new HarmonyMethod(typeof(WardHostRange), nameof(Contains_Postfix)));

                bool transfer = HarmonyPatchGuard.TryPatch(
                    harmony, canTransfer, "Quartermaster.BaseNetwork.CanTransfer",
                    postfix: new HarmonyMethod(typeof(WardHostRange), nameof(CanTransfer_Postfix)));

                bool grouped = _rebuildNetworks != null && HarmonyPatchGuard.TryPatch(
                    harmony, _rebuildNetworks, "Quartermaster.RebuildNetworks",
                    postfix: new HarmonyMethod(typeof(WardHostRange), nameof(RebuildNetworks_Postfix)));

                IsReady = counted && validated && contained && transfer && grouped;

                if (IsReady && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[WardHostRange] Ward bubbles own base membership and crafting range " +
                        $"(counting={counted}, validation={validated}, contains={contained}, " +
                        $"transfer={transfer}, grouping={grouped}, toggle is " +
                        $"{(BridgePlugin.WardHostRange.Value ? "on" : "off")}).");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[WardHostRange] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Quartermaster's warehouse types are internal and their nesting has moved before, so find
        /// them by shape instead of a hardcoded full name.
        /// </summary>
        private static void ResolveNetworkTypes(Assembly qm)
        {
            if (qm == null) return;

            Type[] types;
            try
            {
                types = qm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            foreach (Type type in types)
            {
                if (type == null) continue;

                if (type.Name == "BaseNetwork")
                {
                    _baseNetworkType = type;
                    _hubsField = type.GetField("Hubs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _chestsField = type.GetField("Chests", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _machinesField = type.GetField("Machines", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_networkFor == null)
                {
                    _networkFor = type.GetMethod(
                        "Network",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(Component), typeof(string) },
                        null);
                }

                if (_rebuildNetworks == null)
                {
                    _rebuildNetworks = type.GetMethod(
                        "RebuildNetworks",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);

                    if (_rebuildNetworks != null)
                    {
                        _networksField = type.GetField(
                            "networks",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
            }
        }

        // ---- base network membership ------------------------------------------------------------

        /// <summary>
        /// A network whose deposit chest sits in a ward is bounded by that ward, not by BaseRange.
        /// This is what makes a base unreachable from outside the bubble and stops it stretching past
        /// the bubble's edge.
        /// </summary>
        public static void Contains_Postfix(object __instance, Vector3 point, ref bool __result)
        {
            if (!Active) return;

            int ward = WardOfNetwork(__instance);
            if (ward < 0) return;

            __result = Inside(Wards[ward], point);
        }

        public static void CanTransfer_Postfix(object __instance, Vector3 from, Vector3 to, ref bool __result)
        {
            if (!Active) return;

            int ward = WardOfNetwork(__instance);
            if (ward < 0) return;

            __result = Inside(Wards[ward], from) && Inside(Wards[ward], to);
        }

        /// <summary>
        /// Deposit chests sharing a ward become one network, whatever BaseRange says.
        /// </summary>
        public static void RebuildNetworks_Postfix()
        {
            if (!Active || _networksField == null) return;

            if (!(_networksField.GetValue(null) is IList networks) || networks.Count < 2) return;

            for (int i = 0; i < networks.Count; i++)
            {
                int ward = WardOfNetwork(networks[i]);
                if (ward < 0) continue;

                for (int j = networks.Count - 1; j > i; j--)
                {
                    if (WardOfNetwork(networks[j]) != ward) continue;

                    Merge(networks[i], networks[j]);
                    networks.RemoveAt(j);
                }
            }
        }

        private static void Merge(object target, object source)
        {
            Union(_hubsField, target, source);
            Union(_chestsField, target, source);
            Union(_machinesField, target, source);
        }

        private static void Union(FieldInfo field, object target, object source)
        {
            if (field == null) return;
            if (!(field.GetValue(target) is IList into)) return;
            if (!(field.GetValue(source) is IEnumerable from)) return;

            foreach (object item in from)
            {
                if (!into.Contains(item)) into.Add(item);
            }
        }

        // ---- crafting range ----------------------------------------------------------------------

        /// <summary>
        /// Inside a ward: only the bubble counts. Outside every ward: Quartermaster's CraftRange,
        /// minus bases that are hosted in a ward (those are only reachable from inside it).
        /// </summary>
        public static bool Nearby_Prefix(Player player, ref IEnumerable<Container> __result)
        {
            if (!Active || player == null) return true;

            List<Bubble> wards = AllWards();
            if (wards.Count == 0) return true;   // no wards in the world: leave Quartermaster alone

            int frame = Time.frameCount;
            if (frame == _cachedSourcesFrame && _cachedSources != null)
            {
                __result = _cachedSources;
                return false;
            }

            Vector3 playerPosition = player.transform.position;
            List<Bubble> playerWards = wards.Where(w => Inside(w, playerPosition)).ToList();
            bool insideWard = playerWards.Count > 0;

            List<Container> sources = new List<Container>();
            foreach (Container container in _allContainers)
            {
                if (container == null) continue;

                try
                {
                    if (!_canSupply(container) || !_craftingSupply(container)) continue;
                    if (Reachable(player, container, playerWards, insideWard)) sources.Add(container);
                }
                catch (Exception ex)
                {
                    QIDebugger.LogDebug("[WardHostRange] Skipped a container while filtering", ex);
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
                    $"[WardHostRange] {sources.Count} container(s) reachable " +
                    (insideWard ? $"inside {playerWards.Count} ward bubble(s)." : "by Quartermaster range outside wards."));
            }

            __result = sources;
            _cachedSources = sources;
            _cachedSourcesFrame = frame;
            return false;
        }

        /// <summary>
        /// Mirrors <see cref="Nearby_Prefix"/>. Quartermaster re-checks the chosen sources before it
        /// withdraws, so the bubble rule has to answer the same way or the craft is refused.
        /// </summary>
        public static bool SourcesValid_Prefix(Player player, Container[] sources, ref bool __result)
        {
            if (!Active || player == null || sources == null) return true;

            List<Bubble> wards = AllWards();
            if (wards.Count == 0) return true;

            Vector3 playerPosition = player.transform.position;
            List<Bubble> playerWards = wards.Where(w => Inside(w, playerPosition)).ToList();
            bool insideWard = playerWards.Count > 0;

            __result = sources.All(container =>
            {
                if (container == null) return false;

                try
                {
                    if (!_canSupply(container) || !_craftingSupply(container)) return false;
                    return Reachable(player, container, playerWards, insideWard);
                }
                catch
                {
                    return false;
                }
            });

            return false;
        }

        private static bool Reachable(Player player, Container container, List<Bubble> playerWards, bool insideWard)
        {
            Vector3 position = container.transform.position;

            if (insideWard)
            {
                // Strict: while in a bubble, nothing outside it counts.
                for (int i = 0; i < playerWards.Count; i++)
                {
                    if (Inside(playerWards[i], position)) return WardIsLoveIntegration.IsPermitted(player, container);
                }

                return false;
            }

            if ((position - player.transform.position).sqrMagnitude > CraftRangeSquared()) return false;

            // A base hosted in a ward can only be used from inside that ward.
            return !InWardedBase(container);
        }

        private static bool InWardedBase(Container container)
        {
            if (_networkFor == null) return false;

            object network = _networkFor.Invoke(null, new object[] { container, null });
            return network != null && WardOfNetwork(network) >= 0;
        }

        // ---- ward geometry -----------------------------------------------------------------------

        private static List<Bubble> AllWards()
        {
            int frame = Time.frameCount;
            if (frame == _wardFrame) return Wards;

            _wardFrame = frame;
            Wards.Clear();
            NetworkWard.Clear();

            if (!(_allAreas.GetValue(null) is List<PrivateArea> areas)) return Wards;

            foreach (PrivateArea area in areas)
            {
                if (area == null) continue;

                try
                {
                    // Only wards WardIsLove hosts; a plain vanilla ward keeps Quartermaster's rules.
                    Component ward = area.GetComponent(_wardType);
                    if (ward == null) continue;

                    float radius = (float)_getWardRadius.Invoke(null, new object[] { ward });
                    if (radius <= 0f) continue;

                    Wards.Add(new Bubble { Centre = area.transform.position, Radius = radius });
                }
                catch (Exception ex)
                {
                    QIDebugger.LogError("[WardHostRange] Failed to read a ward bubble", ex);
                }
            }

            return Wards;
        }

        private static int WardOfNetwork(object network)
        {
            if (network == null) return -1;

            // Keep the per-frame ward list current: a network rebuild can happen before anything
            // else has asked for it this frame.
            AllWards();

            if (NetworkWard.TryGetValue(network, out int cached)) return cached;

            int found = -1;
            if (_hubsField?.GetValue(network) is IEnumerable hubs)
            {
                foreach (Container hub in hubs)
                {
                    if (hub == null) continue;
                    int index = WardIndex(hub.transform.position);
                    if (index < 0) continue;
                    found = index;
                    break;
                }
            }

            NetworkWard[network] = found;
            return found;
        }

        private static int WardIndex(Vector3 position)
        {
            for (int i = 0; i < Wards.Count; i++)
            {
                if (Inside(Wards[i], position)) return i;
            }

            return -1;
        }

        private static bool Inside(Bubble ward, Vector3 position) =>
            Utils.DistanceXZ(ward.Centre, position) <= ward.Radius;

        private static float CraftRangeSquared()
        {
            float range = _craftRange != null ? _craftRange.Value : 100f;
            return range * range;
        }
    }
}
