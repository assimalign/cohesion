using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Internal;

using Shouldly;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// Locks in the security-relevant defaults of <see cref="HttpLoggingOptions"/> and the
/// validation performed when they are frozen into a snapshot.
/// </summary>
public class HttpLoggingOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Options: Credential-bearing headers must not be in the default allowlists")]
    public void Defaults_ShouldExcludeCredentialBearingHeaders()
    {
        // Arrange
        HttpLoggingOptions options = new();

        // Assert — the redaction-by-default contract of issue #794.
        options.AllowedRequestHeaders.ShouldNotContain(HttpHeaderKey.Authorization);
        options.AllowedRequestHeaders.ShouldNotContain(HttpHeaderKey.ProxyAuthorization);
        options.AllowedRequestHeaders.ShouldNotContain(HttpHeaderKey.Cookie);
        options.AllowedResponseHeaders.ShouldNotContain(HttpHeaderKey.SetCookie);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Options: Default allowlists carry the standard access-log headers")]
    public void Defaults_ShouldAllowCommonAccessLogHeaders()
    {
        // Arrange
        HttpLoggingOptions options = new();

        // Assert — User-Agent and Referer feed the W3C/NCSA formats; Content-Type/Length are
        // routine diagnostics.
        options.AllowedRequestHeaders.ShouldContain(HttpHeaderKey.UserAgent);
        options.AllowedRequestHeaders.ShouldContain(HttpHeaderKey.Referer);
        options.AllowedRequestHeaders.ShouldContain(HttpHeaderKey.ContentType);
        options.AllowedRequestHeaders.ShouldContain(HttpHeaderKey.ContentLength);
        options.AllowedRequestHeaders.ShouldContain(HttpHeaderKey.Host);
        options.AllowedResponseHeaders.ShouldContain(HttpHeaderKey.ContentType);
        options.AllowedResponseHeaders.ShouldContain(HttpHeaderKey.Location);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Options: Defaults exclude bodies and query string from the field set")]
    public void Defaults_ShouldExcludeBodiesAndQuery()
    {
        // Arrange
        HttpLoggingOptions options = new();

        // Assert
        options.Fields.ShouldBe(HttpLoggingFields.Default);
        (options.Fields & HttpLoggingFields.RequestBody).ShouldBe(HttpLoggingFields.None);
        (options.Fields & HttpLoggingFields.ResponseBody).ShouldBe(HttpLoggingFields.None);
        (options.Fields & HttpLoggingFields.RequestQuery).ShouldBe(HttpLoggingFields.None);
        (options.Fields & HttpLoggingFields.RequestHeaders).ShouldNotBe(HttpLoggingFields.None);
        (options.Fields & HttpLoggingFields.Duration).ShouldNotBe(HttpLoggingFields.None);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Snapshot: Should reject a LogLevel.None level")]
    public void Snapshot_LevelNone_ShouldThrow()
    {
        // Arrange
        HttpLoggingOptions options = new() { Level = LogLevel.None };

        // Act + Assert
        Should.Throw<ArgumentException>(() => HttpLoggingSnapshot.Create(options));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Snapshot: Should reject invalid textual and numeric options")]
    public void Snapshot_InvalidOptions_ShouldThrow()
    {
        Should.Throw<ArgumentException>(() => HttpLoggingSnapshot.Create(new HttpLoggingOptions { Category = "" }));
        Should.Throw<ArgumentException>(() => HttpLoggingSnapshot.Create(new HttpLoggingOptions { RedactedValue = "" }));
        Should.Throw<ArgumentOutOfRangeException>(() => HttpLoggingSnapshot.Create(new HttpLoggingOptions { RequestBodyLimit = -1 }));
        Should.Throw<ArgumentOutOfRangeException>(() => HttpLoggingSnapshot.Create(new HttpLoggingOptions { ResponseBodyLimit = -1 }));
        Should.Throw<ArgumentNullException>(() => HttpLoggingSnapshot.Create(new HttpLoggingOptions { TimeProvider = null! }));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Snapshot: Mutating options after freezing has no effect")]
    public void Snapshot_ShouldFreezeOptionValues()
    {
        // Arrange
        HttpLoggingOptions options = new();
        HttpLoggingSnapshot snapshot = HttpLoggingSnapshot.Create(options);

        // Act — mutations after composition must be invisible.
        options.AllowedRequestHeaders.Add(HttpHeaderKey.Authorization);
        options.RedactedValue = "changed";

        // Assert
        snapshot.AllowedRequestHeaders.Contains(HttpHeaderKey.Authorization).ShouldBeFalse();
        snapshot.RedactedValue.ShouldBe("[Redacted]");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - Snapshot: Body content-type gate matches prefixes and structured-syntax suffixes")]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("application/soap+xml; action=\"x\"", true)]
    [InlineData("text/plain", true)]
    [InlineData("text/html; charset=utf-8", true)]
    [InlineData("application/x-www-form-urlencoded", true)]
    [InlineData("application/octet-stream", false)]
    [InlineData("image/png", false)]
    [InlineData("", false)]
    public void Snapshot_BodyContentTypeGate_ShouldMatchExpectations(string contentType, bool expected)
    {
        // Arrange
        HttpLoggingSnapshot snapshot = HttpLoggingSnapshot.Create(new HttpLoggingOptions());

        // Act + Assert
        snapshot.IsBodyContentTypeLoggable(contentType).ShouldBe(expected);
    }
}
