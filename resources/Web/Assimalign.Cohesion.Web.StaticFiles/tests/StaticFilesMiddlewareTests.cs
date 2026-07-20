using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// Middleware-level coverage over a fake <see cref="IHttpContext"/>: raw hostile paths an
/// <c>HttpClient</c> would normalize away, plus precise header semantics for conditional GET,
/// ranges, default documents, content types, and precompressed siblings.
/// </summary>
public class StaticFilesMiddlewareTests
{
    private static async Task<TestHttpContext> RunAsync(
        IFileSystem fileSystem,
        string path,
        HttpMethod method,
        Action<StaticFilesOptions>? configure = null,
        Action<TestHttpContext>? prepare = null,
        bool[]? passedThrough = null)
    {
        var builder = new TestPipelineBuilder();
        builder.UseStaticFiles(fileSystem, configure);
        builder.Use((context, next) =>
        {
            if (passedThrough is not null)
            {
                passedThrough[0] = true;
            }
            return Task.CompletedTask;
        });

        IWebApplicationPipeline pipeline = builder.Build();
        var context = new TestHttpContext(path, method);
        prepare?.Invoke(context);

        await pipeline.ExecuteAsync(context);
        return context;
    }

    private static string Header(TestHttpContext context, HttpHeaderKey key)
        => context.Response.Headers.TryGetValue(key, out HttpHeaderValue value) ? (string)value : string.Empty;

    // ---------------------------------------------------------------- serving basics

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: GET existing file should serve 200 with entity headers")]
    public async Task Invoke_GetExistingFile_ShouldServe200WithEntityHeaders()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("site.html", "<html>hello</html>"));

        // Act
        TestHttpContext context = await RunAsync(site, "/site.html", HttpMethod.Get);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("<html>hello</html>");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/html");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("18");
        Header(context, HttpHeaderKey.AcceptRanges).ShouldBe("bytes");
        Header(context, HttpHeaderKey.ETag).ShouldStartWith("\"");
        HttpDate.TryParse(Header(context, HttpHeaderKey.LastModified), out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: non-GET/HEAD methods should pass through")]
    public async Task Invoke_PostMethod_ShouldPassThrough()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("site.html", "content"));
        bool[] passedThrough = [false];

        // Act
        await RunAsync(site, "/site.html", HttpMethod.Post, passedThrough: passedThrough);

        // Assert
        passedThrough[0].ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a missing file should pass through")]
    public async Task Invoke_MissingFile_ShouldPassThrough()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("site.html", "content"));
        bool[] passedThrough = [false];

        // Act
        await RunAsync(site, "/missing.html", HttpMethod.Get, passedThrough: passedThrough);

        // Assert
        passedThrough[0].ShouldBeTrue();
    }

    // ---------------------------------------------------------------- request-path prefix

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: prefixed mount should serve under the prefix")]
    public async Task Invoke_PrefixedMount_ShouldServeUnderPrefix()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("app.css", "body{}"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/static/app.css", HttpMethod.Get,
            options => options.RequestPath = new HttpPath("/static"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("body{}");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/css");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a path outside the prefix should pass through")]
    public async Task Invoke_PathOutsidePrefix_ShouldPassThrough()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("app.css", "body{}"));
        bool[] passedThrough = [false];

        // Act
        await RunAsync(
            site, "/other/app.css", HttpMethod.Get,
            options => options.RequestPath = new HttpPath("/static"),
            passedThrough: passedThrough);

        // Assert
        passedThrough[0].ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: prefix matching should be segment-aligned")]
    public async Task Invoke_PrefixSharingTextButNotSegment_ShouldPassThrough()
    {
        // Arrange — "/staticfiles" shares the "/static" text but is a different segment.
        using InMemoryFileSystem site = StaticSite.Create(("app.css", "body{}"));
        bool[] passedThrough = [false];

        // Act
        await RunAsync(
            site, "/staticfiles/app.css", HttpMethod.Get,
            options => options.RequestPath = new HttpPath("/static"),
            passedThrough: passedThrough);

        // Assert
        passedThrough[0].ShouldBeTrue();
    }

    // ---------------------------------------------------------------- path traversal defense

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: dot-segment traversal should be rejected with 404")]
    [InlineData("/static/../secret.txt")]
    [InlineData("/static/../../secret.txt")]
    [InlineData("/static/nested/../../secret.txt")]
    [InlineData("/static/./secret.txt")]
    [InlineData("/static/..")]
    public async Task Invoke_DotSegmentTraversal_ShouldRespond404(string path)
    {
        // Arrange — dot-segment paths must be rejected outright, not resolved: even
        // "/static/./secret.txt" (which canonicalizes to a file that exists in the mount) gets
        // a 404, and nothing under the prefix ever reaches a downstream handler.
        using InMemoryFileSystem site = StaticSite.Create(
            ("public.txt", "public"),
            ("secret.txt", "top-secret"));
        bool[] passedThrough = [false];

        // Act
        TestHttpContext context = await RunAsync(
            site, path, HttpMethod.Get,
            options => options.RequestPath = new HttpPath("/static"),
            passedThrough: passedThrough);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.ReadResponseBody().ShouldNotContain("top-secret");
        passedThrough[0].ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: the middleware serves the path verbatim without re-decoding percent sequences")]
    public async Task Invoke_PercentEncodedName_IsNotReDecodedByMiddleware()
    {
        // Arrange — every transport now percent-decodes the request-target path exactly once before
        // middleware runs (Http1MessageReader / Http2Stream / Http3HeaderCodec via
        // HttpPath.FromUriComponent). A literal "%2E" that still reaches the middleware came from a
        // double-encoded wire target ("%252E") the transport decoded once, so it is a plain name
        // character — decoding it again would resolve the wrong file. The middleware must treat
        // "/report%2Etxt" verbatim and never turn it into "/report.txt". (The encoded-traversal and
        // encoded-name decode behaviors themselves are pinned at the transport — see the
        // Http.Connections parity suite and the E2E traversal/name tests.)
        using InMemoryFileSystem site = StaticSite.Create(("report.txt", "sensitive-report"));

        // Act
        TestHttpContext context = await RunAsync(site, "/report%2Etxt", HttpMethod.Get);

        // Assert — report.txt is never served; a served body would mean the middleware re-decoded
        // "%2E" into "." and resolved "/report.txt".
        context.ReadResponseBody().ShouldNotContain("sensitive-report", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: backslash traversal should be rejected with 404")]
    [InlineData("/static/..\\secret.txt")]
    [InlineData("/static/nested\\..\\..\\secret.txt")]
    public async Task Invoke_BackslashTraversal_ShouldRespond404(string path)
    {
        // Arrange — transports decode %5C to a literal backslash, which FileSystemPath treats
        // as a separator; the gate must see through it.
        using InMemoryFileSystem site = StaticSite.Create(("public.txt", "public"));

        // Act
        TestHttpContext context = await RunAsync(
            site, path, HttpMethod.Get,
            options => options.RequestPath = new HttpPath("/static"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: colon and NUL paths should be rejected with 404")]
    [InlineData("/static/c:/windows/win.ini")]
    [InlineData("/static/file.txt::$DATA")]
    [InlineData("/static/file\0.txt")]
    public async Task Invoke_ColonOrNulPath_ShouldRespond404(string path)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("file.txt", "content"));

        // Act
        TestHttpContext context = await RunAsync(
            site, path, HttpMethod.Get,
            options => options.RequestPath = new HttpPath("/static"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- default documents

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: the mount root should serve the first default document")]
    public async Task Invoke_RootRequest_ShouldServeDefaultDocument()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("index.html", "<html>home</html>"));

        // Act
        TestHttpContext context = await RunAsync(site, "/", HttpMethod.Get);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("<html>home</html>");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/html");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: default documents should probe in configured order")]
    public async Task Invoke_DefaultDocuments_ShouldProbeInOrder()
    {
        // Arrange — only the second default name exists.
        using InMemoryFileSystem site = StaticSite.Create(("docs/index.htm", "second choice"));

        // Act
        TestHttpContext context = await RunAsync(site, "/docs/", HttpMethod.Get);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("second choice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a directory without a trailing slash should 301 to the slash form")]
    public async Task Invoke_DirectoryWithoutTrailingSlash_ShouldRedirectAppendingSlash()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("docs/index.html", "docs home"));

        // Act
        TestHttpContext context = await RunAsync(site, "/docs", HttpMethod.Get);

        // Assert — canonicalize before serving so relative links inside the document resolve.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);
        Header(context, HttpHeaderKey.Location).ShouldBe("/docs/");
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: the exact mount prefix should 301 to its slash form")]
    public async Task Invoke_ExactPrefixWithoutSlash_ShouldRedirectAppendingSlash()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("index.html", "home"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/static", HttpMethod.Get,
            options => options.RequestPath = new HttpPath("/static"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);
        Header(context, HttpHeaderKey.Location).ShouldBe("/static/");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a directory with no default document should pass through")]
    public async Task Invoke_DirectoryWithoutDefaultDocument_ShouldPassThrough()
    {
        // Arrange — directory browsing is a deferred follow-up, so a bare directory is not servable.
        using InMemoryFileSystem site = StaticSite.Create(("docs/page.html", "page"));
        bool[] passedThrough = [false];

        // Act
        await RunAsync(site, "/docs/", HttpMethod.Get, passedThrough: passedThrough);

        // Assert
        passedThrough[0].ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: clearing DefaultDocuments should disable default documents")]
    public async Task Invoke_DefaultDocumentsCleared_ShouldPassThrough()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("index.html", "home"));
        bool[] passedThrough = [false];

        // Act
        await RunAsync(
            site, "/", HttpMethod.Get,
            options => options.DefaultDocuments.Clear(),
            passedThrough: passedThrough);

        // Assert
        passedThrough[0].ShouldBeTrue();
    }

    // ---------------------------------------------------------------- content types

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: an unmapped extension should pass through by default")]
    public async Task Invoke_UnknownExtension_ShouldPassThroughByDefault()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.xyzzy", "mystery"));
        bool[] passedThrough = [false];

        // Act
        await RunAsync(site, "/data.xyzzy", HttpMethod.Get, passedThrough: passedThrough);

        // Assert
        passedThrough[0].ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: ServeUnknownContentTypes should serve the fallback type")]
    public async Task Invoke_UnknownExtensionWithFallback_ShouldServeFallbackType()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.xyzzy", "mystery"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/data.xyzzy", HttpMethod.Get,
            options => options.ServeUnknownContentTypes = true);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        Header(context, HttpHeaderKey.ContentType).ShouldBe("application/octet-stream");
        context.ReadResponseBody().ShouldBe("mystery");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: content-type overlays should override the default map")]
    public async Task Invoke_ContentTypeOverlay_ShouldOverrideDefaultMap()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(
            ("model.gltf", "{}"),
            ("page.html", "<html/>"));

        // Act
        TestHttpContext gltf = await RunAsync(
            site, "/model.gltf", HttpMethod.Get,
            options =>
            {
                options.ContentTypeMappings["gltf"] = "model/gltf+json";
                options.ContentTypeMappings[".html"] = "application/xhtml+xml";
            });
        TestHttpContext html = await RunAsync(
            site, "/page.html", HttpMethod.Get,
            options => options.ContentTypeMappings[".html"] = "application/xhtml+xml");

        // Assert — a new mapping lights up and an existing default is replaceable.
        Header(gltf, HttpHeaderKey.ContentType).ShouldBe("model/gltf+json");
        Header(html, HttpHeaderKey.ContentType).ShouldBe("application/xhtml+xml");
    }

    // ---------------------------------------------------------------- cache control

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a configured Cache-Control should be emitted on 200 and 304")]
    public async Task Invoke_CacheControlConfigured_ShouldEmitOn200And304()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("app.js", "let x;"));
        const string cacheControl = "public, max-age=3600, immutable";

        // Act
        TestHttpContext first = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            options => options.CacheControl = cacheControl);
        string etag = Header(first, HttpHeaderKey.ETag);

        TestHttpContext second = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            options => options.CacheControl = cacheControl,
            prepare: context => context.Request.Headers[HttpHeaderKey.IfNoneMatch] = etag);

        // Assert
        Header(first, HttpHeaderKey.CacheControl).ShouldBe(cacheControl);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        Header(second, HttpHeaderKey.CacheControl).ShouldBe(cacheControl);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: an invalid Cache-Control should throw at composition")]
    public void UseStaticFiles_InvalidCacheControl_ShouldThrowAtComposition()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("app.js", "let x;"));
        var builder = new TestPipelineBuilder();

        // Act / Assert — configuration typos surface at composition time, not per request.
        Should.Throw<ArgumentException>(() =>
            builder.UseStaticFiles(site, options => options.CacheControl = "max-age=,,,"));
    }

    // ---------------------------------------------------------------- conditional GET

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a matching If-None-Match should respond 304 without a body")]
    public async Task Invoke_IfNoneMatchMatching_ShouldRespond304WithoutBody()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("page.html", "content"));
        TestHttpContext first = await RunAsync(site, "/page.html", HttpMethod.Get);
        string etag = Header(first, HttpHeaderKey.ETag);

        // Act
        TestHttpContext second = await RunAsync(
            site, "/page.html", HttpMethod.Get,
            prepare: context => context.Request.Headers[HttpHeaderKey.IfNoneMatch] = etag);

        // Assert — validators re-emitted for cache updating; no content headers or body.
        second.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        second.ReadResponseBody().ShouldBeEmpty();
        Header(second, HttpHeaderKey.ETag).ShouldBe(etag);
        second.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeFalse();
        second.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: an unchanged If-Modified-Since should respond 304")]
    public async Task Invoke_IfModifiedSinceUnchanged_ShouldRespond304()
    {
        // Arrange — replay the emitted Last-Modified, the way a real cache revalidates.
        using InMemoryFileSystem site = StaticSite.Create(("page.html", "content"));
        TestHttpContext first = await RunAsync(site, "/page.html", HttpMethod.Get);
        string lastModified = Header(first, HttpHeaderKey.LastModified);

        // Act
        TestHttpContext second = await RunAsync(
            site, "/page.html", HttpMethod.Get,
            prepare: context => context.Request.Headers[HttpHeaderKey.IfModifiedSince] = lastModified);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: If-None-Match should take precedence over If-Modified-Since")]
    public async Task Invoke_IfNoneMatchMismatch_ShouldSuppressIfModifiedSince()
    {
        // Arrange — the tag mismatches (so If-None-Match says "send it") while the date alone
        // would have produced a 304; RFC 9110 §13.1.3 says the date must then be ignored.
        using InMemoryFileSystem site = StaticSite.Create(("page.html", "content"));
        TestHttpContext first = await RunAsync(site, "/page.html", HttpMethod.Get);
        string lastModified = Header(first, HttpHeaderKey.LastModified);

        // Act
        TestHttpContext second = await RunAsync(
            site, "/page.html", HttpMethod.Get,
            prepare: context =>
            {
                context.Request.Headers[HttpHeaderKey.IfNoneMatch] = "\"different\"";
                context.Request.Headers[HttpHeaderKey.IfModifiedSince] = lastModified;
            });

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.ReadResponseBody().ShouldBe("content");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a failed If-Unmodified-Since should respond 412")]
    public async Task Invoke_IfUnmodifiedSinceInPast_ShouldRespond412()
    {
        // Arrange — a guard date well before the file's actual modification time.
        using InMemoryFileSystem site = StaticSite.Create(("page.html", "content"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/page.html", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.IfUnmodifiedSince] = "Sat, 01 Jan 2000 00:00:00 GMT");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- byte ranges

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a single byte range should respond 206 with Content-Range")]
    public async Task Invoke_SingleByteRange_ShouldRespond206WithContentRange()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/data.txt", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=2-5");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        context.ReadResponseBody().ShouldBe("2345");
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes 2-5/10");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("4");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a suffix range should serve the final bytes")]
    public async Task Invoke_SuffixRange_ShouldServeFinalBytes()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/data.txt", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=-3");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        context.ReadResponseBody().ShouldBe("789");
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes 7-9/10");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: an unsatisfiable range should respond 416 with bytes */N")]
    public async Task Invoke_UnsatisfiableRange_ShouldRespond416()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/data.txt", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=100-200");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes */10");
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a multi-range set should fall back to the full 200")]
    public async Task Invoke_MultiRange_ShouldFallBackToFull200()
    {
        // Arrange — multipart/byteranges is out of scope; the whole representation is honest.
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/data.txt", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-1,4-5");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("0123456789");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentRange).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a stale If-Range validator should serve the full 200")]
    public async Task Invoke_IfRangeMismatch_ShouldServeFull200()
    {
        // Arrange — the client's stored validator no longer matches, so the range is ignored
        // and the whole current representation is sent (RFC 9110 §13.1.5).
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/data.txt", HttpMethod.Get,
            prepare: ctx =>
            {
                ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-4";
                ctx.Request.Headers[HttpHeaderKey.IfRange] = "\"stale-etag\"";
            });

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("0123456789");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a current If-Range validator should honor the range")]
    public async Task Invoke_IfRangeMatching_ShouldHonorRange()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        TestHttpContext first = await RunAsync(site, "/data.txt", HttpMethod.Get);
        string etag = Header(first, HttpHeaderKey.ETag);

        // Act
        TestHttpContext second = await RunAsync(
            site, "/data.txt", HttpMethod.Get,
            prepare: ctx =>
            {
                ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-4";
                ctx.Request.Headers[HttpHeaderKey.IfRange] = etag;
            });

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        second.ReadResponseBody().ShouldBe("01234");
    }

    // ---------------------------------------------------------------- HEAD

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: HEAD should emit the GET header section without a body")]
    public async Task Invoke_HeadRequest_ShouldEmitHeadersWithoutBody()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("page.html", "content"));

        // Act
        TestHttpContext context = await RunAsync(site, "/page.html", HttpMethod.Head);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBeEmpty();
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/html");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("7");
        Header(context, HttpHeaderKey.ETag).ShouldStartWith("\"");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: Range on HEAD should be ignored")]
    public async Task Invoke_RangeOnHead_ShouldBeIgnored()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/data.txt", HttpMethod.Head,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-4");

        // Assert — the full representation's metadata, no partial semantics.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("10");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentRange).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- precompressed siblings

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: Accept-Encoding br should serve the .br sibling")]
    public async Task Invoke_AcceptEncodingBr_ShouldServeBrotliSibling()
    {
        // Arrange — sibling content stands in for real brotli bytes; the middleware never
        // inspects the payload, it only negotiates which file to stream.
        using InMemoryFileSystem site = StaticSite.Create(
            ("app.js", "identity-js"),
            ("app.js.br", "brotli-bytes"),
            ("app.js.gz", "gzip-bytes"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.AcceptEncoding] = "gzip, br");

        // Assert — server preference (br first) breaks the client tie; the media type stays
        // the logical file's, and the length/body are the sibling's.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("brotli-bytes");
        Header(context, HttpHeaderKey.ContentEncoding).ShouldBe("br");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/javascript");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("12");
        Header(context, HttpHeaderKey.Vary).ShouldBe("Accept-Encoding");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: gzip preference should serve the .gz sibling")]
    public async Task Invoke_AcceptEncodingGzipOnly_ShouldServeGzipSibling()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(
            ("app.js", "identity-js"),
            ("app.js.gz", "gzip-bytes"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.AcceptEncoding] = "gzip");

        // Assert
        context.ReadResponseBody().ShouldBe("gzip-bytes");
        Header(context, HttpHeaderKey.ContentEncoding).ShouldBe("gzip");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: identity clients still get Vary when siblings exist")]
    public async Task Invoke_NoAcceptEncodingWithSiblings_ShouldServeIdentityWithVary()
    {
        // Arrange — the URL's response varies by Accept-Encoding even when this client gets
        // identity; caches must know that.
        using InMemoryFileSystem site = StaticSite.Create(
            ("app.js", "identity-js"),
            ("app.js.br", "brotli-bytes"));

        // Act
        TestHttpContext context = await RunAsync(site, "/app.js", HttpMethod.Get);

        // Assert
        context.ReadResponseBody().ShouldBe("identity-js");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentEncoding).ShouldBeFalse();
        Header(context, HttpHeaderKey.Vary).ShouldBe("Accept-Encoding");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: no siblings means no Vary and no Content-Encoding")]
    public async Task Invoke_NoSiblings_ShouldNotEmitVary()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("app.js", "identity-js"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.AcceptEncoding] = "br, gzip");

        // Assert
        context.ReadResponseBody().ShouldBe("identity-js");
        context.Response.Headers.ContainsKey(HttpHeaderKey.Vary).ShouldBeFalse();
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentEncoding).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: disabling precompression should always serve identity")]
    public async Task Invoke_PrecompressionDisabled_ShouldServeIdentity()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(
            ("app.js", "identity-js"),
            ("app.js.br", "brotli-bytes"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            options => options.ServePrecompressedAssets = false,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.AcceptEncoding] = "br");

        // Assert
        context.ReadResponseBody().ShouldBe("identity-js");
        context.Response.Headers.ContainsKey(HttpHeaderKey.Vary).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: each encoding is its own representation with its own ETag")]
    public async Task Invoke_PrecompressedSibling_ShouldCarryDistinctETag()
    {
        // Arrange — distinct representations must not share a strong validator (RFC 9110 §8.8.3).
        using InMemoryFileSystem site = StaticSite.Create(
            ("app.js", "identity-js"),
            ("app.js.br", "brotli-bytes"));

        // Act
        TestHttpContext identity = await RunAsync(site, "/app.js", HttpMethod.Get);
        TestHttpContext brotli = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            prepare: ctx => ctx.Request.Headers[HttpHeaderKey.AcceptEncoding] = "br");

        // Assert
        Header(brotli, HttpHeaderKey.ContentEncoding).ShouldBe("br");
        Header(identity, HttpHeaderKey.ETag).ShouldNotBe(Header(brotli, HttpHeaderKey.ETag));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: ranges should apply to the negotiated representation")]
    public async Task Invoke_RangeWithPrecompressedSibling_ShouldSliceSiblingBytes()
    {
        // Arrange — RFC 9110 §14.2: the range addresses the selected representation, which is
        // the encoded sibling when negotiation picks it.
        using InMemoryFileSystem site = StaticSite.Create(
            ("app.js", "identity-js"),
            ("app.js.br", "brotli-bytes"));

        // Act
        TestHttpContext context = await RunAsync(
            site, "/app.js", HttpMethod.Get,
            prepare: ctx =>
            {
                ctx.Request.Headers[HttpHeaderKey.AcceptEncoding] = "br";
                ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-5";
            });

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        context.ReadResponseBody().ShouldBe("brotli");
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes 0-5/12");
        Header(context, HttpHeaderKey.ContentEncoding).ShouldBe("br");
    }
}
