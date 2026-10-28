using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// Covers the parameterless <c>UseStaticFiles()</c> over the application's web root, end to end
/// through <see cref="WebApplicationTestFactory"/> with a real content root on disk.
/// </summary>
public sealed class StaticFilesWebRootTests : IDisposable
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private readonly string _contentRootPath;

    public StaticFilesWebRootTests()
    {
        _contentRootPath = Path.Combine(Path.GetTempPath(), $"cohesion-static-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_contentRootPath);
        File.WriteAllText(Path.Combine(_contentRootPath, "appsettings.json"), """{"Secret":"do-not-serve"}""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private WebApplicationTestFactory CreateFactory()
    {
        return new WebApplicationTestFactory(new WebApplicationTestFactoryOptions
        {
            ContentRootPath = FileSystemPath.Parse(_contentRootPath),
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Web root: serves wwwroot and never the content root")]
    public async Task UseStaticFiles_WebRoot_ShouldServeWwwrootAndNotContentRoot()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string webRoot = Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot")).FullName;
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html>home</html>");
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseStaticFiles();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage page = await client.GetAsync("/index.html", cancellation.Token);
        using HttpResponseMessage root = await client.GetAsync("/", cancellation.Token);
        using HttpResponseMessage settings = await client.GetAsync("/appsettings.json", cancellation.Token);
        using HttpResponseMessage traversal = await client.GetAsync("/../appsettings.json", cancellation.Token);

        // Assert
        page.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("<html>home</html>");
        root.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        settings.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        traversal.StatusCode.ShouldNotBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Web root: without wwwroot every request passes through")]
    public async Task UseStaticFiles_NoWebRoot_ShouldPassThrough()
    {
        // Arrange — the content root holds appsettings.json but no wwwroot.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseStaticFiles();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage settings = await client.GetAsync("/appsettings.json", cancellation.Token);

        // Assert
        settings.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Web root: options are configured once, not per request")]
    public async Task AddStaticFiles_Configure_ShouldRunOnce()
    {
        // Arrange — a registration that names no file system serves the web root UseStaticFiles() mounts.
        using CancellationTokenSource cancellation = new(_testTimeout);
        string webRoot = Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot")).FullName;
        File.WriteAllText(Path.Combine(webRoot, "app.css"), "body{}");
        int configured = 0;
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddStaticFiles(files =>
        {
            configured++;
            files.Options.CacheControl = "public, max-age=60";
        });
        factory.Application.UseStaticFiles();
        using HttpClient client = factory.CreateClient();

        // Act
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await client.GetAsync("/app.css", cancellation.Token);
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            response.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        }

        // Assert
        configured.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Web root: invalid options fail when the middleware is registered")]
    public async Task AddStaticFiles_InvalidOptions_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();

        // Act + Assert
        Should.Throw<ArgumentException>(() => factory.Builder.Services.AddStaticFiles(
            files => files.Options.DefaultDocuments.Add("../escape.html")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: Without a registration it should serve the web root with the default options")]
    public async Task UseStaticFiles_WithoutRegistration_ShouldServeTheWebRootWithTheDefaults()
    {
        // Arrange
        string webRoot = Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot")).FullName;
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html>home</html>");
        TestPipelineBuilder pipeline = new() { WebRootPath = FileSystemPath.Parse(webRoot) };
        TestHttpContext context = new("/", HttpMethod.Get);

        // Act
        pipeline.UseStaticFiles();
        await pipeline.Build().ExecuteAsync(context);

        // Assert — the default documents answer the web root's directory.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("<html>home</html>");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: A registration without a file system should serve the web root with its options")]
    public async Task UseStaticFiles_RegistrationWithoutFileSystem_ShouldServeTheWebRootWithItsOptions()
    {
        // Arrange
        string webRoot = Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot")).FullName;
        File.WriteAllText(Path.Combine(webRoot, "app.css"), "body{}");
        StaticFilesBuilder files = new();
        files.Options.RequestPath = new HttpPath("/assets");
        files.Options.CacheControl = "public, max-age=60";
        TestPipelineBuilder pipeline = new() { WebRootPath = FileSystemPath.Parse(webRoot) };
        pipeline.Registered.Add(files.Build());
        TestHttpContext context = new("/assets/app.css", HttpMethod.Get);

        // Act
        pipeline.UseStaticFiles();
        await pipeline.Build().ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("body{}");
        context.Response.Headers[HttpHeaderKey.CacheControl].Value.ShouldBe("public, max-age=60");
    }
}
