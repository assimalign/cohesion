using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Web.Compression.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Compression.Tests;

public sealed class CompressionCompositionValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Compression] - AddResponseCompression: Invalid media-type patterns fail during registration")]
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
    public void AddResponseCompression_InvalidMediaTypePattern_ShouldThrowAtComposition(string? pattern)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act and assert — the build that registration runs throws, so nothing is registered.
        Should.Throw<ArgumentException>(() => builder.Registered.Add(BuildResponseCompression(options => options.MimeTypes.Add(pattern!))));
        builder.Registered.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Compression] - AddResponseCompression: Exact, subtype and full wildcard patterns remain accepted")]
    [InlineData("application/json")]
    [InlineData("APPLICATION/PROBLEM+JSON")]
    [InlineData("application/*+json")]
    [InlineData("te*xt/html")]
    [InlineData(" text/plain\t")]
    [InlineData("text/*")]
    [InlineData("*/*")]
    public void AddResponseCompression_ValidMediaTypePattern_ShouldRegister(string pattern)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act
        builder.Registered.Add(BuildResponseCompression(options => options.MimeTypes.Add(pattern)));
        builder.UseResponseCompression();

        // Assert
        builder.Registered.ShouldHaveSingleItem();
        builder.LastComposed.ShouldNotBeNull();
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

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - AddResponseCompression: Disabling both codings fails during registration")]
    public void AddResponseCompression_NoCodingEnabled_ShouldThrowAtComposition()
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act and assert
        Should.Throw<ArgumentException>(() => builder.Registered.Add(BuildResponseCompression(options =>
        {
            options.EnableGzip = false;
            options.EnableBrotli = false;
        })));
        builder.Registered.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Add: Zero thresholds and limits remain accepted")]
    public void Add_ZeroSizeOptions_ShouldRegister()
    {
        // Arrange
        TestPipelineBuilder response = new();
        TestPipelineBuilder request = new();

        // Act
        response.Registered.Add(BuildResponseCompression(options => options.MinimumResponseSizeBytes = 0));
        request.Registered.Add(BuildRequestDecompression(options => options.MaxDecompressedSizeBytes = 0));
        response.UseResponseCompression();
        request.UseRequestDecompression();

        // Assert
        response.LastComposed.ShouldNotBeNull();
        request.LastComposed.ShouldNotBeNull();
    }

    private static IWebApplicationMiddleware BuildResponseCompression(Action<ResponseCompressionOptions> configure)
    {
        ResponseCompressionBuilder compression = new();
        configure.Invoke(compression.Options);

        return compression.Build();
    }

    private static IWebApplicationMiddleware BuildRequestDecompression(Action<RequestDecompressionOptions> configure)
    {
        RequestDecompressionBuilder decompression = new();
        configure.Invoke(decompression.Options);

        return decompression.Build();
    }
}
