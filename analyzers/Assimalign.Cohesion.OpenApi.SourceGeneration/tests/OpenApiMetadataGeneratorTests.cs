using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration.Tests;

public class OpenApiMetadataGeneratorTests
{
    private static (string GeneratedSource, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompilationErrors) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(OpenApiOperationAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(OperationType).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OpenApiMetadataGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var runResult = driver.GetRunResult();
        var generated = runResult.GeneratedTrees.Length > 0 ? runResult.GeneratedTrees[0].ToString() : string.Empty;
        var compileErrors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();

        return (generated, runResult.Diagnostics, compileErrors);
    }

    private const string ValidApi = """
        using Assimalign.Cohesion.OpenApi;
        using Assimalign.Cohesion.OpenApi.Attributes;

        [OpenApiSchema(Description = "A pet.")]
        public sealed class Pet
        {
            [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
            public long Id { get; set; }

            [OpenApiSchemaProperty(SchemaType = OpenApiSchemaKind.String)]
            public string Name { get; set; }
        }

        [OpenApiTag("pets", Description = "Pet operations")]
        public static class PetApi
        {
            [OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getPet", Tags = new[] { "pets" })]
            [OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer)]
            [OpenApiResponse(200, Description = "The pet", ContentType = "application/json", ModelType = typeof(Pet))]
            [OpenApiResponse(404, Description = "Not found")]
            public static void GetPet() { }
        }
        """;

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Generator: a valid API emits the registry and compiles")]
    public void Generator_ValidApi_EmitsRegistry()
    {
        var (generated, generatorDiagnostics, compileErrors) = Run(ValidApi);

        generatorDiagnostics.ShouldBeEmpty();
        compileErrors.ShouldBeEmpty(compileErrors.Length == 0 ? "" : string.Join("; ", compileErrors.Select(d => d.GetMessage())));

        generated.ShouldContain("class OpenApiMetadataRegistry", Case.Sensitive);
        generated.ShouldContain("Path = \"/pets/{id}\"", Case.Sensitive);
        generated.ShouldContain("OperationId = \"getPet\"", Case.Sensitive);
        generated.ShouldContain("#/components/schemas/Pet", Case.Sensitive);
        generated.ShouldContain("OpenApiSchemaMetadata", Case.Sensitive);
        generated.ShouldContain("Name = \"pets\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Generator: an ambiguous body schema reports a diagnostic")]
    public void Generator_AmbiguousSchema_ReportsDiagnostic()
    {
        const string source = """
            using Assimalign.Cohesion.OpenApi;
            using Assimalign.Cohesion.OpenApi.Attributes;

            public sealed class Pet { }

            public static class Api
            {
                [OpenApiOperation(OperationType.Get, "/pets")]
                [OpenApiResponse(200, Description = "ok", ModelType = typeof(Pet), SchemaReference = "#/components/schemas/Other")]
                public static void Get() { }
            }
            """;

        var (_, generatorDiagnostics, _) = Run(source);

        generatorDiagnostics.ShouldContain(d => d.Id == "OPENAPIATTR0002" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Generator: a non-required path parameter reports a warning")]
    public void Generator_PathParameterNotRequired_ReportsWarning()
    {
        const string source = """
            using Assimalign.Cohesion.OpenApi;
            using Assimalign.Cohesion.OpenApi.Attributes;

            public static class Api
            {
                [OpenApiOperation(OperationType.Get, "/pets/{id}")]
                [OpenApiParameter("id", ParameterLocation.Path)]
                public static void Get() { }
            }
            """;

        var (_, generatorDiagnostics, _) = Run(source);

        generatorDiagnostics.ShouldContain(d => d.Id == "OPENAPIATTR0003" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Generator: an empty operation path reports an error")]
    public void Generator_EmptyPath_ReportsError()
    {
        const string source = """
            using Assimalign.Cohesion.OpenApi;
            using Assimalign.Cohesion.OpenApi.Attributes;

            public static class Api
            {
                [OpenApiOperation(OperationType.Get, "")]
                public static void Get() { }
            }
            """;

        var (_, generatorDiagnostics, _) = Run(source);

        generatorDiagnostics.ShouldContain(d => d.Id == "OPENAPIATTR0001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Generator: an incomplete API key scheme reports an error")]
    public void Generator_IncompleteApiKey_ReportsError()
    {
        const string source = """
            using Assimalign.Cohesion.OpenApi;
            using Assimalign.Cohesion.OpenApi.Attributes;

            [assembly: OpenApiSecurityScheme("key", SecuritySchemeType.ApiKey)]
            """;

        var (_, generatorDiagnostics, _) = Run(source);

        generatorDiagnostics.ShouldContain(d => d.Id == "OPENAPIATTR0006" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Generator: a project with no attributes emits nothing")]
    public void Generator_NoAttributes_EmitsNothing()
    {
        var (generated, generatorDiagnostics, compileErrors) = Run("public class Empty { }");

        generated.ShouldBeEmpty();
        generatorDiagnostics.ShouldBeEmpty();
        compileErrors.ShouldBeEmpty();
    }
}
