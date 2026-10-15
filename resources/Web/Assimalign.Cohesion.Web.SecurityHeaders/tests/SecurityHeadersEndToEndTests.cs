using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.StaticFiles;
using Assimalign.Cohesion.Web.Testing;

using static Assimalign.Cohesion.Web.SecurityHeaders.Tests.SecurityHeadersTestHost;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// The middleware over the in-memory <see cref="WebApplicationTestFactory"/> (HTTP/1.1, real wire
/// exchange): the defaults, the opt-in fields, responses that never reach an endpoint (unmatched routes,
/// short-circuiting middleware, the exception boundary's error page, static files), the no-clobber and
/// framing-ownership rules, the per-request nonce, and streamed responses that commit their own head.
/// </summary>
public class SecurityHeadersEndToEndTests
{
    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Defaults: Should emit nosniff, clickjacking protection and the referrer policy")]
    public async Task UseSecurityHeaders_Defaults_ShouldEmitSafeDefaults()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) => WriteTextAsync(context, "home"));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Referrer-Policy").ShouldBe("strict-origin-when-cross-origin");
        Header(response, "Content-Security-Policy-Report-Only").ShouldBeNull();
        Header(response, "Cross-Origin-Opener-Policy").ShouldBeNull();
        Header(response, "Cross-Origin-Embedder-Policy").ShouldBeNull();
        Header(response, "Cross-Origin-Resource-Policy").ShouldBeNull();
        Header(response, "Permissions-Policy").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Defaults: Should be switchable off one by one")]
    public async Task UseSecurityHeaders_DefaultsTurnedOff_ShouldEmitNoField()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy =>
        {
            policy.ContentTypeOptions = false;
            policy.Framing = null;
            policy.ReferrerPolicy = null;
        });
        factory.Application.Use((context, next) => WriteTextAsync(context, "home"));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        Header(response, "X-Content-Type-Options").ShouldBeNull();
        Header(response, "Content-Security-Policy").ShouldBeNull();
        Header(response, "X-Frame-Options").ShouldBeNull();
        Header(response, "Referrer-Policy").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Opt-ins: Should emit every configured field with its exact value")]
    public async Task UseSecurityHeaders_OptIns_ShouldEmitConfiguredFields()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy =>
        {
            policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
                .DefaultSrc(sources => sources.Self())
                .ImgSrc(sources => sources.Self().Scheme("data"))
                .ObjectSrc(sources => sources.None()));
            policy.ContentSecurityPolicyReportOnly = ContentSecurityPolicy.Create(csp => csp
                .ScriptSrc(sources => sources.Self())
                .ReportTo("csp-endpoint"));
            policy.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin;
            policy.CrossOriginEmbedderPolicy = CrossOriginEmbedderPolicy.RequireCorp;
            policy.CrossOriginResourcePolicy = CrossOriginResourcePolicy.SameSite;
            policy.PermissionsPolicy = PermissionsPolicy.Create(permissions => permissions
                .Disable("camera")
                .AllowSelf("geolocation"));
            policy.ReferrerPolicy = ReferrerPolicy.NoReferrer;
        });
        factory.Application.Use((context, next) => WriteTextAsync(context, "home"));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'");
        Header(response, "Content-Security-Policy-Report-Only").ShouldBe("script-src 'self'; report-to csp-endpoint");
        Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin");
        Header(response, "Cross-Origin-Embedder-Policy").ShouldBe("require-corp");
        Header(response, "Cross-Origin-Resource-Policy").ShouldBe("same-site");
        Header(response, "Permissions-Policy").ShouldBe("camera=(), geolocation=(self)");
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Coverage: An unmatched route's 404 should carry the headers")]
    public async Task UseSecurityHeaders_UnmatchedRoute_ShouldCoverNotFound()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/known", Ok());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/missing", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Coverage: A response a later middleware writes without routing should carry the headers")]
    public async Task UseSecurityHeaders_ShortCircuitingMiddleware_ShouldCoverItsResponse()
    {
        // Arrange — a middleware that answers on its own, the way host filtering or a static file would.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Forbidden;
            return Task.CompletedTask;
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Referrer-Policy").ShouldBe("strict-origin-when-cross-origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Coverage: The exception boundary's error page should carry the headers")]
    public async Task UseSecurityHeaders_AheadOfErrorHandling_ShouldCoverErrorResponse()
    {
        // Arrange — the boundary clears the faulted response's headers before writing its problem; the
        // headers are staged after that, when the server commits the head.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.UseErrorHandling();
        factory.Application.Use((context, next) => throw new InvalidOperationException("Deliberate handler fault."));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Coverage: An outer exception boundary should preserve the starting callback")]
    public async Task UseSecurityHeaders_AfterErrorHandling_ShouldCoverErrorResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseErrorHandling();
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) => throw new InvalidOperationException("Deliberate handler fault."));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Coverage: A static file should carry the headers and its 304 should carry none")]
    public async Task UseSecurityHeaders_StaticFile_ShouldCoverFileAndSkipNotModified()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = CreateSite(("index.html", "<html>home</html>"));
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.UseStaticFiles(site);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage file = await client.GetAsync("/index.html", cancellation.Token);
        using HttpRequestMessage revalidation = new(NetHttpMethod.Get, "/index.html");
        revalidation.Headers.IfNoneMatch.Add(file.Headers.ETag!);
        using HttpResponseMessage notModified = await client.SendAsync(revalidation, cancellation.Token);

        // Assert — the 304 updates a stored response that already carries the fields.
        file.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        Header(file, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(file, "X-Frame-Options").ShouldBe("DENY");
        notModified.StatusCode.ShouldBe(NetHttpStatusCode.NotModified);
        Header(notModified, "X-Content-Type-Options").ShouldBeNull();
        Header(notModified, "Content-Security-Policy").ShouldBeNull();
        Header(notModified, "X-Frame-Options").ShouldBeNull();
        Header(notModified, "Referrer-Policy").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - No-clobber: A field the application set should be kept")]
    public async Task UseSecurityHeaders_ApplicationSetFields_ShouldNotBeOverwritten()
    {
        // Arrange — the application's own policy has no frame-ancestors, so X-Frame-Options still guards framing.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy => policy.CrossOriginResourcePolicy = CrossOriginResourcePolicy.SameOrigin);
        factory.Application.Use((context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ReferrerPolicy] = "no-referrer";
            context.Response.Headers[HttpHeaderKey.ContentSecurityPolicy] = "default-src 'self'";
            context.Response.Headers[HttpHeaderKey.CrossOriginResourcePolicy] = "cross-origin";
            return WriteTextAsync(context, "home");
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'self'");
        Header(response, "Cross-Origin-Resource-Policy").ShouldBe("cross-origin");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - No-clobber: An application X-Frame-Options should take ownership of framing")]
    public async Task UseSecurityHeaders_ApplicationSetFrameOptions_ShouldOmitFrameAncestors()
    {
        // Arrange — a frame-ancestors 'none' beside the application's SAMEORIGIN would override it.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .DefaultSrc(sources => sources.Self())));
        factory.Application.Use((context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.XFrameOptions] = "SAMEORIGIN";
            return WriteTextAsync(context, "home");
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        Header(response, "X-Frame-Options").ShouldBe("SAMEORIGIN");
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'self'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - No-clobber: An application frame-ancestors directive should take ownership of framing")]
    public async Task UseSecurityHeaders_ApplicationSetFrameAncestors_ShouldOmitFrameOptions()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ContentSecurityPolicy] = "default-src 'self'; FRAME-ANCESTORS https://partner.example.com";
            return WriteTextAsync(context, "home");
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'self'; FRAME-ANCESTORS https://partner.example.com");
        Header(response, "X-Frame-Options").ShouldBeNull();
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Overwrite: OverwriteExistingHeaders should replace application values")]
    public async Task UseSecurityHeaders_OverwriteExistingHeaders_ShouldReplaceApplicationValues()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy => policy.OverwriteExistingHeaders = true);
        factory.Application.Use((context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ReferrerPolicy] = "unsafe-url";
            context.Response.Headers[HttpHeaderKey.XFrameOptions] = "SAMEORIGIN";
            context.Response.Headers[HttpHeaderKey.ContentSecurityPolicy] = "frame-ancestors *";
            return WriteTextAsync(context, "home");
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        Header(response, "Referrer-Policy").ShouldBe("strict-origin-when-cross-origin");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Nonce: The header nonce should be the nonce the handler stamped")]
    public async Task UseSecurityHeaders_NonceSource_ShouldMatchHandlerNonce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Nonce().StrictDynamic())
            .ObjectSrc(sources => sources.None())));
        factory.Application.Use((context, next) => WriteTextAsync(context, ReadNonce(context)));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);
        string nonce = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        Header(response, "Content-Security-Policy")
            .ShouldBe($"script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'; frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Nonce: Each request should get a fresh 128-bit nonce")]
    public async Task UseSecurityHeaders_NonceSource_ShouldBeUniquePerRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Nonce())));
        factory.Application.Use((context, next) => WriteTextAsync(context, ReadNonce(context)));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/", cancellation.Token);
        using HttpResponseMessage second = await client.GetAsync("/", cancellation.Token);
        string firstNonce = await first.Content.ReadAsStringAsync(cancellation.Token);
        string secondNonce = await second.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        firstNonce.ShouldNotBe(secondNonce);
        Convert.FromBase64String(firstNonce).Length.ShouldBe(16);
        Convert.FromBase64String(secondNonce).Length.ShouldBe(16);
        Header(first, "Content-Security-Policy").ShouldBe($"script-src 'nonce-{firstNonce}'; frame-ancestors 'none'");
        Header(second, "Content-Security-Policy").ShouldBe($"script-src 'nonce-{secondNonce}'; frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Nonce: The enforced and report-only policies should share one nonce")]
    public async Task UseSecurityHeaders_NonceInBothPolicies_ShouldShareOneNonce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy =>
        {
            policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources.Self().Nonce()));
            policy.ContentSecurityPolicyReportOnly = ContentSecurityPolicy.Create(csp => csp.StyleSrc(sources => sources.Nonce()));
        });
        factory.Application.Use((context, next) => WriteTextAsync(context, ReadNonce(context)));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);
        string nonce = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        Header(response, "Content-Security-Policy").ShouldBe($"script-src 'self' 'nonce-{nonce}'; frame-ancestors 'none'");
        Header(response, "Content-Security-Policy-Report-Only").ShouldBe($"style-src 'nonce-{nonce}'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Nonce: An application-supplied feature's nonce should be used")]
    public async Task UseSecurityHeaders_ApplicationSuppliedFeature_ShouldUseItsNonce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        FixedNonceFeature feature = new("Zml4ZWQtbm9uY2U=");
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.Use((context, next) =>
        {
            context.Features.Set<ISecurityHeadersFeature>(feature);
            return next.Invoke(context);
        });
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Nonce())));
        factory.Application.Use((context, next) => WriteTextAsync(context, ReadNonce(context)));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("Zml4ZWQtbm9uY2U=");
        Header(response, "Content-Security-Policy").ShouldBe("script-src 'nonce-Zml4ZWQtbm9uY2U='; frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Nonce: A policy without a nonce source should never read the nonce")]
    public async Task UseSecurityHeaders_NoNonceSource_ShouldNotReadNonce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        FixedNonceFeature feature = new("Zml4ZWQtbm9uY2U=");
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.Use((context, next) =>
        {
            context.Features.Set<ISecurityHeadersFeature>(feature);
            return next.Invoke(context);
        });
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .DefaultSrc(sources => sources.Self())));
        factory.Application.Use((context, next) => WriteTextAsync(context, "home"));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        feature.Reads.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Nonce: A supplied nonce outside the CSP grammar should fail the exchange")]
    public async Task UseSecurityHeaders_InvalidSuppliedNonce_ShouldFailTheExchange()
    {
        // Arrange — a value with a quote and a semicolon would rewrite the policy if it were written out.
        using CancellationTokenSource cancellation = new(TestTimeout);
        FixedNonceFeature feature = new("abc'; script-src *");
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.Use((context, next) =>
        {
            context.Features.Set<ISecurityHeadersFeature>(feature);
            return next.Invoke(context);
        });
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Nonce())));
        factory.Application.Use((context, next) => WriteTextAsync(context, "home"));
        using HttpClient client = factory.CreateClient();

        // Act
        Func<Task> act = async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);
        };

        // Assert — a throwing starting callback faults the exchange before any response head is sent.
        await act.ShouldThrowAsync<HttpRequestException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Streaming: A streamed response should carry the headers on its committed head")]
    public async Task UseSecurityHeaders_StreamedResponse_ShouldCarryHeaders()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(streaming: true);
        factory.Application.UseSecurityHeaders(policy => policy.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin);
        factory.Application.Use((context, next) => StreamTextAsync(context, "streamed"));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert — chunked framing proves the handler committed the head; the post-next path skips a
        // committed head, so the fields can only have come from staging ahead of the first write.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Headers.TransferEncodingChunked.ShouldBe(true);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("streamed");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
        Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Streaming: A refused head should retain security headers on a buffered replacement")]
    public async Task UseSecurityHeaders_RefusedStreamedHead_ShouldPreserveHeadersOnReplacement()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(streaming: true);
        factory.Application.UseSecurityHeaders();
        int calls = 0;
        HttpErrorCode? refusalCode = null;
        factory.Application.Use(async (context, next) =>
        {
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                calls++;
                return Task.CompletedTask;
            }, null);
            context.Response.Headers[new HttpHeaderKey("X-Invalid")] = "bad\r\nfield";
            HttpException refused = await Should.ThrowAsync<HttpException>(
                () => context.Response.Streaming.StartAsync(context.RequestCancelled).AsTask());
            refusalCode = refused.Code;
            context.Response.Headers.Clear();
            await WriteTextAsync(context, "replacement");
            context.Response.StatusCode = CohesionHttpStatusCode.BadGateway;
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        refusalCode.ShouldBe(HttpErrorCode.InvalidResponseField);
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("replacement");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
        Header(response, "X-Invalid").ShouldBeNull();
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Coverage: A refused head should retain an accepted application framing policy on the server replacement")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task UseSecurityHeaders_RefusedHeadWithApplicationFraming_ShouldPreserveNoClobberPolicyOnServerReplacement(WebApplicationTestProtocol protocol)
    {
        // Arrange — the application owns framing, so the starting callback accepts SAMEORIGIN
        // without changing it and omits its default frame-ancestors policy. The server's
        // replacement must retain that accepted field as well as the protections the callback adds.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Application.UseSecurityHeaders();
        int calls = 0;
        factory.Application.Use((context, next) =>
        {
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                calls++;
                return Task.CompletedTask;
            }, null);
            context.Response.StatusCode = CohesionHttpStatusCode.Found;
            context.Response.Headers[HttpHeaderKey.XFrameOptions] = "SAMEORIGIN";
            context.Response.Headers[new HttpHeaderKey("X-Invalid")] = "bad\r\nfield";
            return Task.CompletedTask;
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.Version.Major.ShouldBe(protocol == WebApplicationTestProtocol.Http2 ? 2 : 1);
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        (await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
        Header(response, "X-Frame-Options").ShouldBe("SAMEORIGIN");
        Header(response, "Content-Security-Policy").ShouldBeNull();
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Referrer-Policy").ShouldBe("strict-origin-when-cross-origin");
        Header(response, "X-Invalid").ShouldBeNull();
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Coverage: Invalid unchanged application policy fields must not poison the server replacement")]
    [InlineData(WebApplicationTestProtocol.Http1, false, "bad\r\nfield")]
    [InlineData(WebApplicationTestProtocol.Http1, true, "bad\r\nfield")]
    [InlineData(WebApplicationTestProtocol.Http2, false, "bad\r\nfield")]
    [InlineData(WebApplicationTestProtocol.Http2, true, "bad\r\nfield")]
    [InlineData(WebApplicationTestProtocol.Http1, false, "bad\u0001field")]
    [InlineData(WebApplicationTestProtocol.Http1, true, "bad\u0001field")]
    [InlineData(WebApplicationTestProtocol.Http2, false, "bad\u0001field")]
    [InlineData(WebApplicationTestProtocol.Http2, true, "bad\u0001field")]
    public async Task UseSecurityHeaders_InvalidApplicationPolicy_ShouldSendCleanServerReplacement(
        WebApplicationTestProtocol protocol, bool streamed, string invalidValue)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Builder.Server.UseServer(options => options.Interceptors.Add(new HttpResponseStreamingInterceptor()));
        factory.Application.UseSecurityHeaders();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.XFrameOptions] = new HttpHeaderValue(["SAMEORIGIN", invalidValue]);
            context.Response.Headers[HttpHeaderKey.ContentSecurityPolicy] = "default-src 'self'";
            context.Response.Headers[HttpHeaderKey.ReferrerPolicy] = "no-referrer";
            if (streamed)
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            else
            {
                await WriteTextAsync(context, "refused");
            }
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        (await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
        Header(response, "X-Frame-Options").ShouldBeNull();
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'self'");
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Streaming: Buffered and streamed HTTP/2 responses should both carry the headers")]
    public async Task UseSecurityHeaders_Http2_ShouldCoverBufferedAndStreamedResponses()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });
        factory.Builder.Server.UseServer(options => options.Interceptors.Add(new HttpResponseStreamingInterceptor()));
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Nonce())));
        factory.Application.Use((context, next) => context.Request.Path.ToString() == "/stream"
            ? StreamTextAsync(context, ReadNonce(context))
            : WriteTextAsync(context, ReadNonce(context)));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage buffered = await client.GetAsync("/buffer", cancellation.Token);
        using HttpResponseMessage streamed = await client.GetAsync("/stream", cancellation.Token);
        string bufferedNonce = await buffered.Content.ReadAsStringAsync(cancellation.Token);
        string streamedNonce = await streamed.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        buffered.Version.ShouldBe(System.Net.HttpVersion.Version20);
        Header(buffered, "Content-Security-Policy").ShouldBe($"script-src 'nonce-{bufferedNonce}'; frame-ancestors 'none'");
        Header(streamed, "Content-Security-Policy").ShouldBe($"script-src 'nonce-{streamedNonce}'; frame-ancestors 'none'");
        Header(streamed, "X-Content-Type-Options").ShouldBe("nosniff");
        bufferedNonce.ShouldNotBe(streamedNonce);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Streaming: A streamed response should carry the nonce its handler stamped")]
    public async Task UseSecurityHeaders_StreamedResponseWithNonce_ShouldMatchHandlerNonce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(streaming: true);
        factory.Application.UseSecurityHeaders(policy => policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Nonce())));
        factory.Application.Use((context, next) => StreamTextAsync(context, ReadNonce(context)));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);
        string nonce = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        Header(response, "Content-Security-Policy").ShouldBe($"script-src 'nonce-{nonce}'; frame-ancestors 'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Streaming: A streamed response should keep a field its handler set")]
    public async Task UseSecurityHeaders_StreamedResponseWithApplicationField_ShouldKeepIt()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(streaming: true);
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ReferrerPolicy] = "same-origin";
            return StreamTextAsync(context, "streamed");
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        Header(response, "Referrer-Policy").ShouldBe("same-origin");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Composition: An undefined enumeration value should throw when the middleware is registered")]
    public async Task UseSecurityHeaders_UndefinedEnumerationValue_ShouldThrowAtComposition()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();

        // Act
        Action act = () => factory.Application.UseSecurityHeaders(policy => policy.ReferrerPolicy = (ReferrerPolicy)42);

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Composition: A policy reference kept by the callback should not reconfigure the pipeline")]
    public async Task UseSecurityHeaders_PolicyMutatedAfterRegistration_ShouldKeepCapturedPolicy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        SecurityHeadersPolicy? captured = null;
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseSecurityHeaders(policy => captured = policy);
        factory.Application.Use((context, next) => WriteTextAsync(context, "home"));
        captured!.Framing = null;
        captured.ReferrerPolicy = ReferrerPolicy.UnsafeUrl;
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Referrer-Policy").ShouldBe("strict-origin-when-cross-origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Composition: A null builder should throw ArgumentNullException")]
    public void UseSecurityHeaders_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act
        Action act = () => builder.UseSecurityHeaders();

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }
}
