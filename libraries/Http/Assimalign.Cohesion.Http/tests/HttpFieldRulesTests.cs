using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFieldRulesTests
{
    [Theory]
    [InlineData("Connection")]
    [InlineData("Proxy-Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    [InlineData("connection")] // case-insensitive
    public void IsConnectionSpecific_OnConnectionField_ShouldBeTrue(string name)
    {
        HttpFieldRules.IsConnectionSpecific(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("X-Custom")]
    public void IsConnectionSpecific_OnNonConnectionField_ShouldBeFalse(string name)
    {
        HttpFieldRules.IsConnectionSpecific(name).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("Host")]
    [InlineData("Content-Type")]
    [InlineData("content-length")] // case-insensitive
    public void IsSingleton_OnSingletonField_ShouldBeTrue(string name)
    {
        HttpFieldRules.IsSingleton(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Accept")]
    [InlineData("Set-Cookie")]
    [InlineData("X-Custom")]
    public void IsSingleton_OnListField_ShouldBeFalse(string name)
    {
        HttpFieldRules.IsSingleton(name).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Set-Cookie", true)]
    [InlineData("set-cookie", true)]
    [InlineData("Cookie", false)]
    [InlineData("Accept", false)]
    public void IsSetCookie_ShouldClassify(string name, bool expected)
    {
        HttpFieldRules.IsSetCookie(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Set-Cookie", true)]
    [InlineData("Accept", false)]
    public void ProhibitsCombining_ShouldClassify(string name, bool expected)
    {
        HttpFieldRules.ProhibitsCombining(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Host")]
    [InlineData("Authorization")]
    [InlineData("Content-Type")]
    [InlineData("Trailer")]
    [InlineData("Set-Cookie")]
    [InlineData("Cache-Control")]
    public void IsProhibitedInTrailers_OnForbiddenField_ShouldBeTrue(string name)
    {
        HttpFieldRules.IsProhibitedInTrailers(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("X-Trace-Id")]
    [InlineData("Server-Timing")]
    [InlineData("ETag")]
    public void IsProhibitedInTrailers_OnAllowedField_ShouldBeFalse(string name)
    {
        HttpFieldRules.IsProhibitedInTrailers(name).ShouldBeFalse();
    }

    [Fact]
    public void Classifiers_OnEmptyKey_ShouldBeFalse()
    {
        HttpHeaderKey empty = default;

        HttpFieldRules.IsConnectionSpecific(empty).ShouldBeFalse();
        HttpFieldRules.IsSingleton(empty).ShouldBeFalse();
        HttpFieldRules.IsSetCookie(empty).ShouldBeFalse();
        HttpFieldRules.IsProhibitedInTrailers(empty).ShouldBeFalse();
    }
}
