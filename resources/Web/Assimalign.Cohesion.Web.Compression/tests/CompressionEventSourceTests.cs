using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Compression.Internal;
using Assimalign.Cohesion.Web.Compression.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Compression.Tests;

[Collection("CompressionEventSource")]
public sealed class CompressionEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: provider uses the assembly name")]
    public void GetName_CompressionSource_ShouldMatchAssemblyName()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(CompressionEventSource)).ShouldBe("Assimalign.Cohesion.Web.Compression");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation preserves the directly referenced event source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation deliberately inspects all event methods.")]
    public void GenerateManifest_CompressionSource_ShouldPassStrictValidation()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(CompressionEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: middleware rejections carry declared payloads once")]
    public async Task InvokeAsync_RejectionLifecycle_ShouldPublishDeclaredPayloadsOnce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        using Activity ambient = new("unrelated-work");
        ambient.SetIdFormat(ActivityIdFormat.W3C).Start();
        CompressionEventSource source = CompressionEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        RequestDecompressionMiddleware middleware = new(new RequestDecompressionOptions { MaxDecompressedSizeBytes = 8 });
        await using TestHttpContext unsupported = Context("GZIP, secret-coding-token", []);
        await using TestHttpContext malformed = Context("GZIP", [1, 2, 3, 4, 5]);
        await using TestHttpContext limited = Context("gzip", CompressionPayloads.GzipCompress(new byte[64]));
        limited.Features.Set<IHttpResponseStreamingFeature>(new StartedResponseFeature());
        long before = source.DecompressionLimitRejections;

        // Act
        await middleware.InvokeAsync(unsupported, _ => throw new InvalidOperationException("A rejected coding reached the handler."));
        await middleware.InvokeAsync(malformed, ReadBodyAsync);
        await middleware.InvokeAsync(limited, ReadBodyAsync);

        // Assert
        EventWrittenEventArgs rejected = Event(recorder, 1, unsupported);
        rejected.PayloadNames.ShouldBe(new[] { "reason", "codings", "exceptionType", "requestId" });
        rejected.Level.ShouldBe(EventLevel.Verbose);
        rejected.Payload![0].ShouldBe("UnsupportedCoding");
        rejected.Payload[1].ShouldBe("gzip");
        rejected.Payload[2].ShouldBe(string.Empty);
        EventWrittenEventArgs invalid = Event(recorder, 1, malformed);
        invalid.PayloadNames.ShouldBe(rejected.PayloadNames);
        invalid.Payload![0].ShouldBe("MalformedContent");
        invalid.Payload[1].ShouldBe("gzip");
        invalid.Payload[2].ShouldBe(typeof(InvalidDataException).FullName);
        EventWrittenEventArgs limit = Event(recorder, 2, limited);
        limit.PayloadNames.ShouldBe(new[] { "limitBytes", "codings", "suppressedCount", "requestId" });
        limit.Level.ShouldBe(EventLevel.Warning);
        limit.Payload![0].ShouldBe(8L);
        limit.Payload[1].ShouldBe("gzip");
        EventWrittenEventArgs aborted = Event(recorder, 3, limited);
        aborted.PayloadNames.ShouldBe(new[] { "statusCode", "suppressedCount", "requestId" });
        aborted.Level.ShouldBe(EventLevel.Warning);
        aborted.Payload![0].ShouldBe(413);
        limited.CancelCount.ShouldBe(1);
        malformed.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unsupported.Response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (source.DecompressionLimitRejections - before).ShouldBe(1);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
        recorder.Events.SelectMany(item => item.Payload?.OfType<string>() ?? Enumerable.Empty<string>())
            .ShouldNotContain(value => value.Contains("secret-coding-token", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: declared counter publishes")]
    public async Task OnEventCommand_CountersEnabled_ShouldPublishDeclaredCounter()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(CompressionEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act
        await recorder.WaitForCountersAsync(["decompression-limit-rejections"], cancellation.Token);

        // Assert
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: retrying reads count one middleware rejection")]
    public async Task InvokeAsync_RetriedLimitRead_ShouldCountOnlyTheMiddlewareCatch()
    {
        // Arrange
        CompressionEventSource source = CompressionEventSource.Log;
        long before = source.DecompressionLimitRejections;
        RequestDecompressionMiddleware middleware = new(new RequestDecompressionOptions { MaxDecompressedSizeBytes = 8 });
        await using TestHttpContext context = Context("gzip", CompressionPayloads.GzipCompress(new byte[64]));
        int observedThrows = 0;

        // Act
        await middleware.InvokeAsync(context, async current =>
        {
            byte[] buffer = new byte[9];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await current.Request.Body.ReadExactlyAsync(buffer, CancellationToken.None);
                }
                catch (RequestDecompressionLimitException)
                {
                    observedThrows++;
                    source.DecompressionLimitRejections.ShouldBe(before);
                    if (attempt == 1)
                    {
                        throw;
                    }
                }
            }
        });

        // Assert
        observedThrows.ShouldBe(2);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (source.DecompressionLimitRejections - before).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: disabled events retain exact counts without reading request ids")]
    public async Task InvokeAsync_NoListener_ShouldCountWithoutReadingRequestId()
    {
        // Arrange
        CompressionEventSource source = CompressionEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        long before = source.DecompressionLimitRejections;
        RequestDecompressionMiddleware middleware = new(new RequestDecompressionOptions { MaxDecompressedSizeBytes = 8 });
        await using TestHttpContext context = Context("gzip", CompressionPayloads.GzipCompress(new byte[64]));
        context.Features.Set<IWebRequestIdFeature>(new UnreadableRequestIdFeature());

        // Act
        await middleware.InvokeAsync(context, ReadBodyAsync);

        // Assert
        (source.DecompressionLimitRejections - before).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: repeated warnings are suppressed while counts stay exact")]
    public async Task InvokeAsync_RepeatedLimitAndAbort_ShouldThrottleEachWarningAndCountAllRejections()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        CompressionEventSource source = CompressionEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        RequestDecompressionMiddleware middleware = new(new RequestDecompressionOptions { MaxDecompressedSizeBytes = 8 });
        await using TestHttpContext first = Context("gzip", CompressionPayloads.GzipCompress(new byte[64]));
        await using TestHttpContext second = Context("gzip", CompressionPayloads.GzipCompress(new byte[64]));
        await using TestHttpContext third = Context("gzip", CompressionPayloads.GzipCompress(new byte[64]));
        await using TestHttpContext fourth = Context("gzip", CompressionPayloads.GzipCompress(new byte[64]));
        TestHttpContext[] contexts = [first, second, third, fourth];
        foreach (TestHttpContext context in contexts)
        {
            context.Features.Set<IHttpResponseStreamingFeature>(new StartedResponseFeature());
        }

        long before = source.DecompressionLimitRejections;

        // Act
        await middleware.InvokeAsync(first, ReadBodyAsync);
        await middleware.InvokeAsync(second, ReadBodyAsync);
        await middleware.InvokeAsync(third, ReadBodyAsync);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        await middleware.InvokeAsync(fourth, ReadBodyAsync);

        // Assert
        Event(recorder, 2, first).ShouldNotBeNull();
        Event(recorder, 3, first).ShouldNotBeNull();
        recorder.Events.ShouldNotContain(item => HasRequestId(item, second) || HasRequestId(item, third));
        Event(recorder, 2, fourth).Payload![2].ShouldBe(2);
        Event(recorder, 3, fourth).Payload![1].ShouldBe(2);
        (source.DecompressionLimitRejections - before).ShouldBe(4);
        contexts.ShouldAllBe(context => context.CancelCount == 1);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: coding payloads are bounded recognized tokens")]
    public async Task InvokeAsync_RepeatedRecognizedCodingsBeforeUnknown_ShouldWriteOnlyBoundedCanonicalTokens()
    {
        // Arrange
        using EventSourceRecorder recorder = new(CompressionEventSource.Log, EventLevel.Verbose);
        RequestDecompressionMiddleware middleware = new(new RequestDecompressionOptions());
        string codings = string.Concat(Enumerable.Repeat("GZIP, BR, Deflate, ", 100)) + "private-token";
        await using TestHttpContext context = Context(codings, []);

        // Act
        await middleware.InvokeAsync(context, _ => throw new InvalidOperationException("An unsupported coding reached the handler."));

        // Assert
        Event(recorder, 1, context).Payload![1].ShouldBe("gzip,br,deflate");
        context.Response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: transport framing faults are not reported as decompression rejections")]
    public async Task InvokeAsync_TransportClientFault_ShouldPreserveFaultWithoutDecompressionEvent()
    {
        // Arrange
        CompressionEventSource source = CompressionEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        long before = source.DecompressionLimitRejections;
        RequestDecompressionMiddleware middleware = new(new RequestDecompressionOptions());
        await using TestHttpContext context = Context("gzip", [1, 2, 3, 4, 5]);
        context.Features.Set<IWebClientFaultFeature>(new ClientFaultFeature());

        // Act
        await Should.ThrowAsync<InvalidDataException>(() => middleware.InvokeAsync(context, ReadBodyAsync));

        // Assert
        source.DecompressionLimitRejections.ShouldBe(before);
        recorder.Events.ShouldNotContain(item => HasRequestId(item, context));
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Event source: success and handler exceptions produce no rejection diagnostics")]
    public async Task InvokeAsync_SuccessAndHandlerException_ShouldProduceNoRejectionDiagnostics()
    {
        // Arrange
        using EventSourceRecorder recorder = new(CompressionEventSource.Log, EventLevel.Verbose);
        RequestDecompressionMiddleware middleware = new(new RequestDecompressionOptions());
        await using TestHttpContext success = Context("gzip", CompressionPayloads.GzipCompress([1, 2, 3]));
        await using TestHttpContext failed = Context("gzip", CompressionPayloads.GzipCompress([1, 2, 3]));

        // Act
        await middleware.InvokeAsync(success, ReadBodyAsync);
        await Should.ThrowAsync<InvalidDataException>(() => middleware.InvokeAsync(failed, _ => throw new InvalidDataException("private-handler-message")));

        // Assert
        recorder.Events.ShouldNotContain(item => HasRequestId(item, success) || HasRequestId(item, failed));
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    private static TestHttpContext Context(string codings, byte[] body)
    {
        TestHttpContext context = new();
        context.Request.Headers[HttpHeaderKey.ContentEncoding] = codings;
        context.Request.Body = new MemoryStream(body);
        context.Features.Set<IWebRequestIdFeature>(new RequestIdFeature());
        return context;
    }

    private static Task ReadBodyAsync(IHttpContext context)
        => context.Request.Body.CopyToAsync(Stream.Null, CancellationToken.None);

    private static EventWrittenEventArgs Event(EventSourceRecorder recorder, int id, TestHttpContext context)
        => recorder.Events.Single(item => item.EventId == id && HasRequestId(item, context));

    private static bool HasRequestId(EventWrittenEventArgs item, TestHttpContext context)
    {
        int index = item.PayloadNames?.IndexOf("requestId") ?? -1;
        return index >= 0 && Equals(item.Payload![index], context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString());
    }

    private sealed class RequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);

        public ActivityTraceId RequestId { get; } = ActivityTraceId.CreateRandom();
    }

    private sealed class UnreadableRequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);

        public ActivityTraceId RequestId => throw new InvalidOperationException("Disabled diagnostics read the request id.");
    }

    private sealed class ClientFaultFeature : IWebClientFaultFeature
    {
        public string Name => nameof(IWebClientFaultFeature);

        public HttpStatusCode? StatusCode => HttpStatusCode.BadRequest;
    }

    private sealed class StartedResponseFeature : IHttpResponseStreamingFeature
    {
        public string Name => nameof(IHttpResponseStreamingFeature);

        public bool HasStarted => true;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
