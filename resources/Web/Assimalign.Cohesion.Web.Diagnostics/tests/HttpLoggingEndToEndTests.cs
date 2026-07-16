using System.Net;
using System.Text;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpHeaderKey = Assimalign.Cohesion.Http.HttpHeaderKey;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// End-to-end coverage over the Web.Testing factory: requests flow the real pipeline
/// (in-memory transport, HTTP/1.1) and the middleware's entries are captured through a real
/// <see cref="LoggerFactoryBuilder"/>-built factory, asserting field capture, redaction,
/// per-endpoint overrides, fault escalation, and correlation.
/// </summary>
public class HttpLoggingEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits until the recording provider has seen <paramref name="count"/> entries. The
    /// completion entry is emitted just before the pipeline returns to the server, which can
    /// race the client observing the final response bytes.
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

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: A GET emits one exchange entry with the default fields")]
    public async Task Get_DefaultFields_ShouldEmitExchangeEntry()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category))
            .Use(async (context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("hello"), context.RequestCancelled);
            });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/probe", cancellation.Token);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];

        entry.Category.ShouldBe("Assimalign.Cohesion.Web.Diagnostics.HttpLogging");
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldStartWith("GET /probe -> 200 in ");
        entry.Message.ShouldEndWith(" ms");

        entry.Attributes[HttpLoggingAttributes.Event].ShouldBe(HttpLoggingAttributes.EventExchange);
        entry.Attributes[HttpLoggingAttributes.RequestMethod].ShouldBe("GET");
        entry.Attributes[HttpLoggingAttributes.RequestPath].ShouldBe("/probe");
        entry.Attributes[HttpLoggingAttributes.RequestHost].ShouldBe("localhost");
        entry.Attributes[HttpLoggingAttributes.RequestProtocol].ShouldBe("HTTP/1.1");
        entry.Attributes[HttpLoggingAttributes.ResponseStatusCode].ShouldBe(200);
        entry.Attributes[HttpLoggingAttributes.ResponseBodyBytes].ShouldBe(5L);
        entry.Attributes[HttpLoggingAttributes.Duration].ShouldBeOfType<double>().ShouldBeGreaterThanOrEqualTo(0d);

        // Default field set captures no bodies and no query.
        entry.Attributes.ContainsKey(HttpLoggingAttributes.RequestBody).ShouldBeFalse();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.ResponseBody).ShouldBeFalse();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.RequestQuery).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: Credential-bearing header values are redacted, allowlisted ones are not")]
    public async Task Headers_OutsideAllowlist_ShouldBeRedacted()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category))
            .Use((context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                context.Response.Headers[HttpHeaderKey.SetCookie] = "sid=response-cookie-value";
                context.Response.Headers[HttpHeaderKey.Server] = "Cohesion";
                return Task.CompletedTask;
            });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/secure");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-token-123");
        request.Headers.TryAddWithoutValidation("Cookie", "session=cookie-secret-456");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "custom-secret-789");
        request.Headers.TryAddWithoutValidation("User-Agent", "CohesionTests/1.0");

        // Act
        (await client.SendAsync(request, cancellation.Token)).Dispose();

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];

        // Names always log; values outside the allowlist are the redaction placeholder.
        entry.Attributes["http.request.header.authorization"].ShouldBe("[Redacted]");
        entry.Attributes["http.request.header.cookie"].ShouldBe("[Redacted]");
        entry.Attributes["http.request.header.x-api-key"].ShouldBe("[Redacted]");
        entry.Attributes["http.request.header.user-agent"].ShouldBe("CohesionTests/1.0");
        entry.Attributes["http.response.header.set-cookie"].ShouldBe("[Redacted]");
        entry.Attributes["http.response.header.server"].ShouldBe("Cohesion");

        // Belt and braces: no secret value survives anywhere in the entry.
        foreach (object? value in entry.Attributes.Values)
        {
            if (value is string text)
            {
                text.ShouldNotContain("secret-token-123");
                text.ShouldNotContain("cookie-secret-456");
                text.ShouldNotContain("custom-secret-789");
                text.ShouldNotContain("response-cookie-value");
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: The query string logs only when opted in")]
    public async Task Query_OptIn_ShouldLogSerializedQuery()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(
                loggerFactory.Create(new HttpLoggingOptions().Category),
                options => options.Fields = HttpLoggingFields.Default | HttpLoggingFields.RequestQuery)
            .Use((context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        using HttpClient client = factory.CreateClient();

        // Act
        (await client.GetAsync("/search?q=alpha&flag", cancellation.Token)).Dispose();

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Attributes[HttpLoggingAttributes.RequestQuery].ShouldBeOfType<string>().ShouldContain("q=alpha", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: Opt-in body capture is bounded and content-type gated")]
    public async Task Bodies_OptIn_ShouldCaptureBoundedPrefixes()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(
                loggerFactory.Create(new HttpLoggingOptions().Category),
                options =>
                {
                    options.Fields = HttpLoggingFields.All;
                    options.RequestBodyLimit = 8;
                    options.ResponseBodyLimit = 8;
                })
            .Use(async (context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                context.Response.Headers[HttpHeaderKey.ContentType] = context.Request.Headers[HttpHeaderKey.ContentType].Value;
                await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestCancelled);
            });

        using HttpClient client = factory.CreateClient();

        // Act — 16 bytes of JSON-typed payload, echoed back.
        using StringContent json = new("0123456789ABCDEF", Encoding.UTF8, "application/json");
        (await client.PostAsync("/echo", json, cancellation.Token)).Dispose();

        // Assert — captures stop at the 8-byte cap; byte counts see the full body.
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Attributes[HttpLoggingAttributes.RequestBody].ShouldBe("01234567");
        entry.Attributes[HttpLoggingAttributes.ResponseBody].ShouldBe("01234567");
        entry.Attributes[HttpLoggingAttributes.RequestBodyBytes].ShouldBe(16L);
        entry.Attributes[HttpLoggingAttributes.ResponseBodyBytes].ShouldBe(16L);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: Binary content types are counted but never captured")]
    public async Task Bodies_BinaryContentType_ShouldCountWithoutCapturing()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(
                loggerFactory.Create(new HttpLoggingOptions().Category),
                options => options.Fields = HttpLoggingFields.All)
            .Use(async (context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                context.Response.Headers[HttpHeaderKey.ContentType] = "application/octet-stream";
                await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestCancelled);
            });

        using HttpClient client = factory.CreateClient();

        using ByteArrayContent binary = new(Encoding.UTF8.GetBytes("0123456789ABCDEF"));
        binary.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        // Act
        (await client.PostAsync("/blob", binary, cancellation.Token)).Dispose();

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Attributes.ContainsKey(HttpLoggingAttributes.RequestBody).ShouldBeFalse();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.ResponseBody).ShouldBeFalse();
        entry.Attributes[HttpLoggingAttributes.RequestBodyBytes].ShouldBe(16L);
        entry.Attributes[HttpLoggingAttributes.ResponseBodyBytes].ShouldBe(16L);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: HttpLoggingFields.None endpoint metadata silences the endpoint")]
    public async Task EndpointMetadata_None_ShouldSuppressEntry()
    {
        // Arrange — a health-style route silenced via the endpoint metadata bag, and a normal
        // route that still logs.
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category));

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/healthz",
            new RouterRouteHandler(context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            }),
            new RouterRouteMetadataCollection(new HttpLoggingMetadata(HttpLoggingFields.None))));
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/orders",
            new RouterRouteHandler(context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            })));

        using HttpClient client = factory.CreateClient();

        // Act — the probe first; sequential requests on one connection serialize, so if the
        // probe were going to log, its entry would precede the /orders one.
        (await client.GetAsync("/healthz", cancellation.Token)).Dispose();
        (await client.GetAsync("/orders", cancellation.Token)).Dispose();

        // Assert
        IReadOnlyList<ILoggerEntry> entries = await WaitForEntriesAsync(recorded, 1, cancellation.Token);
        entries.Count.ShouldBe(1);
        entries[0].Attributes[HttpLoggingAttributes.RequestPath].ShouldBe("/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: Endpoint metadata narrows the emitted field set")]
    public async Task EndpointMetadata_NarrowedFields_ShouldLimitAttributes()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category));

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/mini",
            new RouterRouteHandler(context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            }),
            new RouterRouteMetadataCollection(
                new HttpLoggingMetadata(HttpLoggingFields.RequestMethod | HttpLoggingFields.ResponseStatusCode))));

        using HttpClient client = factory.CreateClient();

        // Act
        (await client.GetAsync("/mini", cancellation.Token)).Dispose();

        // Assert — only the two overridden fields survive; the message mirrors the reduction.
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Message.ShouldBe("GET - -> 200");
        entry.Attributes[HttpLoggingAttributes.RequestMethod].ShouldBe("GET");
        entry.Attributes[HttpLoggingAttributes.ResponseStatusCode].ShouldBe(200);
        entry.Attributes.ContainsKey(HttpLoggingAttributes.RequestPath).ShouldBeFalse();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.Duration).ShouldBeFalse();
        entry.Attributes.Keys.ShouldNotContain(key => key.StartsWith(HttpLoggingAttributes.RequestHeaderPrefix, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: A faulting downstream escalates the entry to Error and rethrows")]
    public async Task Fault_Downstream_ShouldEscalateToErrorAndRethrow()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category))
            // Async lambda so overload resolution binds the request-time middleware shape
            // (Func<IHttpContext, WebApplicationMiddleware, Task>) - a bare throw expression
            // would also satisfy the pipeline-build-time Use overload.
            .Use(async (context, next) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });

        using HttpClient client = factory.CreateClient();

        // Act — the middleware rethrows, so the server's exception-isolation boundary tears the
        // connection down and the client observes a transport failure.
        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("/kaboom", cancellation.Token));

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("boom");
        entry.Message.ShouldEndWith("(faulted)");
        entry.Attributes[HttpLoggingAttributes.RequestPath].ShouldBe("/kaboom");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: An inbound traceparent yields trace and span id attributes")]
    public async Task TraceContext_InboundTraceparent_ShouldAttachIds()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category))
            .Use((context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/traced");
        request.Headers.TryAddWithoutValidation("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        // Act
        (await client.SendAsync(request, cancellation.Token)).Dispose();

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Attributes[HttpLoggingAttributes.TraceId].ShouldBe("0af7651916cd43dd8448eb211c80319c");
        entry.Attributes[HttpLoggingAttributes.SpanId].ShouldBe("b7ad6b7169203331");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: LogRequestStart correlates start and completion via ParentId")]
    public async Task RequestStart_Enabled_ShouldCorrelateEntries()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(
                loggerFactory.Create(new HttpLoggingOptions().Category),
                options => options.LogRequestStart = true)
            .Use((context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        using HttpClient client = factory.CreateClient();

        // Act
        (await client.GetAsync("/scoped", cancellation.Token)).Dispose();

        // Assert — the start entry seeds the scope; the completion entry inherits its id.
        IReadOnlyList<ILoggerEntry> entries = await WaitForEntriesAsync(recorded, 2, cancellation.Token);
        entries.Count.ShouldBe(2);

        ILoggerEntry start = entries[0];
        ILoggerEntry exchange = entries[1];

        start.Attributes[HttpLoggingAttributes.Event].ShouldBe(HttpLoggingAttributes.EventStart);
        start.Message.ShouldBe("GET /scoped started");
        exchange.Attributes[HttpLoggingAttributes.Event].ShouldBe(HttpLoggingAttributes.EventExchange);
        exchange.ParentId.ShouldBe(start.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: The client-address resolver seam overrides the socket peer")]
    public async Task ClientAddress_ResolverSeam_ShouldOverrideSocketPeer()
    {
        // Arrange — until the #778 forwarded middleware merges, the resolver is the seam a
        // proxy-aware composition plugs in; the default remains the socket peer.
        using CancellationTokenSource cancellation = new(TestTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();

        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(
                loggerFactory.Create(new HttpLoggingOptions().Category),
                options => options.ClientAddressResolver = _ => IPAddress.Parse("203.0.113.9"))
            .Use((context, next) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        using HttpClient client = factory.CreateClient();

        // Act
        (await client.GetAsync("/fronted", cancellation.Token)).Dispose();

        // Assert
        ILoggerEntry entry = (await WaitForEntriesAsync(recorded, 1, cancellation.Token))[0];
        entry.Attributes[HttpLoggingAttributes.ClientAddress].ShouldBe("203.0.113.9");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - E2E: The W3C provider writes an access-log line with no redacted secrets")]
    public async Task W3CProvider_EndToEnd_ShouldWriteRedactedAccessLog()
    {
        // Arrange — the full composition: middleware emits through a factory that fans out to
        // both the recording provider and the W3C file provider.
        using CancellationTokenSource cancellation = new(TestTimeout);
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
                .Use((context, next) =>
                {
                    context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                    return Task.CompletedTask;
                });

            using HttpClient client = factory.CreateClient();
            using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/w3c-e2e");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer hunter2-secret");
            request.Headers.TryAddWithoutValidation("User-Agent", "CohesionW3C/1.0");

            // Act
            (await client.SendAsync(request, cancellation.Token)).Dispose();
            await WaitForEntriesAsync(recorded, 1, cancellation.Token);
            accessLog.Flush();

            // Assert
            string[] files = Directory.GetFiles(directory, "access-*.log");
            files.Length.ShouldBe(1);

            // The provider still holds the file open for writing (FileShare.Read), so the
            // reader must share write access.
            string content;
            using (FileStream stream = new(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new(stream))
            {
                content = reader.ReadToEnd();
            }
            content.ShouldStartWith("#Version: 1.0\n");
            content.ShouldContain("GET /w3c-e2e - 200", Case.Sensitive);
            content.ShouldContain("CohesionW3C/1.0", Case.Sensitive);
            content.ShouldNotContain("hunter2-secret");
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
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
