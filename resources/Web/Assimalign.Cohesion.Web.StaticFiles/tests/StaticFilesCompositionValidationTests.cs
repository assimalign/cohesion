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
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - AddStaticFiles: Invalid default file names fail during registration")]
    [InlineData("bad?.html")]
    [InlineData("bad*.html")]
    [InlineData("bad\".html")]
    [InlineData("bad<.html")]
    [InlineData("bad>.html")]
    [InlineData("bad|.html")]
    [InlineData("bad:.html")]
    [InlineData("bad\0.html")]
    [InlineData("bad\u001f.html")]
    public void AddStaticFiles_InvalidDefaultDocument_ShouldThrowAtRegistration(string document)
    {
        // Arrange — an explicit mount and the web root validate alike.
        using InMemoryFileSystem site = StaticSite.Create(("index.html", "content"));
        StaticFilesBuilder explicitMount = new StaticFilesBuilder().UseFileSystem(site);
        StaticFilesBuilder webRoot = new();
        explicitMount.Options.DefaultDocuments.Add(document);
        webRoot.Options.DefaultDocuments.Add(document);

        // Act and assert
        Should.Throw<ArgumentException>(() => explicitMount.Build());
        Should.Throw<ArgumentException>(() => webRoot.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - AddStaticFiles: Overlong default names fail during registration")]
    public void AddStaticFiles_OverlongDefaultDocument_ShouldThrowAtRegistration()
    {
        // Arrange
        StaticFilesBuilder files = new();
        files.Options.DefaultDocuments.Add(new string('x', FileName.MaxLength + 1));

        // Act and assert
        Should.Throw<ArgumentException>(() => files.Build());
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
