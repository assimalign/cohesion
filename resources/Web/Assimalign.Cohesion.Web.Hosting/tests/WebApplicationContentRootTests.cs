using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class WebApplicationContentRootTests : IDisposable
{
    private readonly string _contentRootPath;

    public WebApplicationContentRootTests()
    {
        _contentRootPath = Path.Combine(Path.GetTempPath(), $"cohesion-web-content-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_contentRootPath);
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Content root: wwwroot under the content root becomes the web root")]
    public async Task Build_ContentRootWithWwwroot_ShouldSetWebRoot()
    {
        // Arrange
        string webRootPath = Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot")).FullName;
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = FileSystemPath.Parse(_contentRootPath),
        });

        // Act
        await using WebApplication application = builder.Build();

        // Assert
        application.Context.ContentRootPath.ShouldBe(FileSystemPath.Parse(Path.GetFullPath(_contentRootPath)));
        application.Context.WebRootPath.ShouldBe(FileSystemPath.Parse(webRootPath));
        builder.Environment.ContentRootPath.ShouldBe(application.Context.ContentRootPath);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Content root: without wwwroot the application has no web root")]
    public async Task Build_ContentRootWithoutWwwroot_ShouldHaveNoWebRoot()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_contentRootPath, "appsettings.json"), "{}");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = FileSystemPath.Parse(_contentRootPath),
        });

        // Act
        await using WebApplication application = builder.Build();

        // Assert — never the content root itself.
        application.Context.WebRootPath.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Content root: a relative web root resolves against the content root")]
    public async Task Build_RelativeWebRoot_ShouldResolveAgainstContentRoot()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = FileSystemPath.Parse(_contentRootPath),
            WebRootPath = FileSystemPath.Parse("public"),
        });

        // Act
        await using WebApplication application = builder.Build();

        // Assert — a configured web root is kept even before the directory exists.
        application.Context.WebRootPath.ShouldBe(FileSystemPath.Parse(Path.GetFullPath(Path.Combine(_contentRootPath, "public"))));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Content root: a plain application defaults to the base directory")]
    public async Task Build_NoContentRoot_ShouldUseBaseDirectory()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());

        // Act
        await using WebApplication application = builder.Build();

        // Assert
        application.Context.ContentRootPath.ShouldBe(FileSystemPath.Parse(AppContext.BaseDirectory));
    }
}
