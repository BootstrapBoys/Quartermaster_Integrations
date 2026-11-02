using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// All of this mod's diagnostics live here: the debug log file, the log level plumbing, and the
    /// inspection dumps that make an integration testable without a debugger.
    ///
    /// Everything is inert unless DebugMode is on, except LogError, which always reaches the BepInEx
    /// console and is mirrored to the debug file when one has been initialised.
    /// </summary>
    public static class QIDebugger
    {
        /// <summary>DebugMode, with a not-yet-bound config treated as off.</summary>
        public static bool Enabled => BridgePlugin.DebugMode != null && BridgePlugin.DebugMode.Value;

        private static string _logFilePath;
        private static readonly object LogLock = new object();

        private static Recipe _lastSnapshotRecipe;
        private static int _lastSnapshotMultiplier = -1;

        /// <summary>
        /// Creates BepInEx/BSBLogs and starts this plugin's own log file, named after the plugin, so
        /// every Bootstrap Boys mod writes its diagnostics to one predictable place. The header
        /// stamps the build, and the file is rewritten per launch (matching BepInEx's own
        /// LogOutput.log) so it cannot grow across sessions.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                string logDir = Path.Combine(Paths.BepInExRootPath, "BSBLogs");
                Directory.CreateDirectory(logDir);   // no-op when it already exists

                _logFilePath = Path.Combine(logDir, SafeFileName(LogFileStem()) + ".log");

                File.WriteAllText(
                    _logFilePath,
                    $"--- {BridgePlugin.PluginName} v{BridgePlugin.PluginVersion} [{DateTime.Now:O}] ---" +
                    Environment.NewLine);
            }
            catch (Exception ex)
            {
                BridgePlugin.Log?.LogWarning($"Failed to initialize BSBLogs debug log file: {ex.Message}");
            }
        }

        /// <summary>
        /// The assembly name (QuarterMaster_Integrations) rather than the display name, so the log
        /// matches the DLL on disk and needs no separator mangling.
        /// </summary>
        private static string LogFileStem()
        {
            try
            {
                string name = typeof(QIDebugger).Assembly.GetName().Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch
            {
                // Fall through to the display name rather than risking a throw during startup.
            }

            return BridgePlugin.PluginName.Replace(' ', '_');
        }

        /// <summary>
        /// The plugin name is a display string, so strip anything a filesystem would reject rather
        /// than trusting it to be a legal file name.
        /// </summary>
        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Quartermaster Integrations";

            char[] chars = name.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] != invalid[j]) continue;
                    chars[i] = '_';
                    break;
                }
            }

            return new string(chars);
        }

        public static void LogDebug(string message, Exception ex = null)
        {
            if (!Enabled) return;

            string formatted = FormatLine("DEBUG", message, ex);
            BridgePlugin.Log?.LogInfo(formatted);
            AppendToDebugFile(formatted);
        }

        public static void LogError(string message, Exception ex = null)
        {
            string formatted = FormatLine("ERROR", message, ex);
            BridgePlugin.Log?.LogError(formatted);
            AppendToDebugFile(formatted);
        }

        private static string FormatLine(string level, string message, Exception ex)
        {
            string formatted = $"[{level}] [{DateTime.Now:HH:mm:ss.fff}] {message}";
            if (ex != null)
            {
                formatted += $"\nException: {ex.Message}\n{ex.StackTrace}";
            }

            return formatted;
        }

        private static void AppendToDebugFile(string line)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(_logFilePath, line + "\n");
                }
            }
            catch
            {
                // Losing a log line must never take the game down; the console copy above already
                // carries the message.
            }
        }

        /// <summary>
        /// The methods this mod patches, with the signature the game actually ships, so a changed
        /// signature is visible instead of presenting as a feature that silently stopped working.
        /// </summary>
        public static void LogUiSignatures()
        {
            if (!Enabled) return;

            try
            {
                LogSignature(typeof(InventoryGui), "SetupRequirement");
                LogSignature(typeof(InventoryGui), "UpdateRecipe");
                LogSignature(typeof(InventoryGui), "Update");
            }
            catch (Exception ex)
            {
                LogDebug("Failed to log UI method signatures", ex);
            }
        }

        private static void LogSignature(Type type, string methodName)
        {
            try
            {
                LogSignatureCore(type, methodName);
            }
            catch (Exception ex)
            {
                LogDebug($"[Signature] Failed reading {type.Name}.{methodName}", ex);
            }
        }

        private static void LogSignatureCore(Type type, string methodName)
        {
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;

                ParameterInfo[] parameters = method.GetParameters();
                string[] names = new string[parameters.Length];
                for (int p = 0; p < parameters.Length; p++)
                {
                    names[p] = $"{parameters[p].ParameterType.Name} {parameters[p].Name}";
                }

                LogDebug($"[Signature] InventoryGui.{methodName}({string.Join(", ", names)}) : {method.ReturnType.Name}");
            }
        }

        /// <summary>
        /// Debug-only: every method this mod has patched, with patch counts. A patch that failed to
        /// bind is simply absent, which turns "is that integration live?" into a lookup instead of
        /// an inference from gameplay.
        /// </summary>
        public static void LogPatchInventory()
        {
            if (!Enabled) return;

            try
            {
                int owned = 0;
                foreach (MethodBase method in Harmony.GetAllPatchedMethods())
                {
                    Patches info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;

                    int prefix = CountOwned(info.Prefixes);
                    int postfix = CountOwned(info.Postfixes);
                    int transpiler = CountOwned(info.Transpilers);
                    int finalizer = CountOwned(info.Finalizers);
                    if (prefix + postfix + transpiler + finalizer == 0) continue;

                    owned++;

                    // Include parameter types: overloads otherwise produce identical lines
                    // (IsProtectedPlayerSlot(int,int) and (ItemData) looked the same in the log).
                    ParameterInfo[] parameters = method.GetParameters();
                    string[] parameterTypes = new string[parameters.Length];
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        parameterTypes[p] = parameters[p].ParameterType.Name;
                    }

                    LogDebug(
                        $"[Patches] {method.DeclaringType?.Name}.{method.Name}({string.Join(",", parameterTypes)}) <- " +
                        $"prefix {prefix}, postfix {postfix}, transpiler {transpiler}, finalizer {finalizer}");
                }

                LogDebug($"[Patches] {owned} method(s) patched by {BridgePlugin.PluginGUID}.");
            }
            catch (Exception ex)
            {
                LogDebug("[Patches] Failed to enumerate patched methods", ex);
            }
        }

        private static int CountOwned(IList<Patch> patches)
        {
            if (patches == null) return 0;

            int count = 0;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch != null && string.Equals(patch.owner, BridgePlugin.PluginGUID, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Debug-only: the settings that change behaviour, so a log can be read without also asking
        /// for the user's config file.
        /// </summary>
        public static void LogConfigSnapshot()
        {
            if (!Enabled) return;

            LogDebug(
                $"[Config] DisplayMode: {BridgePlugin.DisplayMode?.Value}, " +
                $"DeferToOtherFormatting: {BridgePlugin.DeferToOtherFormatting?.Value}, " +
                $"FlashSpeed: {BridgePlugin.FlashSpeed?.Value}, " +
                $"SufficientColor: #{BridgePlugin.SufficientColorHex}, " +
                $"BatchLimit: {MultiCraftBridgeManager.GetBatchLimit()}");
        }

        /// <summary>
        /// Debug-only: which optional integrations bound, what each one hooks, and which
        /// Quartermaster entry points are available. A feature that silently does nothing is almost
        /// always a `false` here, so this is the first block to read in a submitted log.
        /// </summary>
        public static void LogIntegrationSnapshot()
        {
            if (!Enabled) return;

            LogDebug(
                "[Integrations] " +
                $"EpicLoot: {EpicLootIntegration.IsReady} (provider '{EpicLootIntegration.ProviderId}'), " +
                $"PlanBuild: {PlanBuildIntegration.IsReady} (GetInventories adapter), " +
                $"WardIsLove: {WardIsLoveIntegration.IsReady} (ward filter), " +
                $"AAABuildMenu: {AAABuildMenuIntegration.IsReady}, " +
                $"AAA: {AAACraftingIntegration.IsReady}, " +
                $"ExtraSlots: {ExtraSlotsIntegration.IsReady}, " +
                $"Backpacks: {AdventureBackpacksIntegration.IsReady}, " +
                $"MLUI: {MLUIIntegration.IsReady}, " +
                $"DropMoreLoot: {DropMoreLootIntegration.IsReady}");

            LogDebug(
                "[QM] Bindings: " +
                $"CountAvailableNearPlayer={BridgePlugin.HasStorageCountBinding}, " +
                $"CraftStores={BridgePlugin.HasStorageContainerBinding}");

            LogDebug(
                "[WardHostRange] " +
                $"Toggle={BridgePlugin.WardHostRange?.Value}, " +
                $"Active={WardHostRange.Active}, Bound={WardHostRange.IsReady}" +
                (string.IsNullOrEmpty(WardHostRange.BindError) ? string.Empty : $", Error={WardHostRange.BindError}"));
        }

        /// <summary>
        /// Debug-only: one dump per recipe selection or craft-amount change, showing how each
        /// requirement row got its number. This is the log that answers "why does this row read 3
        /// when my chest has 40".
        /// </summary>
        public static void LogCraftingSnapshot(Recipe recipe, Player player, int multiplier, bool upgrading)
        {
            if (!Enabled || recipe == null || player == null) return;

            if (ReferenceEquals(recipe, _lastSnapshotRecipe) && multiplier == _lastSnapshotMultiplier) return;
            _lastSnapshotRecipe = recipe;
            _lastSnapshotMultiplier = multiplier;

            try
            {
                string name = recipe.m_item?.m_itemData?.m_shared?.m_name ?? "?";

                LogDebug($"[Craft] '{name}' {(upgrading ? "upgrade" : "x" + multiplier.ToString(CultureInfo.InvariantCulture))}");
                if (recipe.m_resources == null) return;

                for (int i = 0; i < recipe.m_resources.Length; i++)
                {
                    Piece.Requirement req = recipe.m_resources[i];
                    if (req?.m_resItem == null) continue;

                    string itemName = req.m_resItem.m_itemData?.m_shared?.m_name;
                    if (string.IsNullOrEmpty(itemName)) continue;

                    int need = req.GetAmount(1);
                    BridgePlugin.GetItemCounts(player, itemName, out int pocket, out int storage, out int total);

                    LogDebug(
                        $"[Craft]   {itemName}: need {need} | pockets {pocket} + storage {storage} = {total} | " +
                        $"enough {total >= need}");
                }
            }
            catch (Exception ex)
            {
                LogDebug("[Craft] Failed to snapshot the selected recipe", ex);
            }
        }
    }
}
