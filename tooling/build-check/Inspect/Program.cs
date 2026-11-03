// Audits every reflection binding the mod performs at runtime against real assemblies.
// Valheim/BepInEx members are proven by the compiler; this checks the *string*-addressed targets.
using System.Reflection;

internal static class Program
{
    // Resolved against the working directory (repo root, locally and in CI) so the audit is not
    // tied to one machine's layout; override with QI_* when the refs live elsewhere.
    private static readonly string Managed = Env("QI_MANAGED", "refs/game/valheim_Data/Managed");
    private static readonly string Core = Env("QI_CORE", "refs/game/BepInEx/core");
    private static readonly string Mods = Env("QI_MODS", "refs/mods");
    private static readonly string Candidates = Env("QI_CANDIDATES", "refs/candidates");
    private static readonly string ModDll = Env("QI_MOD_DLL", "bin/Release/net48/QuarterMaster_Integrations.dll");

    private static string Env(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    /// <summary>
    /// Optional integrations live in refs/mods when CI fetched them and in refs/candidates when
    /// they are being evaluated locally; both are valid audit sources.
    /// </summary>
    private static string DepPath(string fileName)
    {
        string inMods = Path.Combine(Mods, fileName);
        return File.Exists(inMods) ? inMods : Path.Combine(Candidates, fileName);
    }

    private static int _fail;

    private static int Main()
    {
        string[] dirs = { Managed, Core, Mods, Candidates };
        var paths = dirs.Where(Directory.Exists)
                        .SelectMany(d => Directory.GetFiles(d, "*.dll"))
                        .GroupBy(Path.GetFileName)
                        .Select(g => g.First())
                        .ToList();

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));

        // ---- Valheim targets that the mod's attribute patches address ---------------------------
        var vh = mlc.LoadFromAssemblyPath(Path.Combine(Managed, "assembly_valheim.dll"));
        var ut = mlc.LoadFromAssemblyPath(Path.Combine(Managed, "assembly_utils.dll"));
        Method(vh, "InventoryGui", "SetupRequirement", new[] { "Transform", "Requirement", "Player", "Boolean", "Int32", "Int32" }, "Boolean", true);
        Method(vh, "InventoryGui", "UpdateRecipe", new[] { "Player", "Single" }, "Void", false);
        Method(vh, "InventoryGui", "Update", Array.Empty<string>(), "Void", false);
        Method(vh, "Inventory", "Changed", new[] { "Boolean", "Boolean" }, "Void", false);
        Method(vh, "Container", "Load", Array.Empty<string>(), "Boolean", false);
        Method(vh, "Container", "TakeAll", new[] { "Humanoid" }, "Boolean", false);
        Method(vh, "Player", "HaveRequirements", new[] { "Recipe", "Boolean", "Int32", "Int32" }, "Boolean", false);

        // Private InventoryGui state we address by name - a rename here fails silently (no patch
        // error, just a recipe that can no longer be resolved), so it has to be audited explicitly.
        Field(vh, "InventoryGui", "m_selectedRecipe", "RecipeDataPair", false);
        Field(vh, "InventoryGui", "m_craftTimer", "Single", false);
        Field(vh, "InventoryGui", "m_currentContainer", "Container", false);
        Field(vh, "InventoryGui", "m_craftButton", "Button", false);

        // ---- ExtraSlots 1.2.21 ---------------------------------------------------------------
        var es = Load(mlc, Path.Combine(Mods, "ExtraSlots.dll"));
        Method(es, "ExtraSlots.Slots", "IsGridPositionASlot", new[] { "Vector2i" }, "Boolean", true);
        Method(es, "ExtraSlots.Slots", "IsItemInSlot", new[] { "ItemData" }, "Boolean", true);
        Property(es, "ExtraSlots.Slots", "InventoryHeightPlayer", "Int32", true);

        // ---- MyLittleUI 1.2.32 ----------------------------------------------------------------
        var mlui = Load(mlc, Path.Combine(Mods, "MyLittleUI.dll"));
        Field(mlui, "MyLittleUI.MultiCraft", "amount", "Int32", true);

        // ---- AdventureBackpacks 2.4.6 ---------------------------------------------------------
        var ab = Load(mlc, Path.Combine(Mods, "AdventureBackpacks.dll"));
        Method(ab, "AdventureBackpacks.API.ABAPI", "IsBackpackOpen", Array.Empty<string>(), "Boolean", true);

        // ---- Quartermaster 0.1.65 (pack runs 0.1.66; nearest published build) -------------------
        var qm = Load(mlc, Path.Combine(Mods, "Quartermaster.dll"));
        Method(qm, "Quartermaster.WarehouseService", "CountAvailableNearPlayer", new[] { "String", "Int32", "Boolean" }, "Int32", true);
        Method(qm, "Quartermaster.ChestUi", "LayoutActions", Array.Empty<string>(), "Void", true);
        Method(qm, "Quartermaster.ChestUi", "SortChest", Array.Empty<string>(), "Void", true);
        Method(qm, "Quartermaster.ContainerRegistry", "Accessible",
            new[] { "Container" }, "Boolean", true);
        Method(qm, "Quartermaster.ContainerRegistry", "IsUsable",
            new[] { "Container", "Boolean" }, "Boolean", true);
        Method(qm, "Quartermaster.NativeStorageAccess", "CanWrite",
            new[] { "ZNetView" }, "Boolean", true);
        Method(qm, "Quartermaster.WarehouseService", "SortInventory", new[] { "Inventory", "Boolean", "Boolean" }, "Boolean", true);
        Method(qm, "Quartermaster.WarehouseService", "StoreAllInOpenChest", new[] { "Player", "Container" }, "Int32", true);
        Method(qm, "Quartermaster.ExternalSlotCompatibility", "IsProtectedPlayerSlot", new[] { "Int32", "Int32" }, "Boolean", true);
        Method(qm, "Quartermaster.ExternalSlotCompatibility", "IsProtectedPlayerSlot", new[] { "ItemData" }, "Boolean", true);

        // ---- AAA Crafting 2.1.8 (pack runs 2.1.12; nearest published build) --------------------
        var aaa = Load(mlc, Path.Combine(Mods, "AzuAntiArthriticCrafting.dll"));
        Field(aaa, "AzuAntiArthriticCrafting.AzuAntiArthriticCraftingPlugin", "CfcLoaded", "Boolean", true);
        Field(aaa, "AzuAntiArthriticCrafting.MaxCraftAmount", "InventoryChanged", "Boolean", true);
        Field(aaa, "AzuAntiArthriticCrafting.Patches.AAACraft", "amount", "Int32", true);
        Field(aaa, "AzuAntiArthriticCrafting.Patches.AAACraft", "inputAmount", "TMP_InputField", true);
        Field(aaa, "AzuAntiArthriticCrafting.Patches.AAACraft", "btnMinus", "Button", true);
        Field(aaa, "AzuAntiArthriticCrafting.Patches.AAACraft", "btnPlus", "Button", true);
        Method(aaa, "AzuAntiArthriticCrafting.Patches.AAACraft", "CalculateMaxCraftAmount", Array.Empty<string>(), "Int32", true);
        Method(aaa, "AzuAntiArthriticCrafting.Utilities.Utilities", "CalculateMaxCraftAmount", Array.Empty<string>(), "Int32", true);
        Method(aaa, "AzuAntiArthriticCrafting.Utilities.Utilities", "GetItemsInCloseContainers", new[] { "String" }, "Int32", true);
        Method(aaa, "AzuAntiArthriticCrafting.Patches.RecipeListPerfCache", "Invalidate", Array.Empty<string>(), "Void", true);

        // ---- AAABuildMenu 1.0.6 ----------------------------------------------------------------
        var abm = Load(mlc, DepPath("AAABuildMenu.dll"));
        Method(abm, "AAABuildMenu.PinnedRecipeTracker", "CountItem", new[] { "Inventory", "String" }, "Int32", true);

        // ---- Quartermaster storage surface the new integrations walk ----------------------------
        Method(qm, "Quartermaster.WarehouseService", "CraftStores", new[] { "Player", "Boolean" }, "IEnumerable`1", true);
        Method(qm, "Quartermaster.InventoryTransfers", "AvailableInContainer", new[] { "Container", "String", "Int32", "Boolean" }, "Int32", true);

        // ---- EpicLoot 0.15.5 --------------------------------------------------------------------
        var el = Load(mlc, DepPath("EpicLoot.dll"));
        MethodExact(el, "EpicLoot.API", "RegisterInventoryProvider", new[]
        {
            "System.String",
            "System.Func`1[System.Collections.Generic.List`1[ItemDrop+ItemData]]",
            "System.Func`2[System.String,System.Int32]",
            "System.Func`3[System.String,System.Int32,System.Int32]",
            "System.Func`3[ItemDrop+ItemData,System.Int32,System.Int32]"
        }, "Boolean", true);
        Method(el, "EpicLoot.API", "UnregisterInventoryProvider", new[] { "String" }, "Boolean", true);

        // ---- PlanBuild 0.20.0 -------------------------------------------------------------------
        var pb = Load(mlc, DepPath("PlanBuild.dll"));
        Method(pb, "PlanBuild.Plans.PlanPiece", "GetInventories", new[] { "Humanoid" }, "List`1", false);
        NestedInterface(pb, "PlanBuild.Plans.PlanPiece", "IInventory", new[]
        {
            "CountItems(String):Int32",
            "RemoveItem(String,Int32):Void",
            "HaveItem(String):Boolean"
        });

        // ---- WardIsLove 4.0.9 -------------------------------------------------------------------
        var wil = Load(mlc, DepPath("WardIsLove.dll"));
        Method(wil, "WardIsLove.Util.CustomCheck", "CheckAccess", new[] { "Int64", "Vector3", "Single", "Boolean" }, "Boolean", true);

        // ---- DropMoreLoot stack deferral -------------------------------------------------------
        var dml = Load(mlc, DepPath("DropMoreLoot.dll"));
        Field(dml, "DropMoreLoot.DropMoreLootMain", "enableStacking", "ConfigEntry`1", true);
        Field(dml, "DropMoreLoot.DropMoreLootMain", "itemStackMultiplier", "ConfigEntry`1", true);
        Method(qm, "Quartermaster.ItemStacks", "Configure", new[] { "Boolean", "Int32" }, "Void", true);
        Method(qm, "Quartermaster.ChestUi", "StackBody", new string[0], "Void", true);
        Field(qm, "Quartermaster.ChestUi", "body", "RectTransform", true);

        Field(qm, "Quartermaster.Plugin", "Range", "ConfigEntry`1", true);

        // ---- WardIsLove host range: every Quartermaster/WIL member the filter relies on ----------
        Method(vh, "InventoryGui", "DoCrafting", new[] { "Player" }, "Void", false);
        Method(vh, "InventoryGui", "UpdateCraftingPanel", new[] { "Boolean" }, "Void", false);
        Method(qm, "Quartermaster.ChestOwnership", "Request",
            new[] { "ZNetView", "String", "Boolean" }, "Boolean", true);
        Method(qm, "Quartermaster.BaseNetwork", "Contains", new[] { "Vector3" }, "Boolean", false);
        Method(qm, "Quartermaster.BaseNetwork", "CanTransfer", new[] { "Vector3", "Vector3" }, "Boolean", false);
        Field(qm, "Quartermaster.BaseNetwork", "Hubs", "List`1", false);
        Field(qm, "Quartermaster.BaseNetwork", "Chests", "List`1", false);
        Field(qm, "Quartermaster.BaseNetwork", "Machines", "List`1", false);
        Method(qm, "Quartermaster.Automation", "RebuildNetworks", new string[0], "Void", true);
        Field(qm, "Quartermaster.Automation", "networks", "List`1", true);
        Method(qm, "Quartermaster.CraftStorageAccess", "Nearby", new[] { "Player" }, "IEnumerable`1", true);
        Method(qm, "Quartermaster.CraftStorageAccess", "CanSupply", new[] { "Container" }, "Boolean", true);
        Method(qm, "Quartermaster.InventoryTransfers", "Stackable", new[] { "ItemData", "ItemData" }, "Boolean", true);
        Method(qm, "Quartermaster.Automation", "Destinations",
            new[] { "BaseNetwork", "ItemData", "Vector3", "Container", "Boolean" }, "List`1", true);
        Method(qm, "Quartermaster.Automation", "ExplainUnsorted",
            new[] { "BaseNetwork", "Container", "ItemData" }, "String", true);
        Method(qm, "Quartermaster.Automation", "Writable",
            new[] { "Container", "ItemData", "String" }, "Boolean", true);
        Method(qm, "Quartermaster.FurnitureAssignment", "Accepts",
            new[] { "Container", "ItemData" }, "Boolean", true);
        Method(qm, "Quartermaster.InventoryTransfers", "ItemId", new[] { "ItemData" }, "String", true);
        Method(qm, "Quartermaster.WarehouseService", "SortInventory",
            new[] { "Inventory", "Boolean", "Boolean" }, "Boolean", true);
        Method(qm, "Quartermaster.InventoryTransfers", "StackWithin",
            new[] { "Inventory" }, "Void", true);
        Field(qm, "Quartermaster.ChestSettings", "Remembered", "List`1", false);
        Field(qm, "Quartermaster.ChestSettings", "Forgotten", "List`1", false);
        Field(qm, "Quartermaster.ChestSettings", "AcceptStorage", "Boolean", false);
        Field(qm, "Quartermaster.ChestSettings", "Deposit", "Boolean", false);
        Field(qm, "Quartermaster.ChestSettings", "Overflow", "Boolean", false);
        Field(qm, "Quartermaster.ChestSettings", "AutoAssign", "Boolean", false);
        Method(qm, "Quartermaster.ChestSettings", "Forget", new[] { "String" }, "Void", false);
        Method(qm, "Quartermaster.SharedCrafting", "SourcesValid", new[] { "Player", "Container[]" }, "Boolean", true);
        Field(qm, "Quartermaster.ContainerRegistry", "All", "List`1", true);
        Method(qm, "Quartermaster.ContainerRegistry", "GetSettings", new[] { "Container" }, "ChestSettings", true);
        Field(qm, "Quartermaster.Plugin", "CraftRange", "ConfigEntry`1", true);
        Field(wil, "WardIsLove.Util.WardMonoscript", "m_wardInstances", "List`1", true);
        Method(wil, "WardIsLove.Util.WardMonoscript", "IsEnabled", new string[0], "Boolean", false);
        Method(wil, "WardIsLove.Extensions.WardMonoscriptExt", "GetWardRadius", new[] { "WardMonoscript" }, "Single", true);
        Method(ut, "Utils", "DistanceXZ", new[] { "Vector3", "Vector3" }, "Single", true);

        Console.WriteLine("\n### versions audited");
        Console.WriteLine("  Quartermaster 0.1.66, AAA Crafting 2.1.12, MyLittleUI 1.2.33,");
        Console.WriteLine("  ExtraSlots 1.2.21, AdventureBackpacks 2.4.6  (exact deployed builds, from Hexium)");
        Console.WriteLine("  AAABuildMenu 1.0.6  (Hexium only - not on Thunderstore)");
        Console.WriteLine("  EpicLoot 0.15.5, PlanBuild 0.20.0, WardIsLove 4.0.9  (candidates)");

        Console.WriteLine("\n### built mod assembly");
        if (File.Exists(ModDll))
        {
            var modAsm = mlc.LoadFromAssemblyPath(ModDll);
            var v = modAsm.GetName().Version;
            Console.WriteLine($"  {modAsm.GetName().Name} {v}  (toString(3) = {v?.ToString(3)})");
        }
        else
        {
            Console.WriteLine("  not built yet");
        }

        Console.WriteLine(_fail == 0 ? "\nALL AUDITED BINDINGS OK" : $"\n{_fail} BINDING(S) FAILED");
        return _fail == 0 ? 0 : 1;
    }

    private static Assembly Load(MetadataLoadContext mlc, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"### MISSING ASSEMBLY {path}");
            _fail++;
            return null;
        }

        try
        {
            return mlc.LoadFromAssemblyPath(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"### could not load {Path.GetFileName(path)}: {ex.Message}");
            _fail++;
            return null;
        }
    }

    private static void Method(Assembly asm, string typeName, string method, string[] paramTypeNames, string returnType, bool expectStatic)
    {
        if (asm == null) return;

        Type t = asm.GetType(typeName);
        if (t == null)
        {
            Report(false, $"{typeName}.{method}", "type not found");
            return;
        }

        MethodInfo m = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(x => x.Name == method &&
                                             x.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(paramTypeNames) &&
                                             x.ReturnType.Name == returnType &&
                                             x.IsStatic == expectStatic);

        if (m == null)
        {
            var near = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(x => x.Name == method)
                        .Select(x => $"{(x.IsStatic ? "static " : "")}({string.Join(",", x.GetParameters().Select(p => p.ParameterType.Name))}):{x.ReturnType.Name}");
            Report(false, $"{typeName}.{method}", "no matching overload; found: " + string.Join(" | ", near));
            return;
        }

        Report(true, $"{typeName}.{method}({string.Join(",", paramTypeNames)}):{returnType}{(expectStatic ? " [static]" : "")}", null);
    }

    private static void Field(Assembly asm, string typeName, string field, string fieldType, bool expectStatic)
    {
        if (asm == null) return;

        Type t = asm.GetType(typeName);
        FieldInfo f = t?.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                       .FirstOrDefault(x => x.Name == field && x.FieldType.Name == fieldType && x.IsStatic == expectStatic);

        if (f == null)
        {
            string actual = t == null
                ? "type not found"
                : string.Join(" | ", t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                      .Where(x => x.Name == field)
                                      .Select(x => $"{x.FieldType.Name} {(x.IsStatic ? "static" : "instance")}"));
            Report(false, $"{typeName}.{field}:{fieldType}", string.IsNullOrEmpty(actual) ? "field not found" : "found: " + actual);
            return;
        }

        Report(true, $"{typeName}.{field}:{fieldType} [{(expectStatic ? "static" : "instance")}]", null);
    }

    private static void Property(Assembly asm, string typeName, string prop, string propType, bool expectStatic)
    {
        if (asm == null) return;

        Type t = asm.GetType(typeName);
        MethodInfo g = t?.GetProperty(prop, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);

        if (g == null || g.ReturnType.Name != propType || g.IsStatic != expectStatic)
        {
            Report(false, $"{typeName}.{prop}:{propType}", g == null
                ? "property/getter not found"
                : $"getter returns {g.ReturnType.Name} {(g.IsStatic ? "static" : "instance")}");
            return;
        }

        Report(true, $"{typeName}.{prop}:{propType} [{(expectStatic ? "static" : "instance")} getter]", null);
    }

    /// <summary>
    /// Like <see cref="Method"/>, but compares the full parameter type text, so generic arguments
    /// (Func&lt;List&lt;ItemData&gt;&gt; vs Func&lt;List&lt;object&gt;&gt;) are audited rather than just the arity.
    /// </summary>
    private static void MethodExact(Assembly asm, string typeName, string method, string[] paramTypes, string returnType, bool expectStatic)
    {
        if (asm == null) return;

        Type t = asm.GetType(typeName);
        if (t == null)
        {
            Report(false, $"{typeName}.{method}", "type not found");
            return;
        }

        static string Norm(string s) => s.Replace(" ", string.Empty);
        string[] want = paramTypes.Select(Norm).ToArray();

        MethodInfo m = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(x => x.Name == method &&
                                             x.ReturnType.Name == returnType &&
                                             x.IsStatic == expectStatic &&
                                             x.GetParameters().Select(p => Norm(p.ParameterType.ToString())).SequenceEqual(want));

        if (m == null)
        {
            var near = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(x => x.Name == method)
                        .Select(x => $"{(x.IsStatic ? "static " : "")}({string.Join(",", x.GetParameters().Select(p => p.ParameterType.ToString()))}):{x.ReturnType.Name}");
            Report(false, $"{typeName}.{method}({string.Join(",", paramTypes)}):{returnType}", "no exact overload; found: " + string.Join(" | ", near));
            return;
        }

        Report(true, $"{typeName}.{method}({string.Join(",", paramTypes)}):{returnType}{(expectStatic ? " [static]" : "")}", null);
    }

    /// <summary>
    /// Proves a nested interface still declares the exact members an adapter implements. A proxy
    /// created against a renamed member compiles fine and silently answers nothing.
    /// </summary>
    private static void NestedInterface(Assembly asm, string typeName, string nestedName, string[] expected)
    {
        Type outer = asm?.GetType(typeName);
        Type nested = outer?.GetNestedType(nestedName, BindingFlags.Public | BindingFlags.NonPublic);

        if (nested == null || !nested.IsInterface)
        {
            Report(false, $"{typeName}+{nestedName}", nested == null ? "nested type not found" : "not an interface");
            return;
        }

        var actual = nested.GetMethods()
            .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))}):{m.ReturnType.Name}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        var want = expected.OrderBy(s => s, StringComparer.Ordinal).ToArray();

        bool ok = actual.SequenceEqual(want);
        Report(ok, $"{typeName}+{nestedName} ({string.Join("; ", want)})", ok ? null : "found: " + string.Join(" | ", actual));
    }

    private static void Report(bool ok, string what, string detail)
    {
        if (!ok) _fail++;
        Console.WriteLine(ok ? $"  OK    {what}" : $"  FAIL  {what}  ->  {detail}");
    }
}
