using System;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

/// <summary>
/// Composition-surface coverage: what <c>AddContentSerialization</c> attaches to the application
/// and what <see cref="ContentSerializationBuilder"/> accepts.
/// </summary>
public class ContentSerializationBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddContentSerialization: Should attach the registry feature to the application")]
    public void AddContentSerialization_OnBuilder_ShouldAttachRegistryFeature()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();

        // Act
        builder.AddContentSerialization();

        // Assert
        builder.Features.OfType<IHttpContentSerializationFeature>().Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddReader: Should expose readers in registration order")]
    public void AddReader_MultipleReaders_ShouldExposeInRegistrationOrder()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        FakeContentReader first = new(HttpMediaType.ApplicationJson);
        FakeContentReader second = new(HttpMediaType.TextPlain);

        // Act
        builder.AddContentSerialization().AddReader(first).AddReader(second);

        // Assert
        IHttpContentSerializationFeature feature = builder.Features.OfType<IHttpContentSerializationFeature>().Single();
        feature.Readers.Count.ShouldBe(2);
        feature.Readers[0].ShouldBeSameAs(first);
        feature.Readers[1].ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddReader: Should reject a null reader")]
    public void AddReader_NullReader_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new TestWebApplicationBuilder().AddContentSerialization();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddReader(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddReader: Should reject a reader that declares no media types")]
    public void AddReader_NoMediaTypes_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new TestWebApplicationBuilder().AddContentSerialization();

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.AddReader(new FakeContentReader()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddWriter: Should reject a null writer")]
    public void AddWriter_NullWriter_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new TestWebApplicationBuilder().AddContentSerialization();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddWriter(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddWriter: Should reject a wildcard canonical media type")]
    public void AddWriter_WildcardFirstMediaType_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new TestWebApplicationBuilder().AddContentSerialization();
        FakeContentWriter writer = new(HttpMediaType.Parse("application/*"), HttpMediaType.ApplicationJson);

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.AddWriter(writer));
    }
}
