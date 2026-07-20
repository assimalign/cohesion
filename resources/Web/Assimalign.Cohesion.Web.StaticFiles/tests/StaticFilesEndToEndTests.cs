using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// Full-pipeline coverage driven end to end over the in-memory transport through
/// <see cref="WebApplicationTestFactory"/>: real client, real HTTP/1.1 wire exchange, real
/// server dispatch — including the encoded traversal forms only a live transport decodes.
/// </summary>
public class StaticFilesEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static WebApplicationTestFactory CreateFactory(InMemoryFileSystem site, Action<StaticFilesOptions>? configure = null)
    {
        var factory = new WebApplicationTestFactory();
        factory.Application.UseStaticFiles(site, configure);
        return factory;
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: GET should serve the file with entity headers over the wire")]
    public async Task E2E_Get_ShouldServeFileWithEntityHeaders()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("index.html", "<html>home</html>"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/index.html", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("<html>home</html>");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        response.Content.Headers.ContentLength.ShouldBe(17);
        response.Headers.ETag.ShouldNotBeNull();
        response.Content.Headers.LastModified.ShouldNotBeNull();
        response.Headers.AcceptRanges.ShouldContain("bytes");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: revalidating with the returned ETag should yield 304")]
    public async Task E2E_RevalidateWithETag_ShouldYield304()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("app.css", "body{}"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage first = await client.GetAsync("/app.css", cancellation.Token);
        EntityTagHeaderValue etag = first.Headers.ETag!;

        // Act — the natural cache revalidation flow.
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/app.css");
        request.Headers.IfNoneMatch.Add(etag);
        using HttpResponseMessage second = await client.SendAsync(request, cancellation.Token);

        // Assert
        second.StatusCode.ShouldBe(NetHttpStatusCode.NotModified);
        (await second.Content.ReadAsStringAsync(cancellation.Token)).ShouldBeEmpty();
        second.Headers.ETag.ShouldBe(etag);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: a single byte range should yield 206 with Content-Range")]
    public async Task E2E_SingleByteRange_ShouldYield206()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        // Act
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/data.txt");
        request.Headers.Range = new RangeHeaderValue(2, 5);
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.PartialContent);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("2345");
        response.Content.Headers.ContentRange!.ToString().ShouldBe("bytes 2-5/10");
        response.Content.Headers.ContentLength.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: an unsatisfiable range should yield 416 with bytes */N")]
    public async Task E2E_UnsatisfiableRange_ShouldYield416()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        // Act
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/data.txt");
        request.Headers.Range = new RangeHeaderValue(100, 200);
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.RequestedRangeNotSatisfiable);
        response.Content.Headers.ContentRange!.ToString().ShouldBe("bytes */10");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: HEAD should return the GET header section with an empty body")]
    public async Task E2E_Head_ShouldReturnHeadersWithoutBody()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("page.html", "content"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        // Act
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Head, "/page.html");
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
        response.Content.Headers.ContentLength.ShouldBe(7);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        response.Headers.ETag.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: encoded traversal must never escape the mounted root")]
    [InlineData("/static/%2e%2e/secret.txt")]
    [InlineData("/static/%2E%2E/secret.txt")]
    [InlineData("/static/%2e%2e/%2e%2e/secret.txt")]
    [InlineData("/static/..%5csecret.txt")]
    [InlineData("/static/nested/%2e%2e/%2e%2e/secret.txt")]
    public async Task E2E_EncodedTraversal_ShouldYield404(string attackPath)
    {
        // Arrange — every transport (HTTP/1.1 included, now that Http1MessageReader percent-decodes
        // the request-target path to h2/h3 parity) decodes these into literal dot segments before the
        // middleware sees them, so the traversal gate catches them uniformly; the URI client-side keeps
        // them encoded, so the hostile form actually reaches the server (a literal "/../" would be
        // normalized away by HttpClient).
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(
            ("public.txt", "public"),
            ("secret.txt", "top-secret"));
        await using WebApplicationTestFactory factory = CreateFactory(
            site, options => options.RequestPath = new HttpPath("/static"));
        using HttpClient client = factory.CreateClient();

        // Act — DangerousDisablePathAndQueryCanonicalization stops the client-side Uri from
        // collapsing the encoded dot segments, so the hostile bytes actually hit the wire.
        var rawUri = new Uri(
            "http://localhost" + attackPath,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using HttpResponseMessage response = await client.GetAsync(rawUri, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldNotContain("top-secret");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: a legitimately percent-encoded name should decode over the wire and resolve")]
    public async Task E2E_EncodedName_ShouldResolveDecodedFile()
    {
        // Arrange — the correctness counterpart to the traversal defense: a reserved character that is
        // legitimately percent-encoded on the wire ("%24" → "$") must decode in the h1 transport and
        // resolve the file whose name actually contains it. Before Http1MessageReader gained decode
        // parity, "%24" reached the middleware raw and this file was unreachable on HTTP/1.1.
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("prices$.json", "{\"ok\":true}"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        // Act — keep the client from canonicalizing "%24" away so the encoded octet reaches the wire.
        var rawUri = new Uri(
            "http://localhost/prices%24.json",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using HttpResponseMessage response = await client.GetAsync(rawUri, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("{\"ok\":true}");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: Accept-Encoding negotiation should serve the precompressed sibling")]
    public async Task E2E_AcceptEncodingBr_ShouldServePrecompressedSibling()
    {
        // Arrange — SocketsHttpHandler does not auto-decompress unless configured, so the raw
        // sibling bytes and the Content-Encoding header are observable.
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(
            ("app.js", "identity-js"),
            ("app.js.br", "brotli-bytes"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        // Act
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/app.js");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("brotli-bytes");
        response.Content.Headers.ContentEncoding.ShouldContain("br");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        response.Headers.Vary.ShouldContain("Accept-Encoding");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: a directory URL should redirect to its slash form and serve the default document")]
    public async Task E2E_DirectoryWithoutSlash_ShouldRedirectAndServeDefaultDocument()
    {
        // Arrange — HttpClient follows the 301 automatically, so the observable outcome is the
        // default document served from the slash form.
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("docs/index.html", "docs home"));
        await using WebApplicationTestFactory factory = CreateFactory(site);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/docs", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("docs home");
        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/docs/");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - E2E: unmatched requests should flow to downstream middleware")]
    public async Task E2E_UnmatchedRequest_ShouldFlowDownstream()
    {
        // Arrange — a downstream marker middleware owns everything static files declines.
        using CancellationTokenSource cancellation = new(TestTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("app.css", "body{}"));

        await using var factory = new WebApplicationTestFactory();
        factory.Application.UseStaticFiles(site, options => options.RequestPath = new HttpPath("/static"));
        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            await context.Response.Body.WriteAsync("downstream"u8.ToArray(), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage served = await client.GetAsync("/static/app.css", cancellation.Token);
        using HttpResponseMessage unmatched = await client.GetAsync("/api/data", cancellation.Token);

        // Assert
        served.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        unmatched.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        (await unmatched.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("downstream");
    }
}
