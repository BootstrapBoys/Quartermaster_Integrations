using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// Lets DropMoreLoot own stack sizes instead of fighting it.
    ///
    /// DropMoreLoot multiplies `SharedData.m_maxStackSize` in `ItemDrop.Awake`; Quartermaster overrides
    /// the same field through ItemStacks.Configure/Apply with its own absolute MaximumStackSize, so
    /// whichever runs last wins and DropMoreLoot's multiplier is erased in practice.
    ///
    /// With `DeferToDropMoreLootStacks` on (default) and DropMoreLoot's own stacking enabled,
    /// Quartermaster is told its stack override is off, so DropMoreLoot's multiplied value stands.
    /// The ledger's stack tab then disables its "Use custom stack sizes" toggle and names DropMoreLoot
    /// on the button itself, so the tab cannot claim a maximum that is not being applied.
    ///
    /// This is display/config only: no item, recipe or inventory amounts are touched here.
    /// </summary>
    public static class DropMoreLootIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnableDropMoreLoot);
        public static string BindError { get; private set; }

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _multiplier;
        private static FieldInfo _bodyField;

        private const string CustomToggle = "Use custom stack sizes";
        private const string NormalToggle = "Use normal stack sizes";
        private const string SaveButton = "Save stack sizes";

        /// <summary>True when DropMoreLoot is present, stacking is on, and the toggle says defer.</summary>
        public static bool Active
        {
            get
            {
                if (!IsReady || !BridgePlugin.Enabled) return false;
                if (BridgePlugin.DeferToDropMoreLootStacks == null || !BridgePlugin.DeferToDropMoreLootStacks.Value) return false;

                try
                {
                    return _enabled != null && _enabled.Value;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static int Multiplier
        {
            get
            {
                try
                {
                    return _multiplier != null ? _multiplier.Value : 1;
                }
                catch
                {
                    return 1;
                }
            }
        }

        public static void Initialize(Harmony harmony)
        {
            if (!BridgePlugin.Enabled) return;

            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.DropMoreLootGUID, out PluginInfo info) ||
                info?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly assembly = info.Instance.GetType().Assembly;
                Type main = assembly.GetType("DropMoreLoot.DropMoreLootMain");

                _enabled = AccessTools.Field(main, "enableStacking")?.GetValue(null) as ConfigEntry<bool>;
                _multiplier = AccessTools.Field(main, "itemStackMultiplier")?.GetValue(null) as ConfigEntry<int>;

                Assembly qm = BridgePlugin.QuartermasterAssembly;
                Type itemStacks = qm?.GetType("Quartermaster.ItemStacks");
                Type chestUi = qm?.GetType("Quartermaster.ChestUi");

                MethodInfo configure = itemStacks == null
                    ? null
                    : AccessTools.Method(itemStacks, "Configure", new[] { typeof(bool), typeof(int) });

                MethodInfo stackBody = chestUi == null
                    ? null
                    : AccessTools.Method(chestUi, "StackBody", Type.EmptyTypes);

                _bodyField = chestUi?.GetField("body", BindingFlags.Static | BindingFlags.NonPublic);

                if (_enabled == null || _multiplier == null || configure == null || stackBody == null ||
                    _bodyField == null)
                {
                    BindError =
                        $"enableStacking={_enabled != null}, itemStackMultiplier={_multiplier != null}, " +
                        $"ItemStacks.Configure={configure != null}, ChestUi.StackBody={stackBody != null}, " +
                        $"body={_bodyField != null}";

                    QIDebugger.LogError("[DropMoreLoot] Bindings incomplete: " + BindError);
                    return;
                }

                bool stackPatch = HarmonyPatchGuard.TryPatch(
                    harmony, configure, "Quartermaster.ItemStacks.Configure",
                    prefix: new HarmonyMethod(typeof(DropMoreLootIntegration), nameof(Configure_Prefix)));

                bool tabPatch = HarmonyPatchGuard.TryPatch(
                    harmony, stackBody, "Quartermaster.ChestUi.StackBody",
                    postfix: new HarmonyMethod(typeof(DropMoreLootIntegration), nameof(StackBody_Postfix)));

                _ready = stackPatch && tabPatch;

                if (_ready && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[DropMoreLoot] Stack sizes defer to DropMoreLoot (stacking={_enabled.Value}, x{_multiplier.Value}); " +
                        $"toggle is {(BridgePlugin.DeferToDropMoreLootStacks.Value ? "on" : "off")}.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("[DropMoreLoot] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Forces Quartermaster's stack override off while DropMoreLoot owns stacking, so its
        /// multiplied max-stack value is the one that survives.
        /// </summary>
        public static void Configure_Prefix(ref bool active)
        {
            if (Active) active = false;
        }

        /// <summary>
        /// Runs after the ledger builds its stack tab: disables the custom/normal stack toggle, puts
        /// DropMoreLoot's name on it, and disables the maximum field and Save button, which would
        /// otherwise appear to change a setting Quartermaster is not applying. Component access is by
        /// reflection so this needs neither a TextMeshPro nor a UnityEngine.UI reference.
        /// </summary>
        public static void StackBody_Postfix()
        {
            if (!Active) return;

            try
            {
                if (!(_bodyField.GetValue(null) is Transform body) || body == null) return;

                string notice = $"Stack sizes are managed by DropMoreLoot (x{Multiplier})";
                int changed = 0;

                foreach (Component component in body.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;

                    Type type = component.GetType();

                    if (type.Name.IndexOf("InputField", StringComparison.Ordinal) >= 0)
                    {
                        if (SetInteractable(component, false)) changed++;
                        continue;
                    }

                    PropertyInfo textProperty = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    if (textProperty == null || textProperty.PropertyType != typeof(string)) continue;

                    string label = textProperty.GetValue(component, null) as string;
                    if (label == null) continue;

                    bool toggle = label == CustomToggle || label == NormalToggle;
                    if (!toggle && label != SaveButton) continue;

                    if (toggle)
                    {
                        try
                        {
                            textProperty.SetValue(component, notice, null);
                        }
                        catch
                        {
                            // Cosmetic only; still disable the control below.
                        }
                    }

                    for (Transform parent = component.transform; parent != null; parent = parent.parent)
                    {
                        if (SetInteractable(parent.gameObject, false)) break;
                        if (parent == body) break;
                    }

                    changed++;
                }

                if (changed > 0 && QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug(
                        $"[DropMoreLoot] Ledger stack tab: {changed} control(s) disabled, toggle now reads '{notice}'.");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[DropMoreLoot] Could not adjust the ledger stack tab", ex);
            }
        }

        private static bool SetInteractable(GameObject target, bool value)
        {
            foreach (Component component in target.GetComponents<Component>())
            {
                if (SetInteractable(component, value)) return true;
            }

            return false;
        }

        private static bool SetInteractable(Component component, bool value)
        {
            PropertyInfo interactable = component == null
                ? null
                : component.GetType().GetProperty("interactable", BindingFlags.Instance | BindingFlags.Public);

            if (interactable == null || interactable.PropertyType != typeof(bool) || !interactable.CanWrite) return false;

            try
            {
                interactable.SetValue(component, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
