using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

/// <summary>
/// Composition-surface coverage: what <c>builder.Services.AddContentSerialization</c> registers
/// and what <see cref="ContentSerializationBuilder"/> accepts.
/// </summary>
public class ContentSerializationBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddContentSerialization: Should register the registry as one IHttpFeature singleton")]
    public void AddContentSerialization_OnServices_ShouldRegisterRegistryFeatureSingleton()
    {
        // Arrange
        ServiceProviderBuilder services = new();
        FakeContentReader reader = new(HttpMediaType.ApplicationJson);

        // Act
        IServiceProviderBuilder returned = services.AddContentSerialization(serialization => serialization.AddReader(reader));

        // Assert — one singleton the host stamps onto every exchange (owner decision 35).
        returned.ShouldBeSameAs(services);
        ServiceDescriptor descriptor = services.Container.ShouldHaveSingleItem();
        descriptor.ServiceType.ShouldBe(typeof(IHttpFeature));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        Resolve(services).Readers.ShouldHaveSingleItem().ShouldBeSameAs(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddJsonSerialization: Should register a JSON registry as one IHttpFeature singleton")]
    public void AddJsonSerialization_OnServices_ShouldRegisterJsonRegistrySingleton()
    {
        // Arrange
        ServiceProviderBuilder services = new();

        // Act
        services.AddJsonSerialization(TestJsonContext.Default);

        // Assert
        ServiceDescriptor descriptor = services.Container.ShouldHaveSingleItem();
        descriptor.ServiceType.ShouldBe(typeof(IHttpFeature));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        IHttpContentSerializationFeature feature = Resolve(services);
        feature.Readers.ShouldHaveSingleItem().CanRead(typeof(TestOrder)).ShouldBeTrue();
        feature.Writers.ShouldHaveSingleItem().CanWrite(typeof(TestReceipt)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Build: Should snapshot the formats registered so far")]
    public void Build_ThenAddReader_ShouldNotChangeTheBuiltRegistry()
    {
        // Arrange
        ContentSerializationBuilder builder = new ContentSerializationBuilder().AddReader(new FakeContentReader(HttpMediaType.ApplicationJson));
        IHttpContentSerializationFeature built = builder.Build();

        // Act
        builder.AddReader(new FakeContentReader(HttpMediaType.TextPlain));

        // Assert
        built.Readers.Count.ShouldBe(1);
        builder.Build().Readers.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddReader: Should expose readers in registration order")]
    public void AddReader_MultipleReaders_ShouldExposeInRegistrationOrder()
    {
        // Arrange
        FakeContentReader first = new(HttpMediaType.ApplicationJson);
        FakeContentReader second = new(HttpMediaType.TextPlain);

        // Act
        IHttpContentSerializationFeature feature = new ContentSerializationBuilder().AddReader(first).AddReader(second).Build();

        // Assert
        feature.Readers.Count.ShouldBe(2);
        feature.Readers[0].ShouldBeSameAs(first);
        feature.Readers[1].ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddReader: Should reject a null reader")]
    public void AddReader_NullReader_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new ContentSerializationBuilder();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddReader(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddReader: Should reject a reader that declares no media types")]
    public void AddReader_NoMediaTypes_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new ContentSerializationBuilder();

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.AddReader(new FakeContentReader()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddWriter: Should reject a null writer")]
    public void AddWriter_NullWriter_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new ContentSerializationBuilder();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddWriter(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - AddWriter: Should reject a wildcard canonical media type")]
    public void AddWriter_WildcardFirstMediaType_ShouldThrow()
    {
        // Arrange
        ContentSerializationBuilder builder = new ContentSerializationBuilder();
        FakeContentWriter writer = new(HttpMediaType.Parse("application/*"), HttpMediaType.ApplicationJson);

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.AddWriter(writer));
    }

    // Resolves the registry the way the host does when it composes the pipeline.
    private static IHttpContentSerializationFeature Resolve(IServiceProviderBuilder services)
    {
        IServiceProvider provider = services.Build();
        return provider.GetRequiredService<IEnumerable<IHttpFeature>>()
            .ShouldHaveSingleItem()
            .ShouldBeAssignableTo<IHttpContentSerializationFeature>()!;
    }
}
