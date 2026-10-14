using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

public sealed class StaticFilesFieldValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: Invalid mapped field values fail at composition")]
    [InlineData("text/plain\r\nX-Injected: true")]
    [InlineData("text/plain\0")]
    [InlineData("text/plain\u001f")]
    [InlineData("text/plain\u007f")]
    [InlineData(" text/plain")]
    [InlineData("text/plain ")]
    [InlineData("\ttext/plain")]
    [InlineData("text/plain\t")]
    [InlineData("")]
    [InlineData(null)]
    public void UseStaticFiles_InvalidMappedFieldValue_ShouldThrowAtComposition(string? contentType)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("asset.txt", "content"));
        var builder = new TestPipelineBuilder();

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.UseStaticFiles(
            site, options => options.ContentTypeMappings[".txt"] = contentType!));
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: Invalid fallback field values fail at composition")]
    [InlineData("text/plain\r\nX-Injected: true")]
    [InlineData("text/plain\0")]
    [InlineData("text/plain\u001f")]
    [InlineData("text/plain\u007f")]
    [InlineData(" text/plain")]
    [InlineData("text/plain ")]
    [InlineData("\ttext/plain")]
    [InlineData("text/plain\t")]
    public void UseStaticFiles_InvalidFallbackFieldValue_ShouldThrowAtComposition(string contentType)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("asset.unknown", "content"));
        var builder = new TestPipelineBuilder();

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.UseStaticFiles(site, options =>
        {
            options.ServeUnknownContentTypes = true;
            options.FallbackContentType = contentType;
        }));
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: Configured content types preserve C1 obs-text")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseStaticFiles_C1ObsText_ShouldPreserveContentType(bool fallback)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("asset.unknown", "content"));
        TestPipelineBuilder builder = new();
        TestHttpContext context = new("/asset.unknown", HttpMethod.Get);
        const string contentType = "text/plain; custom=\"\u0080\u0085\u009f\"";

        // Act
        builder.UseStaticFiles(site, options =>
        {
            if (fallback)
            {
                options.ServeUnknownContentTypes = true;
                options.FallbackContentType = contentType;
            }
            else
            {
                options.ContentTypeMappings[".unknown"] = contentType;
            }
        });
        await builder.Build().ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe(contentType);
        context.ReadResponseBody().ShouldBe("content");
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: An unused invalid fallback still fails at composition")]
    [InlineData("text/plain\r\nX-Injected: true")]
    [InlineData("")]
    [InlineData(null)]
    public void UseStaticFiles_InvalidDisabledFallback_ShouldThrowAtComposition(string? contentType)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("asset.txt", "content"));
        var builder = new TestPipelineBuilder();

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.UseStaticFiles(
            site, options => options.FallbackContentType = contentType!));
    }
}
