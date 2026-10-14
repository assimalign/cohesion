using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// Proves an 8.3 short-name alias cannot change the content type a physical mount's file is served
/// with. On a Windows volume that generates short names, <c>upload.htmlx</c> also answers to
/// <c>UPLOAD~1.HTM</c>, and the type is read from the name the request spells, so without the gate the
/// alias serves an unmapped upload as <c>text/html</c>. Each test returns early off Windows, or when the
/// volume under the temporary directory generates no short names, since no alias exists to reach there.
/// </summary>
public sealed class StaticFilesShortNameAliasTests : IDisposable
{
    private const string Payload = "<script>alert(1)</script>";

    private readonly string _root;
    private readonly string _uploads;
    private readonly PhysicalFileSystem _mount;

    public StaticFilesShortNameAliasTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cohesion-short-name-{Guid.NewGuid():N}")).FullName;
        _uploads = Directory.CreateDirectory(Path.Combine(_root, "uploads")).FullName;
        File.WriteAllText(Path.Combine(_uploads, "upload.htmlx"), Payload);
        File.WriteAllText(Path.Combine(_uploads, "image.svgz"), Payload);
        File.WriteAllText(Path.Combine(_uploads, "data.xmlx"), Payload);

        _mount = new PhysicalFileSystem(new PhysicalFileSystemOptions
        {
            Root = FileSystemPath.Parse(_root),
            IsReadOnly = true,
            Name = "short-name-site",
        });
    }

    public void Dispose()
    {
        _mount.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // The alias is reachable only on Windows, and only when the volume generated one for the file.
    private bool AliasExists(string alias)
        => OperatingSystem.IsWindows() && File.Exists(Path.Combine(_uploads, alias));

    private async Task<TestHttpContext> RunMiddlewareAsync(string path, bool[] passedThrough)
    {
        var builder = new TestPipelineBuilder();
        builder.UseStaticFiles(_mount);
        builder.Use((context, next) =>
        {
            passedThrough[0] = true;
            return Task.CompletedTask;
        });

        var context = new TestHttpContext(path, HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);
        return context;
    }

    private static string Header(TestHttpContext context, HttpHeaderKey key)
        => context.Response.Headers.TryGetValue(key, out HttpHeaderValue value) ? (string)value : string.Empty;

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: a short-name alias on a physical mount should respond 404, not serve the active type")]
    [InlineData("upload.htmlx", "UPLOAD~1.HTM")]
    [InlineData("image.svgz", "IMAGE~1.SVG")]
    [InlineData("data.xmlx", "DATA~1.XML")]
    public async Task Invoke_ShortNameAliasOnPhysicalMount_ShouldRespond404(string longName, string alias)
    {
        // Arrange
        if (!AliasExists(alias))
        {
            return;
        }
        bool[] longNamePassedThrough = [false];
        bool[] aliasPassedThrough = [false];

        // Act
        TestHttpContext byLongName = await RunMiddlewareAsync("/uploads/" + longName, longNamePassedThrough);
        TestHttpContext byAlias = await RunMiddlewareAsync("/uploads/" + alias, aliasPassedThrough);

        // Assert — the long name has an unmapped extension and passes through; its alias must not
        // turn it into text/html, image/svg+xml, or application/xml.
        longNamePassedThrough[0].ShouldBeTrue();
        byLongName.ReadResponseBody().ShouldBeEmpty();
        byAlias.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Header(byAlias, HttpHeaderKey.ContentType).ShouldBeEmpty();
        byAlias.ReadResponseBody().ShouldBeEmpty();
        aliasPassedThrough[0].ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a short-name alias path on a physical mount should respond 404")]
    public async Task SendFileAsync_ShortNameAliasOnPhysicalMount_ShouldRespond404()
    {
        // Arrange
        if (!AliasExists("UPLOAD~1.HTM"))
        {
            return;
        }
        var context = new TestHttpContext("/download", HttpMethod.Get);

        // Act
        await context.Response.SendFileAsync(_mount, "uploads/UPLOAD~1.HTM", cancellationToken: CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Header(context, HttpHeaderKey.ContentType).ShouldBeEmpty();
        context.ReadResponseBody().ShouldBeEmpty();
    }
}
