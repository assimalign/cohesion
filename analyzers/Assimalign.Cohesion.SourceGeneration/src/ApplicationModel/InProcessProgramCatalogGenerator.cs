using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

using Assimalign.Cohesion.SourceGeneration.Internal;

namespace Assimalign.Cohesion.SourceGeneration;

/// <summary>
/// Binds the resource programs a gateway program references to the resources they run, so the
/// gateway's in-process infrastructure runs them with no <c>AddProgram</c> call.
/// </summary>
/// <remarks>
/// <para>
/// The gateway SDK turns each project reference that carries <c>CohesionResource</c> metadata into
/// an additional file with that metadata, the optional <c>CohesionProvider</c>, and the referenced
/// assembly's name (<c>CohesionResourceAssembly</c>). In a compilation that references the in-process
/// gateway package, this generator finds each referenced assembly's <c>InProcessProgramAttribute</c>
/// and emits a module initializer that registers the program it names in <c>InProcessProgramCatalog</c>.
/// </para>
/// <para>
/// <c>CohesionProvider</c> overrides the provider the program's build recorded. A reference whose
/// assembly records no program, a resource with no provider, and a resource bound twice are
/// reported, and nothing is registered for them.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class InProcessProgramCatalogGenerator : IIncrementalGenerator
{
    internal const string CatalogMetadataName = "Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.InProcessProgramCatalog";

    private const string ResourceMetadata = "build_metadata.AdditionalFiles.CohesionResource";
    private const string ProviderMetadata = "build_metadata.AdditionalFiles.CohesionProvider";
    private const string AssemblyMetadata = "build_metadata.AdditionalFiles.CohesionResourceAssembly";

    private static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<ResourceReference>> references = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) => ReadReference(pair.Left, pair.Right.GetOptions(pair.Left)))
            .Where(static reference => reference is not null)
            .Select(static (reference, _) => reference!)
            .Collect();

        IncrementalValueProvider<CollectedRegistrations> registrations = references
            .Combine(context.CompilationProvider)
            .Select(static (pair, cancellationToken) => Collect(pair.Left, pair.Right, cancellationToken));

        context.RegisterSourceOutput(registrations, static (productionContext, collected) =>
        {
            foreach (DiagnosticInfo diagnostic in collected.Diagnostics)
            {
                productionContext.ReportDiagnostic(diagnostic.ToDiagnostic(InProcessProgramDiagnostics.GetDescriptor(diagnostic.DescriptorId)));
            }

            if (collected.Registrations.Count > 0)
            {
                productionContext.AddSource("InProcessProgramRegistrations.g.cs", Emit(collected.Registrations));
            }
        });
    }

    private static ResourceReference? ReadReference(AdditionalText file, AnalyzerConfigOptions options)
    {
        if (!options.TryGetValue(ResourceMetadata, out string? resource) || string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        options.TryGetValue(ProviderMetadata, out string? provider);
        options.TryGetValue(AssemblyMetadata, out string? assembly);
        return new ResourceReference(
            file.Path,
            resource.Trim(),
            string.IsNullOrWhiteSpace(provider) ? null : provider!.Trim(),
            assembly?.Trim() ?? string.Empty);
    }

    private static CollectedRegistrations Collect(ImmutableArray<ResourceReference> references, Compilation compilation, CancellationToken cancellationToken)
    {
        if (references.IsDefaultOrEmpty || compilation.GetTypeByMetadataName(CatalogMetadataName) is null)
        {
            return CollectedRegistrations.Empty;
        }

        var assemblies = new Dictionary<string, IAssemblySymbol>(StringComparer.Ordinal);
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            assemblies[assembly.Identity.Name] = assembly;
        }

        var registrations = ImmutableArray.CreateBuilder<Registration>();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var bound = new Dictionary<(string Provider, string Name), string>();

        // The order of the references in the project file is the order the build sees them in, but
        // sorting keeps the output, and which of two duplicates is reported, independent of it.
        foreach (ResourceReference reference in references
            .OrderBy(static reference => reference.Assembly, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Resource, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Location location = Location.Create(reference.Path, default, default);

            if (!assemblies.TryGetValue(reference.Assembly, out IAssemblySymbol? assembly)
                || !TryGetProgram(assembly, out string? bridge, out string? recordedProvider))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    InProcessProgramDiagnostics.ProgramMissing,
                    location,
                    $"The project reference to '{reference.Assembly}' names resource '{reference.Resource}', but the assembly has no in-process program. "
                        + "The resource's project must be an executable that references Assimalign.Cohesion.ApplicationModel.Hosting.InProcess "
                        + "and opts its resource into the platform with UseInProcess(); otherwise bind it with AddProgram."));
                continue;
            }

            string? provider = reference.Provider ?? recordedProvider;
            if (provider is null)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    InProcessProgramDiagnostics.ProviderMissing,
                    location,
                    $"The program of resource '{reference.Resource}' in '{reference.Assembly}' does not record its provider; "
                        + "set CohesionProvider on the project reference, such as CohesionProvider=\"Web\"."));
                continue;
            }

            if (bound.TryGetValue((provider, reference.Resource), out string? earlier))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    InProcessProgramDiagnostics.ResourceDuplicate,
                    location,
                    $"Resource '{provider}/{reference.Resource}' is bound to the programs of both '{earlier}' and '{reference.Assembly}'; "
                        + "give each project reference its own CohesionResource."));
                continue;
            }

            bound.Add((provider, reference.Resource), reference.Assembly);
            registrations.Add(new Registration(provider, reference.Resource, bridge!));
        }

        return new CollectedRegistrations(
            new EquatableArray<Registration>(registrations.ToImmutable()),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    private static bool TryGetProgram(IAssemblySymbol assembly, out string? bridge, out string? provider)
    {
        foreach (AttributeData attribute in assembly.GetAttributes())
        {
            if (!string.Equals(attribute.AttributeClass?.ToDisplayString(), InProcessProgramBridgeGenerator.AttributeMetadataName, StringComparison.Ordinal)
                || attribute.ConstructorArguments.Length == 0
                || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol program)
            {
                continue;
            }

            bridge = program.ToDisplayString(_typeFormat);
            provider = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is string { Length: > 0 } recorded
                ? recorded
                : null;
            return true;
        }

        bridge = null;
        provider = null;
        return false;
    }

    private static string Emit(EquatableArray<Registration> registrations)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Assimalign.Cohesion.Generated;");
        builder.AppendLine();
        builder.AppendLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        builder.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"Assimalign.Cohesion.SourceGeneration\", \"1.0.0.0\")]");
        builder.AppendLine("internal static class InProcessProgramRegistrations");
        builder.AppendLine("{");
        builder.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        builder.AppendLine("    internal static void Register()");
        builder.AppendLine("    {");
        foreach (Registration registration in registrations)
        {
            builder.Append("        global::").Append(CatalogMetadataName).Append(".Register(")
                .Append(SymbolDisplay.FormatLiteral(registration.Provider, quote: true)).Append(", ")
                .Append(SymbolDisplay.FormatLiteral(registration.Resource, quote: true)).Append(", ")
                .Append(registration.Bridge).AppendLine(".RunAsync);");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private sealed record ResourceReference(string Path, string Resource, string? Provider, string Assembly);

    private readonly record struct Registration(string Provider, string Resource, string Bridge);

    private readonly record struct CollectedRegistrations(
        EquatableArray<Registration> Registrations,
        EquatableArray<DiagnosticInfo> Diagnostics)
    {
        internal static CollectedRegistrations Empty => new(
            EquatableArray<Registration>.Empty,
            EquatableArray<DiagnosticInfo>.Empty);
    }
}
