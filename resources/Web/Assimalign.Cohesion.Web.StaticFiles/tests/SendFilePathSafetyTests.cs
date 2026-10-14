using System;
using System.IO;
using System.Net.Http;
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
/// Proves the path overload of <c>SendFileAsync</c> cannot be turned into a traversal hole when an
/// application feeds it request input: a physical mount over <c>public/</c> sits next to a
/// <c>secret.txt</c> that every attempt below tries, and fails, to reach.
/// </summary>
public sealed class SendFilePathSafetyTests : IDisposable
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root;
    private readonly string _secretPath;
    private readonly PhysicalFileSystem _mount;

    public SendFilePathSafetyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cohesion-send-file-{Guid.NewGuid():N}");
        string publicDirectory = Directory.CreateDirectory(Path.Combine(_root, "public")).FullName;
        _secretPath = Path.Combine(_root, "secret.txt");
        File.WriteAllText(_secretPath, "top-secret");
        File.WriteAllText(Path.Combine(publicDirectory, "report.txt"), "public report");

        _mount = new PhysicalFileSystem(new PhysicalFileSystemOptions
        {
            Root = FileSystemPath.Parse(publicDirectory),
            IsReadOnly = true,
            Name = "public",
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

    private async Task<TestHttpContext> SendAsync(string path)
    {
        var context = new TestHttpContext("/download", HttpMethod.Get);
        await context.Response.SendFileAsync(_mount, path, cancellationToken: CancellationToken.None);
        return context;
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a file inside a physical mount should be sent")]
    public async Task SendFileAsync_FileInPhysicalMount_ShouldServeIt()
    {
        // Act
        TestHttpContext context = await SendAsync("report.txt");

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("public report");
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: traversal out of a physical mount should respond 404")]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("/../secret.txt")]
    [InlineData("report.txt/../../secret.txt")]
    [InlineData("nested/..\\..\\secret.txt")]
    [InlineData("..")]
    public async Task SendFileAsync_TraversalOnPhysicalMount_ShouldRespond404(string path)
    {
        // Act
        TestHttpContext context = await SendAsync(path);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.ReadResponseBody().ShouldNotContain("top-secret");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: an absolute host path should not escape a physical mount")]
    public async Task SendFileAsync_AbsoluteHostPath_ShouldRespond404()
    {
        // Arrange — the secret's real location. On Windows its drive colon is refused outright; on
        // Unix the leading '/' is the mount root, so the path is looked up inside the mount.
        string absolute = _secretPath;
        string forwardSlashes = _secretPath.Replace('\\', '/');

        // Act
        TestHttpContext native = await SendAsync(absolute);
        TestHttpContext normalized = await SendAsync(forwardSlashes);

        // Assert
        native.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        native.ReadResponseBody().ShouldNotContain("top-secret");
        normalized.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        normalized.ReadResponseBody().ShouldNotContain("top-secret");
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: encoded traversal in a request path handed to the helper should respond 404")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/%2E%2E/secret.txt")]
    [InlineData("/..%5csecret.txt")]
    [InlineData("/report.txt/%2e%2e/%2e%2e/secret.txt")]
    public async Task SendFileAsync_EncodedTraversalOverHttp11_ShouldRespond404(string attackPath)
    {
        // Arrange — the careless shape: a handler that passes the decoded request path straight to
        // the helper. The transport decodes "%2e%2e" to ".." and "%5c" to "\" before the handler
        // runs; the helper's gate must still refuse them.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using var factory = new WebApplicationTestFactory();
        factory.Application.Use((context, next) => context.Response.SendFileAsync(_mount, context.Request.Path.Value));
        using HttpClient client = factory.CreateClient();

        // Act — keep the client from collapsing the encoded dot segments before they hit the wire.
        var rawUri = new Uri(
            "http://localhost" + attackPath,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using HttpResponseMessage attack = await client.GetAsync(rawUri, cancellation.Token);
        using HttpResponseMessage legitimate = await client.GetAsync("/report.txt", cancellation.Token);

        // Assert
        attack.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        (await attack.Content.ReadAsStringAsync(cancellation.Token)).ShouldNotContain("top-secret");
        legitimate.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await legitimate.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("public report");
    }
}
