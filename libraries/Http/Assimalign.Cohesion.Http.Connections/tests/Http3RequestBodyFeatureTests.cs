using System;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3RequestBodyFeatureTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 body: Repeated reads resolve directional abort once, including its absence")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAsync_WithRepeatedReads_ShouldResolveDirectionalAbortOnce(bool hasDirectionalAbort)
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using TestMultiplexedConnection connection = new();
        byte[] content = [1, 2, 3];
        byte[] frame = HttpProtocolPayloadFactory.CreateHttp3Frame(0, content);
        await using FeatureLookupCountingConnection stream = new(frame, hasDirectionalAbort);
        Http3RequestStreamReader reader = new(stream.Input);
        using Http3RequestBodyStream body = CreateBody(connection, stream, reader);
        stream.FeatureAccesses.ShouldBe(0);
        byte[] received = new byte[content.Length];

        // Act — each octet is a separate application read with its own rate-deadline checks.
        for (int index = 0; index < received.Length; index++)
        {
            (await body.ReadAsync(received.AsMemory(index, 1), timeout.Token)).ShouldBe(1);
        }
        int end = await body.ReadAsync(new byte[1], timeout.Token);

        // Assert
        received.ShouldBe(content);
        end.ShouldBe(0);
        stream.FeatureAccesses.ShouldBe(1);
        stream.FeatureEnumerations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 body: Setup and a known FIN do not access connection features")]
    public async Task StopReading_WithKnownInputEnd_ShouldNotAccessFeatures()
    {
        // Arrange — the head consumed the stream's FIN before the body was constructed.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using TestMultiplexedConnection connection = new();
        await using FeatureLookupCountingConnection stream = new([], hasDirectionalAbort: true);
        Http3RequestStreamReader reader = new(stream.Input);
        (await reader.ReadFrameHeaderAsync(timeout.Token)).ShouldBeNull();

        // Act
        using Http3RequestBodyStream body = CreateBody(connection, stream, reader);
        body.RefuseRemainder();
        body.StopReading();

        // Assert
        body.IsCompleted.ShouldBeTrue();
        stream.FeatureAccesses.ShouldBe(0);
        stream.FeatureEnumerations.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 body: An unread buffered FIN avoids feature lookup when the response ends")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefuseRemainder_WithBufferedFinAfterHead_ShouldNotAccessFeatures(bool streamBacked)
    {
        // Arrange — HEADERS consumed all bytes, but no subsequent read has observed EOF.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using TestMultiplexedConnection connection = new();
        byte[] request = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a");
        using MemoryStream payload = new(request);
        PipeReader? input = streamBacked ? PipeReader.Create(payload) : null;
        await using FeatureLookupCountingConnection stream = new(request, hasDirectionalAbort: true, input);
        Http3RequestStreamReader reader = new(stream.Input,
            streamBacked ? new TestConnectionInputCompletion { IsInputCompleted = true } : null);

        try
        {
            (await reader.ReadHeaderSectionAsync(65536, timeout.Token)).ShouldNotBeNull();
            reader.IsEndBuffered().ShouldBeTrue();
            reader.IsCompleted.ShouldBeFalse();
            using Http3RequestBodyStream body = CreateBody(connection, stream, reader);

            // Act — the send path refuses any remainder and releases input after sending its FIN.
            body.RefuseRemainder();
            body.StopReading();

            // Assert — both pipe completion and transport-only FIN need no directional capability.
            body.IsCompleted.ShouldBeTrue();
            stream.FeatureAccesses.ShouldBe(0);
            stream.FeatureEnumerations.ShouldBe(0);
        }
        finally
        {
            if (input is not null)
            {
                await input.CompleteAsync();
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 body: A known transport FIN does not hide unread data or a partial trailer frame")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefuseRemainder_WithUnreadFrameBytes_ShouldNotReportCompletion(bool partialTrailer)
    {
        // Arrange — the transport knows FIN, but bytes after HEADERS still need framing or refusal.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using TestMultiplexedConnection connection = new();
        byte[] head = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a");
        byte[] remainder = partialTrailer ? [0x01] : HttpProtocolPayloadFactory.CreateHttp3Frame(0, [1]);
        await using FeatureLookupCountingConnection stream = new([.. head, .. remainder], hasDirectionalAbort: true);
        Http3RequestStreamReader reader = new(stream.Input, new TestConnectionInputCompletion { IsInputCompleted = true });
        (await reader.ReadHeaderSectionAsync(65536, timeout.Token)).ShouldNotBeNull();
        reader.IsEndBuffered().ShouldBeFalse();
        using Http3RequestBodyStream body = CreateBody(connection, stream, reader);

        // Act
        body.RefuseRemainder();
        body.StopReading();

        // Assert — neither DATA nor a truncated trailer header is mistaken for a clean frame boundary.
        body.IsCompleted.ShouldBeFalse();
        stream.FeatureAccesses.ShouldBe(1);
        stream.FeatureEnumerations.ShouldBe(1);
    }

    private static Http3RequestBodyStream CreateBody(
        TestMultiplexedConnection connection,
        FeatureLookupCountingConnection stream,
        Http3RequestStreamReader reader)
    {
        Http3ConnectionContext context = new(
            connection,
            isSecure: true,
            new Http3ConnectionListenerOptions.Http3Limits(),
            requestInterceptors: [],
            responseInterceptors: [],
            featureCapacity: 0,
            new Http3QPackOptions());
        return new Http3RequestBodyStream(
            context,
            reader,
            stream,
            streamId: 0,
            new HttpTrailerCollection(),
            declaredContentLength: null,
            isTunnel: false,
            fallbackCap: null,
            maxHeadersFrameSize: 65536,
            CancellationToken.None,
            minDataRate: new HttpMinDataRate(1, TimeSpan.FromMinutes(1)));
    }
}
