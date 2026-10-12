using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class Http3InputCompletionAllocationTests
{
    private const int requestCount = 16;
    private readonly ITestOutputHelper _output;

    public Http3InputCompletionAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HTTP/3 input completion: A known FIN should avoid default client-fault feature allocations")]
    public async Task ReceiveAsync_OnKnownHttp3Fin_ShouldAvoidClientFaultFeatureAllocations()
    {
        // Arrange — warm both identical stream-backed paths, changing only whether the transport
        // exposes its already-received FIN. Keep every actual Web.Hosting default interceptor.
        await MeasureReceiveAsync(publishCompletion: false);
        await MeasureReceiveAsync(publishCompletion: true);

        long withoutCompletion = long.MaxValue;
        long withCompletion = long.MaxValue;

        // Act — take the minimum of warmed samples to exclude incidental runtime bookkeeping.
        for (int sample = 0; sample < 3; sample++)
        {
            (long hiddenBytes, int hiddenFeatures) = await MeasureReceiveAsync(publishCompletion: false);
            (long visibleBytes, int visibleFeatures) = await MeasureReceiveAsync(publishCompletion: true);
            withoutCompletion = Math.Min(withoutCompletion, hiddenBytes);
            withCompletion = Math.Min(withCompletion, visibleBytes);

            // Assert — the response-starting interceptor still participates in every response, but
            // the client-fault interceptor and its per-exchange feature are unnecessary on these GETs.
            hiddenFeatures.ShouldBe(requestCount);
            visibleFeatures.ShouldBe(0);
        }

        // Each omitted feature alone saves an object header and one reference (24 bytes on x64).
        // Its response-phase claim and merged interceptor list also disappear; avoid pinning their
        // implementation-dependent sizes or claiming the independent response-starting cost is gone.
        long saved = withoutCompletion - withCompletion;
        _output.WriteLine($"{requestCount} requests: FIN hidden {withoutCompletion} B; FIN exposed {withCompletion} B; saved {saved} B ({saved / requestCount} B/request).");
        saved.ShouldBeGreaterThanOrEqualTo(requestCount * 3L * IntPtr.Size);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HTTP/3 input completion: A declared body missing at FIN should retain the client-fault latch")]
    public async Task ReceiveAsync_OnDeclaredBodyMissingAtFin_ShouldRetainClientFaultFeature()
    {
        // Arrange — the stream-backed reader has not read zero, but the transport knows the FIN.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CancellationToken cancellationToken = timeout.Token;
        await using InMemoryMultiplexedConnectionListener transport = new();
        HttpConnectionListenerOptions options = new() { AddDateHeader = false };
        options.UseHttp3(new CompletedStreamInputListener(transport, publishCompletion: true));
        WebApplicationServerBuilder.ApplyDefaultInterceptors(options);
        await using HttpConnectionListener listener = new(options);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);
        await client.OpenControlStreamAsync(cancellationToken);
        await client.SendRequestAsync("POST", "/incomplete", [("content-length", "5")], [], endStream: true, cancellationToken);
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken);
        await using IAsyncEnumerator<IHttpContext> requests = connectionContext.ReceiveAsync(cancellationToken).GetAsyncEnumerator();

        // Act — late framing validation still needs the host-visible fault report.
        (await requests.MoveNextAsync()).ShouldBeTrue();
        await using IHttpContext context = requests.Current;
        IWebClientFaultFeature? feature = context.Features.Get<IWebClientFaultFeature>();

        // Assert.
        feature.ShouldNotBeNull();
        feature.StatusCode.ShouldBeNull();
        await Should.ThrowAsync<IOException>(() => context.Request.Body.CopyToAsync(Stream.Null, cancellationToken));
        feature.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<(long Bytes, int ClientFaultFeatures)> MeasureReceiveAsync(bool publishCompletion)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CancellationToken cancellationToken = timeout.Token;
        await using InMemoryMultiplexedConnectionListener transport = new();
        HttpConnectionListenerOptions options = new() { AddDateHeader = false };
        options.UseHttp3(new CompletedStreamInputListener(transport, publishCompletion));
        WebApplicationServerBuilder.ApplyDefaultInterceptors(options);
        await using HttpConnectionListener listener = new(options);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);
        await client.OpenControlStreamAsync(cancellationToken);

        for (int index = 0; index < requestCount; index++)
        {
            await client.SendRequestAsync("GET", "/complete", [], [], endStream: true, cancellationToken);
        }

        await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken);
        await using IAsyncEnumerator<IHttpContext> requests = connectionContext.ReceiveAsync(cancellationToken).GetAsyncEnumerator();

        // Every head was queued before receive starts: the first MoveNext parses all sixteen
        // synchronously, then parks acceptance. Count only that call, never an asynchronous await.
        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<bool> first = requests.MoveNextAsync();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        first.IsCompletedSuccessfully.ShouldBeTrue();
        first.Result.ShouldBeTrue();

        int clientFaultFeatures = 0;
        for (int index = 0; index < requestCount; index++)
        {
            if (index > 0)
            {
                (await requests.MoveNextAsync()).ShouldBeTrue();
            }

            await using IHttpContext context = requests.Current;
            if (context.Features.Get<IWebClientFaultFeature>() is not null)
            {
                clientFaultFeatures++;
            }

            await connectionContext.SendAsync(context, cancellationToken);
        }

        return (allocated, clientFaultFeatures);
    }
}
