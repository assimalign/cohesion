using System;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

/// <summary>
/// RFC 6265 &#167; 4.1.1 attribute grammar for <see cref="HttpCookieOptions.Path"/>
/// (<c>path-value</c>) and <see cref="HttpCookieOptions.Domain"/>
/// (<c>domain-value</c>), checked in the setters (#1153), and the matching
/// drop-on-parse robustness in <see cref="HttpCookieCollection"/>. A value built
/// from untrusted input must never add an attribute to, or corrupt, the
/// serialized <c>Set-Cookie</c> line.
/// </summary>
public class HttpCookieOptionsValidationTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Path: a ';' cannot inject a SameSite attribute")]
    public void Path_SetToAttributeInjection_ShouldThrowArgumentExceptionNamingPath()
    {
        // Arrange
        HttpCookieOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.Path = "/x; SameSite=None");

        // Assert
        exception.ParamName.ShouldBe(nameof(HttpCookieOptions.Path));
        exception.Message.ShouldContain("Path");
        exception.Message.ShouldNotContain("SameSite=None");
        options.Path.ShouldBe("/");
    }

    // Addressed by code point so no raw control character appears in source.
    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Path: a character outside path-value is refused")]
    [InlineData(0x00)] // NUL
    [InlineData(0x09)] // HTAB, a CTL
    [InlineData(0x0A)] // LF
    [InlineData(0x0D)] // CR
    [InlineData(0x1F)] // last C0 control
    [InlineData(0x3B)] // ';' ends the attribute
    [InlineData(0x7F)] // DEL
    [InlineData(0xE9)] // 'é' is not a US-ASCII CHAR
    public void Path_SetWithForbiddenCharacter_ShouldThrowArgumentExceptionNamingPath(int character)
    {
        // Arrange
        HttpCookieOptions options = new() { Path = "/keep" };
        string path = "/a" + (char)character + "b";

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.Path = path);

        // Assert
        exception.ParamName.ShouldBe(nameof(HttpCookieOptions.Path));
        options.Path.ShouldBe("/keep");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Path: a path-value is accepted")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/app/v1")]
    [InlineData("/a b")] // SP is a CHAR and not a CTL
    [InlineData("/~user/!$&'()*+,=:@\"\\")]
    [InlineData("/caf%C3%A9")]
    [InlineData("relative")] // not a path a user agent keeps (RFC 6265 §5.2.4), but in the grammar
    public void Path_SetToPathValue_ShouldStoreIt(string path)
    {
        // Arrange
        HttpCookieOptions options = new();

        // Act
        options.Path = path;

        // Assert
        options.Path.ShouldBe(path);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Path: null clears the attribute")]
    public void Path_SetToNull_ShouldOmitTheAttribute()
    {
        // Arrange
        HttpCookieOptions options = new();

        // Act
        options.Path = null;

        // Assert
        options.Path.ShouldBeNull();
        new HttpCookie("a", "1", options).ToString().ShouldBe("a=1");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Domain: a value outside domain-value is refused")]
    [InlineData("example.com; Secure")] // attribute injection
    [InlineData("example.com;")]
    [InlineData("exa mple.com")]
    [InlineData(" ")]
    [InlineData("a..b")] // empty label
    [InlineData("example.com.")] // trailing dot: an empty last label
    [InlineData(".")]
    [InlineData("..example.com")] // only one leading dot is ignored
    [InlineData("-a.example")] // a label starts with a letter or digit
    [InlineData("a-.example")] // and ends with one
    [InlineData("exa_mple.com")]
    [InlineData("café.example")] // write it as xn--caf-dma.example
    [InlineData("[::1]")]
    [InlineData("example.com:443")]
    [InlineData("user@example.com")]
    public void Domain_SetToNonDomainValue_ShouldThrowArgumentExceptionNamingDomain(string domain)
    {
        // Arrange
        HttpCookieOptions options = new() { Domain = "keep.example" };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.Domain = domain);

        // Assert
        exception.ParamName.ShouldBe(nameof(HttpCookieOptions.Domain));
        exception.Message.ShouldContain("Domain");
        options.Domain.ShouldBe("keep.example");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Domain: a control character is refused")]
    [InlineData(0x00)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x7F)]
    public void Domain_SetWithControlCharacter_ShouldThrowArgumentException(int character)
    {
        // Arrange
        HttpCookieOptions options = new();
        string domain = "exa" + (char)character + "mple.com";

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.Domain = domain);

        // Assert
        exception.ParamName.ShouldBe(nameof(HttpCookieOptions.Domain));
        options.Domain.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Domain: a domain-value is accepted")]
    [InlineData("example.com")]
    [InlineData(".example.com")] // the leading dot is tolerated (RFC 6265 §5.2.3)
    [InlineData("localhost")]
    [InlineData("EXAMPLE.Com")]
    [InlineData("a-b.c-d.example")]
    [InlineData("xn--caf-dma.example")]
    [InlineData("3com.example")] // RFC 1123 §2.1 lets a label start with a digit
    [InlineData("192.0.2.1")]
    public void Domain_SetToDomainValue_ShouldStoreIt(string domain)
    {
        // Arrange
        HttpCookieOptions options = new();

        // Act
        options.Domain = domain;

        // Assert
        options.Domain.ShouldBe(domain);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Domain: null and empty clear the attribute")]
    [InlineData(null)]
    [InlineData("")]
    public void Domain_SetToNullOrEmpty_ShouldOmitTheAttribute(string? domain)
    {
        // Arrange
        HttpCookieOptions options = new() { Domain = "example.com" };

        // Act
        options.Domain = domain;

        // Assert
        options.Domain.ShouldBe(domain);
        new HttpCookie("a", "1", options).ToString().ShouldBe("a=1; Path=/");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Domain: labels of 63 characters and names of 253 are the limits")]
    public void Domain_SetAtLengthLimits_ShouldAcceptTheLimitAndRefuseOneMore()
    {
        // Arrange
        HttpCookieOptions options = new();
        string longestLabel = new string('a', 63) + ".example";
        string overlongLabel = new string('a', 64) + ".example";
        string longestName = string.Join('.', Enumerable.Repeat(new string('a', 63), 3)) + "." + new string('b', 61); // 3 * 64 + 61 = 253
        string overlongName = longestName + "b";

        // Act
        options.Domain = longestLabel;
        Exception? labelRefusal = Record.Exception(() => options.Domain = overlongLabel);
        options.Domain = "." + longestName;
        Exception? nameRefusal = Record.Exception(() => options.Domain = overlongName);

        // Assert
        longestName.Length.ShouldBe(253);
        labelRefusal.ShouldBeOfType<ArgumentException>();
        nameRefusal.ShouldBeOfType<ArgumentException>();
        options.Domain.ShouldBe("." + longestName);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Copy: the copy carries the checked Path and Domain")]
    public void Ctor_CopyOfValidOptions_ShouldCarryPathAndDomain()
    {
        // Arrange
        HttpCookieOptions source = new() { Path = "/app", Domain = ".example.com" };

        // Act
        HttpCookieOptions copy = new(source);
        HttpCookie cookie = new("a", "1", source);

        // Assert
        copy.Path.ShouldBe("/app");
        copy.Domain.ShouldBe(".example.com");
        cookie.ToString().ShouldBe("a=1; Domain=.example.com; Path=/app");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Cookie options: an issued cookie's Path cannot be changed to an injection")]
    public void Path_SetOnIssuedCookieOptions_ShouldThrowAndKeepTheLine()
    {
        // Arrange
        HttpCookie cookie = new("sid", "abc", new HttpCookieOptions { Path = "/app", HttpOnly = true });

        // Act
        Should.Throw<ArgumentException>(() => cookie.Options.Path = "/app; Domain=evil.example");

        // Assert
        cookie.ToString().ShouldBe("sid=abc; Path=/app; HttpOnly");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Response parse: a cookie whose Path or Domain is outside the grammar is dropped")]
    [InlineData("bad=1; Domain=exa_mple.com")]
    [InlineData("bad=1; Domain=example.com.")]
    [InlineData("bad=1; Path=/a\tb")]
    [InlineData("bad=1; Path=/café")]
    public void ResponseParse_AttributeOutsideGrammar_ShouldDropThatCookieWithoutThrowing(string setCookie)
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(new[] { "good=1; Domain=example.com; Path=/app", setCookie });

        // Act
        HttpCookieCollection cookies = Should.NotThrow(() => new HttpCookieCollection(headers, HttpHeaderKey.SetCookie));

        // Assert
        HttpCookie cookie = cookies.ShouldHaveSingleItem();
        cookie.Name.ShouldBe("good");
        cookie.Options.Domain.ShouldBe("example.com");
        cookie.Options.Path.ShouldBe("/app");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Response parse: an empty Domain is ignored, not a reason to drop the cookie")]
    public void ResponseParse_EmptyDomain_ShouldKeepTheCookieHostOnly()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "a=1; Domain=; Path=/";

        // Act
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        // Assert
        HttpCookie cookie = cookies.ShouldHaveSingleItem();
        cookie.Options.Domain.ShouldBeNullOrEmpty();
        cookie.ToString().ShouldBe("a=1; Path=/");
    }
}
