using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Assimalign.Cohesion.SourceGeneration;

/// <summary>
/// Exposes a resource program's entry point to the gateway programs that run it in process.
/// </summary>
/// <remarks>
/// A top-level program compiles to an internal <c>Program</c> type with a private <c>&lt;Main&gt;$</c>
/// method that nothing outside its assembly can name. In an executable that references the in-process
/// hosting package and not the gateway, this generator emits a public bridge whose <c>RunAsync</c>
/// calls the entry point, and an assembly-level <c>InProcessProgramAttribute</c> that names the bridge
/// and the resource's provider for the gateway's generator to find.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class InProcessProgramBridgeGenerator : IIncrementalGenerator
{
    internal const string HostingAssemblyName = "Assimalign.Cohesion.ApplicationModel.Hosting.InProcess";
    internal const string GatewayAssemblyName = "Assimalign.Cohesion.ApplicationModel.Gateway";
    internal const string AttributeMetadataName = "Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.InProcessProgramAttribute";
    internal const string BridgeTypeName = "InProcessEntryPoint";
    internal const string BridgeNamespacePrefix = "Assimalign.Cohesion.Generated.";

    // Every area's application-model integration is named for its area, and its resources take the
    // area as their provider (Web's resources are "Web" resources).
    private static readonly Regex _areaIntegration = new(
        @"^Assimalign\.Cohesion\.(?<area>[A-Za-z][A-Za-z0-9]*)\.Hosting\.ApplicationModel$",
        RegexOptions.CultureInvariant);

    private static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<BridgeModel?> bridge = context.CompilationProvider
            .Select(static (compilation, cancellationToken) => CreateModel(compilation, cancellationToken));

        context.RegisterSourceOutput(bridge, static (productionContext, model) =>
        {
            if (model is { } value)
            {
                productionContext.AddSource("InProcessEntryPoint.g.cs", Emit(value));
            }
        });
    }

    /// <summary>
    /// Derives the namespace of an assembly's bridge from its name, so the bridges of the several
    /// resources one gateway references never collide, whatever namespaces their code declares.
    /// </summary>
    internal static string GetBridgeNamespace(string assemblyName)
    {
        IEnumerable<string> segments = assemblyName.Split('.').Select(static segment =>
        {
            var builder = new StringBuilder(segment.Length + 1);
            foreach (char character in segment)
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }

            if (builder.Length == 0 || char.IsDigit(builder[0]))
            {
                builder.Insert(0, '_');
            }

            string identifier = builder.ToString();
            return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;
        });

        return BridgeNamespacePrefix + string.Join(".", segments);
    }

    private static BridgeModel? CreateModel(Compilation compilation, CancellationToken cancellationToken)
    {
        // The cheap checks first: most compilations are not an in-process resource program.
        if (compilation.Options.OutputKind is not (OutputKind.ConsoleApplication or OutputKind.WindowsApplication))
        {
            return null;
        }

        bool hosting = false;
        var areas = new SortedSet<string>(StringComparer.Ordinal);
        foreach (AssemblyIdentity identity in compilation.ReferencedAssemblyNames)
        {
            if (string.Equals(identity.Name, GatewayAssemblyName, StringComparison.Ordinal))
            {
                // A gateway is never a resource, even one that runs its resources in process.
                return null;
            }

            if (string.Equals(identity.Name, HostingAssemblyName, StringComparison.Ordinal))
            {
                hosting = true;
            }
            else if (_areaIntegration.Match(identity.Name) is { Success: true } match)
            {
                areas.Add(match.Groups["area"].Value);
            }
        }

        if (!hosting || compilation.GetTypeByMetadataName(AttributeMetadataName) is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (compilation.GetEntryPoint(cancellationToken) is not { } entryPoint
            || entryPoint.ContainingType is not { } containingType
            || !TryGetReturnKind(compilation, entryPoint, out ReturnKind returnKind))
        {
            return null;
        }

        // An entry point the bridge can name is called directly. Otherwise the runtime reaches it
        // with an unsafe accessor, which is resolved without reflection and works under NativeAOT:
        // typed by its declaring type where the bridge can name that type as a parameter, and by
        // the type's runtime name where it cannot (a static class, a file-local one, or one nested
        // out of reach).
        CallKind call = IsAccessible(entryPoint)
            ? CallKind.Direct
            : !containingType.IsStatic && IsAccessible(containingType) ? CallKind.TypedAccessor : CallKind.NamedAccessor;

        return new BridgeModel(
            GetBridgeNamespace(compilation.AssemblyName ?? "Program"),
            areas.Count == 1 ? areas.Min : null,
            call,
            containingType.ToDisplayString(_typeFormat),
            containingType.IsReferenceType,
            GetRuntimeTypeName(containingType, compilation.AssemblyName ?? string.Empty),
            entryPoint.MetadataName,
            returnKind,
            entryPoint.Parameters.Length == 1);
    }

    private static bool TryGetReturnKind(Compilation compilation, IMethodSymbol entryPoint, out ReturnKind kind)
    {
        ITypeSymbol returnType = entryPoint.ReturnType;
        INamedTypeSymbol? task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        INamedTypeSymbol? taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");

        if (entryPoint.ReturnsVoid)
        {
            kind = ReturnKind.Void;
        }
        else if (returnType.SpecialType == SpecialType.System_Int32)
        {
            kind = ReturnKind.Int32;
        }
        else if (SymbolEqualityComparer.Default.Equals(returnType, task))
        {
            kind = ReturnKind.Task;
        }
        else if (returnType is INamedTypeSymbol { IsGenericType: true } generic
            && SymbolEqualityComparer.Default.Equals(generic.ConstructedFrom, taskOfT)
            && generic.TypeArguments[0].SpecialType == SpecialType.System_Int32)
        {
            kind = ReturnKind.TaskOfInt32;
        }
        else
        {
            kind = default;
            return false;
        }

        return true;
    }

    // The bridge sits in the same assembly, so anything short of private (or protected) is in reach,
    // as long as every type around it is too. A file-local type reports itself internal, but only its
    // own file can name it.
    private static bool IsAccessible(ISymbol symbol)
    {
        for (ISymbol? current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
                || current is INamedTypeSymbol { IsFileLocal: true })
            {
                return false;
            }
        }

        return symbol.CanBeReferencedByName;
    }

    // Metadata names throughout: a display string escapes keywords (@class), and the runtime would
    // look for the escaped name literally. A file-local type's metadata name carries its mangling.
    private static string GetRuntimeTypeName(INamedTypeSymbol type, string assemblyName)
    {
        var types = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            types.Push(current.MetadataName);
        }

        var namespaces = new Stack<string>();
        for (INamespaceSymbol? ns = type.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            namespaces.Push(ns.MetadataName);
        }

        string typeName = string.Join("+", types);
        string qualified = namespaces.Count == 0 ? typeName : string.Join(".", namespaces) + "." + typeName;
        return qualified + ", " + assemblyName;
    }

    private static string Emit(BridgeModel model)
    {
        string arguments = model.HasArguments ? "args" : string.Empty;
        string call = model.Call == CallKind.Direct
            ? $"{model.ContainingTypeName}.{model.MethodName}({arguments})"
            : $"CallEntryPoint(default{(model.HasArguments ? ", args" : string.Empty)})";

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();

        builder.Append("[assembly: global::Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.InProcessProgram(typeof(global::")
            .Append(model.Namespace).Append('.').Append(BridgeTypeName).Append(')');
        if (model.Provider is not null)
        {
            builder.Append(", ").Append(SymbolDisplay.FormatLiteral(model.Provider, quote: true));
        }

        builder.AppendLine(")]");
        builder.AppendLine();
        builder.Append("namespace ").Append(model.Namespace).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine("/// Runs this assembly's program in the process of a gateway that references it.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        builder.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"Assimalign.Cohesion.SourceGeneration\", \"1.0.0.0\")]");
        builder.Append("public static class ").AppendLine(BridgeTypeName);
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Runs the program's entry point.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <param name=\"args\">The program's arguments.</param>");
        builder.AppendLine("    /// <returns>A task that completes when the program ends, and faults when it throws or returns a nonzero exit code.</returns>");

        switch (model.ReturnKind)
        {
            case ReturnKind.Void:
                builder.AppendLine("    public static global::System.Threading.Tasks.Task RunAsync(string[] args)");
                builder.AppendLine("    {");
                builder.Append("        ").Append(call).AppendLine(";");
                builder.AppendLine("        return global::System.Threading.Tasks.Task.CompletedTask;");
                builder.AppendLine("    }");
                break;
            case ReturnKind.Int32:
                builder.AppendLine("    public static global::System.Threading.Tasks.Task RunAsync(string[] args)");
                builder.AppendLine("    {");
                builder.Append("        int exitCode = ").Append(call).AppendLine(";");
                builder.AppendLine("        return exitCode == 0");
                builder.AppendLine("            ? global::System.Threading.Tasks.Task.CompletedTask");
                builder.AppendLine("            : global::System.Threading.Tasks.Task.FromException(CreateExitCodeException(exitCode));");
                builder.AppendLine("    }");
                break;
            case ReturnKind.Task:
                builder.AppendLine("    public static global::System.Threading.Tasks.Task RunAsync(string[] args)");
                builder.AppendLine("    {");
                builder.Append("        return ").Append(call).AppendLine(";");
                builder.AppendLine("    }");
                break;
            case ReturnKind.TaskOfInt32:
                builder.AppendLine("    public static async global::System.Threading.Tasks.Task RunAsync(string[] args)");
                builder.AppendLine("    {");
                builder.Append("        int exitCode = await ").Append(call).AppendLine(".ConfigureAwait(false);");
                builder.AppendLine("        if (exitCode != 0)");
                builder.AppendLine("        {");
                builder.AppendLine("            throw CreateExitCodeException(exitCode);");
                builder.AppendLine("        }");
                builder.AppendLine("    }");
                break;
        }

        if (model.ReturnKind is ReturnKind.Int32 or ReturnKind.TaskOfInt32)
        {
            // A platform takes a process that exits with a nonzero code for one that failed; a
            // program that runs in process fails the same way.
            builder.AppendLine();
            builder.AppendLine("    private static global::System.InvalidOperationException CreateExitCodeException(int exitCode)");
            builder.AppendLine("    {");
            builder.AppendLine("        return new global::System.InvalidOperationException(\"The program exited with code \" + exitCode.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + \".\");");
            builder.AppendLine("    }");
        }

        if (model.Call != CallKind.Direct)
        {
            string returnType = model.ReturnKind switch
            {
                ReturnKind.Void => "void",
                ReturnKind.Int32 => "int",
                ReturnKind.Task => "global::System.Threading.Tasks.Task",
                _ => "global::System.Threading.Tasks.Task<int>",
            };
            string target = model.Call == CallKind.TypedAccessor
                ? model.ContainingTypeName + (model.ContainingTypeIsReferenceType ? "?" : string.Empty) + " program"
                : "[global::System.Runtime.CompilerServices.UnsafeAccessorType(" + SymbolDisplay.FormatLiteral(model.RuntimeTypeName, quote: true) + ")] object? program";

            builder.AppendLine();
            builder.Append("    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = ")
                .Append(SymbolDisplay.FormatLiteral(model.MethodName, quote: true)).AppendLine(")]");
            builder.Append("    private static extern ").Append(returnType).Append(" CallEntryPoint(").Append(target)
                .Append(model.HasArguments ? ", string[] args" : string.Empty).AppendLine(");");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private enum CallKind
    {
        Direct,
        TypedAccessor,
        NamedAccessor,
    }

    private enum ReturnKind
    {
        Void,
        Int32,
        Task,
        TaskOfInt32,
    }

    private sealed record BridgeModel(
        string Namespace,
        string? Provider,
        CallKind Call,
        string ContainingTypeName,
        bool ContainingTypeIsReferenceType,
        string RuntimeTypeName,
        string MethodName,
        ReturnKind ReturnKind,
        bool HasArguments);
}
