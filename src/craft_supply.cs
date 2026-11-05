using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Claims the supply chests a craft is about to pull from.
    ///
    /// Quartermaster counts a chest as craft supply whenever it can *read* it
    /// (ContainerRegistry.CanObserve: accessible, valid ZNetView) and its CraftingSupply toggle is on.
    /// The pull, though, edits the container's inventory, which only sticks when the ZDO is ours.
    ///
    /// Normally ownership arrives through the deposit/automation path - but that path only considers
    /// chests with "Receive matching items" (AcceptStorage) on. A supply-only chest (AcceptStorage off)
    /// is therefore counted in the recipe panel and never claimed, so the materials show as available
    /// and the craft then cannot use them.
    ///
    /// A claim is the same move vanilla makes when a player opens a chest. It is only taken when the
    /// recorded owner is not a connected player and not the dedicated server session, so a co-op
    /// partner who is online keeps their chests.
    /// </summary>
    public static class CraftSupplyPatch
    {
        private static Func<Container, bool> _canSupply;
        private static readonly System.Collections.Generic.Dictionary<string, float> LastLogged =
            new System.Collections.Generic.Dictionary<string, float>();
        private const float Throttle = 30f;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type storage = qm?.GetType("Quartermaster.CraftStorageAccess");

                MethodInfo run = storage == null
                    ? null
                    : AccessTools.Method(storage, "Run", new[]
                    {
                        typeof(string), typeof(Container[]), typeof(Func<bool>), typeof(Action),
                        typeof(Action<string>), typeof(Action)
                    });

                MethodInfo canSupply = storage == null
                    ? null
                    : AccessTools.Method(storage, "CanSupply", new[] { typeof(Container) });

                if (run == null || canSupply == null)
                {
                    QIDebugger.LogError(
                        $"[Craft] Bindings incomplete: Run={run != null}, CanSupply={canSupply != null}");
                    return;
                }

                _canSupply = (Func<Container, bool>)Delegate.CreateDelegate(typeof(Func<Container, bool>), canSupply);

                HarmonyPatchGuard.TryPatch(
                    harmony, run, "Quartermaster.CraftStorageAccess.Run",
                    prefix: new HarmonyMethod(typeof(CraftSupplyPatch), nameof(Run_Prefix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[Craft] Supply chests that are not ours are claimed before a pull (owner offline or the " +
                        "server session), so counted materials can actually be used.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Craft] Initialization failed", ex);
            }
        }

        public static void Run_Prefix(string name, Container[] sources)
        {
            if (BridgePlugin.CraftSupplyClaims == null || !BridgePlugin.CraftSupplyClaims.Value) return;
            if (sources == null || sources.Length == 0) return;

            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return;

            int claimed = 0;
            int blocked = 0;

            foreach (Container chest in sources)
            {
                if (chest == null) continue;

                try
                {
                    if (!_canSupply(chest)) continue;

                    ZNetView view = View(chest);
                    if (view == null || !view.IsValid() || view.IsOwner()) continue;

                    long owner = view.GetZDO().GetOwner();
                    if (PlayerConnected(owner))
                    {
                        blocked++;
                        continue;
                    }

                    view.ClaimOwnership();
                    if (view.IsOwner()) claimed++;
                }
                catch (Exception ex)
                {
                    QIDebugger.LogDebug("[Craft] Could not claim a supply chest", ex);
                }
            }

            if (!QIDebugger.Enabled) return;
            if (claimed == 0 && blocked == 0) return;

            float now = Time.time;
            if (LastLogged.TryGetValue(name, out float last) && now - last < Throttle) return;
            LastLogged[name] = now;

            if (claimed > 0)
            {
                QIDebugger.LogDebug($"[Craft] Claimed {claimed} supply chest(s) before pulling '{name}'.");
            }

            if (blocked > 0)
            {
                QIDebugger.LogDebug(
                    $"[Craft] {blocked} supply chest(s) for '{name}' are owned by a connected player - they are " +
                    "counted but cannot be pulled from.");
            }
        }

        private static bool PlayerConnected(long owner)
        {
            if (owner == 0L) return false;

            ZNet net = ZNet.instance;
            if (net == null) return false;

            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer == null || peer.m_uid != owner) continue;
                if (peer.m_server) continue;   // the world session: nobody is holding it
                return true;
            }

            return false;
        }

        private static ZNetView View(Container c)
        {
            if (c == null) return null;
            if (c.m_rootObjectOverride != null) return c.m_rootObjectOverride;

            ZNetView view = c.GetComponent<ZNetView>();
            return view != null && view.IsValid() ? view : null;
        }
    }
}
