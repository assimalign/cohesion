using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Web.Compression.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Compression.Tests;

public sealed class CompressionCompositionValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Compression] - UseResponseCompression: Invalid media-type patterns fail during registration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("application json")]
    [InlineData("application")]
    [InlineData("/json")]
    [InlineData("application/")]
    [InlineData("application/json/extra")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("text/ht ml")]
    [InlineData("text/html\r\n")]
    [InlineData("*")]
    [InlineData("*/json")]
    public void UseResponseCompression_InvalidMediaTypePattern_ShouldThrowAtComposition(string? pattern)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act and assert
        Should.Throw<ArgumentException>(() => builder.UseResponseCompression(options => options.MimeTypes.Add(pattern!)));
        builder.LastMiddleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Compression] - UseResponseCompression: Exact, subtype and full wildcard patterns remain accepted")]
    [InlineData("application/json")]
    [InlineData("APPLICATION/PROBLEM+JSON")]
    [InlineData("application/*+json")]
    [InlineData("te*xt/html")]
    [InlineData(" text/plain\t")]
    [InlineData("text/*")]
    [InlineData("*/*")]
    public void UseResponseCompression_ValidMediaTypePattern_ShouldRegister(string pattern)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act
        builder.UseResponseCompression(options => options.MimeTypes.Add(pattern));

        // Assert
        builder.LastMiddleware.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Compression] - MinimumResponseSizeBytes: Negative thresholds fail on assignment")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MinimumResponseSizeBytes_NegativeValue_ShouldThrowAtAssignment(int threshold)
    {
        // Arrange
        ResponseCompressionOptions options = new();

        // Act and assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.MinimumResponseSizeBytes = threshold);
        options.MinimumResponseSizeBytes.ShouldBe(1024);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Compression] - MaxDecompressedSizeBytes: Negative limits fail on assignment")]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void MaxDecompressedSizeBytes_NegativeValue_ShouldThrowAtAssignment(long limit)
    {
        // Arrange
        RequestDecompressionOptions options = new();

        // Act and assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxDecompressedSizeBytes = limit);
        options.MaxDecompressedSizeBytes.ShouldBe(100L * 1024 * 1024);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Use: Zero thresholds and limits remain accepted")]
    public void Use_ZeroSizeOptions_ShouldRegister()
    {
        // Arrange
        TestPipelineBuilder response = new();
        TestPipelineBuilder request = new();

        // Act
        response.UseResponseCompression(options => options.MinimumResponseSizeBytes = 0);
        request.UseRequestDecompression(options => options.MaxDecompressedSizeBytes = 0);

        // Assert
        response.LastMiddleware.ShouldNotBeNull();
        request.LastMiddleware.ShouldNotBeNull();
    }
}
