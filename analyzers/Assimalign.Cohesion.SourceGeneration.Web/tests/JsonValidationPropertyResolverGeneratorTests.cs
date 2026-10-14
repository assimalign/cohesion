using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.SourceGeneration.Web.Tests;

/// <summary>Coverage for JSON-context validation property lookup without runtime member discovery.</summary>
public class JsonValidationPropertyResolverGeneratorTests
{
    private const string resolverContract = """
        namespace Assimalign.Cohesion.Web.Validation
        {
            public interface IValidationJsonPropertyResolver
            {
                System.Text.Json.Serialization.Metadata.JsonPropertyInfo? GetProperty(
                    System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo, string memberName);
            }
        }
        """;

    private const string contextMembers = """
        public Context() : base(null) { }
        protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => null;
        public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => null;
        """;

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - JSON validation names: no optional contract emits no resolver")]
    public void Generate_WithoutValidationContract_EmitsNothing()
    {
        // Arrange
        string source = CreateContext();

        // Act
        (string generated, Compilation output) = Generate(source);

        // Assert — a compatibility case: this also held before the optional generator was added.
        generated.ShouldBeEmpty();
        Errors(output).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - JSON validation names: application implementation remains authoritative")]
    public void Generate_ExistingResolverImplementation_EmitsNothing()
    {
        // Arrange
        string source = resolverContract + CreateContext(
            ", Assimalign.Cohesion.Web.Validation.IValidationJsonPropertyResolver",
            """
            System.Text.Json.Serialization.Metadata.JsonPropertyInfo?
                Assimalign.Cohesion.Web.Validation.IValidationJsonPropertyResolver.GetProperty(
                    System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo, string memberName) => null;
            """);

        // Act
        (string generated, Compilation output) = Generate(source);

        // Assert — a compatibility case: an explicit resolver must never be replaced.
        generated.ShouldBeEmpty();
        Errors(output).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - JSON validation names: distinct context identities receive distinct source hints")]
    public void Generate_ContextsWithPreviouslyCollidingNames_CompilesBothResolvers()
    {
        // Arrange
        string source = resolverContract + """
            public sealed class Model { public string Name { get; set; } = ""; }
            namespace Example_A
            {
                [System.Text.Json.Serialization.JsonSerializable(typeof(Model))]
                public partial class Context : System.Text.Json.Serialization.JsonSerializerContext
                {
            """ + contextMembers + """
                }
            }
            namespace Example
            {
                [System.Text.Json.Serialization.JsonSerializable(typeof(Model))]
                public partial class A_Context : System.Text.Json.Serialization.JsonSerializerContext
                {
            """ + contextMembers.Replace("public Context()", "public A_Context()") + """
                }
            }
            """;

        // Act
        (string generated, Compilation output) = Generate(source);

        // Assert
        generated.ShouldContain("partial class Context :", Case.Sensitive);
        generated.ShouldContain("partial class A_Context :", Case.Sensitive);
        Errors(output).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - JSON validation names: nested contexts map policies, attributes, collections and inherited members")]
    public void Generate_NestedContextWithRenamedCollectionMembers_CompilesPropertyResolver()
    {
        // Arrange
        string source = resolverContract + """
            namespace Example
            {
                public class BaseModel { public string DisplayName { get; set; } = ""; }
                public sealed class Model : BaseModel
                {
                    [System.Text.Json.Serialization.JsonPropertyName("delivery_address")]
                    public Address Address { get; set; } = new();
                    public System.Collections.Generic.List<Item[]> Items { get; set; } = new();
                    public IDerived Contract { get; set; } = null!;
                    [System.Text.Json.Serialization.JsonIgnore]
                    public string Ignored { get; set; } = "";
                    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
                    public string? NullableValue { get; set; }
                    [System.Text.Json.Serialization.JsonInclude]
                    private string Included { get; set; } = "";
                    public Node<string>? Recursive { get; set; }
                }
                public sealed class Address
                {
                    [System.Text.Json.Serialization.JsonPropertyName("postal_code")]
                    public string PostalCode { get; set; } = "";
                    [System.Text.Json.Serialization.JsonPropertyName("")]
                    public string EmptyName { get; set; } = "";
                }
                public sealed class Item
                {
                    public int Quantity;
                    [System.Text.Json.Serialization.JsonInclude]
                    public int IncludedQuantity;
                }
                public interface IBase
                {
                    [System.Text.Json.Serialization.JsonPropertyName("base_name")]
                    string BaseName { get; }
                }
                public interface IDerived : IBase { string Name { get; } }
                public sealed class Node<T> { public Node<System.Collections.Generic.List<T>>? Next { get; set; } }
                public static partial class Container
                {
                    [System.Text.Json.Serialization.JsonSerializable(typeof(Model))]
                    public partial class Context : System.Text.Json.Serialization.JsonSerializerContext
                    {
            """ + contextMembers + """
                    }
                    [System.Text.Json.Serialization.JsonSerializable(typeof(Address))]
                    public partial class Context { }
                }
            }
            """;

        // Act
        (string generated, Compilation output) = Generate(source);

        // Assert
        generated.ShouldContain("\"PostalCode\" => \"postal_code\"", Case.Sensitive);
        generated.ShouldContain("\"EmptyName\" => \"\"", Case.Sensitive);
        generated.ShouldContain("typeof(global::Example.Item)", Case.Sensitive);
        generated.ShouldContain("typeof(global::Example.IDerived)", Case.Sensitive);
        generated.ShouldContain("\"BaseName\" => \"base_name\"", Case.Sensitive);
        generated.ShouldNotContain("\"Ignored\" =>", Case.Sensitive);
        generated.ShouldContain("\"NullableValue\" =>", Case.Sensitive);
        generated.ShouldContain("\"Included\" =>", Case.Sensitive);
        generated.ShouldContain("\"Quantity\" when typeInfo.Options.IncludeFields =>", Case.Sensitive);
        generated.ShouldContain("\"IncludedQuantity\" =>", Case.Sensitive);
        generated.ShouldContain("PropertyNamingPolicy?.ConvertName(memberName)", Case.Sensitive);
        Errors(output).ShouldBeEmpty();
    }

    private static string CreateContext(string interfaces = "", string implementation = "") => """
        public sealed class Model { public string Name { get; set; } = ""; }
        [System.Text.Json.Serialization.JsonSerializable(typeof(Model))]
        public partial class Context : System.Text.Json.Serialization.JsonSerializerContext
        """ + interfaces + "\n{\n" + contextMembers + "\n" + implementation + "\n}";

    private static (string Generated, Compilation Output) Generate(string source)
    {
        string validationAssembly = typeof(Assimalign.Cohesion.Web.Validation.ValidationMetadata).Assembly.Location;
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !string.Equals(path, validationAssembly, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ValidationPropertyGeneratorTest_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, options)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new JsonValidationPropertyResolverGenerator().AsSourceGenerator()], parseOptions: options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out var diagnostics);
        diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        string generated = string.Join(Environment.NewLine, driver.GetRunResult().GeneratedTrees.Select(tree => tree.ToString()));
        return (generated, output);
    }

    private static string[] Errors(Compilation compilation) => compilation.GetDiagnostics()
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .Select(diagnostic => diagnostic.ToString())
        .ToArray();
}
