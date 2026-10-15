using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

public sealed class StaticFilesCompositionValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: Invalid default file names fail during registration")]
    [InlineData("bad?.html")]
    [InlineData("bad*.html")]
    [InlineData("bad\".html")]
    [InlineData("bad<.html")]
    [InlineData("bad>.html")]
    [InlineData("bad|.html")]
    [InlineData("bad:.html")]
    [InlineData("bad\0.html")]
    [InlineData("bad\u001f.html")]
    public void UseStaticFiles_InvalidDefaultDocument_ShouldThrowAtComposition(string document)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("index.html", "content"));
        TestPipelineBuilder explicitMount = new();
        TestPipelineBuilder webRoot = new();
        Action<StaticFilesOptions> configure = options => options.DefaultDocuments.Add(document);

        // Act and assert
        Should.Throw<ArgumentException>(() => explicitMount.UseStaticFiles(site, configure));
        Should.Throw<ArgumentException>(() => webRoot.UseStaticFiles(configure));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: Overlong default names fail during registration")]
    public void UseStaticFiles_OverlongDefaultDocument_ShouldThrowAtComposition()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        string document = new('x', FileName.MaxLength + 1);

        // Act and assert
        Should.Throw<ArgumentException>(() => builder.UseStaticFiles(options => options.DefaultDocuments.Add(document)));
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - MapFallbackToFile: Unparseable paths fail during registration")]
    [InlineData("bad?.html")]
    [InlineData("nested/bad<.html")]
    [InlineData("nested/bad>.html")]
    [InlineData("nested/bad|.html")]
    [InlineData("nested/bad\u001f.html")]
    [InlineData("/")]
    [InlineData("\\index.html")]
    public async Task MapFallbackToFile_InvalidRelativePath_ShouldThrowAtComposition(string filePath)
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        // Act and assert
        Should.Throw<ArgumentException>(() => factory.Application.MapFallbackToFile(filePath));
        Should.Throw<ArgumentException>(() => factory.Application.MapFallbackToFile("app/{**path:nonfile}", filePath));
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - MapFallbackToFile: Valid mount-relative paths remain accepted")]
    [InlineData("index.html")]
    [InlineData("nested/index.html")]
    [InlineData("/nested/index.html")]
    [InlineData("nested\\index.html")]
    public async Task MapFallbackToFile_ValidRelativePath_ShouldRegister(string filePath)
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        // Act
        IRouterRouteBuilder route = factory.Application.MapFallbackToFile(filePath);

        // Assert
        route.ShouldNotBeNull();
    }
}
