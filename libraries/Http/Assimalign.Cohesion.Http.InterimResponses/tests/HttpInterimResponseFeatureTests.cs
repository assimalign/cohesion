using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.InterimResponses.Tests;

/// <summary>
/// Unit tests for the interim-response feature package: the interceptor wraps the transport's
/// exchange control (<see cref="IHttpExchangeControl"/>) in an <see cref="IHttpInterimResponseFeature"/>,
/// the feature forwards to the control, and the ergonomic extensions build the common interim
/// responses. Uses a recording fake control and a hand-built <see cref="HttpExchangeInterceptorResponseContext"/>
/// — no transport is involved (the wire behavior is covered by the transport's InterimResponseTests).
/// </summary>
public class HttpInterimResponseFeatureTests
{
    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Interceptor: Should install the feature over the transport capability")]
    public void Interceptor_BeforeResponse_ShouldInstallFeature()
    {
        RecordingControl control = new();
        HttpExchangeInterceptorResponseContext context = CreateContext(control);

        HttpInterimResponses.CreateInterceptor().BeforeResponse(context);

        context.Features.Get<IHttpInterimResponseFeature>().ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Interceptor: Should install nothing when the transport offers no capability")]
    public void Interceptor_WithoutCapability_ShouldInstallNothing()
    {
        HttpExchangeInterceptorResponseContext context = CreateContext(control: null);

        HttpInterimResponses.CreateInterceptor().BeforeResponse(context);

        context.Features.Get<IHttpInterimResponseFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Feature: IsInterimResponseSupported mirrors the capability")]
    public void Feature_IsInterimResponseSupported_ShouldMirrorCapability()
    {
        RecordingControl control = new() { CanWrite = true };
        IHttpInterimResponseFeature feature = CreateFeature(control);

        feature.IsInterimResponseSupported.ShouldBeTrue();

        control.CanWrite = false;
        feature.IsInterimResponseSupported.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Feature: SendInterimResponseAsync forwards the status and headers to the capability")]
    public async Task Feature_SendInterimResponseAsync_ShouldForwardToCapability()
    {
        RecordingControl control = new();
        IHttpInterimResponseFeature feature = CreateFeature(control);

        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Link] = "</a.css>; rel=preload";
        await feature.SendInterimResponseAsync(HttpStatusCode.EarlyHints, headers);

        control.Calls.Count.ShouldBe(1);
        control.Calls[0].StatusCode.Value.ShouldBe(103);
        control.Calls[0].Headers.ShouldBeSameAs(headers);
    }

    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Extensions: SendEarlyHintsAsync emits 103 with Link fields and reports true")]
    public async Task SendEarlyHintsAsync_ShouldEmit103WithLinks()
    {
        RecordingControl control = new();
        FakeHttpContext context = CreateContextWithFeature(control);

        bool emitted = await context.SendEarlyHintsAsync(["</a.css>; rel=preload", "</b.js>; rel=preload"]);

        emitted.ShouldBeTrue();
        control.Calls.Count.ShouldBe(1);
        control.Calls[0].StatusCode.Value.ShouldBe(103);
        control.Calls[0].Headers.ShouldNotBeNull();
        control.Calls[0].Headers!.TryGetValue(HttpHeaderKey.Link, out HttpHeaderValue link).ShouldBeTrue();
        link.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Extensions: SendContinueAsync emits 100 and reports true")]
    public async Task SendContinueAsync_ShouldEmit100()
    {
        RecordingControl control = new();
        FakeHttpContext context = CreateContextWithFeature(control);

        bool emitted = await context.SendContinueAsync();

        emitted.ShouldBeTrue();
        control.Calls.Count.ShouldBe(1);
        control.Calls[0].StatusCode.Value.ShouldBe(100);
        control.Calls[0].Headers.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Extensions: SendEarlyHintsAsync no-ops (false) when unsupported")]
    public async Task SendEarlyHintsAsync_WhenUnsupported_ShouldReturnFalse()
    {
        RecordingControl control = new() { CanWrite = false };
        FakeHttpContext context = CreateContextWithFeature(control);

        bool emitted = await context.SendEarlyHintsAsync(["</a.css>; rel=preload"]);

        emitted.ShouldBeFalse();
        control.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.InterimResponses] - Extensions: SendEarlyHintsAsync no-ops (false) when the feature is absent")]
    public async Task SendEarlyHintsAsync_WhenFeatureAbsent_ShouldReturnFalse()
    {
        FakeHttpContext context = new(new HttpFeatureCollection());

        bool emitted = await context.SendEarlyHintsAsync(["</a.css>; rel=preload"]);

        emitted.ShouldBeFalse();
        context.InterimResponse.ShouldBeNull();
    }

    // ------------------------------------------------------------------- Helpers

    private static HttpExchangeInterceptorResponseContext CreateContext(IHttpExchangeControl? control) => new()
    {
        Version = HttpVersion.Http11,
        Headers = new HttpHeaderCollection(),
        Features = new HttpFeatureCollection(),
        ConnectionInfo = new HttpConnectionInfo(),
        ResponseBody = Stream.Null,
        Control = control,
    };

    private static IHttpInterimResponseFeature CreateFeature(IHttpExchangeControl control)
    {
        HttpExchangeInterceptorResponseContext context = CreateContext(control);
        HttpInterimResponses.CreateInterceptor().BeforeResponse(context);
        return context.Features.Get<IHttpInterimResponseFeature>()!;
    }

    private static FakeHttpContext CreateContextWithFeature(IHttpExchangeControl control)
    {
        HttpExchangeInterceptorResponseContext interceptorContext = CreateContext(control);
        HttpInterimResponses.CreateInterceptor().BeforeResponse(interceptorContext);
        return new FakeHttpContext(interceptorContext.Features);
    }

    /// <summary>
    /// Recording fake <see cref="IHttpExchangeControl"/>: interim-response capability is toggled via
    /// <see cref="CanWrite"/>, writes are recorded, takeover is never offered, and the directive
    /// takeover is never offered.
    /// </summary>
    private sealed class RecordingControl : IHttpExchangeControl
    {
        public bool CanWrite { get; set; } = true;

        public List<(HttpStatusCode StatusCode, IHttpHeaderCollection? Headers)> Calls { get; } = new();


        public bool HasResponseStarted => !CanWrite;

        public bool CanWriteInterimResponse => CanWrite;

        public ValueTask WriteInterimResponseAsync(
            HttpStatusCode statusCode,
            IHttpHeaderCollection? headers = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((statusCode, headers));
            return ValueTask.CompletedTask;
        }

        public bool CanTakeOver => false;

        public Stream TakeOver() => throw new InvalidOperationException("This fake control does not offer takeover.");
    }

    /// <summary>
    /// Minimal <see cref="IHttpContext"/> exposing only the feature collection — the sole member the
    /// interim-response extensions touch.
    /// </summary>
    private sealed class FakeHttpContext : IHttpContext
    {
        public FakeHttpContext(IHttpFeatureCollection features) => Features = features;

        public IHttpFeatureCollection Features { get; }

        public HttpVersion Version => throw new NotSupportedException();
        public IHttpRequest Request => throw new NotSupportedException();
        public IHttpResponse Response => throw new NotSupportedException();
        public IHttpConnectionInfo ConnectionInfo => throw new NotSupportedException();
        public IDictionary<string, object?> Items => throw new NotSupportedException();
        public CancellationToken RequestCancelled => CancellationToken.None;

        public void Cancel() => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
