using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Lets a WardIsLove ward act as the shared range for storage inside it.
    ///
    /// Inside a ward the bubble is the region: a permitted chest anywhere in that ward counts from
    /// anywhere in the same ward, with no distance test. A chest outside every ward the player is
    /// standing in keeps Quartermaster's normal CraftRange behaviour, and everything behaves exactly
    /// as before when the player is outside all wards.
    ///
    /// This replaces Quartermaster's container list rather than its radius number, because "inside
    /// this ward" is a region test, not a distance from the player: swapping only the radius would
    /// still skip a chest at the far edge of a large ward and could reach one outside it.
    ///
    /// Two seams, matching where Quartermaster decides range:
    ///  * CraftStorageAccess.Nearby    - the source list behind counts, max-craft and CraftStores
    ///  * SharedCrafting.SourcesValid  - the validation a craft must pass before it pulls
    /// Both are patched, so what the window shows and what a craft may take stay identical.
    /// </summary>
    public static class WardHostRange
    {
        public static bool IsReady { get; private set; }
        public static string BindError { get; private set; }

        /// <summary>A WardIsLove ward the player is currently standing in.</summary>
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

        private static readonly List<Bubble> PlayerBubbles = new List<Bubble>();
        private static int _bubbleFrame = -1;

        // Quartermaster asks for this list more than once per frame (counts and CraftStores), and
        // each container costs a settings read, so the result is reused within the frame.
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

                if (_craftRange == null || _allContainers == null || canSupply == null || supplyField == null ||
                    _allAreas == null || _wardType == null || _getWardRadius == null ||
                    nearby == null || sourcesValid == null)
                {
                    BindError =
                        $"CraftRange={_craftRange != null}, ContainerRegistry.All={_allContainers != null}, " +
                        $"CanSupply={canSupply != null}, CraftingSupply={supplyField != null}, " +
                        $"m_allAreas={_allAreas != null}, WardMonoscript={_wardType != null}, " +
                        $"GetWardRadius={_getWardRadius != null}, Nearby={nearby != null}, SourcesValid={sourcesValid != null}";

                    QIDebugger.LogError("[WardHostRange] Bindings incomplete: " + BindError);
                    return;
                }

                _canSupply = (Func<Container, bool>)Delegate.CreateDelegate(typeof(Func<Container, bool>), canSupply);

                // GetSettings returns an internal struct, so the supply flag is read through the
                // struct's own field rather than a typed delegate.
                _craftingSupply = container =>
                    (bool)supplyField.GetValue(getSettings.Invoke(null, new object[] { container }));

                bool counted = HarmonyPatchGuard.TryPatch(
                    harmony,
                    nearby,
                    "Quartermaster.CraftStorageAccess.Nearby",
                    prefix: new HarmonyMethod(typeof(WardHostRange), nameof(Nearby_Prefix)));

                bool validated = HarmonyPatchGuard.TryPatch(
                    harmony,
                    sourcesValid,
                    "Quartermaster.SharedCrafting.SourcesValid",
                    prefix: new HarmonyMethod(typeof(WardHostRange), nameof(SourcesValid_Prefix)));

                IsReady = counted && validated;

                if (IsReady && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[WardHostRange] Ward bubbles are the shared storage range " +
                        $"(toggle is {(BridgePlugin.WardHostRange.Value ? "on" : "off")}).");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[WardHostRange] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Replaces Quartermaster's "every container within CraftRange of the player" with
        /// "within CraftRange, or inside a ward the player is standing in".
        /// </summary>
        public static bool Nearby_Prefix(Player player, ref IEnumerable<Container> __result)
        {
            if (!Active || player == null) return true;

            List<Bubble> bubbles = BubblesAround(player);
            if (bubbles.Count == 0) return true;   // outside every ward: Quartermaster's own rules

            int frame = Time.frameCount;
            if (_cachedSourcesFrame == frame && _cachedSources != null)
            {
                __result = _cachedSources;
                return false;
            }

            List<Container> sources = new List<Container>();
            float rangeSq = CraftRangeSquared();
            Vector3 playerPosition = player.transform.position;

            foreach (Container container in _allContainers)
            {
                if (container == null) continue;

                try
                {
                    if (!_canSupply(container) || !_craftingSupply(container)) continue;

                    Vector3 position = container.transform.position;
                    bool withinRange = (position - playerPosition).sqrMagnitude <= rangeSq;
                    bool insideBubble = InsideAnyBubble(position, bubbles) &&
                                        WardIsLoveIntegration.IsPermitted(player, container);

                    if (!withinRange && !insideBubble) continue;

                    sources.Add(container);
                }
                catch (Exception ex)
                {
                    QIDebugger.LogDebug("[WardHostRange] Skipped a container while filtering", ex);
                }
            }

            // Distance first, then instance id for a stable order - Quartermaster itself drains the
            // nearest eligible chests first.
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
                    $"[WardHostRange] {sources.Count} container(s) available across {bubbles.Count} ward bubble(s).");
            }

            __result = sources;
            _cachedSources = sources;
            _cachedSourcesFrame = frame;
            return false;
        }

        /// <summary>
        /// Quartermaster re-checks that every chosen source is still within CraftRange before it
        /// pulls. A chest admitted by the bubble rule would fail that test, so the same rule is
        /// applied here - otherwise the window would offer materials the craft then refuses.
        /// </summary>
        public static bool SourcesValid_Prefix(Player player, Container[] sources, ref bool __result)
        {
            if (!Active || player == null || sources == null) return true;

            List<Bubble> bubbles = BubblesAround(player);
            if (bubbles.Count == 0) return true;

            float rangeSq = CraftRangeSquared();
            Vector3 playerPosition = player.transform.position;

            __result = sources.All(container =>
            {
                if (container == null) return false;

                try
                {
                    if (!_canSupply(container) || !_craftingSupply(container)) return false;

                    Vector3 position = container.transform.position;
                    if ((position - playerPosition).sqrMagnitude <= rangeSq) return true;

                    return InsideAnyBubble(position, bubbles) && WardIsLoveIntegration.IsPermitted(player, container);
                }
                catch
                {
                    return false;
                }
            });

            return false;
        }

        private static float CraftRangeSquared()
        {
            float range = _craftRange != null ? _craftRange.Value : 100f;
            return range * range;
        }

        /// <summary>
        /// WardIsLove wards containing the player this frame. Empty means they stand outside every
        /// ward, which is the signal to leave Quartermaster completely alone.
        /// </summary>
        private static List<Bubble> BubblesAround(Player player)
        {
            int frame = Time.frameCount;
            if (frame == _bubbleFrame) return PlayerBubbles;

            _bubbleFrame = frame;
            PlayerBubbles.Clear();

            if (!(_allAreas.GetValue(null) is List<PrivateArea> areas)) return PlayerBubbles;

            Vector3 playerPosition = player.transform.position;

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

                    Vector3 centre = area.transform.position;
                    if (Utils.DistanceXZ(centre, playerPosition) > radius) continue;

                    PlayerBubbles.Add(new Bubble { Centre = centre, Radius = radius });
                }
                catch (Exception ex)
                {
                    QIDebugger.LogError("[WardHostRange] Failed to read a ward bubble", ex);
                }
            }

            return PlayerBubbles;
        }

        private static bool InsideAnyBubble(Vector3 position, List<Bubble> bubbles)
        {
            foreach (Bubble bubble in bubbles)
            {
                if (Utils.DistanceXZ(bubble.Centre, position) <= bubble.Radius) return true;
            }

            return false;
        }
    }
}
