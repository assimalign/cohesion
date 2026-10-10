using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

using static Assimalign.Cohesion.Http.Connections.Tests.TestObjects.RequestInterceptorDoubles;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// <see cref="HttpConnectionListenerOptions.ExchangeFeatureCapacity"/> sizes each exchange's feature
/// collection for the features a host stamps onto it (#1381), so stamping them never grows it. Every
/// creation site is covered on every protocol: the parse-time collection an interceptor fills (the
/// HTTP/1.1 parser, and the shared HTTP/2 and HTTP/3 interceptor pipeline) and the exchange context's
/// own on the zero-interceptor fast path. Growing the collection is the only thing a stamp allocates,
/// so a stamp that allocates nothing did not grow it.
/// </summary>
public class HttpExchangeFeatureCapacityTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Feature capacity: Stamping up to the capacity onto an interceptor-filled collection should not grow it")]
    [InlineData(HttpProtocol.Http11, 8)]
    [InlineData(HttpProtocol.Http11, 16)]
    [InlineData(HttpProtocol.Http20, 8)]
    [InlineData(HttpProtocol.Http20, 16)]
    [InlineData(HttpProtocol.Http30, 8)]
    [InlineData(HttpProtocol.Http30, 16)]
    public async Task Stamping_WithCapacityAndInterceptor_ShouldNotGrowTheCollection(HttpProtocol protocol, int featureCount)
    {
        // Arrange — the interceptor attaches the first feature while the request is parsed, which
        // creates the collection's dictionary; the host stamps the rest after dispatch.
        HttpConnectionListenerOptions options = new() { ExchangeFeatureCapacity = featureCount };
        options.Interceptors.Add(new HostFeatureAttachingInterceptor());
        IHttpFeature[] stamped = CreateFeatures(featureCount - 1);
        WarmUp(stamped);
        IHttpContext exchange = await ReceiveExchangeAsync(protocol, options);

        // Act
        long allocated = Stamp(exchange.Features, stamped);

        // Assert
        allocated.ShouldBe(0L);
        exchange.Features.Get<RecordingFeature>().ShouldNotBeNull();
        AssertStamped(exchange.Features, stamped);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Feature capacity: Stamping up to the capacity onto the fast path's collection should not grow it")]
    [InlineData(HttpProtocol.Http11, 8)]
    [InlineData(HttpProtocol.Http11, 16)]
    [InlineData(HttpProtocol.Http20, 8)]
    [InlineData(HttpProtocol.Http20, 16)]
    [InlineData(HttpProtocol.Http30, 8)]
    [InlineData(HttpProtocol.Http30, 16)]
    public async Task Stamping_WithCapacityAndNoInterceptor_ShouldNotGrowTheCollection(HttpProtocol protocol, int featureCount)
    {
        // Arrange — no interceptor, so the exchange context creates the collection itself. The first
        // stamp creates its dictionary; the rest must fit in it.
        HttpConnectionListenerOptions options = new() { ExchangeFeatureCapacity = featureCount };
        IHttpFeature[] features = CreateFeatures(featureCount);
        IHttpFeature[] stamped = features[1..];
        WarmUp(features);
        IHttpContext exchange = await ReceiveExchangeAsync(protocol, options);
        exchange.Features.Set(features[0]);

        // Act
        long allocated = Stamp(exchange.Features, stamped);

        // Assert
        allocated.ShouldBe(0L);
        AssertStamped(exchange.Features, features);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Feature capacity: Without a capacity, stamping should grow the collection")]
    [InlineData(HttpProtocol.Http11)]
    [InlineData(HttpProtocol.Http20)]
    [InlineData(HttpProtocol.Http30)]
    public async Task Stamping_WithoutCapacity_ShouldGrowTheCollection(HttpProtocol protocol)
    {
        // Arrange — the measurement the other tests rely on: the same eight stamps onto a collection
        // left at the default size do allocate, because the dictionary grows from three slots.
        HttpConnectionListenerOptions options = new();
        IHttpFeature[] features = CreateFeatures(8);
        IHttpFeature[] stamped = features[1..];
        WarmUp(features);
        IHttpContext exchange = await ReceiveExchangeAsync(protocol, options);
        exchange.Features.Set(features[0]);

        // Act
        long allocated = Stamp(exchange.Features, stamped);

        // Assert
        allocated.ShouldBeGreaterThan(0L);
        AssertStamped(exchange.Features, features);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Feature capacity: Stamping past the capacity should still install every feature")]
    [InlineData(HttpProtocol.Http11)]
    [InlineData(HttpProtocol.Http20)]
    [InlineData(HttpProtocol.Http30)]
    public async Task Stamping_PastCapacity_ShouldInstallEveryFeature(HttpProtocol protocol)
    {
        // Arrange
        HttpConnectionListenerOptions options = new() { ExchangeFeatureCapacity = 2 };
        options.Interceptors.Add(new HostFeatureAttachingInterceptor());
        IHttpFeature[] stamped = CreateFeatures(16);
        IHttpContext exchange = await ReceiveExchangeAsync(protocol, options);

        // Act
        Stamp(exchange.Features, stamped);

        // Assert
        exchange.Features.Get<RecordingFeature>().ShouldNotBeNull();
        AssertStamped(exchange.Features, stamped);
    }

    // ------------------------------------------------------------------ helpers

    private static IHttpFeature[] CreateFeatures(int count)
    {
        IHttpFeature[] features = new IHttpFeature[count];

        for (int i = 0; i < count; i++)
        {
            features[i] = new StampedFeature($"Cohesion.Tests.StampedFeature{i}");
        }

        return features;
    }

    /// <summary>
    /// Runs the measured code once against a throwaway collection, so the measured run allocates
    /// nothing for first-call work.
    /// </summary>
    private static void WarmUp(IHttpFeature[] features)
    {
        Stamp(new HttpFeatureCollection(), features);
    }

    /// <summary>
    /// Sets every feature and returns the bytes the current thread allocated doing it.
    /// </summary>
    private static long Stamp(IHttpFeatureCollection collection, IHttpFeature[] features)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        foreach (IHttpFeature feature in features)
        {
            collection.Set(feature);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void AssertStamped(IHttpFeatureCollection collection, IHttpFeature[] features)
    {
        foreach (IHttpFeature feature in features)
        {
            collection.Get(feature.Name).ShouldBeSameAs(feature);
        }
    }

    private static async Task<IHttpContext> ReceiveExchangeAsync(HttpProtocol protocol, HttpConnectionListenerOptions options)
    {
        switch (protocol)
        {
            case HttpProtocol.Http11:
                options.UseHttp1(new TestConnectionListener(new TestConnection(HttpProtocolPayloadFactory.CreateHttp1Request(
                    "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"))));
                break;
            case HttpProtocol.Http20:
                options.UseHttp2(new TestConnectionListener(new TestConnection(
                    HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test"))));
                break;
            default:
                options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(new TestConnection(
                    HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "api.test")))));
                break;
        }

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private sealed class StampedFeature : IHttpFeature
    {
        public StampedFeature(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }
}
