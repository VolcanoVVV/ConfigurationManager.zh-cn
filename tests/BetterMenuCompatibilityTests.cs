using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ConfigurationManagerZhCn;
using HarmonyLib;
using Mono.Cecil;

internal static partial class Program
{
    private static void CheckBetterMenuCompatibility(string assemblyPath)
    {
        const string pluginName = "BetterBepInEx.BetterBepInExPlugin";
        const string menuName = "BetterBepInEx.MenuWindow";
        var installed = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var plugin = installed.GetType(pluginName, true);
        var notes = new List<string>();
        Check(BetterBepInExCompatibility.ResolveActiveGetter(plugin).ReturnType == typeof(bool), "BetterBepInEx activation can be observed");
        var plan = BetterBepInExCompatibility.ResolveHooks(plugin, notes.Add, out var rebuild);
        Check(plan.Count > 10 && rebuild.Name == "RebuildModel" && notes.Count == 0,
            "all hooks match the installed BetterBepInEx: " + string.Join(", ", notes));

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var menu = installed.GetType(menuName, true);
        var adapter = typeof(BetterBepInExCompatibility);
        foreach (var hook in new[]
        {
            (Target: "OnGUI", Patch: "DrawWorkbench", Inserted: "DrawWorkbenchWindow"),
            (Target: "SidebarPage", Patch: "CapturePlugin", Inserted: "CapturePluginLabel"),
            (Target: "HandleEscape", Patch: "HandleWorkbenchEscape", Inserted: "KeepMenuOpen")
        })
        {
            var target = menu.GetMethod(hook.Target, flags);
            var transpiler = adapter.GetMethod(hook.Patch, BindingFlags.Static | BindingFlags.NonPublic);
            var instructions = (IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { PatchProcessor.GetOriginalInstructions(target), target });
            Check(instructions.Count(code => code.operand is MethodInfo method && method.Name == hook.Inserted && method.DeclaringType == adapter) == 1,
                hook.Target + " rewrites exactly one matching call in the real DLL");
        }

        using var variant = AssemblyDefinition.ReadAssembly(assemblyPath);
        var tag = variant.MainModule.GetType(pluginName).CustomAttributes.Single(attribute => attribute.AttributeType.FullName == "BepInEx.BepInPlugin");
        tag.ConstructorArguments[2] = new CustomAttributeArgument(variant.MainModule.TypeSystem.String, "1.9.172");
        var later = LoadBetterMenuVariant(variant).GetType(pluginName, true);
        Check(BetterBepInExCompatibility.ResolveHooks(later, notes.Add, out _).Count == plan.Count && notes.Count == 0,
            "unlisted version metadata with the same contracts keeps all features");

        variant.MainModule.GetType(menuName).Methods.Single(method => method.Name == "UndoTip").Name = "UpdatedUndoTip";
        later = LoadBetterMenuVariant(variant).GetType(pluginName, true);
        Check(BetterBepInExCompatibility.ResolveHooks(later, notes.Add, out _).Count == plan.Count - 1 &&
            notes.Count == 1 && notes[0].Contains("UndoTip"), "missing cosmetic hook does not disable the adapter");

        var onGui = variant.MainModule.GetType(menuName).Methods.Single(method => method.Name == "OnGUI");
        var ensureRectCall = onGui.Body.Instructions.Single(code => code.Operand is MethodReference target && target.Name == "EnsureRect");
        ensureRectCall.OpCode = Mono.Cecil.Cil.OpCodes.Pop;
        ensureRectCall.Operand = null;
        later = LoadBetterMenuVariant(variant).GetType(pluginName, true);
        try
        {
            BetterBepInExCompatibility.ResolveHooks(later, notes.Add, out _);
            throw new Exception("Expected incompatible workbench insertion point to be rejected");
        }
        catch (MissingMemberException ex)
        {
            Check(ex.Message.Contains("EnsureRect"), "incompatible workbench insertion point reports the unmatched call");
        }
    }

    private static Assembly LoadBetterMenuVariant(AssemblyDefinition variant)
    {
        using var buffer = new MemoryStream();
        variant.Write(buffer);
        buffer.Position = 0;
        return new AssemblyLoadContext(null, isCollectible: true).LoadFromStream(buffer);
    }
}
