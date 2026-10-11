using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Web.Hosting;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.HttpsPolicy.Tests;

/// <summary>
/// <c>UseHttpsRedirection</c> over the real HTTP/1.1 transport: raw request bytes written to an in-memory
/// connection, so the request-target forms an <c>HttpClient</c> never sends (absolute-form) reach the
/// middleware the way a client or proxy sends them. The <c>Location</c> pins what the transport hands the
/// middleware as much as what the middleware writes.
/// </summary>
public class HttpsRedirectionEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Web.HttpsPolicy] - E2E: The Location should name the resource the request-target named, in origin-form and absolute-form")]
    [InlineData("GET /caf%C3%A9 HTTP/1.1\r\nHost: example.com\r\n\r\n", "https://example.com/caf%C3%A9")]
    [InlineData("GET http://example.com/caf%C3%A9 HTTP/1.1\r\nHost: example.com\r\n\r\n", "https://example.com/caf%C3%A9")]
    [InlineData("GET http://example.com/a%2Fb/100%25?q=a%20b HTTP/1.1\r\nHost: example.com\r\n\r\n", "https://example.com/a%2Fb/100%25?q=a%20b")]
    public async Task Redirection_EncodedRequestTarget_ShouldRedirectToTheSameResource(string request, string expectedLocation)
    {
        // Arrange — RFC 9112 §3.2.2: a server accepts the absolute-form of any request-target. Both forms
        // reach the middleware with the decoded path, so it is encoded once, not twice ("%25C3%25A9").
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        RawHttp1Application application = await RawHttp1Application.StartAsync(cancellationToken);

        await using (application)
        {
            // Act
            string response = await application.ExchangeAsync(request, cancellationToken);

            // Assert
            response.ShouldStartWith("HTTP/1.1 307");
            ReadHeader(response, "Location").ShouldBe(expectedLocation);
            application.TerminalInvocations.ShouldBe(0);
        }
    }

    private static string? ReadHeader(string response, string name)
    {
        foreach (string line in response.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// A minimal application serving HTTP/1.1 on a private in-memory listener: <c>UseHttpsRedirection</c>
    /// first, then a terminal handler that answers 200 and counts the requests that reach it.
    /// </summary>
    private sealed class RawHttp1Application : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly IWebApplicationServer _server;
        private readonly InMemoryConnectionListener _listener;
        private readonly StrongBox<int> _terminalInvocations;

        private RawHttp1Application(
            WebApplication application,
            IWebApplicationServer server,
            InMemoryConnectionListener listener,
            StrongBox<int> terminalInvocations)
        {
            _application = application;
            _server = server;
            _listener = listener;
            _terminalInvocations = terminalInvocations;
        }

        public int TerminalInvocations => Volatile.Read(ref _terminalInvocations.Value);

        public static async Task<RawHttp1Application> StartAsync(CancellationToken cancellationToken)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            InMemoryConnectionListener listener = new();
            builder.Server.UseServer(options => options.UseHttp1(listener));

            WebApplication application = builder.Build();
            StrongBox<int> terminalInvocations = new(0);

            application.UseHttpsRedirection();
            application.Use((context, next) =>
            {
                Interlocked.Increment(ref terminalInvocations.Value);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            });

            IWebApplicationServer server = application.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
            await server.StartAsync(cancellationToken);

            return new RawHttp1Application(application, server, listener, terminalInvocations);
        }

        /// <summary>
        /// Dials the listener, writes one raw HTTP/1.1 request, and reads until the end of the response
        /// head (the blank line), returning everything read as ASCII text.
        /// </summary>
        public async Task<string> ExchangeAsync(string request, CancellationToken cancellationToken)
        {
            InMemoryConnectionFactory connectionFactory = _listener.CreateFactory();
            Connection connection = await connectionFactory.ConnectAsync(_listener.EndPoint, cancellationToken);

            await using (connection.ConfigureAwait(false))
            {
                Stream stream = connection.AsStream();

                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
                await stream.FlushAsync(cancellationToken);

                byte[] buffer = new byte[4096];
                StringBuilder response = new();

                while (!response.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    response.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                return response.ToString();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _server.StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)_application).DisposeAsync();
        }
    }
}
