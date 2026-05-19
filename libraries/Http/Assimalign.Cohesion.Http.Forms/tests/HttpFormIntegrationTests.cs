using System;
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
/// These tests run against a loopback socket on an ephemeral port; they
/// exercise the wire path the unit tests skip:
/// <c>HttpClient -&gt; kernel -&gt; HttpConnectionListener -&gt;
/// Http1MessageReader -&gt; HttpFormFeature</c>. A bug anywhere along that
/// chain shows up here as a parse mismatch or a response failure.
/// Each test owns its own listener (no cross-test reuse) to keep
/// connection-lifecycle races from leaking between tests.
/// </remarks>
public class HttpFormIntegrationTests
{
    [Fact(DisplayName = "Forms integration: HttpClient POSTs urlencoded body, server parses via HttpFormFeature")]
    public async Task UrlEncodedForm_FromHttpClient_ShouldRoundTripThroughHttpFormFeature()
    {
        await using FormIntegrationServer server = await FormIntegrationServer.StartAsync();

        FormUrlEncodedContent content = new(new[]
        {
            new KeyValuePair<string, string>("name", "alice"),
            new KeyValuePair<string, string>("role", "admin"),
            new KeyValuePair<string, string>("note", "hello world"),
        });

        using HttpClient client = NewClient();
        HttpResponseMessage response = await client.PostAsync(server.Url, content, server.Token);

        response.IsSuccessStatusCode.ShouldBeTrue();

        IHttpFormCollection parsed = await server.WaitForFormAsync();
        parsed.Count.ShouldBe(3);
        parsed["name"].Value.ShouldBe("alice");
        parsed["role"].Value.ShouldBe("admin");
        parsed["note"].Value.ShouldBe("hello world");
    }

    [Fact(DisplayName = "Forms integration: HttpClient POSTs multipart body with field + file, server parses via HttpFormFeature")]
    public async Task MultipartForm_FromHttpClient_ShouldRoundTripThroughHttpFormFeature()
    {
        await using FormIntegrationServer server = await FormIntegrationServer.StartAsync();

        byte[] fileBytes = Encoding.UTF8.GetBytes("contents of test.txt — with non-ASCII: αβγ");

        MultipartFormDataContent multipart = new()
        {
            { new StringContent("alice"), "name" },
        };
        ByteArrayContent fileContent = new(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        multipart.Add(fileContent, "attachment", "test.txt");

        using HttpClient client = NewClient();
        HttpResponseMessage response = await client.PostAsync(server.Url, multipart, server.Token);

        response.IsSuccessStatusCode.ShouldBeTrue();

        IHttpFormCollection parsed = await server.WaitForFormAsync();
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
    /// client, no leftover pooled connections, and no surprise keep-alive
    /// behaviour on rapid-fire runs.
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
    /// One-shot HTTP/1.1 server harness: starts a listener on an
    /// ephemeral loopback port, accepts a single request, runs
    /// <see cref="HttpFormFeature.ReadFormAsync"/> against it, and exposes
    /// the parsed form through <see cref="WaitForFormAsync"/>.
    /// </summary>
    private sealed class FormIntegrationServer : IAsyncDisposable
    {
        private readonly HttpConnectionListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _serverTask;
        private readonly TaskCompletionSource<IHttpFormCollection> _formTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FormIntegrationServer(HttpConnectionListener listener, int port, CancellationTokenSource cts)
        {
            _listener = listener;
            _cts = cts;
            Url = new Uri($"http://127.0.0.1:{port}/form");
            Token = cts.Token;
            _serverTask = Task.Run(ServerLoopAsync);
        }

        public Uri Url { get; }
        public CancellationToken Token { get; }

        public static async Task<FormIntegrationServer> StartAsync()
        {
            int port = GetAvailablePort();
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

            HttpConnectionListener listener = HttpConnectionListener.Create(options =>
            {
                options.UseHttp1(transport =>
                {
                    transport.EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);
                });
            });

            FormIntegrationServer server = new(listener, port, cts);
            // Grace window so Task.Run schedules the loop and the listener
            // binds before HttpClient races us with a connect.
            await Task.Delay(200, cts.Token);
            return server;
        }

        public Task<IHttpFormCollection> WaitForFormAsync() => _formTcs.Task;

        private async Task ServerLoopAsync()
        {
            try
            {
                await using IHttpConnection connection = await _listener.AcceptOrListenAsync(Token);
                IHttpConnectionContext connectionContext = await connection.OpenAsync(Token);

                await foreach (IHttpContext context in connectionContext.ReceiveAsync(Token))
                {
                    try
                    {
                        HttpFormFeature feature = new(context.Request);
                        IHttpFormCollection form = await feature.ReadFormAsync(Token);
                        _formTcs.TrySetResult(form);

                        // Connection: close ends the wire conversation cleanly:
                        // HttpClient stops expecting more bytes from this socket
                        // the moment it reads this header, so closing it from
                        // the server side cannot race the client's response read.
                        context.Response.StatusCode = HttpStatusCode.Ok;
                        context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
                        context.Response.Headers[HttpHeaderKey.Connection] = "close";
                        byte[] ok = Encoding.UTF8.GetBytes("ok");
                        await context.Response.Body.WriteAsync(ok, 0, ok.Length, Token);
                        await connectionContext.SendAsync(context, Token);
                    }
                    finally
                    {
                        await context.DisposeAsync();
                    }

                    break;
                }
            }
            catch (OperationCanceledException)
            {
                _formTcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _formTcs.TrySetException(ex);
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
