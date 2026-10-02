using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Validation;
using Assimalign.Cohesion.Web.OpenApi.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.OpenApi.Tests;

/// <summary>
/// Schemas derived from the application's source-generated System.Text.Json contracts: object types
/// become components, nullability follows the contracts, numbers lose the read-from-string leniency, and
/// recursion, polymorphism, collections and dictionaries survive being split into components.
/// </summary>
public class OpenApiSchemaGenerationTests
{
    private static async Task<OpenApiDocument> GetDocumentAsync(OpenApiSpecVersion version)
    {
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id:long}", (long id) => new Order(id, "book", 9.5m, OrderStatus.Shipped, null, [], DateTimeOffset.UnixEpoch));
        factory.Application.MapGet("/trees/{id:int}", (int id) => new TreeNode { Value = id });
        factory.Application.MapGet("/shapes/{id:int}", (int id) => (Shape)new Circle { Radius = id });
        factory.Application.MapGet("/envelope", () => new Envelope());
        factory.Application.MapGet("/count", () => 42);

        // Starting the application builds the router, closing the route table.
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/count", cancellation.Token);
        response.EnsureSuccessStatusCode();

        return factory.Application.GetOpenApiDescriptionProvider().GetDocument(version);
    }

    private static OpenApiSchema GetComponent(OpenApiDocument document, string name) => document.Components!.Schemas[name];

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: object types become components referenced from their usages")]
    public async Task Schemas_ObjectTypes_ShouldBecomeComponents()
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_1);

        // Assert — Circle is only a branch of Shape's anyOf, never a usage of its own, so it is no component.
        document.Components!.Schemas.Keys.ShouldBe(
            ["Address", "Customer", "Envelope", "Order", "OrderLine", "Point", "ProblemDetails", "Shape", "TreeNode"]);

        OpenApiSchema order = GetComponent(document, "Order");
        order.Type.ShouldBe(SchemaType.Object);
        order.Properties.Keys.ShouldBe(["id", "item", "total", "status", "customer", "lines", "created"]);
        order.Properties["lines"].Type.ShouldBe(SchemaType.Array);
        order.Properties["lines"].Items!.Reference!.Ref.ShouldBe("#/components/schemas/OrderLine");
        order.Properties["created"].Format.ShouldBe("date-time");
        GetComponent(document, "Customer").Properties["addresses"].Items!.Reference!.Ref.ShouldBe("#/components/schemas/Address");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: nullability follows the contract's annotations")]
    public async Task Schemas_Nullability_ShouldFollowAnnotations()
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_1);
        OpenApiSchema order = GetComponent(document, "Order");
        OpenApiSchema address = GetComponent(document, "Address");

        // Assert — a nullable reference is anyOf the component and null; a nullable string is a type pair.
        OpenApiSchema customer = order.Properties["customer"];
        customer.AnyOf.Count.ShouldBe(2);
        customer.AnyOf[0].Reference!.Ref.ShouldBe("#/components/schemas/Customer");
        customer.AnyOf[1].Type.ShouldBe(SchemaType.Null);
        order.Properties["item"].Nullable.ShouldBeFalse();
        address.Properties["city"].Nullable.ShouldBeTrue();
        address.Properties["city"].Type.ShouldBe(SchemaType.String);
        address.Properties["street"].Nullable.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: numbers are described in the form the writer emits, with their format")]
    public async Task Schemas_Numbers_ShouldDropStringLeniencyAndCarryFormat()
    {
        // Arrange / Act — the web defaults read numbers from strings, but write JSON numbers.
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_1);
        OpenApiSchema order = GetComponent(document, "Order");

        // Assert
        order.Properties["id"].Types.ShouldBe([SchemaType.Integer]);
        order.Properties["id"].Format.ShouldBe("int64");
        order.Properties["id"].Pattern.ShouldBeNull();
        order.Properties["total"].Types.ShouldBe([SchemaType.Number]);
        order.Properties["total"].Format.ShouldBe("decimal");
        order.Properties["status"].Type.ShouldBe(SchemaType.Integer);

        OpenApiSchema count = document.Paths!.Items["/count"].Operations[OperationType.Get].Responses!.Items["200"].Content["application/json"].Schema!;
        count.Type.ShouldBe(SchemaType.Integer);
        count.Format.ShouldBe("int32");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: a recursive type references its own component")]
    public async Task Schemas_RecursiveType_ShouldReferenceItself()
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_1);
        OpenApiSchema tree = GetComponent(document, "TreeNode");

        // Assert
        tree.Properties["parent"].AnyOf[0].Reference!.Ref.ShouldBe("#/components/schemas/TreeNode");
        tree.Properties["children"].Items!.Reference!.Ref.ShouldBe("#/components/schemas/TreeNode");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: a polymorphic base keeps its derived types as discriminated branches")]
    public async Task Schemas_PolymorphicType_ShouldListBranches()
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_1);
        OpenApiSchema shape = GetComponent(document, "Shape");

        // Assert
        shape.AnyOf.Count.ShouldBe(2);
        shape.Required.ShouldBe(["kind"]);
        shape.AnyOf[0].Properties["kind"].Const.ShouldNotBeNull();
        shape.AnyOf[1].Properties.Keys.ShouldContain("side");
        document.Paths!.Items["/shapes/{id}"].Operations[OperationType.Get].Responses!.Items["200"].Content["application/json"].Schema!.Reference!.Ref
            .ShouldBe("#/components/schemas/Shape");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: collections, dictionaries, enums, binary and value-type objects")]
    public async Task Schemas_Envelope_ShouldDescribeEveryShape()
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_1);
        OpenApiSchema envelope = GetComponent(document, "Envelope");

        // Assert — a repeated List<int> is written inline both times, never as a pointer into another schema.
        envelope.Properties["buckets"].AdditionalProperties!.Type.ShouldBe(SchemaType.Array);
        envelope.Properties["buckets"].AdditionalProperties!.Items!.Format.ShouldBe("int32");
        envelope.Properties["matrix"].Items!.Type.ShouldBe(SchemaType.Array);
        envelope.Properties["matrix"].Items!.Items!.Type.ShouldBe(SchemaType.Integer);
        envelope.Properties["named"].Type.ShouldBe(SchemaType.String);
        envelope.Properties["named"].Enum.Count.ShouldBe(2);
        envelope.Properties["maybe"].Nullable.ShouldBeTrue();
        envelope.Properties["blob"].Format.ShouldBe("byte");
        envelope.Properties["where"].Reference!.Ref.ShouldBe("#/components/schemas/Point");
        envelope.Properties["maybeWhere"].AnyOf[0].Reference!.Ref.ShouldBe("#/components/schemas/Point");
        GetComponent(document, "Point").Nullable.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: no schema keeps a pointer relative to another export")]
    public async Task Schemas_References_ShouldAllTargetComponents()
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_1);
        string json = Assimalign.Cohesion.OpenApi.Serialization.OpenApiJson.Serialize(document, indented: false);

        // Assert
        int index = 0;

        while ((index = json.IndexOf("\"$ref\":\"", index, StringComparison.Ordinal)) >= 0)
        {
            index += 8;
            json[index..].ShouldStartWith("#/components/", Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: the schemas validate on every supported line")]
    [InlineData(OpenApiSpecVersion.V3_0)]
    [InlineData(OpenApiSpecVersion.V3_1)]
    [InlineData(OpenApiSpecVersion.V3_2)]
    public async Task Schemas_EverySupportedLine_ShouldValidate(OpenApiSpecVersion version)
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(version);
        OpenApiValidationResult validation = document.Validate();

        // Assert
        validation.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => diagnostic.ToString())));
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Schemas: OpenAPI 3.0 spells const as a one-value enum")]
    public async Task Schemas_Const_ShouldBecomeEnumFor30()
    {
        // Arrange / Act
        OpenApiDocument document = await GetDocumentAsync(OpenApiSpecVersion.V3_0);
        OpenApiSchema kind = GetComponent(document, "Shape").AnyOf[0].Properties["kind"];

        // Assert
        kind.Const.ShouldBeNull();
        kind.Enum.ShouldHaveSingleItem();
    }
}
