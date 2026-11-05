using System.Reflection;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// The container/ZDO questions every patch here asks, in one place.
    ///
    /// The subtle one is ownership. "The recorded owner is connected" is not the same as "a player is
    /// present": a dedicated server is itself a ZNetPeer with m_server = true and owns every ZDO it
    /// created or loaded, so matching the owner against every peer UID exempts the server and skips
    /// forever - after a restart that leaves containers unwritable while the log claims to be respecting
    /// an owner who is not there. Every owner test goes through OwnerIsConnectedPlayer, which excludes
    /// the server peer, and OwnerIsServer, which names it.
    /// </summary>
    internal static class ContainerZdo
    {
        /// <summary>The ZNetView Quartermaster uses, honouring a container's root override.</summary>
        internal static ZNetView View(Container c)
        {
            if (c == null) return null;
            if (c.m_rootObjectOverride != null) return c.m_rootObjectOverride;

            ZNetView view = c.GetComponent<ZNetView>();
            return view != null && view.IsValid() ? view : null;
        }

        /// <summary>The prefab name without Unity's "(Clone)" suffix, for log lines.</summary>
        internal static string Name(Container c)
        {
            return c == null ? "container" : c.gameObject.name.Replace("(Clone)", "");
        }

        internal static long Owner(ZNetView view)
        {
            return view == null || !view.IsValid() ? 0L : view.GetZDO().GetOwner();
        }

        /// <summary>True when the recorded owner is a real player peer - a dedicated server does not count.</summary>
        internal static bool OwnerIsConnectedPlayer(ZNetView view)
        {
            return MatchesPeer(view, serverPeer: false);
        }

        /// <summary>True when the recorded owner is the dedicated server session, which no player holds.</summary>
        internal static bool OwnerIsServer(ZNetView view)
        {
            return MatchesPeer(view, serverPeer: true);
        }

        private static bool MatchesPeer(ZNetView view, bool serverPeer)
        {
            long owner = Owner(view);
            if (owner == 0L) return false;

            ZNet net = ZNet.instance;
            if (net == null) return false;

            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer == null || peer.m_uid != owner) continue;
                if (peer.m_server == serverPeer) return true;
            }

            return false;
        }
    }
}
