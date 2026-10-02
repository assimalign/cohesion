using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;

using Assimalign.Cohesion.OpenApi.Attributes;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration.Tests;

/// <summary>
/// Compiles test sources with the OpenApi metadata generator and emits them as PE images, so a
/// referencing compilation reads each annotated assembly as real metadata, the way a build does.
/// </summary>
internal static class OpenApiGeneratorHarness
{
    // Documentation mode Diagnose is what GenerateDocumentationFile turns on, so a public generated
    // member without XML documentation surfaces as CS1591 in every test that asserts a clean build.
    private static readonly CSharpParseOptions _parseOptions = new(LanguageVersion.Latest, DocumentationMode.Diagnose);

    private static readonly IReadOnlyList<MetadataReference> _platformReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(OpenApiOperationAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(OperationType).Assembly.Location))
            .ToList();

    /// <summary>Runs the generator over one source file compiled as <paramref name="assemblyName"/>.</summary>
    internal static GeneratorRun Run(string assemblyName, string source, params MetadataReference[] references) =>
        Run(assemblyName, source, OutputKind.DynamicallyLinkedLibrary, references);

    /// <summary>Runs the generator over one source file compiled as <paramref name="assemblyName"/>.</summary>
    internal static GeneratorRun Run(string assemblyName, string source, OutputKind outputKind, params MetadataReference[] references)
    {
        // The file path lets a generator diagnostic keep its source position, as it does in a build.
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, _parseOptions, path: assemblyName + ".cs")],
            _platformReferences.Concat(references),
            new CSharpCompilationOptions(outputKind));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OpenApiMetadataGenerator().AsSourceGenerator()],
            parseOptions: _parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        return new GeneratorRun(output, driver.GetRunResult());
    }

    /// <summary>Runs the generator, asserts the result compiles, and emits it as a PE image.</summary>
    internal static EmittedAssembly Emit(string assemblyName, string source, params MetadataReference[] references)
    {
        var run = Run(assemblyName, source, references);
        run.Errors.ShouldBeEmpty(Format(run.Errors));

        using var stream = new MemoryStream();
        var result = run.Output.Emit(stream);
        result.Success.ShouldBeTrue(Format(result.Diagnostics));

        var image = stream.ToArray();
        return new EmittedAssembly(assemblyName, image, MetadataReference.CreateFromImage(image), run);
    }

    /// <summary>Formats diagnostics into one assertion message.</summary>
    internal static string Format(IEnumerable<Diagnostic> diagnostics) => string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.ToString()));

    /// <summary>The outcome of one generator run.</summary>
    /// <param name="Output">The compilation including the generated sources.</param>
    /// <param name="Result">The generator driver's run result.</param>
    internal sealed record GeneratorRun(Compilation Output, GeneratorDriverRunResult Result)
    {
        /// <summary>Gets the generated registry source, or an empty string when nothing was generated.</summary>
        public string GeneratedSource => Result.GeneratedTrees.Length > 0 ? Result.GeneratedTrees[0].ToString() : string.Empty;

        /// <summary>Gets the diagnostics the generator reported.</summary>
        public ImmutableArray<Diagnostic> GeneratorDiagnostics => Result.Diagnostics;

        /// <summary>Gets every compiler warning and error in the output compilation.</summary>
        public ImmutableArray<Diagnostic> Problems => Output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .ToImmutableArray();

        /// <summary>Gets every compiler error in the output compilation.</summary>
        public ImmutableArray<Diagnostic> Errors => Problems
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    /// <summary>An annotated assembly compiled to a PE image.</summary>
    /// <param name="Name">The assembly name.</param>
    /// <param name="Image">The PE image.</param>
    /// <param name="Reference">A metadata reference read from the image.</param>
    /// <param name="Run">The generator run that produced it.</param>
    internal sealed record EmittedAssembly(string Name, byte[] Image, MetadataReference Reference, GeneratorRun Run);
}
