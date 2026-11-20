using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

/// <summary>
/// Serializes values with a contract and validates them against the document generated from the same
/// contract, in each dialect. Each document is also validated against its dialect's meta-schema.
/// </summary>
public class RoundTripTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Generate round trip: ";

    public static TheoryData<string> Dialects => TestDialects.Names;

    [Theory(DisplayName = DisplayPrefix + "Should accept orders: required and nullable members, enums, a list and a dictionary")]
    [MemberData(nameof(Dialects))]
    public void Generate_Order_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument document = Generate(TestContracts.Default.Order, dialect);

        // Act & Assert
        ShouldAccept(document, TestContracts.Default.Order, CreateOrder());
        ShouldAccept(document, TestContracts.Default.Order, CreateMinimalOrder());
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept a root list, whose null-oblivious items allow null")]
    [MemberData(nameof(Dialects))]
    public void Generate_RootList_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument document = Generate(TestContracts.Default.ListOrder, dialect);

        // Act & Assert
        ShouldAccept(document, TestContracts.Default.ListOrder, [CreateOrder(), CreateMinimalOrder(), null!]);
        ShouldAccept(document, TestContracts.Default.ListOrder, null!);
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept the names of a naming policy and the strings of a string enum converter")]
    [MemberData(nameof(Dialects))]
    public void Generate_NamingPolicies_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument web = Generate(WebContracts.Default.Order, dialect);
        JsonSchemaDocument snakeCase = Generate(SnakeCaseContracts.Default.Order, dialect);

        // Act & Assert
        ShouldAccept(web, WebContracts.Default.Order, CreateOrder());
        ShouldAccept(web, WebContracts.Default.Order, CreateMinimalOrder());
        ShouldAccept(snakeCase, SnakeCaseContracts.Default.Order, CreateOrder());
        ShouldAccept(snakeCase, SnakeCaseContracts.Default.Order, CreateMinimalOrder());
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept shared types, inline and defined, null or not")]
    [MemberData(nameof(Dialects))]
    public void Generate_SharedTypes_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument customer = Generate(TestContracts.Default.Customer, dialect);
        JsonSchemaDocument person = Generate(TestContracts.Default.Person, dialect);
        JsonSchemaDocument report = Generate(TestContracts.Default.Report, dialect);
        JsonSchemaDocument segment = Generate(TestContracts.Default.Segment, dialect);
        JsonSchemaDocument route = Generate(TestContracts.Default.Route, dialect);
        JsonSchemaDocument stock = Generate(TestContracts.Default.Stock, dialect);

        // Act & Assert
        ShouldAccept(customer, TestContracts.Default.Customer, new Customer { Name = "Ada", Home = new Address { Street = "1 Main St" } });
        ShouldAccept(person, TestContracts.Default.Person, new Person { Name = "Ada", Home = new Address { Street = "1 Main St", City = "Springfield" } });
        ShouldAccept(person, TestContracts.Default.Person, new Person { Name = "Ada", Work = new Address { Street = "2 Side St" } });
        ShouldAccept(report, TestContracts.Default.Report, new Report { Current = new Page<Order> { Items = { CreateOrder(), CreateMinimalOrder() }, Total = 2 } });
        ShouldAccept(report, TestContracts.Default.Report, new Report());
        ShouldAccept(segment, TestContracts.Default.Segment, new Segment { Start = new Point { X = 1, Y = 2 } });
        ShouldAccept(segment, TestContracts.Default.Segment, new Segment { Start = new Point { X = 1, Y = 2 }, End = new Point { X = 3, Y = 4 } });
        ShouldAccept(route, TestContracts.Default.Route, new Route(new Point { X = 5, Y = 6 }));
        ShouldAccept(stock, TestContracts.Default.Stock, new Stock
        {
            Listed = new Catalog.Item { Sku = "A-1" },
            Featured = new Catalog.Item { Sku = "B-2" },
            OnHand = new Inventory.Item { Count = 4 },
        });
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept recursive values: a chain, a tree, and collections of themselves")]
    [MemberData(nameof(Dialects))]
    public void Generate_RecursiveTypes_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument node = Generate(TestContracts.Default.Node, dialect);
        JsonSchemaDocument nonNullableNode = Generate(TestContracts.Default.Node, dialect, new JsonSchemaGenerationOptions { TreatNullObliviousAsNonNullable = true });
        JsonSchemaDocument tree = Generate(TestContracts.Default.TreeNode, dialect);
        JsonSchemaDocument pair = Generate(TestContracts.Default.NodePair, dialect);
        JsonSchemaDocument nested = Generate(TestContracts.Default.Nested, dialect);
        JsonSchemaDocument nestedPair = Generate(TestContracts.Default.NestedPair, dialect);
        Node chain = new() { Value = 1, Next = new Node { Value = 2, Next = new Node { Value = 3 } } };

        // Act & Assert
        ShouldAccept(node, TestContracts.Default.Node, chain);
        ShouldAccept(node, TestContracts.Default.Node, null!);
        ShouldAccept(nonNullableNode, TestContracts.Default.Node, chain);
        ShouldAccept(tree, TestContracts.Default.TreeNode, new TreeNode
        {
            Name = "root",
            Children = { new TreeNode { Name = "a", Children = { new TreeNode { Name = "a.1" } } }, new TreeNode { Name = "b" } },
            Index = new Dictionary<string, TreeNode> { ["c"] = new TreeNode { Name = "c" } },
        });
        ShouldAccept(pair, TestContracts.Default.NodePair, new NodePair { First = chain, Second = new Node { Value = 9 } });
        ShouldAccept(nested, TestContracts.Default.Nested, [[], [[], [[]]]]);
        ShouldAccept(nestedPair, TestContracts.Default.NestedPair, new NestedPair { Left = [[[]]], Right = [] });
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept polymorphic values: string and number discriminators, and a derived type without one")]
    [MemberData(nameof(Dialects))]
    public void Generate_Polymorphism_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument shape = Generate(TestContracts.Default.Shape, dialect);
        JsonSchemaDocument animal = Generate(TestContracts.Default.Animal, dialect);

        // Act & Assert
        ShouldAccept(shape, TestContracts.Default.Shape, new Circle { Radius = 2, Label = "wheel" });
        ShouldAccept(shape, TestContracts.Default.Shape, new Square { Side = 3, Inner = new Circle { Radius = 1 } });
        ShouldAccept(shape, TestContracts.Default.Shape, new Square { Side = 3, Inner = new Square { Side = 1 } });
        ShouldAccept(animal, TestContracts.Default.Animal, new Dog { Name = "Rex", Good = true });
        ShouldAccept(animal, TestContracts.Default.Animal, new Cat { Name = "Tom", Lives = 9 });
        ShouldAccept(animal, TestContracts.Default.Animal, new Animal { Name = "Generic" });
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept polymorphic values of mixed kinds, and null where the member is nullable")]
    [MemberData(nameof(Dialects))]
    public void Generate_MixedKindPolymorphism_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument envelope = Generate(TestContracts.Default.Envelope, dialect);
        JsonSchemaDocument nonNullable = Generate(TestContracts.Default.Envelope, dialect, new JsonSchemaGenerationOptions { TreatNullObliviousAsNonNullable = true });
        Envelope[] values =
        [
            new Envelope { Body = new TextPayload { Value = "hello" } },
            new Envelope { Body = new NumberPayload { 1, 2, 3 }, Attachment = new TextPayload { Value = "note" } },
            new Envelope { Body = new TextPayload { Value = "hello" }, Attachment = new NumberPayload { 4 } },
        ];

        // Act & Assert
        foreach (Envelope value in values)
        {
            ShouldAccept(envelope, TestContracts.Default.Envelope, value);
            ShouldAccept(nonNullable, TestContracts.Default.Envelope, value);
        }
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept members a converter, extension data or a strict contract shapes")]
    [MemberData(nameof(Dialects))]
    public void Generate_ConvertersAndMemberHandling_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument invoice = Generate(TestContracts.Default.Invoice, dialect);
        JsonSchemaDocument strict = Generate(TestContracts.Default.Strict, dialect);
        JsonSchemaDocument flexible = Generate(TestContracts.Default.Flexible, dialect);

        // Act & Assert
        ShouldAccept(invoice, TestContracts.Default.Invoice, new Invoice { Total = new Money(12.5m), Tax = new Money(1.25m) });
        ShouldAccept(invoice, TestContracts.Default.Invoice, new Invoice { Total = new Money(3m) });
        ShouldAccept(strict, TestContracts.Default.Strict, new Strict("widget", 2));
        ShouldAccept(flexible, TestContracts.Default.Flexible, new Flexible
        {
            Name = "widget",
            Extra = new Dictionary<string, JsonElement> { ["color"] = SchemaJson.Parse("\"red\""), ["size"] = SchemaJson.Parse("3") },
        });
    }

    [Theory(DisplayName = DisplayPrefix + "Should accept primitives, number handling and formats, with format assertion on")]
    [MemberData(nameof(Dialects))]
    public void Generate_PrimitivesWithFormatAssertion_ShouldAcceptSerializedValues(string dialect)
    {
        // Arrange
        JsonSchemaDocument primitives = Generate(TestContracts.Default.Primitives, dialect);
        JsonSchemaDocument order = Generate(TestContracts.Default.Order, dialect);
        JsonSchemaEvaluationOptions formatAssertion = new() { FormatAssertion = true };

        // Act & Assert
        ShouldAccept(primitives, TestContracts.Default.Primitives, CreatePrimitives(double.NaN), formatAssertion);
        ShouldAccept(primitives, TestContracts.Default.Primitives, CreatePrimitives(0.5), formatAssertion);
        ShouldAccept(order, TestContracts.Default.Order, CreateOrder(), formatAssertion);
    }

    [Theory(DisplayName = DisplayPrefix + "Should reject values the contract would not write")]
    [MemberData(nameof(Dialects))]
    public void Generate_InvalidValues_ShouldBeRejected(string dialect)
    {
        // Arrange
        JsonSchemaDocument order = Generate(TestContracts.Default.Order, dialect);
        JsonSchemaDocument web = Generate(WebContracts.Default.Order, dialect);
        JsonSchemaDocument person = Generate(TestContracts.Default.Person, dialect);
        JsonSchemaDocument node = Generate(TestContracts.Default.Node, dialect);
        JsonSchemaDocument shape = Generate(TestContracts.Default.Shape, dialect);
        JsonSchemaDocument envelope = Generate(TestContracts.Default.Envelope, dialect);
        JsonSchemaDocument strict = Generate(TestContracts.Default.Strict, dialect);
        JsonSchemaDocument nestedPair = Generate(TestContracts.Default.NestedPair, dialect);

        // Act & Assert
        ShouldReject(order, """{"Number":"SO-1"}""");
        ShouldReject(order, """{"Number":"SO-1","Shipping":{"Street":"1 Main St"},"Priority":"high"}""");
        ShouldReject(order, """{"Number":"SO-1","Shipping":{"Street":"1 Main St"},"Billing":{"Street":7}}""");
        ShouldReject(web, """{"number":"SO-1","shipping":{"street":"1 Main St"},"status":"Cancelled"}""");
        ShouldReject(person, """{"Name":"Ada","Home":null}""");
        ShouldReject(node, """{"Value":1,"Next":{"Value":2,"Next":{"Value":"three"}}}""");
        ShouldReject(shape, """{"kind":"triangle","Side":1}""");
        ShouldReject(shape, """{"kind":2,"Side":1,"Inner":{"kind":"circle","Radius":"wide"}}""");
        ShouldReject(envelope, """{"Body":null}""");
        ShouldReject(envelope, """{"Body":[1,"two"]}""");
        ShouldReject(strict, """{"Name":"widget","Color":"red"}""");
        ShouldReject(nestedPair, """{"Left":[[["deep"]]]}""");
    }

    private static JsonSchemaDocument Generate(JsonTypeInfo typeInfo, string dialect, JsonSchemaGenerationOptions? options = null)
    {
        JsonSchemaGenerationOptions generation = new()
        {
            Dialect = TestDialects.Get(dialect),
            TreatNullObliviousAsNonNullable = options?.TreatNullObliviousAsNonNullable ?? false,
        };

        // Loading with ValidateSchema checks the document against its dialect's meta-schema.
        return JsonSchemaDocument.Generate(typeInfo, generation, new JsonSchemaDocumentOptions { ValidateSchema = true });
    }

    private static void ShouldAccept<T>(JsonSchemaDocument document, JsonTypeInfo<T> typeInfo, T value, JsonSchemaEvaluationOptions? options = null)
    {
        JsonElement instance = JsonSerializer.SerializeToElement(value, typeInfo);
        JsonSchemaEvaluationResult result = document.Evaluate(instance, new JsonSchemaEvaluationOptions
        {
            OutputFormat = JsonSchemaOutputFormat.Basic,
            FormatAssertion = options?.FormatAssertion ?? false,
        });

        result.IsValid.ShouldBeTrue("The document rejected " + SchemaJson.Text(instance) + ": " + result + Environment.NewLine + "Schema: " + SchemaJson.Text(document.Source));
    }

    private static void ShouldReject(JsonSchemaDocument document, string json) =>
        document.IsValid(SchemaJson.Parse(json)).ShouldBeFalse("The document accepted " + json + ": " + SchemaJson.Text(document.Source));

    private static Order CreateOrder() => new("SO-1001", priority: 1)
    {
        Shipping = new Address { Street = "1 Main St", City = "Springfield" },
        Billing = new Address { Street = "PO Box 7" },
        Lines =
        {
            new OrderLine { Sku = "A-1", Quantity = 2, Price = 9.99m },
            new OrderLine { Sku = "B-2", Quantity = 1, Price = 120m },
        },
        Status = OrderStatus.Shipped,
        PreviousStatus = OrderStatus.Pending,
        Metadata = new Dictionary<string, string> { ["channel"] = "web" },
        Created = new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.FromHours(-5)),
        Id = new Guid("8a0f8d5e-6a64-4b5e-9f0d-3c2a1b0e9d7c"),
        Note = "Leave at the door",
    };

    private static Order CreateMinimalOrder() => new("SO-1002")
    {
        Shipping = new Address(),
    };

    private static Primitives CreatePrimitives(double measured) => new()
    {
        Bytes = [1, 2, 3],
        When = new DateTime(2026, 11, 20, 13, 0, 0, DateTimeKind.Utc),
        Day = new DateOnly(2026, 11, 20),
        Duration = new TimeSpan(1, 2, 3, 4, 500),
        Link = new Uri("https://example.com/orders?status=open"),
        Initial = 'Q',
        Big = long.MaxValue,
        Ratio = 0.25,
        Anything = "anything",
        Raw = SchemaJson.Parse("""{"a":[1,true,null]}"""),
        Quoted = 42,
        Measured = measured,
    };
}
