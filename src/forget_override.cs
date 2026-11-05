using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Quartermaster's Forget button does not forget: it removes the item from the chest's
    /// Remembered list but adds it to Forgotten, so the row stays on screen as "(ignored)" and
    /// Accepts() still refuses to store it. The chest then looks assigned while accepting nothing,
    /// which is easy to mistake for a routing failure.
    ///
    /// With ForgetRemovesEntry on, Forget deletes the entry from both lists instead: the row
    /// disappears from the chest's item list, and putting a sample back teaches the chest from
    /// scratch. Off, Quartermaster behaves exactly as shipped.
    ///
    /// Patch-only and filter-free: it never adds or moves items, and it changes nothing on a chest
    /// the player has not pressed Forget on.
    ///
    /// Config: `4. Patches -> ForgetRemovesEntry` (default off).
    /// </summary>
    public static class ForgetOverride
    {
        private static MethodInfo _forget;
        private static FieldInfo _remembered;
        private static FieldInfo _forgotten;

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            try
            {
                Type settings = BridgePlugin.QuartermasterAssembly?.GetType("Quartermaster.ChestSettings");
                if (settings == null)
                {
                    QIDebugger.LogError("[Forget] Quartermaster.ChestSettings not found");
                    return;
                }

                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _forget = AccessTools.Method(settings, "Forget", new[] { typeof(string) });
                _remembered = settings.GetField("Remembered", F);
                _forgotten = settings.GetField("Forgotten", F);

                if (_forget == null || _remembered == null || _forgotten == null)
                {
                    QIDebugger.LogError(
                        $"[Forget] Bindings incomplete: Forget={_forget != null}, " +
                        $"Remembered={_remembered != null}, Forgotten={_forgotten != null}");
                    return;
                }

                HarmonyPatchGuard.TryPatch(
                    harmony, _forget, "Quartermaster.ChestSettings.Forget",
                    prefix: new HarmonyMethod(typeof(ForgetOverride), nameof(Forget_Prefix)));

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        "[Forget] Remove-instead-of-ignore " +
                        (BridgePlugin.ForgetRemovesEntry != null && BridgePlugin.ForgetRemovesEntry.Value
                            ? "enabled: Forget deletes the entry from the chest."
                            : "disabled: Quartermaster's ignore list is kept."));
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Forget] Initialization failed", ex);
            }
        }

        /// <summary>Returns false to skip Quartermaster's own Remembered.Remove + Forgotten.Add.</summary>
        public static bool Forget_Prefix(object __instance, string type)
        {
            if (BridgePlugin.ForgetRemovesEntry == null || !BridgePlugin.ForgetRemovesEntry.Value) return true;
            if (__instance == null || string.IsNullOrEmpty(type)) return true;

            try
            {
                List<string> remembered = _remembered.GetValue(__instance) as List<string>;
                List<string> forgotten = _forgotten.GetValue(__instance) as List<string>;

                bool removedRemembered = remembered != null && remembered.Remove(type);
                bool removedForgotten = forgotten != null && forgotten.Remove(type);

                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[Forget] '{type}' removed from the chest " +
                        $"(remembered={removedRemembered}, forgotten={removedForgotten})");
                }

                return false;
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[Forget] Could not remove the entry; falling back to Quartermaster", ex);
                return true;
            }
        }
    }
}
