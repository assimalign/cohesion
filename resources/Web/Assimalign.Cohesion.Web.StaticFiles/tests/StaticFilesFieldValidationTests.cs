using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

public sealed class StaticFilesFieldValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - UseStaticFiles: Invalid mapped field values fail at composition")]
    [InlineData("text/plain\r\nX-Injected: true")]
    [InlineData("text/plain\0")]
    [InlineData("text/plain\u001f")]
    [InlineData("text/plain\u007f")]
    [InlineData("text/plain\u0085")]
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
    [InlineData("text/plain\u0085")]
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
