using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpConnectionInfo = Assimalign.Cohesion.Http.HttpConnectionInfo;
using HttpHeaderKey = Assimalign.Cohesion.Http.HttpHeaderKey;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// Proxy-awareness coverage (#1050): access-log entries carry the effective client address, scheme,
/// and host, with the transport peer kept beside a forwarded client. End-to-end cases register
/// <c>UseHttpLogging</c> first and the real forwarded-headers middleware after it over
/// <see cref="WebApplicationTestFactory"/> (whose in-memory peer is trusted as a local transport but
/// has no IP); unit cases drive both middleware over <see cref="LoggingTestContext"/> with a plaintext
/// request from an explicit proxy address.
/// </summary>
public class HttpLoggingForwardedTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly IPEndPoint _trustedProxy = new(IPAddress.Parse("10.0.0.2"), 51000);

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Forwarded: Entries behind a trusted proxy should carry the effective client, scheme, and host")]
    public async Task Emit_TrustedProxyForwardedIdentity_ShouldLogEffectiveValues()
    {
        // Arrange — logging first, forwarded headers after it: the entry is built after the pipeline
        // unwinds, by which point the forwarded feature is on the exchange.
        using CancellationTokenSource cancellation = new(_testTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(loggerFactory)
            .UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded)
            .Use((context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        using HttpClient client = factory.CreateClient();

        // Act — plaintext on the wire, addressed to the proxy's upstream name.
        (await SendProxiedAsync(client, "http://internal.upstream/orders", cancellation.Token)).Dispose();

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Attributes[HttpLoggingAttributes.ClientAddress].ShouldBe("203.0.113.9");
        entry.Attributes[HttpLoggingAttributes.RequestScheme].ShouldBe("https");
        entry.Attributes[HttpLoggingAttributes.RequestHost].ShouldBe("public.example");

        // The in-memory transport peer is not an IP endpoint, so there is no peer address to record.
        entry.Attributes.ContainsKey(HttpLoggingAttributes.PeerAddress).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Forwarded: Without UseForwardedHeaders spoofed forwarding headers should not reach the log")]
    public async Task Emit_WithoutForwardedHeaders_ShouldLogTransportValues()
    {
        // Arrange — no forwarded-headers middleware: every effective value is the transport's.
        using CancellationTokenSource cancellation = new(_testTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(loggerFactory)
            .Use((context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        using HttpClient client = factory.CreateClient();

        // Act
        (await SendProxiedAsync(client, "http://internal.upstream/orders", cancellation.Token)).Dispose();

        // Assert — the in-memory peer has no IP, so no client address is logged at all, and in
        // particular not the asserted one.
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Attributes.ContainsKey(HttpLoggingAttributes.ClientAddress).ShouldBeFalse();
        entry.Attributes[HttpLoggingAttributes.RequestScheme].ShouldBe("http");
        entry.Attributes[HttpLoggingAttributes.RequestHost].ShouldBe("internal.upstream");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Forwarded: The W3C access log should record the forwarded client and host")]
    public async Task W3CProvider_TrustedProxyForwardedIdentity_ShouldWriteForwardedClientAndHost()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-w3c-tests", Guid.NewGuid().ToString("N"));

        try
        {
            RecordingLoggerProvider recorded = new();
            W3CAccessLogProvider accessLog = new(new W3CAccessLogOptions
            {
                Directory = directory,
                FlushInterval = TimeSpan.Zero,
            });

            using ILoggerFactory loggerFactory = new LoggerFactoryBuilder()
                .AddProvider(recorded)
                .AddProvider(accessLog)
                .Build();

            await using WebApplicationTestFactory factory = new();
            factory.Application
                .UseHttpLogging(loggerFactory)
                .UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded)
                .Use((context, next) =>
                {
                    context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                    return Task.CompletedTask;
                });

            using HttpClient client = factory.CreateClient();

            // Act
            (await SendProxiedAsync(client, "http://internal.upstream/w3c-proxied", cancellation.Token)).Dispose();
            await WaitForEntriesAsync(recorded, 1, cancellation.Token);
            accessLog.Flush();

            // Assert — c-ip is the forwarded client and cs-host the forwarded host.
            string[] files = Directory.GetFiles(directory, "access-*.log");
            files.Length.ShouldBe(1);

            string content;
            using (FileStream stream = new(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new(stream))
            {
                content = reader.ReadToEnd();
            }

            content.ShouldContain(" 203.0.113.9 GET /w3c-proxied - 200 ", Case.Sensitive);
            content.ShouldContain(" HTTP/1.1 public.example ", Case.Sensitive);
            content.ShouldNotContain("internal.upstream");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Forwarded: A trusted proxy address should be logged as the peer beside the forwarded client")]
    public async Task Emit_TrustedProxyAddress_ShouldLogPeerBesideForwardedClient()
    {
        // Arrange — an RFC 7239 element carrying a client port, from a trusted proxy's IP address.
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        (IWebApplicationMiddleware logging, IWebApplicationMiddleware forwarded) = BuildPipeline(loggerFactory);

        LoggingTestContext context = new(new HttpConnectionInfo(remoteEndPoint: _trustedProxy));
        context.Request.Headers[HttpHeaderKey.Forwarded] = "for=\"203.0.113.9:4711\";proto=https;host=public.example";

        // Act
        await logging.InvokeAsync(context, inner => forwarded.InvokeAsync(inner, _ => Task.CompletedTask));

        // Assert
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.ClientAddress].ShouldBe("203.0.113.9");
        entry.Attributes[HttpLoggingAttributes.ClientPort].ShouldBe(4711);
        entry.Attributes[HttpLoggingAttributes.PeerAddress].ShouldBe("10.0.0.2");
        entry.Attributes[HttpLoggingAttributes.PeerPort].ShouldBe(51000);
        entry.Attributes[HttpLoggingAttributes.RequestScheme].ShouldBe("https");
        entry.Attributes[HttpLoggingAttributes.RequestHost].ShouldBe("public.example");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Forwarded: A direct connection should log the transport client with no separate peer")]
    public async Task Emit_DirectConnection_ShouldLogTransportClientWithoutPeer()
    {
        // Arrange — the peer is outside KnownProxies, so the trust walk accepts no hop and the
        // asserted client never reaches the log.
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        (IWebApplicationMiddleware logging, IWebApplicationMiddleware forwarded) = BuildPipeline(loggerFactory);

        LoggingTestContext context = new(new HttpConnectionInfo(remoteEndPoint: new IPEndPoint(IPAddress.Parse("198.51.100.7"), 52000)));
        context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";

        // Act
        await logging.InvokeAsync(context, inner => forwarded.InvokeAsync(inner, _ => Task.CompletedTask));

        // Assert
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.ClientAddress].ShouldBe("198.51.100.7");
        entry.Attributes[HttpLoggingAttributes.ClientPort].ShouldBe(52000);
        entry.Attributes.ContainsKey(HttpLoggingAttributes.PeerAddress).ShouldBeFalse();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.PeerPort).ShouldBeFalse();
    }

    private static (IWebApplicationMiddleware Logging, IWebApplicationMiddleware Forwarded) BuildPipeline(ILoggerFactory loggerFactory)
    {
        TestPipelineBuilder builder = new();

        builder.UseHttpLogging(loggerFactory);
        IWebApplicationMiddleware logging = builder.LastMiddleware.ShouldNotBeNull();

        builder.UseForwardedHeaders(options =>
        {
            options.Headers = ForwardedHeaderNames.All;
            options.KnownProxies.Add(_trustedProxy.Address);
        });
        IWebApplicationMiddleware forwarded = builder.LastMiddleware.ShouldNotBeNull();

        return (logging, forwarded);
    }

    private static Task<HttpResponseMessage> SendProxiedAsync(HttpClient client, string uri, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9").ShouldBeTrue();
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https").ShouldBeTrue();
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "public.example").ShouldBeTrue();

        return client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Waits until the recording provider has seen <paramref name="count"/> entries: the completion
    /// entry is emitted just before the pipeline returns to the server, which can race the client
    /// observing the response.
    /// </summary>
    private static async Task<IReadOnlyList<ILoggerEntry>> WaitForEntriesAsync(
        RecordingLoggerProvider recorded,
        int count,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<ILoggerEntry> entries = recorded.Entries;
            if (entries.Count >= count)
            {
                return entries;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }
}
