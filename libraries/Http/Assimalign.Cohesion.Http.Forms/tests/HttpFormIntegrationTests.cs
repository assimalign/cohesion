using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Forms.Tests;

/// <summary>
/// End-to-end integration tests that spin up a real HTTP/1.1 listener
/// via <see cref="HttpConnectionListener"/>, fire a request through
/// <see cref="HttpClient"/>, and verify the server-side
/// <see cref="HttpFormFeature"/> parses the body the client produced.
/// </summary>
/// <remarks>
/// <para>
/// These tests run against a loopback socket on an ephemeral port; they
/// exercise the wire path the unit tests skip:
/// <c>HttpClient -&gt; kernel -&gt; HttpConnectionListener -&gt;
/// Http1MessageReader -&gt; HttpFormFeature</c>. A bug anywhere along that
/// chain shows up here as a parse mismatch or a response failure.
/// </para>
/// <para>
/// The class shares a single <see cref="FormIntegrationServer"/> across
/// the test methods through an <see cref="IClassFixture{TFixture}"/>;
/// each request the server handles is keyed by the request path so the
/// tests can find their own parsed form without ordering coupling.
/// Tearing the listener down between tests caused flake on rapid-fire
/// runs (HttpClient saw "response ended prematurely" against a port
/// whose listener had just disposed), so we keep one listener alive for
/// the whole class.
/// </para>
/// </remarks>
public sealed class HttpFormIntegrationTests : IClassFixture<HttpFormIntegrationTests.FormIntegrationServer>
{
    private readonly FormIntegrationServer _server;

    public HttpFormIntegrationTests(FormIntegrationServer server)
    {
        _server = server;
    }

    [Fact(DisplayName = "Forms integration: HttpClient POSTs urlencoded body, server parses via HttpFormFeature")]
    public async Task UrlEncodedForm_FromHttpClient_ShouldRoundTripThroughHttpFormFeature()
    {
        FormUrlEncodedContent content = new(new[]
        {
            new KeyValuePair<string, string>("name", "alice"),
            new KeyValuePair<string, string>("role", "admin"),
            new KeyValuePair<string, string>("note", "hello world"),
        });

        Uri url = _server.UrlFor("/urlencoded");
        using HttpClient client = NewClient();
        HttpResponseMessage response = await client.PostAsync(url, content, _server.Token);

        response.IsSuccessStatusCode.ShouldBeTrue();

        IHttpFormCollection parsed = await _server.WaitForFormAsync("/urlencoded");
        parsed.Count.ShouldBe(3);
        parsed["name"].Value.ShouldBe("alice");
        parsed["role"].Value.ShouldBe("admin");
        parsed["note"].Value.ShouldBe("hello world");
    }

    [Fact(DisplayName = "Forms integration: HttpClient POSTs multipart body with field + file, server parses via HttpFormFeature")]
    public async Task MultipartForm_FromHttpClient_ShouldRoundTripThroughHttpFormFeature()
    {
        byte[] fileBytes = Encoding.UTF8.GetBytes("contents of test.txt — with non-ASCII: αβγ");

        MultipartFormDataContent multipart = new()
        {
            { new StringContent("alice"), "name" },
        };
        ByteArrayContent fileContent = new(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        multipart.Add(fileContent, "attachment", "test.txt");

        Uri url = _server.UrlFor("/multipart");
        using HttpClient client = NewClient();
        HttpResponseMessage response = await client.PostAsync(url, multipart, _server.Token);

        response.IsSuccessStatusCode.ShouldBeTrue();

        IHttpFormCollection parsed = await _server.WaitForFormAsync("/multipart");
        parsed["name"].Value.ShouldBe("alice");
        parsed.Files.Count.ShouldBe(1);
        parsed.Files.TryGetValue("attachment", out IHttpFormFile? file).ShouldBeTrue();
        file!.FileName.ShouldBe("test.txt");
        file.ContentType.ShouldStartWith("text/plain");

        using Stream uploaded = file.OpenReadStream();
        using MemoryStream copy = new();
        await uploaded.CopyToAsync(copy);
        copy.ToArray().ShouldBe(fileBytes);
    }

    /// <summary>
    /// HttpClient with connection pooling disabled. Each test gets a fresh
    /// client, no leftover pooled connections from a previous test, and
    /// no surprise keep-alive behaviour on rapid-fire runs.
    /// </summary>
    private static HttpClient NewClient()
    {
        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    /// <summary>
    /// Long-lived HTTP/1.1 server harness shared across the integration
    /// tests in this class. Listens on an ephemeral loopback port, parses
    /// the form for every request it receives, and surfaces the result
    /// keyed by request path so tests can pick out their own parse without
    /// ordering coupling.
    /// </summary>
    public sealed class FormIntegrationServer : IAsyncDisposable
    {
        private readonly HttpConnectionListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _serverTask;
        private readonly int _port;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IHttpFormCollection>> _byPath =
            new(StringComparer.Ordinal);

        public FormIntegrationServer()
        {
            _port = GetAvailablePort();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            _listener = HttpConnectionListener.Create(options =>
            {
                options.UseHttp1(transport =>
                {
                    transport.EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _port);
                });
            });

            _serverTask = Task.Run(AcceptLoopAsync);
        }

        public CancellationToken Token => _cts.Token;

        public Uri UrlFor(string path) => new($"http://127.0.0.1:{_port}{path}");

        public Task<IHttpFormCollection> WaitForFormAsync(string path)
        {
            TaskCompletionSource<IHttpFormCollection> tcs = _byPath.GetOrAdd(
                path,
                _ => new TaskCompletionSource<IHttpFormCollection>(TaskCreationOptions.RunContinuationsAsynchronously));
            return tcs.Task;
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    IHttpConnection connection;
                    try
                    {
                        connection = await _listener.AcceptOrListenAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    _ = Task.Run(() => HandleConnectionAsync(connection));
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Surface unexpected listener errors on any pending waiters
                // so the test fails loudly rather than hanging.
                foreach (var tcs in _byPath.Values)
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        private async Task HandleConnectionAsync(IHttpConnection connection)
        {
            try
            {
                await using (connection)
                {
                    IHttpConnectionContext connectionContext = await connection.OpenAsync(_cts.Token);

                    await foreach (IHttpContext context in connectionContext.ReceiveAsync(_cts.Token))
                    {
                        try
                        {
                            string path = context.Request.Path.Value;
                            HttpFormFeature feature = new(context.Request);
                            IHttpFormCollection form = await feature.ReadFormAsync(_cts.Token);

                            TaskCompletionSource<IHttpFormCollection> tcs = _byPath.GetOrAdd(
                                path,
                                _ => new TaskCompletionSource<IHttpFormCollection>(TaskCreationOptions.RunContinuationsAsynchronously));
                            tcs.TrySetResult(form);

                            // Explicit Connection: close ends the wire conversation cleanly:
                            // HttpClient stops expecting more bytes from this socket the
                            // moment it reads this header, so closing it from the server
                            // side cannot race the client's response read.
                            context.Response.StatusCode = HttpStatusCode.Ok;
                            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
                            context.Response.Headers[HttpHeaderKey.Connection] = "close";
                            byte[] ok = Encoding.UTF8.GetBytes("ok");
                            await context.Response.Body.WriteAsync(ok, 0, ok.Length, _cts.Token);
                            await connectionContext.SendAsync(context, _cts.Token);
                        }
                        finally
                        {
                            await context.DisposeAsync();
                        }

                        break; // one request per connection; client uses Connection: close anyway
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // listener teardown
            }
            catch (Exception ex)
            {
                foreach (var tcs in _byPath.Values)
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _serverTask;
            }
            catch
            {
                // dispose path — assertions are the authoritative signal
            }
            await _listener.DisposeAsync();
            _cts.Dispose();
        }

        private static int GetAvailablePort()
        {
            System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }
    }
}
