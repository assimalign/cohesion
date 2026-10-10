using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Generation;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration.Tests;

/// <summary>
/// Multi-assembly coverage for the provider seam (#1169). Each annotated library is compiled with the
/// generator and emitted as a PE image, and the host compilation references those images, so these
/// tests see exactly the metadata a referencing build sees.
/// </summary>
public class OpenApiMetadataCompositionTests
{
    private const string generatedNamespace = "global::Assimalign.Cohesion.OpenApi.Generated.";

    private const string petsLibrary = """
        using Assimalign.Cohesion.OpenApi;
        using Assimalign.Cohesion.OpenApi.Attributes;

        namespace Contoso.Pets;

        [OpenApiSchema(Description = "A pet.")]
        internal sealed class Pet
        {
            [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
            public long Id { get; set; }
        }

        [OpenApiTag("pets", Description = "Pet operations")]
        internal static class PetsApi
        {
            [OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getPet", Tags = new[] { "pets" })]
            [OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer)]
            [OpenApiResponse(200, Description = "The pet", ContentType = "application/json", ModelType = typeof(Pet))]
            public static void GetPet() { }
        }
        """;

    private const string ordersLibrary = """
        using Assimalign.Cohesion.OpenApi;
        using Assimalign.Cohesion.OpenApi.Attributes;

        namespace Contoso.Orders;

        [OpenApiSchema(Description = "An order.")]
        internal sealed class Order
        {
            [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
            public long Id { get; set; }
        }

        [OpenApiTag("orders", Description = "Order operations")]
        internal static class OrdersApi
        {
            [OpenApiOperation(OperationType.Get, "/orders/{id}", OperationId = "getOrder", Tags = new[] { "orders" })]
            [OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer)]
            [OpenApiResponse(200, Description = "The order", ContentType = "application/json", ModelType = typeof(Order))]
            public static void GetOrder() { }
        }
        """;

    private const string probeSource = """
        internal static class Probe
        {
            internal static object[] Read() => new object[]
            {
                global::Assimalign.Cohesion.OpenApi.Generated.OpenApiMetadataRegistry.Providers,
                global::Assimalign.Cohesion.OpenApi.Generated.OpenApiMetadataRegistry.Operations,
                global::Assimalign.Cohesion.OpenApi.Generated.OpenApiMetadataRegistry.Schemas,
                global::Assimalign.Cohesion.OpenApi.Generated.OpenApiMetadataRegistry.Tags
            };
        }
        """;

    private const string annotatedHost = """
        using Assimalign.Cohesion.OpenApi;
        using Assimalign.Cohesion.OpenApi.Attributes;

        namespace Contoso.Shop;

        [OpenApiSchema(Description = "A health report.")]
        internal sealed class Health
        {
            [OpenApiSchemaProperty(SchemaType = OpenApiSchemaKind.String)]
            public string Status { get; set; } = "ok";
        }

        internal static class HealthApi
        {
            [OpenApiOperation(OperationType.Get, "/health", OperationId = "getHealth")]
            [OpenApiResponse(200, Description = "ok", ContentType = "application/json", ModelType = typeof(Health))]
            public static void GetHealth() { }
        }

        """ + probeSource;

    private const string unannotatedHost = """
        namespace Contoso.Shop;

        """ + probeSource;

    private const string providerMembers = """
            /// <summary>Gets the operations.</summary>
            public IReadOnlyList<OpenApiOperationMetadata> Operations => [];

            /// <summary>Gets the schemas.</summary>
            public IReadOnlyList<OpenApiSchemaMetadata> Schemas => [];

            /// <summary>Gets the tags.</summary>
            public IReadOnlyList<OpenApiTagMetadata> Tags => [];

            /// <summary>Gets the security schemes.</summary>
            public IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes => [];
        """;

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: two annotated libraries and an unannotated host compile without CS0433")]
    public void Compose_TwoAnnotatedLibrariesAndUnannotatedHost_CompilesWithoutCS0433()
    {
        // Arrange
        var pets = OpenApiGeneratorHarness.Emit("Contoso.Pets", petsLibrary);
        var orders = OpenApiGeneratorHarness.Emit("Contoso.Orders", ordersLibrary);

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", unannotatedHost, pets.Reference, orders.Reference);

        // Assert
        host.Problems.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Problems));
        host.GeneratedSource.ShouldContain("internal static class OpenApiMetadataRegistry", Case.Sensitive);
        host.GeneratedSource.ShouldContain($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Orders()", Case.Sensitive);
        host.GeneratedSource.ShouldContain($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Pets()", Case.Sensitive);
        host.GeneratedSource.ShouldNotContain("OpenApiMetadataProvider_Contoso__Shop", Case.Sensitive);
        host.GeneratedSource.ShouldNotContain("[assembly:", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: two annotated libraries and an annotated host compile without CS0436")]
    public void Compose_TwoAnnotatedLibrariesAndAnnotatedHost_CompilesWithoutCS0436()
    {
        // Arrange
        var pets = OpenApiGeneratorHarness.Emit("Contoso.Pets", petsLibrary);
        var orders = OpenApiGeneratorHarness.Emit("Contoso.Orders", ordersLibrary);

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", annotatedHost, pets.Reference, orders.Reference);

        // Assert
        host.Problems.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Problems));
        host.GeneratorDiagnostics.ShouldBeEmpty();
        host.GeneratedSource.ShouldContain("public sealed class OpenApiMetadataProvider_Contoso__Shop", Case.Sensitive);
        host.GeneratedSource.ShouldContain($"[assembly: global::Assimalign.Cohesion.OpenApi.Attributes.OpenApiMetadataProviderAttribute(typeof({generatedNamespace}OpenApiMetadataProvider_Contoso__Shop))]", Case.Sensitive);

        var orderIndex = host.GeneratedSource.IndexOf($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Orders()", StringComparison.Ordinal);
        var petsIndex = host.GeneratedSource.IndexOf($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Pets()", StringComparison.Ordinal);
        var shopIndex = host.GeneratedSource.IndexOf($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Shop()", StringComparison.Ordinal);
        orderIndex.ShouldBeGreaterThan(0);
        petsIndex.ShouldBeGreaterThan(orderIndex);
        shopIndex.ShouldBeGreaterThan(petsIndex);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: the host registry exposes every assembly's operations and schemas at run time")]
    public void Compose_TwoAnnotatedLibrariesAndAnnotatedHost_ExposesEveryAssemblysMetadata()
    {
        // Arrange
        var pets = OpenApiGeneratorHarness.Emit("Contoso.Pets", petsLibrary);
        var orders = OpenApiGeneratorHarness.Emit("Contoso.Orders", ordersLibrary);
        var host = OpenApiGeneratorHarness.Emit("Contoso.Shop", annotatedHost, pets.Reference, orders.Reference);
        var context = new ImageLoadContext(new Dictionary<string, byte[]>
        {
            [pets.Name] = pets.Image,
            [orders.Name] = orders.Image
        });

        try
        {
            // Act
            var probe = context.LoadFromStream(new MemoryStream(host.Image)).GetType("Contoso.Shop.Probe", throwOnError: true)!;
            var values = (object[])probe.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            var providers = (IReadOnlyList<IOpenApiMetadataProvider>)values[0];
            var operations = (IReadOnlyList<OpenApiOperationMetadata>)values[1];
            var schemas = (IReadOnlyList<OpenApiSchemaMetadata>)values[2];
            var tags = (IReadOnlyList<OpenApiTagMetadata>)values[3];

            var document = OpenApiDocumentGenerator.Generate(
                new OpenApiGenerationInput { Operations = operations, Schemas = schemas, Tags = tags },
                new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "Shop", ApiVersion = "1.0.0" });

            // Assert
            providers.Select(provider => provider.GetType().Name).ShouldBe(
            [
                "OpenApiMetadataProvider_Contoso__Orders",
                "OpenApiMetadataProvider_Contoso__Pets",
                "OpenApiMetadataProvider_Contoso__Shop"
            ]);
            operations.Select(operation => operation.OperationId).ShouldBe(["getOrder", "getPet", "getHealth"]);
            schemas.Select(schema => schema.Name).ShouldBe(["Order", "Pet", "Health"]);
            tags.Select(tag => tag.Name).ShouldBe(["orders", "pets"]);
            document.Paths!.Items.Keys.ShouldBe(["/orders/{id}", "/pets/{id}", "/health"], ignoreOrder: true);
            document.Components!.Schemas.Keys.ShouldBe(["Order", "Pet", "Health"], ignoreOrder: true);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: a library that references another annotated library advertises only its own metadata")]
    public void Compose_AnnotatedLibraryReferencingAnnotatedLibrary_ComposesEachProviderOnce()
    {
        // Arrange
        var pets = OpenApiGeneratorHarness.Emit("Contoso.Pets", petsLibrary);
        var orders = OpenApiGeneratorHarness.Emit("Contoso.Orders", ordersLibrary, pets.Reference);

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", annotatedHost, pets.Reference, orders.Reference);

        // Assert
        orders.Run.GeneratedSource.ShouldContain($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Pets()", Case.Sensitive);
        orders.Run.GeneratedSource.ShouldNotContain("PetsApi", Case.Sensitive);
        orders.Run.GeneratedSource.ShouldNotContain("getPet", Case.Sensitive);

        host.Problems.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Problems));
        CountOccurrences(host.GeneratedSource, $"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Pets()").ShouldBe(1);
        CountOccurrences(host.GeneratedSource, $"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Orders()").ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: assembly names that differ only in punctuation get distinct providers")]
    public void Compose_AssemblyNamesDifferingOnlyInPunctuation_EmitDistinctProviders()
    {
        // Arrange
        var dotted = OpenApiGeneratorHarness.Emit("Contoso.Pets", petsLibrary);
        var underscored = OpenApiGeneratorHarness.Emit("Contoso_Pets", petsLibrary.Replace("namespace Contoso.Pets;", "namespace Contoso.PetsCopy;"));

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", unannotatedHost, dotted.Reference, underscored.Reference);

        // Assert
        host.Problems.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Problems));
        host.GeneratedSource.ShouldContain($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Pets()", Case.Sensitive);
        host.GeneratedSource.ShouldContain($"new {generatedNamespace}OpenApiMetadataProvider_Contoso_x005FPets()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: a hand-written provider a library advertises is composed")]
    public void Compose_HandWrittenProviderAdvertisedByLibrary_IsComposed()
    {
        // Arrange
        var manual = OpenApiGeneratorHarness.Emit("Contoso.Manual", $$"""
            using System.Collections.Generic;
            using Assimalign.Cohesion.OpenApi.Attributes;

            [assembly: OpenApiMetadataProvider(typeof(Contoso.Manual.ManualProvider))]

            namespace Contoso.Manual;

            /// <summary>A provider written by hand.</summary>
            public sealed class ManualProvider : IOpenApiMetadataProvider
            {
            {{providerMembers}}
            }
            """);

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", annotatedHost, manual.Reference);

        // Assert
        manual.Run.GeneratorDiagnostics.ShouldBeEmpty();
        manual.Run.GeneratedSource.ShouldContain("new global::Contoso.Manual.ManualProvider()", Case.Sensitive);
        manual.Run.GeneratedSource.ShouldNotContain("OpenApiMetadataProvider_Contoso__Manual", Case.Sensitive);
        host.Problems.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Problems));
        host.GeneratedSource.ShouldContain("new global::Contoso.Manual.ManualProvider()", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: a referenced provider that cannot be constructed is skipped with a warning")]
    [InlineData("internal sealed", ": IOpenApiMetadataProvider", "", "it is not public")]
    [InlineData("public abstract", ": IOpenApiMetadataProvider", "", "it is not a non-abstract class")]
    [InlineData("public sealed", "", "", "it does not implement IOpenApiMetadataProvider")]
    [InlineData("public sealed", ": IOpenApiMetadataProvider", "/// <summary>Creates the provider.</summary>\n/// <param name=\"seed\">Unused.</param>\npublic Provider(int seed) { }", "it has no public parameterless constructor")]
    public void Compose_UnusableReferencedProvider_ReportsWarningAndSkipsIt(string modifiers, string baseList, string constructor, string reason)
    {
        // Arrange
        var library = OpenApiGeneratorHarness.Emit("Contoso.Broken", $$"""
            using System.Collections.Generic;
            using Assimalign.Cohesion.OpenApi.Attributes;

            [assembly: OpenApiMetadataProvider(typeof(Contoso.Broken.Provider))]

            namespace Contoso.Broken;

            /// <summary>A provider that cannot be composed.</summary>
            {{modifiers}} class Provider {{baseList}}
            {
            {{constructor}}

            {{providerMembers}}
            }
            """);

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", annotatedHost, library.Reference);

        // Assert
        var warning = host.GeneratorDiagnostics.ShouldHaveSingleItem();
        warning.Id.ShouldBe("OPENAPIGEN0001");
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldContain("Contoso.Broken.Provider", Case.Sensitive);
        warning.GetMessage().ShouldContain(reason, Case.Sensitive);
        host.Errors.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Errors));
        host.GeneratedSource.ShouldNotContain("Contoso.Broken", Case.Sensitive);
        host.GeneratedSource.ShouldContain($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Shop()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: an assembly cannot advertise a provider another assembly declares")]
    public void Compose_ProviderDeclaredInAnotherAssembly_ReportsWarningAndSkipsIt()
    {
        // Arrange
        var owner = OpenApiGeneratorHarness.Emit("Contoso.Owner", $$"""
            using System.Collections.Generic;
            using Assimalign.Cohesion.OpenApi.Attributes;

            namespace Contoso.Owner;

            /// <summary>A provider its own assembly does not advertise.</summary>
            public sealed class SharedProvider : IOpenApiMetadataProvider
            {
            {{providerMembers}}
            }
            """);
        var borrower = OpenApiGeneratorHarness.Emit("Contoso.Borrower", """
            [assembly: Assimalign.Cohesion.OpenApi.Attributes.OpenApiMetadataProvider(typeof(Contoso.Owner.SharedProvider))]
            """, owner.Reference);

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", annotatedHost, owner.Reference, borrower.Reference);

        // Assert
        var warning = host.GeneratorDiagnostics.ShouldHaveSingleItem();
        warning.Id.ShouldBe("OPENAPIGEN0001");
        warning.GetMessage().ShouldContain("advertised by assembly 'Contoso.Borrower'", Case.Sensitive);
        warning.GetMessage().ShouldContain("declared in another assembly", Case.Sensitive);
        host.Errors.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Errors));
        host.GeneratedSource.ShouldNotContain("SharedProvider", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: an unusable provider advertised in source is reported at the attribute")]
    public void Compose_UnusableProviderAdvertisedInSource_ReportsWarningAtTheAttribute()
    {
        // Arrange
        const string source = """
            using Assimalign.Cohesion.OpenApi.Attributes;

            [assembly: OpenApiMetadataProvider(typeof(Contoso.Shop.NotAProvider))]

            namespace Contoso.Shop;

            /// <summary>Not a provider.</summary>
            public sealed class NotAProvider
            {
            }
            """;

        // Act
        var run = OpenApiGeneratorHarness.Run("Contoso.Shop", source);

        // Assert
        var warning = run.GeneratorDiagnostics.ShouldHaveSingleItem();
        warning.Id.ShouldBe("OPENAPIGEN0001");
        warning.Location.GetLineSpan().Path.ShouldBe("Contoso.Shop.cs");
        warning.Location.GetLineSpan().StartLinePosition.Line.ShouldBe(2);
        run.GeneratedSource.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: an InternalsVisibleTo friend that names the registry binds its own, with CS0436")]
    public void Compose_InternalsVisibleToFriendNamingTheRegistry_WarnsCS0436AndBindsItsOwnRegistry()
    {
        // Arrange: the documented limit. A friend can see the granting assembly's internal registry, so
        // naming the registry reports CS0436 while still binding the friend's own, complete registry.
        var pets = OpenApiGeneratorHarness.Emit(
            "Contoso.Pets",
            petsLibrary.Replace(
                "namespace Contoso.Pets;",
                "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Contoso.Pets.Tests\")]\n\nnamespace Contoso.Pets;"));

        // Act
        var friend = OpenApiGeneratorHarness.Run("Contoso.Pets.Tests", unannotatedHost, pets.Reference);

        // Assert
        friend.Problems.ShouldNotBeEmpty();
        friend.Problems.ShouldAllBe(diagnostic => diagnostic.Id == "CS0436");
        friend.GeneratedSource.ShouldContain($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Pets()", Case.Sensitive);

        var tree = friend.Output.SyntaxTrees.Single(syntaxTree => syntaxTree.FilePath == "Contoso.Pets.Tests.cs");
        var reference = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .First(identifier => identifier.Identifier.Text == "OpenApiMetadataRegistry");
        var bound = friend.Output.GetSemanticModel(tree).GetSymbolInfo(reference).Symbol.ShouldNotBeNull();
        bound.ContainingAssembly.Name.ShouldBe("Contoso.Pets.Tests");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.SourceGeneration] - Composition: an assembly's hand-written providers precede its generated provider")]
    public void Compose_HandWrittenProviderAdvertisedByAnnotatedHost_PrecedesGeneratedProvider()
    {
        // Arrange
        var source = annotatedHost.Replace("namespace Contoso.Shop;", $$"""
            using System.Collections.Generic;

            [assembly: OpenApiMetadataProvider(typeof(Contoso.Shop.ManualProvider))]

            namespace Contoso.Shop;

            /// <summary>A provider written by hand.</summary>
            public sealed class ManualProvider : IOpenApiMetadataProvider
            {
            {{providerMembers}}
            }
            """);
        var pets = OpenApiGeneratorHarness.Emit("Contoso.Pets", petsLibrary);

        // Act
        var host = OpenApiGeneratorHarness.Run("Contoso.Shop", source, pets.Reference);

        // Assert
        host.Problems.ShouldBeEmpty(OpenApiGeneratorHarness.Format(host.Problems));
        var petsIndex = host.GeneratedSource.IndexOf($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Pets()", StringComparison.Ordinal);
        var manualIndex = host.GeneratedSource.IndexOf("new global::Contoso.Shop.ManualProvider()", StringComparison.Ordinal);
        var generatedIndex = host.GeneratedSource.IndexOf($"new {generatedNamespace}OpenApiMetadataProvider_Contoso__Shop()", StringComparison.Ordinal);
        petsIndex.ShouldBeGreaterThan(0);
        manualIndex.ShouldBeGreaterThan(petsIndex);
        generatedIndex.ShouldBeGreaterThan(manualIndex);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
