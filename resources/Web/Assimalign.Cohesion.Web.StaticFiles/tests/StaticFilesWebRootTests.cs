using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

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
    public async Task UseStaticFiles_Configure_ShouldRunOnce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string webRoot = Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot")).FullName;
        File.WriteAllText(Path.Combine(webRoot, "app.css"), "body{}");
        int configured = 0;
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseStaticFiles(options =>
        {
            configured++;
            options.CacheControl = "public, max-age=60";
        });
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

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Web root: invalid options fail when the middleware is added")]
    public async Task UseStaticFiles_InvalidOptions_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();

        // Act + Assert
        Should.Throw<ArgumentException>(() => factory.Application.UseStaticFiles(options => options.DefaultDocuments.Add("../escape.html")));
    }
}
