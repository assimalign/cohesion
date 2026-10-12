using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpResponseInterceptorAllocationTests
{
    private readonly ITestOutputHelper _output;

    public HttpResponseInterceptorAllocationTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: Adding and resolving one interceptor allocates only its singleton array")]
    public void ResolveResponseInterceptors_WithOneAddedAndNoneRegistered_ShouldAllocateOnlyOneArray()
    {
        // Arrange — construction of both contexts and their disposal are outside the allocation
        // samples. Warm the same calls before measuring generic equality and resolution.
        ResponseInterceptor interceptor = new(HttpInterceptorScopes.None);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            _ = MeasureAddedInterceptor(interceptor);
        }

        const int iterations = 1_000;
        long expected = MeasureSingletonArray(interceptor);
        long addedBytes = 0;
        long resolvedBytes = 0;

        // Act
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            (long added, long resolved) = MeasureAddedInterceptor(interceptor);
            addedBytes += added;
            resolvedBytes += resolved;
        }

        // Assert — emit the old cost even when the regression assertion fails. Calibrating the
        // array rather than hardcoding its size also permits a runtime with different object headers.
        _output.WriteLine($"Add: {addedBytes / (double)iterations:F2} bytes/exchange; resolve: {resolvedBytes / (double)iterations:F2}; combined: {(addedBytes + resolvedBytes) / (double)iterations:F2}; singleton array: {expected}.");
        (addedBytes + resolvedBytes).ShouldBe(expected * iterations);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: Report allocations for keep-alive requests with a consumed body and one added interceptor")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveAndSend_WithBodyAndOneAddedInterceptor_ShouldReportBytesPerRequest(bool registeredResponseInterceptor)
    {
        // Arrange — a transport benchmark, with all input preloaded and no pending pipe writes.
        // No timer, Date header, listener setup, request payload, or output reporting enters a sample.
        // The second case represents a listener that already has a response interceptor, as Web.Hosting
        // does for response starting. This measurement intentionally does not pin the whole request cost.
        const int warmupRequests = 1_000;
        const int rounds = 5;
        const int requestsPerRound = 5_000;
        const int requestCount = warmupRequests + rounds * requestsPerRound;
        byte[] request = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: api.test\r\nContent-Length: 3\r\n\r\nabc");
        byte[] payload = new byte[request.Length * requestCount];
        for (int index = 0; index < requestCount; index++)
        {
            request.CopyTo(payload.AsSpan(index * request.Length));
        }

        ResponseInterceptor added = new(HttpInterceptorScopes.None);
        ResponseInterceptor registered = new(HttpInterceptorScopes.Response);
        HttpConnectionListenerOptions options = new() { AddDateHeader = false };
        options.Interceptors.Add(new JoiningInterceptor(added));
        if (registeredResponseInterceptor)
        {
            options.Interceptors.Add(registered);
        }

        await using TestConnection transport = new(payload);
        options.UseHttp1(new TestConnectionListener(transport), static http =>
        {
            http.Limits.KeepAliveTimeout = Timeout.InfiniteTimeSpan;
            http.Limits.RequestHeadersTimeout = Timeout.InfiniteTimeSpan;
            http.Limits.MinRequestBodyDataRate = null;
            http.Limits.MinResponseDataRate = null;
        });

        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(CancellationToken.None);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(CancellationToken.None);
        await using IAsyncEnumerator<IHttpContext> exchanges = connectionContext.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        byte[] body = new byte[3];
        double[] samples = new double[rounds];
        ProcessRequests(connectionContext, exchanges, body, warmupRequests);

        // Act — every operation must complete synchronously, ensuring this thread owns all measured
        // allocations. An unexpected asynchronous operation fails instead of understating the result.
        for (int round = 0; round < rounds; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ProcessRequests(connectionContext, exchanges, body, requestsPerRound);
            samples[round] = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)requestsPerRound;
        }

        // Assert
        CompleteSynchronously(exchanges.MoveNextAsync()).ShouldBeFalse();
        added.Responses.ShouldBe(requestCount);
        registered.Responses.ShouldBe(registeredResponseInterceptor ? requestCount : 0);
        _output.WriteLine($"HTTP/1.1 keep-alive POST, body 3 bytes, consumed then 204; one added interceptor; listener response interceptors: {(registeredResponseInterceptor ? 1 : 0)}; warmup {warmupRequests}; {rounds} rounds of {requestsPerRound} requests.");
        for (int round = 0; round < rounds; round++)
        {
            _output.WriteLine($"Round {round + 1}: {samples[round]:F2} bytes/request.");
        }

        Array.Sort(samples);
        _output.WriteLine($"Median: {samples[rounds / 2]:F2} bytes/request.");
    }

    private static (long Added, long Resolved) MeasureAddedInterceptor(IHttpExchangeInterceptor interceptor)
    {
        HttpExchangeInterceptorRequestContext request = new()
        {
            Version = HttpVersion.Http11,
            Method = HttpMethod.Post,
            Path = HttpPath.Root,
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = new HttpHeaderCollection().AsReadOnly(),
            Features = new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        };

        long beforeAdd = GC.GetAllocatedBytesForCurrentThread();
        request.AddResponseInterceptor(interceptor);
        IReadOnlyList<IHttpExchangeInterceptor> added = request.ResponseInterceptors;
        long addedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAdd;

        MeasurementContext context = new() { AddedResponseInterceptors = added };
        long beforeResolve = GC.GetAllocatedBytesForCurrentThread();
        IHttpExchangeInterceptor[] resolved = context.ResolveResponseInterceptors(Array.Empty<IHttpExchangeInterceptor>());
        long resolvedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeResolve;

        resolved.Length.ShouldBe(1);
        resolved[0].ShouldBeSameAs(interceptor);
        CompleteSynchronously(context.DisposeAsync());
        return (addedBytes, resolvedBytes);
    }

    private static long MeasureSingletonArray(IHttpExchangeInterceptor interceptor)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        IHttpExchangeInterceptor[] single = CreateSingletonArray(interceptor);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(single);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IHttpExchangeInterceptor[] CreateSingletonArray(IHttpExchangeInterceptor interceptor) => [interceptor];

    private static void ProcessRequests(
        IHttpConnectionContext connection,
        IAsyncEnumerator<IHttpContext> exchanges,
        byte[] body,
        int count)
    {
        for (int index = 0; index < count; index++)
        {
            if (!CompleteSynchronously(exchanges.MoveNextAsync()))
            {
                throw new InvalidOperationException("The connection ended before all preloaded requests were received.");
            }

            IHttpContext context = exchanges.Current;
            try
            {
                int consumed = 0;
                while (consumed < body.Length)
                {
                    int read = CompleteSynchronously(context.Request.Body.ReadAsync(body.AsMemory(consumed), CancellationToken.None));
                    if (read == 0)
                    {
                        throw new InvalidOperationException("The request body ended early.");
                    }

                    consumed += read;
                }

                if (body[0] != 'a' || body[1] != 'b' || body[2] != 'c' ||
                    CompleteSynchronously(context.Request.Body.ReadAsync(body.AsMemory(), CancellationToken.None)) != 0)
                {
                    throw new InvalidOperationException("The request body was not the expected three octets.");
                }

                context.Response.StatusCode = HttpStatusCode.NoContent;
                CompleteSynchronously(connection.SendAsync(context, CancellationToken.None));
            }
            finally
            {
                CompleteSynchronously(context.DisposeAsync());
            }
        }
    }

    private static T CompleteSynchronously<T>(ValueTask<T> operation)
    {
        if (!operation.IsCompleted)
        {
            throw new InvalidOperationException("The allocation measurement requires synchronous completion.");
        }

        return operation.GetAwaiter().GetResult();
    }

    private static void CompleteSynchronously(ValueTask operation)
    {
        if (!operation.IsCompleted)
        {
            throw new InvalidOperationException("The allocation measurement requires synchronous completion.");
        }

        operation.GetAwaiter().GetResult();
    }

    private sealed class MeasurementContext : TransportHttpContext
    {
        public MeasurementContext()
            : base(
                HttpVersion.Http11,
                new TransportHttpRequestHead(
                    new HttpHost("api.test"), HttpPath.Root, HttpMethod.Post, HttpScheme.Http,
                    new HttpQueryCollection(), new HttpHeaderCollection(), Stream.Null),
                HttpConnectionInfo.Empty,
                CancellationToken.None,
                featureCapacity: 0)
        {
        }
    }

    private sealed class JoiningInterceptor(IHttpExchangeInterceptor response) : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            if (context.CanHaveBody)
            {
                context.AddResponseInterceptor(response);
            }
        }
    }

    private sealed class ResponseInterceptor(HttpInterceptorScopes scopes) : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => scopes;

        public int Responses { get; private set; }

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context) => Responses++;
    }
}
