using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

public class JsonSchemaGenerationTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Generate: ";

    private static readonly JsonSchemaGenerationOptions _nonNullable = new() { TreatNullObliviousAsNonNullable = true };

    [Fact(DisplayName = DisplayPrefix + "Should refuse a null contract")]
    public void Generate_NullTypeInfo_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => JsonSchemaDocument.Generate(null!));
    }

    [Fact(DisplayName = DisplayPrefix + "Should stamp the 2020-12 $schema first by default")]
    public void Generate_Default_ShouldStampDraft202012SchemaFirst()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person);

        // Assert
        document.Dialect.ShouldBeSameAs(JsonSchemaDialect.Draft202012);
        SchemaJson.Names(document.Source)[0].ShouldBe("$schema");
        SchemaJson.ShouldHave(document, "/$schema", "\"https://json-schema.org/draft/2020-12/schema\"");
        SchemaJson.Has(document, "/$id").ShouldBeFalse();
        document.BaseUri.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Should stamp $id after $schema and load the document with it as base URI")]
    public void Generate_WithId_ShouldStampIdAndBaseUri()
    {
        // Arrange
        Uri id = new("https://example.com/schemas/person.json");

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person, new JsonSchemaGenerationOptions { Id = id });

        // Assert
        SchemaJson.Names(document.Source)[..2].ShouldBe(["$schema", "$id"]);
        SchemaJson.ShouldHave(document, "/$id", "\"https://example.com/schemas/person.json\"");
        document.BaseUri.ShouldBe(id);
    }

    [Fact(DisplayName = DisplayPrefix + "Should register an identified document in the registry the document options name")]
    public void Generate_WithIdAndRegistry_ShouldRegisterDocument()
    {
        // Arrange
        Uri id = new("https://example.com/schemas/order.json");
        JsonSchemaRegistry registry = new();

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(
            TestContracts.Default.Order,
            new JsonSchemaGenerationOptions { Id = id },
            new JsonSchemaDocumentOptions { Registry = registry });

        // Assert
        document.Registry.ShouldBeSameAs(registry);
        registry.TryGetSchema(id, out IJsonSchema? registered).ShouldBeTrue();
        registered.Document.ShouldBeSameAs(document);
        registered.Location.IsRoot.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Should stamp 2019-09 and keep definitions under $defs")]
    public void Generate_Draft201909_ShouldUseDefs()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person, new JsonSchemaGenerationOptions { Dialect = JsonSchemaDialect.Draft201909 });

        // Assert
        document.Dialect.ShouldBeSameAs(JsonSchemaDialect.Draft201909);
        SchemaJson.ShouldHave(document, "/$schema", "\"https://json-schema.org/draft/2019-09/schema\"");
        SchemaJson.ShouldHave(document, "/properties/Home", """{"$ref":"#/$defs/Address"}""");
        SchemaJson.Has(document, "/$defs/Address").ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Should stamp draft-07 and keep definitions under definitions")]
    public void Generate_Draft07_ShouldUseDefinitions()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person, new JsonSchemaGenerationOptions { Dialect = JsonSchemaDialect.Draft07 });

        // Assert
        document.Dialect.ShouldBeSameAs(JsonSchemaDialect.Draft07);
        SchemaJson.ShouldHave(document, "/$schema", "\"http://json-schema.org/draft-07/schema#\"");
        SchemaJson.ShouldHave(document, "/properties/Home", """{"$ref":"#/definitions/Address"}""");
        SchemaJson.ShouldHave(document, "/properties/Work", """{"anyOf":[{"$ref":"#/definitions/Address"},{"type":"null"}]}""");
        SchemaJson.Has(document, "/definitions/Address").ShouldBeTrue();
        SchemaJson.Has(document, "/$defs").ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should keep the siblings of $ref in 2020-12")]
    public void Generate_Draft202012ReferenceWithSiblings_ShouldKeepSiblings()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Route);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/From", """{"$ref":"#/$defs/Point","default":{"X":0,"Y":0}}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should move a $ref with siblings into allOf in draft-07, which ignores the siblings")]
    public void Generate_Draft07ReferenceWithSiblings_ShouldWrapInAllOf()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Route, new JsonSchemaGenerationOptions { Dialect = JsonSchemaDialect.Draft07 });

        // Assert
        SchemaJson.ShouldHave(document, "/properties/From", """{"default":{"X":0,"Y":0},"allOf":[{"$ref":"#/definitions/Point"}]}""");
        SchemaJson.ShouldHave(document, "/properties/To", """{"default":{"X":0,"Y":0},"allOf":[{"$ref":"#/definitions/Point"}]}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should describe a type used in one place inline")]
    public void Generate_TypeUsedOnce_ShouldInline()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Customer);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Home", """{"type":"object","properties":{"Street":{"type":"string"},"City":{"type":["string","null"]}}}""");
        SchemaJson.Has(document, "/$defs").ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should define a type used twice once, and apply each use's nullability to its reference")]
    public void Generate_TypeUsedTwice_ShouldHoistIntoDefs()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Person);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Home", """{"$ref":"#/$defs/Address"}""");
        SchemaJson.ShouldHave(document, "/properties/Work", """{"anyOf":[{"$ref":"#/$defs/Address"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/$defs/Address", """{"type":"object","properties":{"Street":{"type":"string"},"City":{"type":["string","null"]}}}""");
        SchemaJson.Names(document.Source)[^1].ShouldBe("$defs");
    }

    [Fact(DisplayName = DisplayPrefix + "Should define a struct used as a value and as a Nullable<T> once")]
    public void Generate_StructUsedAsValueAndNullable_ShouldShareDefinition()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Segment);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Start", """{"$ref":"#/$defs/Point"}""");
        SchemaJson.ShouldHave(document, "/properties/End", """{"anyOf":[{"$ref":"#/$defs/Point"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/$defs/Point", """{"type":"object","properties":{"X":{"type":"integer"},"Y":{"type":"integer"}}}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should describe a type used once inside a definition inline in that definition")]
    public void Generate_TypeUsedOnceInsideDefinition_ShouldInline()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Report);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Current", """{"anyOf":[{"$ref":"#/$defs/PageOfOrder"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/$defs/PageOfOrder/properties/Items/items/properties/Shipping", """{"$ref":"#/$defs/Address"}""");
        SchemaJson.Has(document, "/$defs/Order").ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should name a generic definition from its type arguments and sort definitions by name")]
    public void Generate_GenericType_ShouldNameFromArgumentsAndSort()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Report);

        // Assert
        SchemaJson.Names(SchemaJson.At(document, "/$defs")).ShouldBe(["Address", "PageOfOrder"]);
    }

    [Fact(DisplayName = DisplayPrefix + "Should number the definitions of different types that share a name, in the order they are met")]
    public void Generate_TypesSharingName_ShouldNumberDefinitions()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Stock);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Featured", """{"anyOf":[{"$ref":"#/$defs/Item"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/properties/Reserved", """{"anyOf":[{"$ref":"#/$defs/Item2"},{"type":"null"}]}""");
        SchemaJson.Has(document, "/$defs/Item/properties/Sku").ShouldBeTrue();
        SchemaJson.Has(document, "/$defs/Item2/properties/Count").ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Should refer to the root with # when a non-nullable root type contains itself")]
    public void Generate_RecursiveRoot_ShouldReferToRoot()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Node, _nonNullable);

        // Assert
        SchemaJson.ShouldHave(document, "/type", "\"object\"");
        SchemaJson.ShouldHave(document, "/properties/Next", """{"anyOf":[{"$ref":"#"},{"type":"null"}]}""");
        SchemaJson.Has(document, "/$defs").ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should define a recursive root type when the root allows null and its references must not")]
    public void Generate_NullableRecursiveRoot_ShouldHoistRoot()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Node);

        // Assert
        SchemaJson.ShouldHave(document, "/anyOf", """[{"$ref":"#/$defs/Node"},{"type":"null"}]""");
        SchemaJson.ShouldHave(document, "/$defs/Node/type", "\"object\"");
        SchemaJson.ShouldHave(document, "/$defs/Node/properties/Next", """{"anyOf":[{"$ref":"#/$defs/Node"},{"type":"null"}]}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should share one definition between the uses of a recursive type and its own reference")]
    public void Generate_RecursiveTypeUsedTwice_ShouldShareDefinition()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.NodePair);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/First", """{"anyOf":[{"$ref":"#/$defs/Node"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/properties/Second", """{"anyOf":[{"$ref":"#/$defs/Node"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/$defs/Node/properties/Next", """{"anyOf":[{"$ref":"#/$defs/Node"},{"type":"null"}]}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should refer to a type that contains itself through a list and a dictionary")]
    public void Generate_RecursionThroughCollections_ShouldReferToDefinition()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.TreeNode);

        // Assert
        SchemaJson.ShouldHave(document, "/$defs/TreeNode/properties/Children/items", """{"anyOf":[{"$ref":"#/$defs/TreeNode"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/$defs/TreeNode/properties/Index/additionalProperties", """{"anyOf":[{"$ref":"#/$defs/TreeNode"},{"type":"null"}]}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should refer to the root from the items of a root collection that contains itself")]
    public void Generate_CollectionContainingItself_ShouldReferToRoot()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Nested);

        // Assert
        SchemaJson.ShouldHave(document, "/items", """{"$ref":"#"}""");
        SchemaJson.Has(document, "/$defs").ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should define a collection that contains itself when it is not the root")]
    public void Generate_CollectionContainingItselfInProperty_ShouldHoistCollection()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.NestedPair);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Left/items", """{"$ref":"#/$defs/Nested"}""");
        SchemaJson.ShouldHave(document, "/properties/Right/items", """{"$ref":"#/$defs/Nested"}""");
        SchemaJson.ShouldHave(document, "/$defs/Nested/items", """{"$ref":"#/$defs/Nested"}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should describe a polymorphic base by its derived types, with their discriminators")]
    public void Generate_PolymorphicBase_ShouldDescribeDerivedTypes()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Shape);

        // Assert
        SchemaJson.ShouldHave(document, "/anyOf", """[{"$ref":"#/$defs/Shape"},{"type":"null"}]""");
        SchemaJson.At(document, "/$defs/Shape/anyOf").GetArrayLength().ShouldBe(2);
        SchemaJson.ShouldHave(document, "/$defs/Shape/anyOf/0/properties/kind", """{"const":"circle"}""");
        SchemaJson.ShouldHave(document, "/$defs/Shape/anyOf/1/properties/kind", """{"const":2}""");
        SchemaJson.ShouldHave(document, "/$defs/Shape/anyOf/1/properties/Inner", """{"anyOf":[{"$ref":"#/$defs/Shape"},{"type":"null"}]}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should allow null where a polymorphic value of mixed kinds is nullable, which the exporter omits")]
    public void Generate_NullableMixedKindPolymorphicMember_ShouldAllowNull()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Envelope, _nonNullable);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Body", """{"$ref":"#/$defs/Payload"}""");
        SchemaJson.ShouldHave(document, "/properties/Attachment", """{"anyOf":[{"$ref":"#/$defs/Payload"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/$defs/Payload/anyOf/1", """{"type":"array","items":{"type":"integer"}}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should accept any value for a property with its own converter, which its contract does not describe")]
    public void Generate_PropertyWithCustomConverter_ShouldAcceptAnyValue()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Invoice);

        // Assert
        SchemaJson.ShouldHave(document, "/properties/Total", "true");
        SchemaJson.ShouldHave(document, "/properties/Tax/properties/Amount", """{"type":"number"}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should forbid other members where the contract disallows unmapped members")]
    public void Generate_UnmappedMembersDisallowed_ShouldForbidAdditionalProperties()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Strict);

        // Assert
        SchemaJson.ShouldHave(document, "/additionalProperties", "false");
        SchemaJson.ShouldHave(document, "/properties/Count/default", "1");
        SchemaJson.ShouldHave(document, "/required", """["Name"]""");
    }

    [Fact(DisplayName = DisplayPrefix + "Should allow other members where the contract has extension data")]
    public void Generate_ExtensionData_ShouldAllowAdditionalProperties()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.Flexible);

        // Assert
        SchemaJson.Has(document, "/additionalProperties").ShouldBeFalse();
        SchemaJson.Has(document, "/properties/Extra").ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Should use the member names and enum strings of the contract's options")]
    public void Generate_NamingPolicyAndStringEnums_ShouldUseSerializedNames()
    {
        // Act
        JsonSchemaDocument snakeCase = JsonSchemaDocument.Generate(SnakeCaseContracts.Default.Order);
        JsonSchemaDocument web = JsonSchemaDocument.Generate(WebContracts.Default.Order);

        // Assert
        SchemaJson.Has(snakeCase, "/properties/previous_status").ShouldBeTrue();
        SchemaJson.ShouldHave(snakeCase, "/required", """["number","shipping"]""");
        SchemaJson.ShouldHave(web, "/properties/status", """{"enum":["Pending","Shipped","Delivered"]}""");
        SchemaJson.Has(web, "/$defs/Address/properties/street").ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Should describe a root collection by its items")]
    public void Generate_RootCollection_ShouldDescribeItems()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.ListOrder, _nonNullable);

        // Assert
        SchemaJson.ShouldHave(document, "/type", "\"array\"");
        SchemaJson.ShouldHave(document, "/items/properties/Billing", """{"anyOf":[{"$ref":"#/$defs/Address"},{"type":"null"}]}""");
        SchemaJson.ShouldHave(document, "/items/type", "\"object\"");
    }

    [Fact(DisplayName = DisplayPrefix + "Should accept anything for a JsonElement root, with only $schema")]
    public void Generate_JsonElementRoot_ShouldAcceptAnything()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Generate(TestContracts.Default.JsonElement);

        // Assert
        SchemaJson.Names(document.Source).ShouldBe(["$schema"]);
        document.IsValid(SchemaJson.Parse("""{"a":[1,"b",null]}""")).ShouldBeTrue();
        document.IsValid(SchemaJson.Parse("null")).ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Should generate the same document for the same contract")]
    public void Generate_SameContract_ShouldProduceSameDocument()
    {
        // Act
        JsonSchemaDocument first = JsonSchemaDocument.Generate(TestContracts.Default.Report);
        JsonSchemaDocument second = JsonSchemaDocument.Generate(TestContracts.Default.Report);

        // Assert
        SchemaJson.Text(second.Source).ShouldBe(SchemaJson.Text(first.Source));
    }

    [Fact(DisplayName = DisplayPrefix + "Should refuse a contract that preserves references, which the exporter cannot describe")]
    public void Generate_PreserveReferences_ShouldThrowNotSupportedException()
    {
        // Arrange
        JsonSerializerOptions options = new()
        {
            TypeInfoResolver = TestContracts.Default,
            ReferenceHandler = ReferenceHandler.Preserve,
        };
        JsonTypeInfo typeInfo = options.GetTypeInfo(typeof(Order));

        // Act & Assert
        Should.Throw<NotSupportedException>(() => JsonSchemaDocument.Generate(typeInfo));
    }
}
