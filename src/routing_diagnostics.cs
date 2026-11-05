using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Debug-only: when a deposit chest reports "No destination", say which filter rejected every
    /// chest in the base.
    ///
    /// Quartermaster builds that status from Automation.Destinations(...).Count == 0, and Destinations
    /// rejects a chest for any of four reasons: it is the source, CanTransfer(sourcePoint, chest)
    /// fails, FurnitureAssignment.Accepts(chest, item) fails, or Writable(chest, item) fails - no
    /// capacity, the chest is busy, or its ZDO is not ours to write. CanTransfer is also the method the
    /// WardIsLove rule patches, so a warded base can lose every destination if the bubble is wrong; the
    /// owner=/inUse= fields on each blocked line tell the remaining three apart.
    ///
    /// Reads only; it never changes routing.
    /// </summary>
    public static class RoutingDiagnostics
    {
        private static MethodInfo _explainUnsorted;
        private static Func<Container, ItemDrop.ItemData, string, bool> _writable;
        private static Func<Container, ItemDrop.ItemData, bool> _accepts;
        private static Func<object, Vector3, Vector3, bool> _canTransfer;
        private static FieldInfo _chestsField;
        private static List<Container> _allContainers;
        private static MethodInfo _getSettings;
        private static MethodInfo _itemId;
        private static FieldInfo _remembered;
        private static FieldInfo _forgotten;
        private static FieldInfo _acceptStorage;
        private static FieldInfo _deposit;
        private static FieldInfo _overflow;
        private static FieldInfo _autoAssign;

        private static readonly Dictionary<string, float> LastLogged = new Dictionary<string, float>();
        private const float Throttle = 10f;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type automation = qm?.GetType("Quartermaster.Automation");
                Type baseNetwork = qm?.GetType("Quartermaster.BaseNetwork");
                Type furniture = qm?.GetType("Quartermaster.FurnitureAssignment");

                MethodInfo destinations = automation == null
                    ? null
                    : AccessTools.Method(automation, "Destinations", new[]
                    {
                        baseNetwork, typeof(ItemDrop.ItemData), typeof(Vector3), typeof(Container), typeof(bool)
                    });

                MethodInfo writable = automation == null
                    ? null
                    : AccessTools.Method(automation, "Writable", new[] { typeof(Container), typeof(ItemDrop.ItemData), typeof(string) });

                MethodInfo accepts = furniture == null
                    ? null
                    : AccessTools.Method(furniture, "Accepts", new[] { typeof(Container), typeof(ItemDrop.ItemData) });

                MethodInfo canTransfer = baseNetwork == null
                    ? null
                    : AccessTools.Method(baseNetwork, "CanTransfer", new[] { typeof(Vector3), typeof(Vector3) });

                _explainUnsorted = automation == null
                    ? null
                    : AccessTools.Method(automation, "ExplainUnsorted", new[] { baseNetwork, typeof(Container), typeof(ItemDrop.ItemData) });

                _chestsField = baseNetwork?.GetField("Chests", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                Type registry = qm?.GetType("Quartermaster.ContainerRegistry");
                Type transfersType = qm?.GetType("Quartermaster.InventoryTransfers");

                _allContainers = AccessTools.Field(registry, "All")?.GetValue(null) as List<Container>;
                _getSettings = AccessTools.Method(registry, "GetSettings", new[] { typeof(Container) });
                _itemId = AccessTools.Method(transfersType, "ItemId", new[] { typeof(ItemDrop.ItemData) });

                Type settingsType = _getSettings?.ReturnType;
                if (settingsType != null)
                {
                    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    _remembered = settingsType.GetField("Remembered", F);
                    _forgotten = settingsType.GetField("Forgotten", F);
                    _acceptStorage = settingsType.GetField("AcceptStorage", F);
                    _deposit = settingsType.GetField("Deposit", F);
                    _overflow = settingsType.GetField("Overflow", F);
                    _autoAssign = settingsType.GetField("AutoAssign", F);
                }

                if (destinations == null || writable == null || accepts == null || canTransfer == null || _chestsField == null)
                {
                    QIDebugger.LogError(
                        "[Routing] Bindings incomplete: " +
                        $"Destinations={destinations != null}, Writable={writable != null}, " +
                        $"Accepts={accepts != null}, CanTransfer={canTransfer != null}, Chests={_chestsField != null}");
                    return;
                }

                _writable = (Func<Container, ItemDrop.ItemData, string, bool>)Delegate.CreateDelegate(
                    typeof(Func<Container, ItemDrop.ItemData, string, bool>), writable);

                _accepts = (Func<Container, ItemDrop.ItemData, bool>)Delegate.CreateDelegate(
                    typeof(Func<Container, ItemDrop.ItemData, bool>), accepts);

                _canTransfer = (instance, from, to) => (bool)canTransfer.Invoke(instance, new object[] { from, to });

                HarmonyPatchGuard.TryPatch(
                    harmony, destinations, "Quartermaster.Automation.Destinations",
                    postfix: new HarmonyMethod(typeof(RoutingDiagnostics), nameof(Destinations_Postfix)));

                if (QIDebugger.Enabled) QIDebugger.LogDebug("[Routing] No-destination diagnostics enabled.");
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Routing] Initialization failed", ex);
            }
        }

        public static void Destinations_Postfix(
            object __0,
            ItemDrop.ItemData __1,
            Vector3 __2,
            Container __3,
            List<Container> __result)
        {
            if (!QIDebugger.Enabled || __result == null || __result.Count > 0) return;
            if (__1?.m_shared == null) return;

            string name = __1.m_shared.m_name;
            float now = Time.time;
            if (LastLogged.TryGetValue(name, out float last) && now - last < Throttle) return;
            LastLogged[name] = now;

            try
            {
                int total = 0, transferOk = 0, acceptsOk = 0, writableOk = 0;
                List<string> samples = new List<string>(4);
                List<string> blocked = new List<string>(4);

                if (_chestsField.GetValue(__0) is IEnumerable chests)
                {
                    foreach (object entry in chests)
                    {
                        if (!(entry is Container chest) || chest == null || chest == __3) continue;

                        total++;
                        bool transfer = _canTransfer(__0, __2, chest.transform.position);
                        bool accept = _accepts(chest, __1);
                        bool writable = _writable(chest, __1, "destination");

                        if (transfer) transferOk++;
                        if (accept) acceptsOk++;
                        if (writable) writableOk++;

                        string line = $"{chest.m_name}: transfer={transfer} accepts={accept} writable={writable}{Why(chest)}";

                        if (transfer && accept && !writable && blocked.Count < 3) blocked.Add(line);
                        else if (samples.Count < 4) samples.Add(line);
                    }
                }

                QIDebugger.LogDebug(
                    $"[Routing] '{name}' x{__1.m_stack}: no destination in base (chests={total}, " +
                    $"transferOk={transferOk}, accepts={acceptsOk}, writable={writableOk})");

                foreach (string line in samples) QIDebugger.LogDebug("[Routing]   " + line);
                foreach (string line in blocked) QIDebugger.LogDebug("[Routing]   full/busy -> " + line);

                ReportAssignments(__0, __1);

                string explain = _explainUnsorted?.Invoke(null, new object[] { __0, __3, __1 }) as string;
                if (!string.IsNullOrEmpty(explain)) QIDebugger.LogDebug("[Routing]   quartermaster: " + explain);
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[Routing] Could not describe the failed route", ex);
            }
        }

        /// <summary>
        /// Finds every chest in the world that has this item remembered, and says whether the network
        /// sees it. An assignment on a chest outside the network - or on a chest with AcceptStorage
        /// off, Deposit on, or the item in Forgotten - explains "no destination" with the assignment
        /// apparently present.
        /// </summary>
        private static void ReportAssignments(object network, ItemDrop.ItemData item)
        {
            if (_allContainers == null || _getSettings == null || _itemId == null || _remembered == null) return;

            string id = _itemId.Invoke(null, new object[] { item }) as string;
            if (string.IsNullOrEmpty(id)) return;

            System.Collections.IList networkChests = _chestsField.GetValue(network) as System.Collections.IList;

            int found = 0;
            foreach (Container chest in _allContainers)
            {
                if (chest == null) continue;

                object settings = _getSettings.Invoke(null, new object[] { chest });
                if (!(Remembered(settings).Contains(id))) continue;

                found++;
                bool inNetwork = networkChests != null && networkChests.Contains(chest);

                QIDebugger.LogDebug(
                    $"[Routing]   assigned on '{chest.m_name}': inNetwork={inNetwork}, " +
                    $"acceptStorage={Flag(_acceptStorage, settings)}, deposit={Flag(_deposit, settings)}, " +
                    $"overflow={Flag(_overflow, settings)}, autoAssign={Flag(_autoAssign, settings)}, " +
                    $"forgotten={Remembered(settings, _forgotten).Contains(id)}");
            }

            QIDebugger.LogDebug($"[Routing]   item id='{id}', shared='{item.m_shared.m_name}'");

            if (found == 0)
            {
                QIDebugger.LogDebug("[Routing]   no chest in the world remembers this item id");

                // Show what chests do remember, so a prefab-name vs shared-name mismatch is visible.
                int sampled = 0;
                foreach (Container chest in _allContainers)
                {
                    if (chest == null) continue;

                    object settings = _getSettings.Invoke(null, new object[] { chest });
                    List<string> entries = Remembered(settings);
                    if (entries.Count == 0) continue;

                    QIDebugger.LogDebug(
                        $"[Routing]   '{chest.m_name}' remembers: " +
                        string.Join(", ", entries.GetRange(0, Math.Min(3, entries.Count))));

                    if (++sampled >= 3) break;
                }
            }
        }

        /// <summary>
        /// Names the reason Writable() said no: who owns the ZDO, and whether the game thinks the lid
        /// is open. "writable=False owner=server inUse=False" is an ownership refusal, not a busy chest.
        /// </summary>
        private static string Why(Container chest)
        {
            try
            {
                ZNetView view = chest.m_rootObjectOverride != null
                    ? chest.m_rootObjectOverride
                    : chest.GetComponent<ZNetView>();

                if (view == null || !view.IsValid()) return " view=invalid";

                ZDO zdo = view.GetZDO();
                long owner = zdo.GetOwner();
                string who = view.IsOwner() ? "self"
                    : owner == 0L ? "none"
                    : ServerOwns(owner) ? "server"
                    : "peer:" + owner;

                return $" owner={who} inUse={chest.IsInUse()} zdoInUse={zdo.GetInt("inUse", 0)}";
            }
            catch
            {
                return "";
            }
        }

        private static bool ServerOwns(long uid)
        {
            ZNet net = ZNet.instance;
            if (net == null) return false;

            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer != null && peer.m_uid == uid && peer.m_server) return true;
            }

            return false;
        }

        private static List<string> Remembered(object settings, FieldInfo field = null)
        {
            return (field ?? _remembered)?.GetValue(settings) as List<string> ?? new List<string>();
        }

        private static string Flag(FieldInfo field, object settings)
        {
            return field == null ? "?" : field.GetValue(settings)?.ToString() ?? "?";
        }
    }
}
