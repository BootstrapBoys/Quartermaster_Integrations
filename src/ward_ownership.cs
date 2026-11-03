using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>How far Quartermaster may go to obtain write access to a permitted ward's containers.</summary>
    public enum WardOwnershipMode
    {
        /// <summary>Never request or claim: the ward owner keeps every chest, and writes from a
        /// permitted visitor fail with Quartermaster's own "not locally available" message.</summary>
        Never,

        /// <summary>Skip while the recorded owner is connected; if that owner is not among the
        /// connected peers, let Quartermaster claim, so deposits and machine collection work.</summary>
        IfOwnerOffline,

        /// <summary>Quartermaster's normal request/claim, even inside a ward you are permitted on.</summary>
        Always
    }

    /// <summary>
    /// Stops Quartermaster from taking ownership of containers that sit in a WardIsLove ward the
    /// player is permitted on.
    ///
    /// Quartermaster asks a chest's current owner to hand the ZDO over, and if three requests go
    /// unanswered it claims the chest from the server. In a shared base that means a permitted
    /// visitor can end up owning the ward owner's chests - and the owner then has to take them back.
    ///
    /// With `WardPermittedSkipsOwnership` on, Request() reports the container as settled for any
    /// object inside a ward the player is permitted on: no request, no claim, ownership stays where
    /// it is. Reads are still correct because Quartermaster deserialises the ZDO's item array when it
    /// does not own a container; writes (deposits, machine collection, store-all) still require real
    /// ownership and fall back to Quartermaster's own "storage is not locally available" message.
    ///
    /// A position with no ward over it is untouched, as is anything the player is not permitted on.
    /// </summary>
    public static class WardOwnershipSkip
    {
        private static readonly System.Collections.Generic.Dictionary<int, float> LastLogged =
            new System.Collections.Generic.Dictionary<int, float>();

        private const float Throttle = 60f;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Type ownership = BridgePlugin.QuartermasterAssembly?.GetType("Quartermaster.ChestOwnership");
                MethodInfo request = ownership == null
                    ? null
                    : AccessTools.Method(ownership, "Request", new[] { typeof(ZNetView), typeof(string), typeof(bool) });

                if (request == null)
                {
                    QIDebugger.LogError("[WardOwnership] Quartermaster.ChestOwnership.Request(ZNetView, String, Boolean) not found.");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, request, "Quartermaster.ChestOwnership.Request",
                    prefix: new HarmonyMethod(typeof(WardOwnershipSkip), nameof(Request_Prefix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[WardOwnership] WardOwnership = " +
                        (BridgePlugin.WardOwnership != null ? BridgePlugin.WardOwnership.Value.ToString() : "unbound") +
                        " (Never: owner keeps every chest; IfOwnerOffline: claim only when that owner is away; " +
                        "Always: Quartermaster's normal request).");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[WardOwnership] Initialization failed", ex);
            }
        }

        /// <summary>Returns false to skip the request/claim, reporting the container as settled.</summary>
        public static bool Request_Prefix(ZNetView view, ref bool __result)
        {
            WardOwnershipMode mode = BridgePlugin.WardOwnership != null
                ? BridgePlugin.WardOwnership.Value
                : WardOwnershipMode.Never;

            if (mode == WardOwnershipMode.Always) return true;
            if (view == null || !view.IsValid() || view.IsOwner()) return true;

            Player player = Player.m_localPlayer;
            if (player == null) return true;

            try
            {
                Vector3 position = view.transform.position;
                if (!WardHostRange.InsidePermittedWard(player, position)) return true;

                // Owner unreachable: let Quartermaster run its normal request, which ends in a claim.
                // That is the only server-sanctioned way to write to a container we do not own, so it
                // is what makes deposits and machine collection work while the ward owner is away.
                if (mode == WardOwnershipMode.IfOwnerOffline && OwnerOffline(view)) return true;

                __result = true;

                int id = view.GetInstanceID();
                float now = Time.time;
                if (QIDebugger.Enabled && (!LastLogged.TryGetValue(id, out float last) || now - last > Throttle))
                {
                    LastLogged[id] = now;
                    QIDebugger.LogDebug(
                        $"[WardOwnership] '{view.name}' is in a permitted ward and its owner (session " +
                        $"{view.GetZDO().GetOwner()}) is connected - leaving it alone (no request, no claim).");
                }

                return false;
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[WardOwnership] Could not evaluate the ward; letting Quartermaster request", ex);
                return true;
            }
        }

        /// <summary>
        /// True when the ZDO's owner is not one of the connected peers (or is unset) - the case where a
        /// claim is the only way to write and cannot take a chest from anyone who is present.
        /// </summary>
        private static bool OwnerOffline(ZNetView view)
        {
            long owner = view.GetZDO().GetOwner();
            if (owner == 0L) return true;
            if (owner == ZDOMan.GetSessionID()) return false;

            ZNet net = ZNet.instance;
            if (net == null) return true;

            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer == null || peer.m_uid != owner) continue;

                // The dedicated server owns anything it created or loaded without a player behind it.
                // It is always "connected", but no player is holding the chest, so a claim cannot take
                // it from anyone present - which is exactly the case that must be allowed.
                if (peer.m_server) continue;

                return false;
            }

            return true;
        }
    }
}
