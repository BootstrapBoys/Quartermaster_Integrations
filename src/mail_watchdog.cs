using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Clears Quartermaster's stale queued-mail links.
    ///
    /// A mailbox stores the parcel it is sending in its own ZDO ("Quartermaster.outgoingMail").
    /// PostalDirectory.Progress turns that link into a MailProgress, and when the parcel ZDO cannot be
    /// found it fabricates a Waiting progress whose status is the literal "Checking queued mail" - with
    /// no way out, because nothing ever clears the link. That happens for real after a restart: a parcel
    /// created between two saves is gone, or one destroyed by the sweep leaves the mailbox pointing at
    /// nothing. Pending() then reports mail forever and the mailbox refuses new work.
    ///
    /// This sweeps registered mailboxes every couple of seconds, and when a link has pointed at a missing
    /// parcel for longer than the grace period it clears the link. A mailbox within ClaimRange of the
    /// player is claimed first (the same claim vanilla makes when a chest is opened), so the repair also
    /// works inside a ward and while the recorded owner is online elsewhere; a mailbox further away is
    /// only repaired if it is already ours. The mailbox then returns to idle with one line in the log
    /// instead of a stuck "Checking queued mail".
    ///
    /// Config: `4. Patches -> MailStaleLinkCleanup` (default on).
    /// </summary>
    public static class MailWatchdogPatch
    {
        internal const string LinkKey = "Quartermaster.outgoingMail";

        /// <summary>How long a link may dangle before it is treated as stale. Rig tests set this to 0.</summary>
        internal static float GraceSeconds = 10f;

        /// <summary>A mailbox this close to the player may be claimed for the repair.</summary>
        internal const float ClaimRange = 30f;

        private static MethodInfo _isMailbox;
        private static FieldInfo _all;
        private static readonly Dictionary<ZDOID, float> FirstSeen = new Dictionary<ZDOID, float>();
        private static float _nextSweep;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type directory = qm?.GetType("Quartermaster.PostalDirectory");
                Type mailbox = qm?.GetType("Quartermaster.PostalMailbox");
                Type registry = qm?.GetType("Quartermaster.ContainerRegistry");

                _isMailbox = AccessTools.Method(mailbox, "IsMailbox", new[] { typeof(Container) });
                _all = AccessTools.Field(registry, "All");

                MethodInfo update = AccessTools.Method(directory, "Update", Type.EmptyTypes);

                if (_isMailbox == null || _all == null || update == null)
                {
                    QIDebugger.LogError(
                        $"[Mail] Bindings incomplete: IsMailbox={_isMailbox != null}, All={_all != null}, Update={update != null}");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, update, "Quartermaster.PostalDirectory.Update",
                    postfix: new HarmonyMethod(typeof(MailWatchdogPatch), nameof(Update_Postfix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[Mail] Watching mailboxes for stale queued-mail links (cleared after " +
                        GraceSeconds.ToString("0") + " s if the parcel no longer exists).");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Mail] Initialization failed", ex);
            }
        }

        public static void Update_Postfix()
        {
            if (BridgePlugin.MailStaleLinkCleanup == null || !BridgePlugin.MailStaleLinkCleanup.Value) return;
            if (Time.unscaledTime < _nextSweep) return;

            _nextSweep = Time.unscaledTime + 2f;
            SweepNow();
        }

        /// <summary>
        /// One pass over every registered mailbox. Returns the number of stale links cleared, which the
        /// rig uses as its self-check.
        /// </summary>
        internal static int SweepNow()
        {
            if (!BridgePlugin.Enabled || ZDOMan.instance == null) return 0;

            IList containers;
            try
            {
                containers = _all?.GetValue(null) as IList;
            }
            catch
            {
                return 0;
            }

            if (containers == null) return 0;

            Dictionary<ZDOID, float> next = new Dictionary<ZDOID, float>();
            int cleared = 0;

            for (int i = 0; i < containers.Count; i++)
            {
                Container c = containers[i] as Container;
                if (c == null) continue;

                bool mailbox;
                try
                {
                    mailbox = (bool)_isMailbox.Invoke(null, new object[] { c });
                }
                catch
                {
                    continue;
                }

                if (!mailbox) continue;

                try
                {
                    if (Check(c, next)) cleared++;
                }
                catch (Exception ex)
                {
                    QIDebugger.LogDebug("[Mail] Could not check a mailbox", ex);
                }
            }

            // Forget mailboxes that no longer exist so the map cannot grow across a session.
            FirstSeen.Clear();
            foreach (KeyValuePair<ZDOID, float> pair in next) FirstSeen[pair.Key] = pair.Value;

            return cleared;
        }

        private static bool Check(Container mailbox, Dictionary<ZDOID, float> next)
        {
            ZNetView view = ContainerZdo.View(mailbox);
            if (view == null || !view.IsValid()) return false;

            ZDO zdo = view.GetZDO();
            ZDOID parcel = zdo.GetZDOID(LinkKey);
            if (parcel == ZDOID.None) return false;             // nothing queued

            if (ZDOMan.instance.GetZDO(parcel) != null) return false;   // parcel still exists: healthy

            float first;
            if (!FirstSeen.TryGetValue(zdo.m_uid, out first))
            {
                first = Time.unscaledTime;
                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[Mail] '{ContainerZdo.Name(mailbox)}' points at parcel {parcel}, which does not exist - " +
                        "waiting " + GraceSeconds.ToString("0") + " s before clearing the link.");
                }
            }

            if (Time.unscaledTime - first < GraceSeconds)
            {
                next[zdo.m_uid] = first;
                return false;
            }

            if (!view.IsOwner())
            {
                // The ward patch stops Quartermaster asking for ownership; it never grants it. So repair
                // the same way vanilla does when a player opens a chest: claim, then write. Scoped to
                // mailboxes near the player so a sweep never steals one from across the map.
                Player player = Player.m_localPlayer;
                float range = ClaimRange * ClaimRange;

                if (player != null && !player.IsDead() &&
                    (player.transform.position - mailbox.transform.position).sqrMagnitude <= range)
                {
                    view.ClaimOwnership();

                    if (view.IsOwner() && QIDebugger.Enabled)
                    {
                        QIDebugger.LogDebug($"[Mail] Claimed '{ContainerZdo.Name(mailbox)}' to repair its queued-mail link.");
                    }
                }
            }

            if (!view.IsOwner())
            {
                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[Mail] '{ContainerZdo.Name(mailbox)}' has a stale queued-mail link to {parcel} but the mailbox " +
                        "is not ours to write; leaving it alone.");
                }
                return false;
            }

            zdo.Set(LinkKey, ZDOID.None);

            if (QIDebugger.Enabled)
            {
                QIDebugger.LogDebug(
                    $"[Mail] Cleared a stale queued-mail link on '{ContainerZdo.Name(mailbox)}' - parcel {parcel} no " +
                    "longer exists (broken send or a restart between saves).");
            }

            return true;
        }

    }
}
