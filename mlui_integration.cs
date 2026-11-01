using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    public static class MLUIIntegration
    {
        private static bool _ready;

        /// <summary>False while its switch under 2. Debug is off, so turning it off applies at once.</summary>
        public static bool IsReady => _ready && BridgePlugin.IntegrationOn(BridgePlugin.EnableMyLittleUI);

        private static FieldInfo _mluiAmountField;

        public static void Initialize()
        {
            if (!Chainloader.PluginInfos.TryGetValue(BridgePlugin.MLUIGUID, out PluginInfo mluiInfo) ||
                mluiInfo?.Instance == null)
            {
                return;
            }

            try
            {
                Assembly mluiAssembly = mluiInfo.Instance.GetType().Assembly;
                Type mluiCraftType = mluiAssembly.GetType("MyLittleUI.MultiCraft");
                if (mluiCraftType != null)
                {
                    _mluiAmountField = AccessTools.Field(mluiCraftType, "amount") ??
                                       mluiCraftType.GetField("amount", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }

                _ready = _mluiAmountField != null;
                if (QIDebugger.Enabled)
                {
                    QIDebugger.LogDebug($"MLUIIntegration initialized. Ready: {_ready}");
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogError("Failed to initialize MLUIIntegration", ex);
            }
        }

        public static bool TryGetCraftAmount(out int amount)
        {
            amount = 1;
            if (!IsReady || _mluiAmountField == null) return false;

            try
            {
                int val = (int)_mluiAmountField.GetValue(null);
                if (val > 0)
                {
                    amount = val;
                    return true;
                }
            }
            catch (Exception ex)
            {
                QIDebugger.LogDebug("[MLUI] Failed reading MultiCraft.amount", ex);
            }

            return false;
        }
    }
}
