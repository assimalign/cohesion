using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

/// <summary>
/// Registry matching semantics: most-specific declared range wins, registration order breaks
/// ties, and no match means <see langword="null"/> (the outcome-friendly, non-throwing surface).
/// </summary>
public class HttpContentSerializationFeatureTests
{
    private static IHttpContentSerializationFeature Compose(params IHttpContentReader[] readers)
    {
        TestWebApplicationBuilder builder = new();
        ContentSerializationBuilder composition = builder.AddContentSerialization();

        foreach (IHttpContentReader reader in readers)
        {
            composition.AddReader(reader);
        }

        return builder.Features.OfType<IHttpContentSerializationFeature>().Single();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - GetReader: Should resolve an exact media-type match")]
    public void GetReader_ExactMatch_ShouldResolve()
    {
        // Arrange
        FakeContentReader json = new(HttpMediaType.ApplicationJson);
        IHttpContentSerializationFeature feature = Compose(json);

        // Act
        IHttpContentReader? resolved = feature.GetReader(HttpMediaType.ApplicationJson);

        // Assert
        resolved.ShouldBeSameAs(json);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - GetReader: Should match a parameterized content type against a parameterless range")]
    public void GetReader_ParameterizedContentType_ShouldMatchParameterlessRange()
    {
        // Arrange
        FakeContentReader json = new(HttpMediaType.ApplicationJson);
        IHttpContentSerializationFeature feature = Compose(json);

        // Act
        IHttpContentReader? resolved = feature.GetReader(HttpMediaType.Parse("application/json; charset=utf-8"));

        // Assert
        resolved.ShouldBeSameAs(json);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - GetReader: Should prefer the more specific range over an earlier wildcard")]
    public void GetReader_WildcardRegisteredFirst_ShouldPreferMoreSpecificRange()
    {
        // Arrange
        FakeContentReader wildcard = new(HttpMediaType.Parse("application/*"));
        FakeContentReader json = new(HttpMediaType.ApplicationJson);
        IHttpContentSerializationFeature feature = Compose(wildcard, json);

        // Act
        IHttpContentReader? resolved = feature.GetReader(HttpMediaType.ApplicationJson);

        // Assert
        resolved.ShouldBeSameAs(json);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - GetReader: Should break specificity ties by registration order")]
    public void GetReader_SpecificityTie_ShouldPreferEarliestRegistration()
    {
        // Arrange
        FakeContentReader first = new(HttpMediaType.ApplicationJson);
        FakeContentReader second = new(HttpMediaType.ApplicationJson);
        IHttpContentSerializationFeature feature = Compose(first, second);

        // Act
        IHttpContentReader? resolved = feature.GetReader(HttpMediaType.ApplicationJson);

        // Assert
        resolved.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - GetReader: Should return null when nothing is registered for the media type")]
    public void GetReader_NoMatch_ShouldReturnNull()
    {
        // Arrange
        IHttpContentSerializationFeature feature = Compose(new FakeContentReader(HttpMediaType.ApplicationJson));

        // Act
        IHttpContentReader? resolved = feature.GetReader(HttpMediaType.Parse("text/csv"));

        // Assert
        resolved.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - GetWriter: Should resolve a range-covered concrete media type")]
    public void GetWriter_RangeCoveredMediaType_ShouldResolve()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        FakeContentWriter writer = new(HttpMediaType.ApplicationJson, HttpMediaType.Parse("application/*"));
        builder.AddContentSerialization().AddWriter(writer);
        IHttpContentSerializationFeature feature = builder.Features.OfType<IHttpContentSerializationFeature>().Single();

        // Act
        IHttpContentWriter? resolved = feature.GetWriter(HttpMediaType.Parse("application/vnd.example"));

        // Assert
        resolved.ShouldBeSameAs(writer);
    }
}
