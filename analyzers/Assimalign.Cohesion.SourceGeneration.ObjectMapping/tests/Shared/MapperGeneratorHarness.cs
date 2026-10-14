using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Assimalign.Cohesion.ObjectMapping;

namespace Assimalign.Cohesion.SourceGeneration.ObjectMapping.Tests;

internal static class MapperGeneratorHarness
{
    private static readonly CSharpParseOptions _parseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Assimalign.Cohesion.ObjectMapping.Generated")]);

    private static readonly ImmutableArray<MetadataReference> _references =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(MapperBuilder).Assembly.Location)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();

    internal static GeneratorRun Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "MappingConsumer",
            [CSharpSyntaxTree.ParseText(source, _parseOptions, path: "Profiles.cs")],
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new MapperProfileGenerator().AsSourceGenerator()],
            parseOptions: _parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        return new GeneratorRun(output, driver.GetRunResult());
    }

    internal sealed record GeneratorRun(Compilation Output, GeneratorDriverRunResult Result)
    {
        internal ImmutableArray<Diagnostic> Errors => Output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }
}
