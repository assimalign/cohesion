using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// Covers the <c>SendFileAsync</c> and <c>WriteStreamAsync</c> response helpers: the RFC 9110
/// &#167; 13 conditional outcomes (<c>304</c>, <c>412</c>), &#167; 14 single ranges (<c>206</c>,
/// <c>416</c>, <c>If-Range</c>), <c>HEAD</c>, streams of unknown length, the mount-confined path
/// overload, and parity with the validators <c>UseStaticFiles</c> emits — over a fake exchange, and
/// end to end over the in-memory HTTP/1.1 transport.
/// </summary>
public class HttpResponseFileExtensionsTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private static async Task<TestHttpContext> RunAsync(
        HttpMethod method,
        Func<IHttpResponse, Task> handler,
        Action<TestHttpContext>? prepare = null)
    {
        var context = new TestHttpContext("/download", method);
        prepare?.Invoke(context);

        await handler(context.Response);
        return context;
    }

    private static IFileSystemFile GetFile(InMemoryFileSystem site, string path)
        => site.GetFile(FileSystemPath.Parse(path));

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Header(TestHttpContext context, HttpHeaderKey key)
        => context.Response.Headers.TryGetValue(key, out HttpHeaderValue value) ? (string)value : string.Empty;

    // ---------------------------------------------------------------- SendFileAsync(IFileSystemFile)

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a file should be sent as 200 with its entity headers")]
    public async Task SendFileAsync_File_ShouldServe200WithEntityHeaders()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        IFileSystemFile file = GetFile(site, "data.txt");

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(file, cancellationToken: CancellationToken.None));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("0123456789");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/plain");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("10");
        Header(context, HttpHeaderKey.AcceptRanges).ShouldBe("bytes");
        HttpEntityTag.TryParse(Header(context, HttpHeaderKey.ETag), out HttpEntityTag etag).ShouldBeTrue();
        etag.IsWeak.ShouldBeFalse();
        HttpDate.TryParse(Header(context, HttpHeaderKey.LastModified), out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a file should carry the validators UseStaticFiles emits for it")]
    public async Task SendFileAsync_SameFileAsMiddleware_ShouldEmitIdenticalValidators()
    {
        // Arrange — one engine serves both, so a handler and the middleware must agree on the
        // validators; a cache that revalidates either URL with the other's ETag gets a 304.
        using InMemoryFileSystem site = StaticSite.Create(("app.js", "let x = 1;"));
        var builder = new TestPipelineBuilder();
        builder.UseStaticFiles(site);
        var served = new TestHttpContext("/app.js", HttpMethod.Get);
        await builder.Build().ExecuteAsync(served);

        // Act
        TestHttpContext sent = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(GetFile(site, "app.js")));

        // Assert
        served.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        Header(sent, HttpHeaderKey.ETag).ShouldBe(Header(served, HttpHeaderKey.ETag));
        Header(sent, HttpHeaderKey.LastModified).ShouldBe(Header(served, HttpHeaderKey.LastModified));
        Header(sent, HttpHeaderKey.ContentType).ShouldBe(Header(served, HttpHeaderKey.ContentType));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: an explicit content type should replace the derived one")]
    public async Task SendFileAsync_ExplicitContentType_ShouldOverrideDerivedType()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("avatar.html", "<script>alert(1)</script>"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(GetFile(site, "avatar.html"), "application/octet-stream"));

        // Assert
        Header(context, HttpHeaderKey.ContentType).ShouldBe("application/octet-stream");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: an unmapped extension should be sent as application/octet-stream")]
    public async Task SendFileAsync_UnmappedExtension_ShouldServeOctetStream()
    {
        // Arrange — unlike UseStaticFiles, which declines unknown types, a handler chose to send this file.
        using InMemoryFileSystem site = StaticSite.Create(("data.xyzzy", "mystery"));

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(GetFile(site, "data.xyzzy")));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        Header(context, HttpHeaderKey.ContentType).ShouldBe("application/octet-stream");
        context.ReadResponseBody().ShouldBe("mystery");
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a file with no extension should be sent as application/octet-stream, whatever its name")]
    [InlineData("html")]
    [InlineData(".json")]
    public async Task SendFileAsync_FileWithoutExtension_ShouldServeOctetStream(string name)
    {
        // Arrange — a name that happens to spell a type is not that type's extension.
        using InMemoryFileSystem site = StaticSite.Create(("uploads/" + name, "<script>alert(1)</script>"));

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(GetFile(site, "uploads/" + name)));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        Header(context, HttpHeaderKey.ContentType).ShouldBe("application/octet-stream");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a mount path to a file with no extension should be sent as application/octet-stream")]
    public async Task SendFileAsync_PathToFileWithoutExtension_ShouldServeOctetStream()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("uploads/html", "<script>alert(1)</script>"));

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(site, "uploads/html"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        Header(context, HttpHeaderKey.ContentType).ShouldBe("application/octet-stream");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a current If-None-Match should respond 304 without content")]
    public async Task SendFileAsync_IfNoneMatchCurrent_ShouldRespond304WithoutContent()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        IFileSystemFile file = GetFile(site, "data.txt");
        TestHttpContext first = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(file));
        string etag = Header(first, HttpHeaderKey.ETag);

        // Act
        TestHttpContext second = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(file),
            context => context.Request.Headers[HttpHeaderKey.IfNoneMatch] = etag);

        // Assert — RFC 9110 §15.4.5: validators for cache updating, no content fields or body.
        second.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        second.ReadResponseBody().ShouldBeEmpty();
        Header(second, HttpHeaderKey.ETag).ShouldBe(etag);
        second.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeFalse();
        second.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: an unchanged If-Modified-Since should respond 304")]
    public async Task SendFileAsync_IfModifiedSinceCurrent_ShouldRespond304()
    {
        // Arrange — replay the emitted Last-Modified, the way a cache revalidates.
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        IFileSystemFile file = GetFile(site, "data.txt");
        TestHttpContext first = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(file));
        string lastModified = Header(first, HttpHeaderKey.LastModified);

        // Act
        TestHttpContext second = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(file),
            context => context.Request.Headers[HttpHeaderKey.IfModifiedSince] = lastModified);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        second.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a stale If-Match should respond 412")]
    public async Task SendFileAsync_IfMatchStale_ShouldRespond412()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfMatch] = "\"stale\"");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a failed If-Unmodified-Since should respond 412")]
    public async Task SendFileAsync_IfUnmodifiedSinceInPast_ShouldRespond412()
    {
        // Arrange — a guard date well before the file was written.
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfUnmodifiedSince] = "Sat, 01 Jan 2000 00:00:00 GMT");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a matching If-None-Match on an unsafe method should respond 412")]
    public async Task SendFileAsync_IfNoneMatchOnPost_ShouldRespond412()
    {
        // Arrange — RFC 9110 §13.1.2: a matched If-None-Match is 304 only for GET and HEAD.
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Post,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfNoneMatch] = "*");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a single byte range should respond 206 with Content-Range")]
    public async Task SendFileAsync_SingleRange_ShouldRespond206WithContentRange()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=2-5");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        context.ReadResponseBody().ShouldBe("2345");
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes 2-5/10");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("4");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: an unsatisfiable range should respond 416 with bytes */N")]
    public async Task SendFileAsync_UnsatisfiableRange_ShouldRespond416()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=100-200");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes */10");
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a multi-range request should fall back to the full 200")]
    public async Task SendFileAsync_MultiRange_ShouldFallBackToFull200()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-1,4-5");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("0123456789");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentRange).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a current If-Range entity-tag should honor the range")]
    public async Task SendFileAsync_IfRangeCurrentETag_ShouldHonorRange()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        IFileSystemFile file = GetFile(site, "data.txt");
        TestHttpContext first = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(file));
        string etag = Header(first, HttpHeaderKey.ETag);

        // Act
        TestHttpContext second = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(file),
            ctx =>
            {
                ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=5-";
                ctx.Request.Headers[HttpHeaderKey.IfRange] = etag;
            });

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        second.ReadResponseBody().ShouldBe("56789");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a current If-Range date should honor the range")]
    public async Task SendFileAsync_IfRangeCurrentDate_ShouldHonorRange()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        IFileSystemFile file = GetFile(site, "data.txt");
        TestHttpContext first = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(file));
        string lastModified = Header(first, HttpHeaderKey.LastModified);

        // Act
        TestHttpContext second = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(file),
            ctx =>
            {
                ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-1";
                ctx.Request.Headers[HttpHeaderKey.IfRange] = lastModified;
            });

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        second.ReadResponseBody().ShouldBe("01");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a stale If-Range should ignore the range and send the full 200")]
    public async Task SendFileAsync_IfRangeStale_ShouldServeFull200()
    {
        // Arrange — RFC 9110 §13.1.5: the client's partial copy is out of date, so it gets the
        // whole current representation instead of bytes that would not splice.
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx =>
            {
                ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-4";
                ctx.Request.Headers[HttpHeaderKey.IfRange] = "\"stale-etag\"";
            });

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("0123456789");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentRange).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: HEAD should emit the GET header section without content")]
    public async Task SendFileAsync_Head_ShouldEmitHeadersWithoutContent()
    {
        // Arrange — a Range on HEAD has no defined effect (RFC 9110 §14.2), so it is ignored too.
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Head,
            response => response.SendFileAsync(GetFile(site, "data.txt")),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-4");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBeEmpty();
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("10");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/plain");
        Header(context, HttpHeaderKey.ETag).ShouldStartWith("\"");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentRange).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: header fields set before the call should be kept, also on a 304")]
    public async Task SendFileAsync_ApplicationHeaders_ShouldBeKeptOn304()
    {
        // Arrange — a Cache-Control the handler chose must reach caches on the revalidation too
        // (RFC 9110 §15.4.5).
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        IFileSystemFile file = GetFile(site, "data.txt");
        TestHttpContext first = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(file));
        string etag = Header(first, HttpHeaderKey.ETag);

        // Act
        TestHttpContext second = await RunAsync(
            HttpMethod.Get,
            response =>
            {
                response.Headers[HttpHeaderKey.CacheControl] = "private, max-age=60";
                return response.SendFileAsync(file);
            },
            ctx => ctx.Request.Headers[HttpHeaderKey.IfNoneMatch] = etag);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        Header(second, HttpHeaderKey.CacheControl).ShouldBe("private, max-age=60");
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a content type that is not a concrete media type should be rejected")]
    [InlineData("")]
    [InlineData("not a media type")]
    [InlineData("text/*")]
    [InlineData("*/*")]
    [InlineData("text/plain\r\nX-Injected: true")]
    public async Task SendFileAsync_InvalidContentType_ShouldThrowArgumentException(string contentType)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        var context = new TestHttpContext("/download", HttpMethod.Get);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => context.Response.SendFileAsync(GetFile(site, "data.txt"), contentType));
        await Should.ThrowAsync<ArgumentException>(() => context.Response.SendFileAsync(site, "data.txt", contentType));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: null arguments should be rejected")]
    public async Task SendFileAsync_NullArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        var context = new TestHttpContext("/download", HttpMethod.Get);
        IHttpResponse? missing = null;

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(() => context.Response.SendFileAsync((IFileSystemFile)null!));
        await Should.ThrowAsync<ArgumentNullException>(() => context.Response.SendFileAsync((IFileSystem)null!, "data.txt"));
        await Should.ThrowAsync<ArgumentNullException>(() => context.Response.SendFileAsync(site, null!));
        await Should.ThrowAsync<ArgumentNullException>(() => missing!.SendFileAsync(GetFile(site, "data.txt")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a cancelled token should stop the content copy")]
    public async Task SendFileAsync_CancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        var context = new TestHttpContext("/download", HttpMethod.Get);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => context.Response.SendFileAsync(GetFile(site, "data.txt"), cancellationToken: cancellation.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: without a token the copy should observe the exchange's cancellation")]
    public async Task SendFileAsync_NoTokenAndRequestCancelled_ShouldThrowOperationCanceledException()
    {
        // Arrange — a client that went away cancels RequestCancelled; a handler that passed no
        // token must not keep copying to it.
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        using CancellationTokenSource aborted = new();
        await aborted.CancelAsync();
        var context = new CancellableTestHttpContext("/download", HttpMethod.Get, aborted.Token);

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => context.Response.SendFileAsync(GetFile(site, "data.txt")));
    }

    // ---------------------------------------------------------------- SendFileAsync(IFileSystem, path)

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a mount-relative path should resolve and send the file")]
    [InlineData("reports/2026.txt")]
    [InlineData("/reports/2026.txt")]
    public async Task SendFileAsync_PathInMount_ShouldServeFile(string path)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("reports/2026.txt", "annual report"));

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(site, path));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("annual report");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/plain");
        Header(context, HttpHeaderKey.ETag).ShouldStartWith("\"");
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: an unsafe path should respond 404 without reaching the mount")]
    [InlineData("../secret.txt")]
    [InlineData("public/../secret.txt")]
    [InlineData("public/../../secret.txt")]
    [InlineData("./secret.txt")]
    [InlineData("public/./page.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("public\\..\\..\\secret.txt")]
    [InlineData("c:/windows/win.ini")]
    [InlineData("secret.txt::$DATA")]
    [InlineData("secret\0.txt")]
    public async Task SendFileAsync_UnsafePath_ShouldRespond404(string path)
    {
        // Arrange — "./secret.txt" and "public/./page.txt" name files that exist once
        // canonicalized; they are refused anyway, because the gate rejects dot segments outright
        // rather than normalizing them (the UseStaticFiles rule).
        using InMemoryFileSystem site = StaticSite.Create(
            ("secret.txt", "top-secret"),
            ("public/page.txt", "public page"));

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(site, path));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a path spelled as an 8.3 short-name alias should respond 404")]
    public async Task SendFileAsync_ShortNameAliasPath_ShouldRespond404()
    {
        // Arrange — the in-memory mount holds the alias as a real name, so only the gate can refuse
        // it. On a volume that generates short names the same path opens upload.htmlx and would be
        // typed text/html from the alias's ".HTM".
        using InMemoryFileSystem site = StaticSite.Create(("uploads/UPLOAD~1.HTM", "<script>alert(1)</script>"));

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(site, "uploads/UPLOAD~1.HTM"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a path naming a directory or nothing should respond 404")]
    [InlineData("missing.txt")]
    [InlineData("reports")]
    [InlineData("reports/")]
    [InlineData("/")]
    [InlineData("")]
    public async Task SendFileAsync_PathWithoutFile_ShouldRespond404(string path)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("reports/2026.txt", "annual report"));

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.SendFileAsync(site, path));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- WriteStreamAsync

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a seekable stream should be sent with its length and range support")]
    public async Task WriteStreamAsync_SeekableStream_ShouldServe200WithLengthAndAcceptRanges()
    {
        // Arrange
        using MemoryStream stream = Content("0123456789");

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.WriteStreamAsync(stream, "text/csv"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("0123456789");
        Header(context, HttpHeaderKey.ContentType).ShouldBe("text/csv");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("10");
        Header(context, HttpHeaderKey.AcceptRanges).ShouldBe("bytes");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a stream should be sent as application/octet-stream by default")]
    public async Task WriteStreamAsync_NoContentType_ShouldServeOctetStream()
    {
        // Arrange
        using MemoryStream stream = Content("bytes");

        // Act
        TestHttpContext context = await RunAsync(HttpMethod.Get, response => response.WriteStreamAsync(stream));

        // Assert
        Header(context, HttpHeaderKey.ContentType).ShouldBe("application/octet-stream");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: validators should be the caller's and never derived from the content")]
    public async Task WriteStreamAsync_Validators_ShouldEmitOnlyCallerSuppliedValues()
    {
        // Arrange — the supplied time carries milliseconds; HTTP-date has whole seconds.
        using MemoryStream withValidators = Content("versioned");
        using MemoryStream without = Content("versioned");
        var updatedOn = new DateTimeOffset(2026, 9, 30, 12, 34, 56, 789, TimeSpan.FromHours(2));

        // Act
        TestHttpContext supplied = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(withValidators, "text/plain", HttpEntityTag.Strong("v42"), updatedOn));
        TestHttpContext none = await RunAsync(HttpMethod.Get, response => response.WriteStreamAsync(without, "text/plain"));

        // Assert — no ETag or Last-Modified is invented when the caller has none.
        Header(supplied, HttpHeaderKey.ETag).ShouldBe("\"v42\"");
        Header(supplied, HttpHeaderKey.LastModified).ShouldBe("Wed, 30 Sep 2026 10:34:56 GMT");
        none.Response.Headers.ContainsKey(HttpHeaderKey.ETag).ShouldBeFalse();
        none.Response.Headers.ContainsKey(HttpHeaderKey.LastModified).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a matching If-None-Match should respond 304 without reading the stream")]
    public async Task WriteStreamAsync_IfNoneMatchCurrent_ShouldRespond304WithoutReading()
    {
        // Arrange
        using var stream = new NonSeekableReadStream("versioned");

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(stream, "text/plain", HttpEntityTag.Strong("v42")),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfNoneMatch] = "\"v41\", \"v42\"");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        Header(context, HttpHeaderKey.ETag).ShouldBe("\"v42\"");
        context.ReadResponseBody().ShouldBeEmpty();
        stream.ReadCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: If-Modified-Since against the supplied time should respond 304")]
    public async Task WriteStreamAsync_IfModifiedSinceCurrent_ShouldRespond304()
    {
        // Arrange — the sub-second part of the supplied time must not defeat revalidation.
        using MemoryStream stream = Content("versioned");
        var updatedOn = new DateTimeOffset(2026, 9, 30, 12, 34, 56, 789, TimeSpan.Zero);

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(stream, "text/plain", lastModified: updatedOn),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfModifiedSince] = "Wed, 30 Sep 2026 12:34:56 GMT");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a stale If-Match should respond 412")]
    public async Task WriteStreamAsync_IfMatchStale_ShouldRespond412()
    {
        // Arrange
        using MemoryStream stream = Content("versioned");

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(stream, "text/plain", HttpEntityTag.Strong("v42")),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfMatch] = "\"v41\"");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: preconditions without validators should only match the * forms")]
    public async Task WriteStreamAsync_NoValidators_ShouldMatchOnlyWildcardPreconditions()
    {
        // Arrange — nothing is hashed, so a listed entity-tag can never match; "*" still does,
        // because the representation exists (RFC 9110 §13.1.1, §13.1.2).
        using MemoryStream listed = Content("content");
        using MemoryStream wildcard = Content("content");

        // Act
        TestHttpContext listedContext = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(listed),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfNoneMatch] = "\"anything\"");
        TestHttpContext wildcardContext = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(wildcard),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfNoneMatch] = "*");

        // Assert
        listedContext.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        listedContext.ReadResponseBody().ShouldBe("content");
        wildcardContext.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a weak entity-tag should satisfy If-None-Match but never If-Match")]
    public async Task WriteStreamAsync_WeakETag_ShouldCompareWeaklyOnlyForIfNoneMatch()
    {
        // Arrange — RFC 9110 §8.8.3.2: If-None-Match compares weakly, If-Match strongly.
        using MemoryStream revalidated = Content("content");
        using MemoryStream guarded = Content("content");

        // Act
        TestHttpContext ifNoneMatch = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(revalidated, "text/plain", HttpEntityTag.Weak("v1")),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfNoneMatch] = "W/\"v1\"");
        TestHttpContext ifMatch = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(guarded, "text/plain", HttpEntityTag.Weak("v1")),
            ctx => ctx.Request.Headers[HttpHeaderKey.IfMatch] = "\"v1\"");

        // Assert
        ifNoneMatch.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        Header(ifNoneMatch, HttpHeaderKey.ETag).ShouldBe("W/\"v1\"");
        ifMatch.Response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a single range on a seekable stream should respond 206")]
    public async Task WriteStreamAsync_SingleRange_ShouldRespond206WithContentRange()
    {
        // Arrange
        using MemoryStream stream = Content("0123456789");

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(stream, "text/plain"),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=-3");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        context.ReadResponseBody().ShouldBe("789");
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes 7-9/10");
        Header(context, HttpHeaderKey.ContentLength).ShouldBe("3");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: the representation should start at the stream's current position")]
    public async Task WriteStreamAsync_PositionedStream_ShouldServeRemainingBytes()
    {
        // Arrange — a handler that already consumed a header from the stream sends the rest.
        using MemoryStream full = Content("HDR:0123456789");
        using MemoryStream ranged = Content("HDR:0123456789");
        full.Position = 4;
        ranged.Position = 4;

        // Act
        TestHttpContext fullContext = await RunAsync(HttpMethod.Get, response => response.WriteStreamAsync(full, "text/plain"));
        TestHttpContext rangedContext = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(ranged, "text/plain"),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=2-4");

        // Assert
        fullContext.ReadResponseBody().ShouldBe("0123456789");
        Header(fullContext, HttpHeaderKey.ContentLength).ShouldBe("10");
        rangedContext.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        rangedContext.ReadResponseBody().ShouldBe("234");
        Header(rangedContext, HttpHeaderKey.ContentRange).ShouldBe("bytes 2-4/10");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: an unsatisfiable range should respond 416 with bytes */N")]
    public async Task WriteStreamAsync_UnsatisfiableRange_ShouldRespond416()
    {
        // Arrange
        using MemoryStream stream = Content("0123456789");

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(stream, "text/plain", HttpEntityTag.Strong("v1")),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=10-");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
        Header(context, HttpHeaderKey.ContentRange).ShouldBe("bytes */10");
        Header(context, HttpHeaderKey.ETag).ShouldBe("\"v1\"");
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: If-Range should honor the range only for the current strong entity-tag")]
    public async Task WriteStreamAsync_IfRange_ShouldHonorRangeOnlyForCurrentStrongETag()
    {
        // Arrange — If-Range compares strongly (RFC 9110 §13.1.5), so a weak current tag never
        // satisfies it, and a stream without validators can never satisfy it either.
        using MemoryStream current = Content("0123456789");
        using MemoryStream weak = Content("0123456789");
        using MemoryStream unvalidated = Content("0123456789");
        static void Prepare(TestHttpContext ctx)
        {
            ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=0-2";
            ctx.Request.Headers[HttpHeaderKey.IfRange] = "\"v1\"";
        }

        // Act
        TestHttpContext currentContext = await RunAsync(
            HttpMethod.Get, response => response.WriteStreamAsync(current, "text/plain", HttpEntityTag.Strong("v1")), Prepare);
        TestHttpContext weakContext = await RunAsync(
            HttpMethod.Get, response => response.WriteStreamAsync(weak, "text/plain", HttpEntityTag.Weak("v1")), Prepare);
        TestHttpContext unvalidatedContext = await RunAsync(
            HttpMethod.Get, response => response.WriteStreamAsync(unvalidated, "text/plain"), Prepare);

        // Assert
        currentContext.Response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        currentContext.ReadResponseBody().ShouldBe("012");
        weakContext.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        weakContext.ReadResponseBody().ShouldBe("0123456789");
        unvalidatedContext.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        unvalidatedContext.ReadResponseBody().ShouldBe("0123456789");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a stream of unknown length should be sent whole, without length or range support")]
    public async Task WriteStreamAsync_NonSeekableStream_ShouldServeFullWithoutLengthOrRanges()
    {
        // Arrange — a Range cannot be reached without reading and discarding the bytes before it,
        // so it is ignored (RFC 9110 §14.2) and the transport delimits the body.
        using var stream = new NonSeekableReadStream("0123456789");

        // Act
        TestHttpContext context = await RunAsync(
            HttpMethod.Get,
            response => response.WriteStreamAsync(stream, "text/plain", HttpEntityTag.Strong("v1")),
            ctx => ctx.Request.Headers[HttpHeaderKey.Range] = "bytes=2-5");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("0123456789");
        Header(context, HttpHeaderKey.ETag).ShouldBe("\"v1\"");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeFalse();
        context.Response.Headers.ContainsKey(HttpHeaderKey.AcceptRanges).ShouldBeFalse();
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentRange).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: HEAD should emit the header section without reading the stream")]
    public async Task WriteStreamAsync_Head_ShouldNotReadStream()
    {
        // Arrange
        using var unknownLength = new NonSeekableReadStream("0123456789");
        using MemoryStream knownLength = Content("0123456789");

        // Act
        TestHttpContext unknown = await RunAsync(HttpMethod.Head, response => response.WriteStreamAsync(unknownLength, "text/plain"));
        TestHttpContext known = await RunAsync(HttpMethod.Head, response => response.WriteStreamAsync(knownLength, "text/plain"));

        // Assert — a known length is reported as GET would; an unknown one cannot be, and is omitted.
        unknown.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        unknown.ReadResponseBody().ShouldBeEmpty();
        unknown.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeFalse();
        unknownLength.ReadCount.ShouldBe(0);
        known.ReadResponseBody().ShouldBeEmpty();
        Header(known, HttpHeaderKey.ContentLength).ShouldBe("10");
        knownLength.Position.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: the stream should be left open for its owner")]
    public async Task WriteStreamAsync_Stream_ShouldNotBeDisposed()
    {
        // Arrange
        using MemoryStream stream = Content("0123456789");

        // Act
        await RunAsync(HttpMethod.Get, response => response.WriteStreamAsync(stream, "text/plain"));

        // Assert
        stream.CanRead.ShouldBeTrue();
        stream.Position.ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: invalid arguments should be rejected")]
    public async Task WriteStreamAsync_InvalidArguments_ShouldThrow()
    {
        // Arrange
        var context = new TestHttpContext("/download", HttpMethod.Get);
        var closed = new MemoryStream();
        await closed.DisposeAsync();
        using MemoryStream open = Content("content");

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(() => context.Response.WriteStreamAsync(null!));
        await Should.ThrowAsync<ArgumentException>(() => context.Response.WriteStreamAsync(closed));
        await Should.ThrowAsync<ArgumentException>(() => context.Response.WriteStreamAsync(open, entityTag: default(HttpEntityTag)));
        await Should.ThrowAsync<ArgumentException>(() => context.Response.WriteStreamAsync(open, "text/plain\nX-Injected: true"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a cancelled token should stop the content copy")]
    public async Task WriteStreamAsync_CancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var context = new TestHttpContext("/download", HttpMethod.Get);
        using MemoryStream stream = Content("0123456789");
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => context.Response.WriteStreamAsync(stream, "text/plain", cancellationToken: cancellation.Token));
    }

    // ---------------------------------------------------------------- end to end over HTTP/1.1

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: revalidation and ranges should round-trip over the wire")]
    public async Task SendFileAsync_OverHttp11_ShouldServeValidatorsRevalidationAndRanges()
    {
        // Arrange — a terminal handler, the shape an endpoint has.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("reports/2026.txt", "0123456789"));
        await using var factory = new WebApplicationTestFactory();
        factory.Application.Use((context, next) => context.Response.SendFileAsync(site, "reports/2026.txt", cancellationToken: context.RequestCancelled));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/report", cancellation.Token);
        EntityTagHeaderValue etag = first.Headers.ETag!;

        using var revalidate = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/report");
        revalidate.Headers.IfNoneMatch.Add(etag);
        using HttpResponseMessage notModified = await client.SendAsync(revalidate, cancellation.Token);

        using var resume = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/report");
        resume.Headers.Range = new RangeHeaderValue(6, null);
        resume.Headers.IfRange = new RangeConditionHeaderValue(etag);
        using HttpResponseMessage partial = await client.SendAsync(resume, cancellation.Token);

        // Assert
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("0123456789");
        first.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        first.Content.Headers.LastModified.ShouldNotBeNull();
        first.Headers.AcceptRanges.ShouldContain("bytes");
        notModified.StatusCode.ShouldBe(NetHttpStatusCode.NotModified);
        (await notModified.Content.ReadAsStringAsync(cancellation.Token)).ShouldBeEmpty();
        partial.StatusCode.ShouldBe(NetHttpStatusCode.PartialContent);
        (await partial.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("6789");
        partial.Content.Headers.ContentRange!.ToString().ShouldBe("bytes 6-9/10");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: HEAD over the wire should return the header section and no content")]
    public async Task SendFileAsync_HeadOverHttp11_ShouldReturnHeadersWithoutContent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("data.txt", "0123456789"));
        await using var factory = new WebApplicationTestFactory();
        factory.Application.Use((context, next) => context.Response.SendFileAsync(site, "data.txt"));
        using HttpClient client = factory.CreateClient();

        // Act
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Head, "/data");
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
        response.Content.Headers.ContentLength.ShouldBe(10);
        response.Headers.ETag.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - WriteStreamAsync: a stream of unknown length should arrive whole over the wire")]
    public async Task WriteStreamAsync_NonSeekableStreamOverHttp11_ShouldDeliverWholeBody()
    {
        // Arrange — 100 KiB, larger than one copy buffer, from a stream that cannot report a length.
        using CancellationTokenSource cancellation = new(_testTimeout);
        string payload = new('x', 100 * 1024);
        await using var factory = new WebApplicationTestFactory();
        factory.Application.Use(async (context, next) =>
        {
            using var stream = new NonSeekableReadStream(payload);
            await context.Response.WriteStreamAsync(stream, "text/plain", HttpEntityTag.Strong("v1"), cancellationToken: context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/stream", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe(payload);
        response.Headers.ETag!.Tag.ShouldBe("\"v1\"");
        response.Headers.AcceptRanges.ShouldBeEmpty();
    }
}
