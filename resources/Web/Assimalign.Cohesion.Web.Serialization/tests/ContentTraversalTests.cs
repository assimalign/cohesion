using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

public class ContentTraversalTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Traversal: Should observe changed custom media types after build")]
    public void TryNegotiate_ChangedCustomMediaTypes_ShouldObserveCurrentValues()
    {
        // Arrange
        HttpMediaType[] mediaTypes = { HttpMediaType.ApplicationJson };
        FakeContentReader reader = new(mediaTypes);
        FakeContentWriter writer = new(mediaTypes);
        IContentSerializationFeature feature = new ContentSerializationBuilder()
            .AddReader(reader).AddWriter(writer).Build();
        feature.TryNegotiate("application/json", out _).ShouldBeTrue();

        // Act
        mediaTypes[0] = HttpMediaType.ApplicationXml;

        // Assert
        feature.GetReader(HttpMediaType.ApplicationJson).ShouldBeNull();
        feature.GetWriter(HttpMediaType.ApplicationJson).ShouldBeNull();
        feature.GetReader(HttpMediaType.ApplicationXml).ShouldBeSameAs(reader);
        feature.GetWriter(HttpMediaType.ApplicationXml).ShouldBeSameAs(writer);
        feature.TryNegotiate("application/json", out _).ShouldBeFalse();
        feature.TryNegotiate("application/xml", out HttpMediaType selected).ShouldBeTrue();
        selected.ShouldBe(HttpMediaType.ApplicationXml);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Serialization] - Negotiated write: Should preserve Vary token behavior")]
    [InlineData(null, "Accept")]
    [InlineData("", "Accept")]
    [InlineData("Origin", "Origin, Accept")]
    [InlineData("Accept", "Accept")]
    [InlineData(" Origin , ACCEPT ", " Origin , ACCEPT ")]
    [InlineData("Origin, Accept-Encoding", "Origin, Accept-Encoding, Accept")]
    [InlineData("Origin, *", "Origin, *")]
    [InlineData(",,\tAccept\t,,", ",,\tAccept\t,,")]
    [InlineData("Origin,,", "Origin,,, Accept")]
    [InlineData("\u2003Accept\u2003", "\u2003Accept\u2003")]
    public async Task WriteNegotiatedContentAsync_ExistingVary_ShouldPreserveTokens(string? vary, string expected)
    {
        // Arrange
        TestHttpContext context = new();
        context.Features.Set<IContentSerializationFeature>(new ContentSerializationBuilder()
            .AddWriter(new FakeContentWriter(HttpMediaType.ApplicationJson)).Build());
        context.Request.Headers[HttpHeaderKey.Accept] = "application/json";
        if (vary is not null)
        {
            context.Response.Headers[HttpHeaderKey.Vary] = vary;
        }

        // Act
        bool written = await context.WriteNegotiatedContentAsync("payload", CancellationToken.None);

        // Assert
        written.ShouldBeTrue();
        context.Response.Headers.GetValue(HttpHeaderKey.Vary).ShouldBe(expected);
        await context.Response.Body.DisposeAsync();
    }
}
