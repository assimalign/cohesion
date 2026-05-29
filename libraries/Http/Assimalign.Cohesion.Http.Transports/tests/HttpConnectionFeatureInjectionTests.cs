using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

/// <summary>
/// Exercises the per-request feature factory surface
/// (<see cref="HttpConnectionListenerOptions.CreateFeatures"/>): each
/// <see cref="IHttpContext"/> gets a freshly produced
/// <see cref="IHttpFeatureCollection"/> visible on
/// <see cref="IHttpContext.Features"/>, and features that own state
/// (<see cref="IDisposable"/> / <see cref="IAsyncDisposable"/>) get
/// disposed when the owning context disposes.
/// </summary>
public class HttpConnectionFeatureInjectionTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: Should be invoked once per IHttpContext on HTTP/1.1")]
    public async Task CreateFeatures_OnHttp1Request_ShouldBeInvokedExactlyOncePerRequest()
    {
        // Arrange — single HTTP/1.1 request (Connection: close → loop yields one context then completes).
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        int invocationCount = 0;
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                invocationCount++;
                return new HttpFeatureCollection();
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act — pull one IHttpContext.
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        invocationCount.ShouldBe(1);
        httpContext.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: Should fire once per request across keep-alive connection")]
    public async Task CreateFeatures_OnHttp1KeepAliveRequests_ShouldBeInvokedOncePerRequest()
    {
        // Arrange — two pipelined requests over a keep-alive connection.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /first HTTP/1.1\r\nHost: api.test\r\n\r\n" +
            "GET /second HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        int invocationCount = 0;
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                invocationCount++;
                return new HttpFeatureCollection();
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act — drain both requests.
        int contextCount = 0;
        await foreach (IHttpContext _ in httpConnectionContext.ReceiveAsync())
        {
            contextCount++;
        }

        // Assert
        contextCount.ShouldBe(2);
        invocationCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: Features supplied by the factory should be visible on IHttpContext.Features")]
    public async Task CreateFeatures_OnRequest_ShouldExposeFactoryFeaturesViaContext()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                HttpFeatureCollection features = new();
                features.Set(new MarkerFeature("hello"));
                return features;
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert — Get by name returns the factory-attached feature.
        IHttpFeature? resolved = httpContext.Features.Get(MarkerFeature.FeatureName);
        resolved.ShouldBeOfType<MarkerFeature>().Label.ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: Each request gets its own factory-produced collection (no cross-request bleed)")]
    public async Task CreateFeatures_OnConcurrentRequests_ShouldIsolatePerRequestState()
    {
        // Arrange — two pipelined requests; verify each context sees its own
        // factory-produced feature, not the other's.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /first HTTP/1.1\r\nHost: api.test\r\n\r\n" +
            "GET /second HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        int counter = 0;
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                HttpFeatureCollection features = new();
                features.Set(new MarkerFeature($"req-{++counter}"));
                return features;
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        List<string> labels = new();
        await foreach (IHttpContext httpContext in httpConnectionContext.ReceiveAsync())
        {
            IHttpFeature feature = httpContext.Features.Get(MarkerFeature.FeatureName)!;
            labels.Add(((MarkerFeature)feature).Label);
        }

        // Assert — each request observed its own per-request feature label.
        labels.ShouldBe(new[] { "req-1", "req-2" });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: IDisposable feature should be disposed when the request disposes")]
    public async Task CreateFeatures_OnRequestDispose_ShouldDisposeDisposableFeatures()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        DisposableFeature feature = new();
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                HttpFeatureCollection features = new();
                features.Set(feature);
                return features;
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Pre-condition — feature has not been disposed yet.
        feature.DisposeCount.ShouldBe(0);

        // Act
        await httpContext.DisposeAsync();

        // Assert
        feature.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: IAsyncDisposable feature should be disposed asynchronously when the request disposes")]
    public async Task CreateFeatures_OnRequestDispose_ShouldAsyncDisposeAsyncDisposableFeatures()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        AsyncDisposableFeature feature = new();
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                HttpFeatureCollection features = new();
                features.Set(feature);
                return features;
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Pre-condition — feature has not been disposed yet.
        feature.AsyncDisposeCount.ShouldBe(0);

        // Act
        await httpContext.DisposeAsync();

        // Assert — IAsyncDisposable path was taken (not the IDisposable fallback).
        feature.AsyncDisposeCount.ShouldBe(1);
        feature.SyncDisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: A throwing feature should not prevent other features (or body streams) from disposing")]
    public async Task CreateFeatures_OnDisposeFailure_ShouldContinueDisposingRemainingFeatures()
    {
        // Arrange — three features. The middle one throws on disposal; the
        // contract is that the remaining features and the body streams still
        // get cleaned up so a single faulty feature cannot leak resources for
        // the whole request.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        DisposableFeature first = new("first");
        ThrowingDisposableFeature broken = new();
        DisposableFeature last = new("last");
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                HttpFeatureCollection features = new();
                features.Set(first);
                features.Set(broken);
                features.Set(last);
                return features;
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Act
        await httpContext.DisposeAsync();

        // Assert — both non-throwing features were disposed; the exception did
        // not escape DisposeAsync.
        first.DisposeCount.ShouldBe(1);
        last.DisposeCount.ShouldBe(1);
        broken.DisposeAttempted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: Null factory should yield an empty per-request feature collection")]
    public async Task CreateFeatures_OnNullFactory_ShouldYieldEmptyFeatureCollection()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert — Features is non-null but empty.
        httpContext.Features.ShouldNotBeNull();
        using IEnumerator<IHttpFeature> enumerator = httpContext.Features.GetEnumerator();
        enumerator.MoveNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - CreateFeatures: Middleware Set calls do not mutate the factory-supplied collection")]
    public async Task CreateFeatures_OnMiddlewareMutation_ShouldNotMutateFactoryCollection()
    {
        // Arrange — capture the factory's collection so we can verify it stays
        // untouched after middleware-style Set calls land on the per-request
        // context.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpFeatureCollection? capturedFactoryCollection = null;
        HttpConnectionListenerOptions options = new()
        {
            CreateFeatures = () =>
            {
                capturedFactoryCollection = new HttpFeatureCollection();
                return capturedFactoryCollection;
            }
        };
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Act — middleware adds a feature to the request-scoped collection.
        MarkerFeature middlewareFeature = new("from-middleware");
        httpContext.Features.Set(middlewareFeature);

        // Assert
        // The request-scoped collection sees the middleware-added feature...
        httpContext.Features.Get(MarkerFeature.FeatureName).ShouldBeSameAs(middlewareFeature);
        // ...but the factory's collection was not touched.
        capturedFactoryCollection.ShouldNotBeNull();
        capturedFactoryCollection!.Get(MarkerFeature.FeatureName).ShouldBeNull();
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private sealed class MarkerFeature : IHttpFeature
    {
        public const string FeatureName = "Cohesion.Tests.MarkerFeature";

        public MarkerFeature(string label)
        {
            Label = label;
        }

        public string Name => FeatureName;

        public string Label { get; }
    }

    private sealed class DisposableFeature : IHttpFeature, IDisposable
    {
        public const string FeatureName = "Cohesion.Tests.DisposableFeature";

        public DisposableFeature(string label = "default")
        {
            Label = label;
        }

        public string Name => $"{FeatureName}:{Label}";

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
