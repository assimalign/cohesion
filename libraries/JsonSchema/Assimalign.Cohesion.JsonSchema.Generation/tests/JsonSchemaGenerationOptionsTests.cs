using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

public class JsonSchemaGenerationOptionsTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - JsonSchemaGenerationOptions: ";

    [Fact(DisplayName = DisplayPrefix + "Default: Should write 2020-12 with no identifier, nullable null-oblivious values and no transform")]
    public void Default_ShouldTargetDraft202012()
    {
        // Act
        JsonSchemaGenerationOptions options = JsonSchemaGenerationOptions.Default;

        // Assert
        options.Dialect.ShouldBeSameAs(JsonSchemaDialect.Draft202012);
        options.Id.ShouldBeNull();
        options.TreatNullObliviousAsNonNullable.ShouldBeFalse();
        options.TransformSchemaNode.ShouldBeNull();
    }

    [Theory(DisplayName = DisplayPrefix + "Dialect: Should accept draft-07, 2019-09 and 2020-12")]
    [InlineData("draft-07")]
    [InlineData("2019-09")]
    [InlineData("2020-12")]
    public void Dialect_Supported_ShouldBeAccepted(string name)
    {
        // Arrange
        JsonSchemaDialect dialect = TestDialects.Get(name);

        // Act
        JsonSchemaGenerationOptions options = new() { Dialect = dialect };

        // Assert
        options.Dialect.ShouldBeSameAs(dialect);
    }

    [Fact(DisplayName = DisplayPrefix + "Dialect: Should refuse null")]
    public void Dialect_Null_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => new JsonSchemaGenerationOptions { Dialect = null! });
    }

    [Fact(DisplayName = DisplayPrefix + "Dialect: Should refuse draft 4 and draft 6, which the generator does not write")]
    public void Dialect_Draft04OrDraft06_ShouldThrowArgumentException()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new JsonSchemaGenerationOptions { Dialect = JsonSchemaDialect.Draft04 })
            .Message.ShouldContain(JsonSchemaDialect.Draft04.Id.OriginalString, Case.Sensitive);
        Should.Throw<ArgumentException>(() => new JsonSchemaGenerationOptions { Dialect = JsonSchemaDialect.Draft06 });
    }

    [Fact(DisplayName = DisplayPrefix + "Dialect: Should refuse a custom dialect, even one built on the 2020-12 vocabularies")]
    public void Dialect_Custom_ShouldThrowArgumentException()
    {
        // Arrange
        JsonSchemaDialect dialect = JsonSchemaDialect.Create(new Uri("https://example.com/dialect/custom"),
        [
            JsonSchemaVocabulary.Draft202012Core,
            JsonSchemaVocabulary.Draft202012Applicator,
            JsonSchemaVocabulary.Draft202012Validation,
        ]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => new JsonSchemaGenerationOptions { Dialect = dialect });
    }

    [Fact(DisplayName = DisplayPrefix + "Id: Should accept an absolute URI without a fragment")]
    public void Id_Absolute_ShouldBeAccepted()
    {
        // Arrange
        Uri id = new("https://example.com/schemas/order.json");

        // Act
        JsonSchemaGenerationOptions options = new() { Id = id };

        // Assert
        options.Id.ShouldBe(id);
    }

    [Fact(DisplayName = DisplayPrefix + "Id: Should refuse a relative URI")]
    public void Id_Relative_ShouldThrowArgumentException()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new JsonSchemaGenerationOptions { Id = new Uri("schemas/order.json", UriKind.Relative) });
    }

    [Theory(DisplayName = DisplayPrefix + "Id: Should refuse a URI with a fragment, empty or not")]
    [InlineData("https://example.com/order.json#")]
    [InlineData("https://example.com/order.json#/definitions/Order")]
    public void Id_WithFragment_ShouldThrowArgumentException(string id)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new JsonSchemaGenerationOptions { Id = new Uri(id) });
    }
}
