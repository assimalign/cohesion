using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// The single-page-application shape (#1056), end to end over a real web root: static assets first,
/// application routes next, and <c>MapFallbackToFile("index.html")</c> for every client-side route, with
/// missing assets and non-GET requests still 404. Also covers static files mounted in a <c>Map(path)</c>
/// branch.
/// </summary>
public sealed class StaticFilesFallbackTests : IDisposable
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private readonly string _contentRootPath;

    public StaticFilesFallbackTests()
    {
        _contentRootPath = Path.Combine(Path.GetTempPath(), $"cohesion-spa-{Guid.NewGuid():N}");
        string webRoot = Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot")).FullName;
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html>spa</html>");
        File.WriteAllText(Path.Combine(webRoot, "app.js"), "console.log('app');");
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

    private WebApplicationTestFactory CreateFactory() => new(new WebApplicationTestFactoryOptions
    {
        ContentRootPath = FileSystemPath.Parse(_contentRootPath),
    });

    private static RouterRouteHandler Text(string body) => new(async context =>
    {
        context.Response.StatusCode = HttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), context.RequestCancelled);
    });

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Fallback: A single-page application serves assets, API routes and index.html for client routes")]
    public async Task MapFallbackToFile_SinglePageApplication_ShouldServeEachKindOfRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseStaticFiles();
        factory.Application.UseRouting().Map(HttpMethod.Get, "/api/ping", Text("pong"));
        factory.Application.MapFallbackToFile("index.html");
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage asset = await client.GetAsync("/app.js", cancellation.Token);
        using HttpResponseMessage api = await client.GetAsync("/api/ping", cancellation.Token);
        using HttpResponseMessage clientRoute = await client.GetAsync("/dashboard/settings", cancellation.Token);
        using HttpResponseMessage missingAsset = await client.GetAsync("/missing.js", cancellation.Token);
        using HttpResponseMessage post = await client.PostAsync("/dashboard", new StringContent("x"), cancellation.Token);

        // Assert
        asset.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await asset.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("console.log('app');");
        (await api.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("pong");
        clientRoute.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        clientRoute.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await clientRoute.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("<html>spa</html>");
        missingAsset.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        post.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Fallback: A missing fallback file answers 404")]
    public async Task MapFallbackToFile_MissingFile_ShouldAnswer404()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting();
        factory.Application.MapFallbackToFile("missing.html");
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/dashboard", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Fallback: A fallback file outside the web root is rejected")]
    [InlineData("../appsettings.json")]
    [InlineData("assets/../../secret.txt")]
    public async Task MapFallbackToFile_TraversalPath_ShouldThrow(string filePath)
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();

        // Act & Assert
        Should.Throw<ArgumentException>(() => factory.Application.MapFallbackToFile(filePath));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Map: Static files mounted in a path branch serve below its prefix")]
    public async Task UseStaticFiles_InPathBranch_ShouldServeBelowPrefix()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.Map("/static", branch => branch.UseStaticFiles());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage mounted = await client.GetAsync("/static/app.js", cancellation.Token);
        using HttpResponseMessage unmounted = await client.GetAsync("/app.js", cancellation.Token);

        // Assert
        mounted.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await mounted.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("console.log('app');");
        unmounted.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
    }
}
