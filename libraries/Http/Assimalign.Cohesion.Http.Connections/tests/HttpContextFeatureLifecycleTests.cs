using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Exercises the per-request feature lifecycle on transport-produced
/// contexts: features attached to <see cref="IHttpContext.Features"/>
/// (middleware-style) that own state (<see cref="IDisposable"/> /
/// <see cref="IAsyncDisposable"/>) are disposed when the owning context
/// disposes, and a single faulty feature cannot leak the rest.
/// </summary>
/// <remarks>
/// The old per-listener feature factory
/// (<c>HttpConnectionListenerOptions.CreateFeatures</c>) no longer exists;
/// features are attached per request via <c>Features.Set</c>.
/// </remarks>
public class HttpContextFeatureLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Features: Should expose an empty per-request feature collection by default")]
    public async Task Features_OnRequest_ShouldExposeEmptyCollection()
    {
        // Arrange + Act
        IHttpContext httpContext = await ReceiveSingleContextAsync();

        // Assert — Features is non-null but empty.
        httpContext.Features.ShouldNotBeNull();
        using IEnumerator<IHttpFeature> enumerator = httpContext.Features.GetEnumerator();
        enumerator.MoveNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Features: IDisposable feature should be disposed when the request disposes")]
    public async Task DisposeAsync_OnDisposableFeature_ShouldDisposeFeature()
    {
        // Arrange
        IHttpContext httpContext = await ReceiveSingleContextAsync();
        DisposableFeature feature = new();
        httpContext.Features.Set(feature);

        // Pre-condition — feature has not been disposed yet.
        feature.DisposeCount.ShouldBe(0);

        // Act
        await httpContext.DisposeAsync();

        // Assert
        feature.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Features: IAsyncDisposable feature should be disposed asynchronously when the request disposes")]
    public async Task DisposeAsync_OnAsyncDisposableFeature_ShouldPreferAsyncDisposal()
    {
        // Arrange
        IHttpContext httpContext = await ReceiveSingleContextAsync();
        AsyncDisposableFeature feature = new();
        httpContext.Features.Set(feature);

        // Act
        await httpContext.DisposeAsync();

        // Assert — IAsyncDisposable path was taken (not the IDisposable fallback).
        feature.AsyncDisposeCount.ShouldBe(1);
        feature.SyncDisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Features: A throwing feature should not prevent other features from disposing")]
    public async Task DisposeAsync_OnThrowingFeature_ShouldContinueDisposingRemainingFeatures()
    {
        // Arrange — three features; the middle one throws on disposal. The
        // contract is that the remaining features still get cleaned up so a
        // single faulty feature cannot leak resources for the whole request.
        IHttpContext httpContext = await ReceiveSingleContextAsync();
        DisposableFeature first = new("first");
        ThrowingDisposableFeature broken = new();
        DisposableFeature last = new("last");
        httpContext.Features.Set(first);
        httpContext.Features.Set(broken);
        httpContext.Features.Set(last);

        // Act
        await httpContext.DisposeAsync();

        // Assert — both non-throwing features were disposed; the exception
        // did not escape DisposeAsync.
        first.DisposeCount.ShouldBe(1);
        last.DisposeCount.ShouldBe(1);
        broken.DisposeAttempted.ShouldBeTrue();
    }

    private static async Task<IHttpContext> ReceiveSingleContextAsync()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private sealed class DisposableFeature : IHttpFeature, IDisposable
    {
        public DisposableFeature(string label = "default")
        {
            Label = label;
        }

        public string Name => $"Cohesion.Tests.DisposableFeature:{Label}";

        public string Label { get; }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class AsyncDisposableFeature : IHttpFeature, IAsyncDisposable, IDisposable
    {
        public string Name => "Cohesion.Tests.AsyncDisposableFeature";

        public int AsyncDisposeCount { get; private set; }

        public int SyncDisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            AsyncDisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            // Should NOT be called when IAsyncDisposable is present; tracked
            // so the test can assert that the async path wins.
            SyncDisposeCount++;
        }
    }

    private sealed class ThrowingDisposableFeature : IHttpFeature, IDisposable
    {
        public string Name => "Cohesion.Tests.ThrowingDisposableFeature";

        public bool DisposeAttempted { get; private set; }

        public void Dispose()
        {
            DisposeAttempted = true;
            throw new InvalidOperationException("Feature disposal failed deliberately for the test.");
        }
    }
}
