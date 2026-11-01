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
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - AddStaticFiles: Invalid mapped field values fail at registration")]
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
    public void AddStaticFiles_InvalidMappedFieldValue_ShouldThrowAtRegistration(string? contentType)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("asset.txt", "content"));

        // Act / Assert
        Should.Throw<ArgumentException>(() => StaticFilesMount.Build(
            site, options => options.ContentTypeMappings[".txt"] = contentType!));
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - AddStaticFiles: Invalid fallback field values fail at registration")]
    [InlineData("text/plain\r\nX-Injected: true")]
    [InlineData("text/plain\0")]
    [InlineData("text/plain\u001f")]
    [InlineData("text/plain\u007f")]
    [InlineData(" text/plain")]
    [InlineData("text/plain ")]
    [InlineData("\ttext/plain")]
    [InlineData("text/plain\t")]
    public void AddStaticFiles_InvalidFallbackFieldValue_ShouldThrowAtRegistration(string contentType)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("asset.unknown", "content"));

        // Act / Assert
        Should.Throw<ArgumentException>(() => StaticFilesMount.Build(site, options =>
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
        builder.Use(StaticFilesMount.Build(site, options =>
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
        }));
        await builder.Build().ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe(contentType);
        context.ReadResponseBody().ShouldBe("content");
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - AddStaticFiles: An unused invalid fallback still fails at registration")]
    [InlineData("text/plain\r\nX-Injected: true")]
    [InlineData("")]
    [InlineData(null)]
    public void AddStaticFiles_InvalidDisabledFallback_ShouldThrowAtRegistration(string? contentType)
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("asset.txt", "content"));

        // Act / Assert
        Should.Throw<ArgumentException>(() => StaticFilesMount.Build(
            site, options => options.FallbackContentType = contentType!));
    }
}
