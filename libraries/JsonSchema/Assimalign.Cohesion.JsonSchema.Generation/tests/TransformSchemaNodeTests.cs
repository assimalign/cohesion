using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

public class TransformSchemaNodeTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - TransformSchemaNode: ";

    public static TheoryData<string> Dialects => TestDialects.Names;

    [Fact(DisplayName = DisplayPrefix + "Should pass a definition once, without a property, and each use as a reference with its property")]
    public void TransformSchemaNode_DefinedType_ShouldSeeDefinitionAndReferences()
    {
        // Arrange
        List<TransformCall> calls = new();

        // Act
        JsonSchemaDocument.Generate(TestContracts.Default.Person, Record(calls));

        // Assert
        calls.FindAll(call => call.Type == typeof(Address) && !call.IsReference)
            .ShouldHaveSingleItem().Property.ShouldBeNull();
        calls.FindAll(call => call.Type == typeof(Address) && call.IsReference)
            .ConvertAll(call => call.Property).ShouldBe(["Home", "Work"]);
    }

    [Fact(DisplayName = DisplayPrefix + "Should pass the Nullable<T> contract for a nullable use and the underlying one for the definition")]
    public void TransformSchemaNode_NullableStruct_ShouldSeeNullableContractAtUse()
    {
        // Arrange
        List<TransformCall> calls = new();

        // Act
        JsonSchemaDocument.Generate(TestContracts.Default.Segment, Record(calls));

        // Assert
        calls.ShouldContain(call => call.Type == typeof(Point) && call.Property == "Start" && call.IsReference);
        calls.ShouldContain(call => call.Type == typeof(Point?) && call.Property == "End" && call.IsReference);
        calls.ShouldContain(call => call.Type == typeof(Point) && call.Property == null && !call.IsReference);
    }

    [Fact(DisplayName = DisplayPrefix + "Should pass a node's children before the node")]
    public void TransformSchemaNode_Children_ShouldBeSeenBeforeParent()
    {
        // Arrange
        List<TransformCall> calls = new();

        // Act
        JsonSchemaDocument.Generate(TestContracts.Default.Person, Record(calls));

        // Assert
        int root = calls.FindIndex(call => call.Type == typeof(Person));
        int definition = calls.FindIndex(call => call.Type == typeof(Address) && !call.IsReference);
        calls.FindIndex(call => call.Property == "Home").ShouldBeLessThan(root);
        calls.FindIndex(call => call.Property == "Work").ShouldBeLessThan(root);
        calls.FindIndex(call => call.Property == "Street").ShouldBeLessThan(definition);
        calls.FindIndex(call => call.Property == "City").ShouldBeLessThan(definition);
    }

    [Fact(DisplayName = DisplayPrefix + "Should pass the root without $schema, which is added after the transform")]
    public void TransformSchemaNode_Root_ShouldBeSeenBeforeSchemaIsStamped()
    {
        // Arrange
        bool rootHadSchema = true;
        JsonSchemaGenerationOptions options = new()
        {
            TransformSchemaNode = (context, node) =>
            {
                if (context.TypeInfo.Type == typeof(Person))
                {
                    rootHadSchema = ((JsonObject)node).ContainsKey("$schema");
                    node["$schema"] = "https://example.com/not-a-dialect";
                }

                return node;
            },
        };

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person, options);

        // Assert
        rootHadSchema.ShouldBeFalse();
        SchemaJson.ShouldHave(document, "/$schema", "\"https://json-schema.org/draft/2020-12/schema\"");
    }

    [Fact(DisplayName = DisplayPrefix + "Should pass each derived type of a polymorphic type with its base type")]
    public void TransformSchemaNode_DerivedType_ShouldSeeBaseTypeInfo()
    {
        // Arrange
        List<TransformCall> calls = new();

        // Act
        JsonSchemaDocument.Generate(TestContracts.Default.Shape, Record(calls));

        // Assert
        calls.ShouldContain(call => call.Type == typeof(Circle) && call.BaseType == typeof(Shape) && call.Property == null);
        calls.ShouldContain(call => call.Type == typeof(Square) && call.BaseType == typeof(Shape) && call.Property == null);
    }

    [Theory(DisplayName = DisplayPrefix + "Should keep what a transform adds to a reference, applied from allOf in draft-07")]
    [MemberData(nameof(Dialects))]
    public void TransformSchemaNode_DescriptionOnReference_ShouldBeKept(string dialect)
    {
        // Arrange
        string definitions = TestDialects.GetDefinitionsKeyword(dialect);
        JsonSchemaGenerationOptions options = new()
        {
            Dialect = TestDialects.Get(dialect),
            TransformSchemaNode = (context, node) =>
            {
                if (context.IsReference && context.PropertyInfo is { } property)
                {
                    node["description"] = "The " + property.Name + " address.";
                }

                return node;
            },
        };

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person, options, new JsonSchemaDocumentOptions { ValidateSchema = true });

        // Assert
        string home = dialect == "draft-07"
            ? """{"description":"The Home address.","allOf":[{"$ref":"#/definitions/Address"}]}"""
            : """{"$ref":"#/$defs/Address","description":"The Home address."}""";
        SchemaJson.ShouldHave(document, "/properties/Home", home);
        SchemaJson.ShouldHave(document, "/properties/Work", """{"anyOf":[{"$ref":"#/""" + definitions + """/Address"},{"type":"null"}],"description":"The Work address."}""");
        document.IsValid(SchemaJson.Parse("""{"Name":"Ada","Home":{"Street":"1 Main St"}}""")).ShouldBeTrue();
        document.IsValid(SchemaJson.Parse("""{"Name":"Ada","Home":{"Street":1}}""")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should use a replacement node, such as a schema for what a property's converter writes")]
    public void TransformSchemaNode_ReplacementNode_ShouldBeUsed()
    {
        // Arrange
        JsonSchemaGenerationOptions options = new()
        {
            TransformSchemaNode = (context, node) =>
                context.PropertyInfo?.Name == "Total" ? new JsonObject { ["type"] = "string", ["pattern"] = "^-?[0-9]+(\\.[0-9]+)?$" } : node,
        };

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Invoice, options);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Total", """{"type":"string","pattern":"^-?[0-9]+(\\.[0-9]+)?$"}""");
        document.IsValid(SchemaJson.Parse("""{"Total":"12.50","Tax":null}""")).ShouldBeTrue();
        document.IsValid(SchemaJson.Parse("""{"Total":12.5,"Tax":null}""")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should merge definitions a transform adds to the root with the generated ones")]
    public void TransformSchemaNode_RootDefinitions_ShouldBeMerged()
    {
        // Arrange
        JsonSchemaGenerationOptions options = new()
        {
            TransformSchemaNode = (context, node) =>
            {
                if (context.TypeInfo.Type == typeof(Person))
                {
                    node["$defs"] = new JsonObject { ["Note"] = new JsonObject { ["type"] = "string" } };
                    node["properties"]!["Name"] = new JsonObject { ["$ref"] = "#/$defs/Note" };
                }

                return node;
            },
        };

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person, options);

        // Assert
        SchemaJson.Names(SchemaJson.At(document, "/$defs")).ShouldBe(["Note", "Address"]);
        document.IsValid(SchemaJson.Parse("""{"Name":"Ada","Home":{"Street":"1 Main St"}}""")).ShouldBeTrue();
        document.IsValid(SchemaJson.Parse("""{"Name":7,"Home":{"Street":"1 Main St"}}""")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should refuse a root definition that has the name of a generated one")]
    public void TransformSchemaNode_ConflictingRootDefinition_ShouldThrowInvalidOperationException()
    {
        // Arrange
        JsonSchemaGenerationOptions options = new()
        {
            TransformSchemaNode = (context, node) =>
            {
                if (context.TypeInfo.Type == typeof(Person))
                {
                    node["$defs"] = new JsonObject { ["Address"] = new JsonObject() };
                }

                return node;
            },
        };

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => JsonSchemaDocument.Generate(TestContracts.Default.Person, options))
            .Message.ShouldContain("'Address'", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Should refuse a null result")]
    public void TransformSchemaNode_NullResult_ShouldThrowInvalidOperationException()
    {
        // Arrange
        JsonSchemaGenerationOptions options = new() { TransformSchemaNode = (context, node) => null! };

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => JsonSchemaDocument.Generate(TestContracts.Default.Person, options));
    }

    [Fact(DisplayName = DisplayPrefix + "Should refuse a result that belongs to another node")]
    public void TransformSchemaNode_ParentedResult_ShouldThrowInvalidOperationException()
    {
        // Arrange
        JsonObject holder = new() { ["held"] = new JsonObject() };
        JsonSchemaGenerationOptions options = new() { TransformSchemaNode = (context, node) => holder["held"]! };

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => JsonSchemaDocument.Generate(TestContracts.Default.Person, options));
    }

    private static JsonSchemaGenerationOptions Record(List<TransformCall> calls) => new()
    {
        TransformSchemaNode = (context, node) =>
        {
            calls.Add(new TransformCall(context.TypeInfo.Type, context.PropertyInfo?.Name, context.BaseTypeInfo?.Type, context.IsReference));
            return node;
        },
    };

    /// <summary>One call of the transform: the contract it described and where.</summary>
    private sealed record TransformCall(Type Type, string? Property, Type? BaseType, bool IsReference);
}
