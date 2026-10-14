using System;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

/// <summary>
/// RFC 6265 &#167; 4.1.1 octet-grammar validation at <see cref="HttpCookie"/>
/// construction and the matching drop-on-parse robustness in
/// <see cref="HttpCookieCollection"/>. These are the anti-header-splitting
/// guards: a name or value carrying <c>;</c>, <c>,</c>, whitespace, a control
/// character, or CR/LF must never reach the serialized <c>Set-Cookie</c> line.
/// </summary>
public class HttpCookieValidationTests
{
    // Each forbidden cookie-octet class, addressed by code point so no raw
    // control byte ever appears in source.
    [Theory(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: rejects values with forbidden octets")]
    [InlineData(0x00)] // NUL control
    [InlineData(0x09)] // HTAB (whitespace / control)
    [InlineData(0x0A)] // LF — header-splitting octet
    [InlineData(0x0D)] // CR — header-splitting octet
    [InlineData(0x20)] // SP (whitespace)
    [InlineData(0x22)] // DQUOTE
    [InlineData(0x2C)] // comma — cookie-list / folding delimiter
    [InlineData(0x3B)] // semicolon — attribute delimiter
    [InlineData(0x5C)] // backslash
    [InlineData(0x7F)] // DEL control
    public void Ctor_ValueWithForbiddenOctet_ShouldThrowArgumentException(int octet)
    {
        string value = "a" + (char)octet + "b";

        ArgumentException ex = Should.Throw<ArgumentException>(() => new HttpCookie("sid", value));

        // The HttpCookieValue conversion throws before the constructor runs, so ParamName is the
        // conversion's own parameter and the message is what names the cookie value.
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldContain("Cookie value", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: CR/LF value cannot inject a second Set-Cookie line")]
    public void Ctor_CrlfInjectionValue_IsRejectedSoSerializationStaysSingleLine()
    {
        // The injection can never be constructed, so ToString() can never emit
        // the smuggled directive on its own header line.
        Should.Throw<ArgumentException>(() => new HttpCookie("sid", "abc\r\nSet-Cookie: role=admin"));
    }

    // Cookie-name must be an RFC 9110 token; separators and controls are out.
    [Theory(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: rejects names outside the token grammar")]
    [InlineData(0x3D)] // '=' separator
    [InlineData(0x3B)] // ';'
    [InlineData(0x20)] // SP
    [InlineData(0x2C)] // ','
    [InlineData(0x09)] // HTAB
    [InlineData(0x0D)] // CR
    [InlineData(0x0A)] // LF
    [InlineData(0x28)] // '(' separator
    [InlineData(0x22)] // DQUOTE
    public void Ctor_NameWithForbiddenOctet_ShouldThrowArgumentException(int octet)
    {
        string name = "a" + (char)octet + "b";

        ArgumentException ex = Should.Throw<ArgumentException>(() => new HttpCookie(name, "value"));

        // Thrown by the HttpCookieName conversion: ParamName is its parameter, the message names the cookie name.
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldContain("Cookie name", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: rejects the default name, which its type cannot validate")]
    public void Ctor_DefaultName_ShouldThrowArgumentNullException()
    {
        ArgumentNullException ex = Should.Throw<ArgumentNullException>(() => new HttpCookie(default, "value"));
        ex.ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: a null name or value string is refused by its conversion")]
    public void Ctor_NullNameOrValueString_ShouldThrowArgumentException()
    {
        Should.Throw<ArgumentException>(() => new HttpCookie((string)null!, "value"));
        Should.Throw<ArgumentException>(() => new HttpCookie("sid", (string)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: an omitted or default value is the empty value")]
    public void Ctor_OmittedOrDefaultValue_ShouldBeEmpty()
    {
        HttpCookie omitted = new("sid");
        HttpCookie defaulted = new("sid", default);

        omitted.Value.ShouldBe<HttpCookieValue>(string.Empty);
        defaulted.Value.ShouldBe<HttpCookieValue>(string.Empty);
        omitted.HasValue.ShouldBeFalse();
        omitted.ToString().ShouldBe("sid=; Path=/");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: accepts well-formed cookie values")]
    [InlineData("abc123")]
    [InlineData("")]                 // cookie-value = *cookie-octet — empty is valid
    [InlineData("YWJjZA+/9g==")]     // base64 payload — '+', '/', '=' are all cookie-octets
    [InlineData("\"quoted-value\"")] // a single surrounding DQUOTE pair is permitted
    [InlineData("a!#$%&'()*-./:<=>?@[]^_`{|}~")] // dense cookie-octet coverage
    public void Ctor_WellFormedValue_ShouldNotThrow(string value)
    {
        HttpCookie cookie = new("sid", value);
        cookie.Value.ShouldBe<HttpCookieValue>(value);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCookie ctor: accepts a dense token name")]
    public void Ctor_DenseTokenName_ShouldNotThrow()
    {
        HttpCookie cookie = new("a!#$%&'*+-.^_`|~09AZaz", "v");
        cookie.Name.ShouldBe<HttpCookieName>("a!#$%&'*+-.^_`|~09AZaz");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Request parse: drops a pair whose value carries a control octet")]
    public void RequestParse_ValueWithControlOctet_ShouldDropOnlyThatCookie()
    {
        // The BEL (0x07) in the second value falls outside cookie-octet, so the
        // pair is dropped while the well-formed pair survives.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Cookie] = "good=ok; bad=va" + (char)0x07 + "lue";

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        cookies.Count.ShouldBe(1);
        cookies.Single().Name.ShouldBe<HttpCookieName>("good");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Response parse: drops a malformed cookie without throwing")]
    public void ResponseParse_MalformedNameValue_ShouldDropAndNotThrow()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(new[] { "id=1", "bad=x" + (char)0x07 + "y" });

        HttpCookieCollection cookies = Should.NotThrow(
            () => new HttpCookieCollection(headers, HttpHeaderKey.SetCookie));

        cookies.Count.ShouldBe(1);
        cookies.Single().Name.ShouldBe<HttpCookieName>("id");
    }
}
