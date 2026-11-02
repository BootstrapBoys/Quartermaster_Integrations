using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
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

        private const float Throttle = 10f;

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
                        "[WardOwnership] Ward-permitted containers " +
                        (BridgePlugin.WardPermittedSkipsOwnership != null && BridgePlugin.WardPermittedSkipsOwnership.Value
                            ? "skip Quartermaster's ownership request."
                            : "still go through Quartermaster's ownership request."));
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
            if (BridgePlugin.WardPermittedSkipsOwnership == null || !BridgePlugin.WardPermittedSkipsOwnership.Value) return true;
            if (view == null || !view.IsValid() || view.IsOwner()) return true;

            Player player = Player.m_localPlayer;
            if (player == null) return true;

            try
            {
                Vector3 position = view.transform.position;
                if (!WardHostRange.InsidePermittedWard(player, position)) return true;

                __result = true;

                int id = view.GetInstanceID();
                float now = Time.time;
                if (QIDebugger.Enabled && (!LastLogged.TryGetValue(id, out float last) || now - last > Throttle))
                {
                    LastLogged[id] = now;
                    QIDebugger.LogDebug(
                        $"[WardOwnership] '{view.name}' is in a permitted ward - leaving its owner alone " +
                        "(no request, no claim).");
                }

                return false;
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[WardOwnership] Could not evaluate the ward; letting Quartermaster request", ex);
                return true;
            }
        }
    }
}
