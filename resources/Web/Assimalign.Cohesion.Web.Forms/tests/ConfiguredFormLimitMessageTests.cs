using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;

using TestHttpContext = Assimalign.Cohesion.Web.Forms.Tests.UseFormsMiddlewareTests.TestHttpContext;

namespace Assimalign.Cohesion.Web.Forms.Tests;

public sealed class ConfiguredFormLimitMessageTests
{
    // These pin existing configured-only messages at all seven original throw sites, both
    // key/value branches, and #1435's eighth site for multipart section counts.
    [Theory(DisplayName = "Cohesion Test [Web.Forms] - ReadFormAsync: every built-in limit message contains configuration only")]
    [InlineData("boundary", HttpFormLimit.MultipartBoundaryLength, "Multipart boundary length limit 3 exceeded.")]
    [InlineData("line", HttpFormLimit.MultipartHeadersLength, "Line length limit 8 exceeded.")]
    [InlineData("key", HttpFormLimit.KeyLength, "Form key or value length limit 3 exceeded.")]
    [InlineData("value", HttpFormLimit.ValueLength, "Form key or value length limit 3 exceeded.")]
    [InlineData("count", HttpFormLimit.ValueCount, "Form value count limit 1 exceeded.")]
    [InlineData("preamble", HttpFormLimit.MultipartHeadersLength, "Multipart preamble exceeded the headers-length limit.")]
    [InlineData("headers", HttpFormLimit.MultipartHeadersCount, "Multipart section header count exceeded the 1 limit.")]
    [InlineData("body", HttpFormLimit.MultipartBodyLength, "Multipart section body exceeded the 3-byte limit.")]
    [InlineData("sections", HttpFormLimit.MultipartSectionCount, "Multipart section count limit 1 exceeded.")]
    public async Task ReadFormAsync_ConfiguredLimitExceeded_ShouldKeepMessageIndependentOfRequest(string scenario, HttpFormLimit expectedLimit, string expectedMessage)
    {
        // Arrange — vary every attacker-controlled field while keeping configuration fixed.
        foreach (string marker in new[] { "private-alpha", "private-omega" })
        {
            (string contentType, string content, HttpFormOptions options) = CreateInput(scenario, marker);
            using var body = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var context = new TestHttpContext(contentType, body);
            var feature = new HttpFormFeature(context.Request, options);

            // Act
            InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync(CancellationToken.None));

            // Assert
            HttpFormLimitExceededException limit = exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>();
            limit.Limit.ShouldBe(expectedLimit);
            limit.Message.ShouldBe(expectedMessage);
            exception.Message.ShouldBe(expectedMessage);
            limit.Message.ShouldNotContain(marker, Case.Sensitive);
        }
    }

    private static (string ContentType, string Content, HttpFormOptions Options) CreateInput(string scenario, string marker)
    {
        const string encoded = "application/x-www-form-urlencoded";
        const string multipart = "multipart/form-data; boundary=B";
        string section = $"--B\r\nContent-Disposition: form-data; name=\"{marker}\"\r\n\r\n{marker}\r\n";
        return scenario switch
        {
            "boundary" => ($"multipart/form-data; boundary={marker}", "", new HttpFormOptions { MultipartBoundaryLengthLimit = 3 }),
            "line" => (multipart, $"{marker}\r\n--B--\r\n", new HttpFormOptions { MultipartHeadersLengthLimit = 8 }),
            "key" => (encoded, $"{marker}=v", new HttpFormOptions { KeyLengthLimit = 3 }),
            "value" => (encoded, $"a={marker}", new HttpFormOptions { ValueLengthLimit = 3 }),
            "count" => (encoded, $"a={marker}&b={marker}", new HttpFormOptions { ValueCountLimit = 1 }),
            "preamble" => (multipart, $"{marker[..6]}\r\n{marker[^6..]}\r\n{marker[..6]}\r\n--B--\r\n", new HttpFormOptions { MultipartHeadersLengthLimit = 8 }),
            "headers" => (multipart, $"--B\r\nContent-Disposition: form-data; name=\"{marker}\"\r\nX-Private: {marker}\r\n\r\nv\r\n--B--\r\n", new HttpFormOptions { MultipartHeadersCountLimit = 1 }),
            "body" => (multipart, section + "--B--\r\n", new HttpFormOptions { MultipartBodyLengthLimit = 3 }),
            "sections" => (multipart, section + section + "--B--\r\n", new HttpFormOptions { ValueCountLimit = 1 }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }
}
