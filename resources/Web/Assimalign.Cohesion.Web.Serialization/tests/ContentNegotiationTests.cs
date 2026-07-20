using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

/// <summary>
/// The negotiation seam (#149) over the registry: exact RFC 9110 §12.5.1 selection delegated to the
/// shared primitive (server-preference order, q-value, q=0 rejection) plus the structured-suffix
/// fallback this package owns. These exercise the media-type decision only — the write path and
/// <c>Vary</c> handling live in <see cref="ContentNegotiationPipelineTests"/>.
/// </summary>
public class ContentNegotiationTests
{
    private static readonly HttpMediaType ProblemJson = HttpMediaType.Parse("application/problem+json");
    private static readonly HttpMediaType TextJson = HttpMediaType.Parse("text/json");

    private static IHttpContentSerializationFeature Compose(params IHttpContentWriter[] writers)
    {
        TestWebApplicationBuilder builder = new();
        ContentSerializationBuilder composition = builder.AddContentSerialization();

        foreach (IHttpContentWriter writer in writers)
        {
            composition.AddWriter(writer);
        }

        return builder.Features.OfType<IHttpContentSerializationFeature>().Single();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should return false when no writers are registered")]
    public void TryNegotiate_NoWriters_ShouldReturnFalse()
    {
        // Arrange
        IHttpContentSerializationFeature feature = Compose();

        // Act
        bool negotiated = feature.TryNegotiate("application/json", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeFalse();
        selected.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should choose the server-preferred type when no Accept is sent")]
    public void TryNegotiate_NoAcceptHeader_ShouldSelectServerPreferred()
    {
        // Arrange
        IHttpContentSerializationFeature feature = Compose(
            new FakeContentWriter(HttpMediaType.ApplicationJson),
            new FakeContentWriter(HttpMediaType.ApplicationXml));

        // Act
        bool negotiated = feature.TryNegotiate(null, out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeTrue();
        selected.ShouldBe(HttpMediaType.ApplicationJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should resolve an exact Accept match")]
    public void TryNegotiate_ExactMatch_ShouldSelectRequested()
    {
        // Arrange
        IHttpContentSerializationFeature feature = Compose(
            new FakeContentWriter(HttpMediaType.ApplicationJson),
            new FakeContentWriter(HttpMediaType.ApplicationXml));

        // Act
        bool negotiated = feature.TryNegotiate("application/xml", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeTrue();
        selected.ShouldBe(HttpMediaType.ApplicationXml);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should prefer the higher-quality media range")]
    public void TryNegotiate_QualityWeights_ShouldPreferHigherQuality()
    {
        // Arrange
        IHttpContentSerializationFeature feature = Compose(
            new FakeContentWriter(HttpMediaType.ApplicationJson),
            new FakeContentWriter(HttpMediaType.ApplicationXml));

        // Act
        bool negotiated = feature.TryNegotiate("application/json;q=0.5, application/xml;q=0.9", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeTrue();
        selected.ShouldBe(HttpMediaType.ApplicationXml);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should reject a media type weighted q=0")]
    public void TryNegotiate_ZeroQuality_ShouldRejectThatType()
    {
        // Arrange
        IHttpContentSerializationFeature feature = Compose(new FakeContentWriter(HttpMediaType.ApplicationJson));

        // Act
        bool negotiated = feature.TryNegotiate("application/json;q=0", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeFalse();
        selected.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should honor server registration order for a wildcard Accept")]
    public void TryNegotiate_WildcardAccept_ShouldFollowRegistrationOrder()
    {
        // Arrange — XML registered first, so it is the server-preferred representation for */*.
        IHttpContentSerializationFeature feature = Compose(
            new FakeContentWriter(HttpMediaType.ApplicationXml),
            new FakeContentWriter(HttpMediaType.ApplicationJson));

        // Act
        bool negotiated = feature.TryNegotiate("*/*", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeTrue();
        selected.ShouldBe(HttpMediaType.ApplicationXml);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should offer a writer's alternate concrete media type")]
    public void TryNegotiate_AlternateMediaType_ShouldBeSelectable()
    {
        // Arrange — the writer canonically emits application/json but also advertises text/json.
        IHttpContentSerializationFeature feature = Compose(new FakeContentWriter(HttpMediaType.ApplicationJson, TextJson));

        // Act
        bool negotiated = feature.TryNegotiate("text/json", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeTrue();
        selected.ShouldBe(TextJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should satisfy a base-type Accept from a structured-suffix writer")]
    public void TryNegotiate_SuffixFallback_ShouldSatisfyBaseTypeFromSuffixedWriter()
    {
        // Arrange — only a problem+json writer is registered; a client asking for application/json
        // should be served it rather than a spurious 406.
        IHttpContentSerializationFeature feature = Compose(new FakeContentWriter(ProblemJson));

        // Act
        bool negotiated = feature.TryNegotiate("application/json", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeTrue();
        selected.ShouldBe(ProblemJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should not widen an already-suffixed Accept range")]
    public void TryNegotiate_SuffixFallback_ShouldNotWidenSuffixedAccept()
    {
        // Arrange — a client asking for a specific +json schema must not be handed a different one.
        IHttpContentSerializationFeature feature = Compose(new FakeContentWriter(ProblemJson));

        // Act
        bool negotiated = feature.TryNegotiate("application/vnd.foo+json", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeFalse();
        selected.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should prefer an exact match over the suffix fallback")]
    public void TryNegotiate_SuffixFallback_ShouldYieldToExactMatch()
    {
        // Arrange — both a plain json and a problem+json writer exist; application/json must bind
        // to the exact writer, not the suffixed fallback.
        IHttpContentSerializationFeature feature = Compose(
            new FakeContentWriter(HttpMediaType.ApplicationJson),
            new FakeContentWriter(ProblemJson));

        // Act
        bool negotiated = feature.TryNegotiate("application/json", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeTrue();
        selected.ShouldBe(HttpMediaType.ApplicationJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiate: Should honor a q=0 refusal of the suffixed type in the fallback")]
    public void TryNegotiate_SuffixFallback_ShouldHonorExplicitRejection()
    {
        // Arrange — the client broadly accepts json but explicitly refuses problem+json; the suffix
        // fallback must not serve the crossed-out representation.
        IHttpContentSerializationFeature feature = Compose(new FakeContentWriter(ProblemJson));

        // Act
        bool negotiated = feature.TryNegotiate("application/json, application/problem+json;q=0", out HttpMediaType selected);

        // Assert
        negotiated.ShouldBeFalse();
        selected.IsEmpty.ShouldBeTrue();
    }
}
