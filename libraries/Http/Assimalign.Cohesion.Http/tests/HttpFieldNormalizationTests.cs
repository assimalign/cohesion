using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFieldNormalizationTests
{
    [Fact]
    public void ResolveAuthority_WhenAuthorityPresent_ShouldWinOverHost()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Host] = "host-header.test";

        HttpHost host = HttpFieldNormalization.ResolveAuthority("authority.test", headers);

        host.Value.ShouldBe("authority.test");
    }

    [Fact]
    public void ResolveAuthority_WhenAuthorityAbsent_ShouldFallBackToHost()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Host] = "host-header.test";

        HttpFieldNormalization.ResolveAuthority(null, headers).Value.ShouldBe("host-header.test");
        HttpFieldNormalization.ResolveAuthority("   ", headers).Value.ShouldBe("host-header.test");
    }

    [Fact]
    public void ResolveAuthority_WhenNeitherPresent_ShouldBeEmpty()
    {
        HttpHeaderCollection headers = new();

        HttpFieldNormalization.ResolveAuthority(null, headers).ShouldBe(HttpHost.Empty);
    }

    [Theory]
    [InlineData("Connection", true)]
    [InlineData("Keep-Alive", true)]
    [InlineData("Proxy-Connection", true)]
    [InlineData("Transfer-Encoding", true)]
    [InlineData("Upgrade", true)]
    [InlineData("TE", false)] // TE is handled separately (allowed with a restricted value)
    [InlineData("Content-Type", false)]
    public void IsForbiddenInHttp2Or3_ShouldClassify(string name, bool expected)
    {
        HttpFieldNormalization.IsForbiddenInHttp2Or3(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("trailers", true)]
    [InlineData("TRAILERS", true)]
    [InlineData("", true)] // empty == absent
    [InlineData("gzip", false)]
    [InlineData("trailers, deflate", false)]
    public void IsTeValueValidInHttp2Or3_ShouldClassify(string value, bool expected)
    {
        HttpFieldNormalization.IsTeValueValidInHttp2Or3(new HttpHeaderValue(value)).ShouldBe(expected);
    }

    [Fact]
    public void CombineFieldValue_OnCookie_ShouldCoalesceWithSemicolon()
    {
        HttpHeaderValue combined = HttpFieldNormalization.CombineFieldValue(
            HttpHeaderKey.Cookie, new HttpHeaderValue("a=1"), new HttpHeaderValue("b=2"));

        combined.Value.ShouldBe("a=1; b=2");
    }

    [Fact]
    public void CombineFieldValue_OnSetCookie_ShouldKeepDistinctValues()
    {
        HttpHeaderValue combined = HttpFieldNormalization.CombineFieldValue(
            HttpHeaderKey.SetCookie, new HttpHeaderValue("a=1"), new HttpHeaderValue("b=2"));

        // Set-Cookie must never be folded into one comma line: the two cookies
        // remain distinct values.
        combined.Count.ShouldBe(2);
        combined[0].ShouldBe("a=1");
        combined[1].ShouldBe("b=2");
    }

    [Fact]
    public void CombineFieldValue_OnListField_ShouldCombineAsValues()
    {
        HttpHeaderValue combined = HttpFieldNormalization.CombineFieldValue(
            HttpHeaderKey.Accept, new HttpHeaderValue("text/html"), new HttpHeaderValue("application/json"));

        combined.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: Combining many repeated list field lines should allocate in proportion to the count")]
    public void CombineFieldValue_OnManyRepeatedListFieldLines_ShouldAllocateLinearly()
    {
        // Arrange — the way every transport combines a repeated field line. Copying the combined value on
        // every repeat allocates about 10 GB for this many; amortized constant-time appends about 1 MB.
        const int repeats = 50_000;
        HttpHeaderValue line = new("*/*");
        HttpHeaderValue combined = line;

        // Act
        long before = System.GC.GetAllocatedBytesForCurrentThread();

        for (int index = 1; index < repeats; index++)
        {
            combined = HttpFieldNormalization.CombineFieldValue(HttpHeaderKey.Accept, combined, line);
        }

        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        combined.Count.ShouldBe(repeats);
        allocated.ShouldBeLessThan(16L * 1024 * 1024);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: An empty :protocol is an extended CONNECT violation on every method")]
    [InlineData("CONNECT")]
    [InlineData("GET")]
    public void ValidateExtendedConnect_OnEmptyProtocol_ShouldReturnViolation(string method)
    {
        // Arrange — RFC 9110 §5.6.2: a protocol name is a token (1*tchar), so "" names no protocol.

        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect(method, "https", "/chat", "api.test", string.Empty);

        // Assert
        violation.ShouldNotBeNull();
        violation.ShouldContain("':protocol'");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: An absent :protocol is not validated")]
    public void ValidateExtendedConnect_OnAbsentProtocol_ShouldReturnNull()
    {
        // Arrange — a classic CONNECT carries only :method and :authority (RFC 9113 §8.5).

        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect("CONNECT", null, null, "api.test:443", null);

        // Assert
        violation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A complete extended CONNECT is valid")]
    public void ValidateExtendedConnect_OnCompleteExtendedConnect_ShouldReturnNull()
    {
        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect("CONNECT", "https", "/chat", "api.test", "websocket");

        // Assert
        violation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A :protocol on a method other than CONNECT is a violation")]
    public void ValidateExtendedConnect_OnProtocolWithoutConnect_ShouldReturnViolation()
    {
        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect("GET", "https", "/", "api.test", "websocket");

        // Assert
        violation.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A classic CONNECT must carry a host and port in :authority and neither :scheme nor :path")]
    [InlineData(null, null, null, "':authority'")]
    [InlineData(null, null, "", "':authority'")]
    [InlineData(null, null, "upstream.test", "host and port")]
    [InlineData(null, null, "upstream.test:", "host and port")]
    [InlineData(null, null, "[::1]", "host and port")]
    [InlineData("https", null, "upstream.test:443", "':scheme'")]
    [InlineData(null, "/", "upstream.test:443", "':path'")]
    public void ValidateRequestTarget_OnMalformedClassicConnect_ShouldReturnViolation(string? scheme, string? path, string? authority, string expected)
    {
        // Arrange — RFC 9113 §8.5 / RFC 9114 §4.4 (#1334).

        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget("CONNECT", scheme, path, authority, protocol: null, new HttpHeaderCollection());

        // Assert
        violation.ShouldNotBeNull();
        violation.ShouldContain(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A classic CONNECT with a host and port is a valid request target")]
    [InlineData("upstream.test:443")]
    [InlineData("10.0.0.1:8080")]
    [InlineData("[::1]:8443")]
    public void ValidateRequestTarget_OnClassicConnectWithHostAndPort_ShouldReturnNull(string authority)
    {
        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget("CONNECT", null, null, authority, protocol: null, new HttpHeaderCollection());

        // Assert
        violation.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A CONNECT :authority that is not host:port should be refused without quoting the peer's text")]
    [InlineData("upstream.test:44x3", "44x3")]
    [InlineData("upstream.test:<script>", "<script>")]
    [InlineData("[::1]:8x", "8x")]
    public void ValidateRequestTarget_OnMalformedConnectAuthority_ShouldNotQuoteThePeerText(string authority, string peerText)
    {
        // Arrange — the violation reaches a stream error's message and from there the logs, so it names the
        // rule and never the attacker-chosen value, as the other request-target violations do.

        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget("CONNECT", null, null, authority, protocol: null, new HttpHeaderCollection());

        // Assert
        violation.ShouldNotBeNull();
        violation.ShouldContain("host and port");
        violation.ShouldNotContain(peerText);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A :method that is not a token is not a valid request")]
    [InlineData("G T")]
    [InlineData("GETé")]
    [InlineData("G@T")]
    [InlineData("GET\u0085")]
    public void ValidateRequestTarget_OnMethodThatIsNotAToken_ShouldReturnViolation(string method)
    {
        // Arrange — RFC 9110 §9.1: a method is a token. HttpMethod refuses anything else by throwing, which
        // the transports did not expect from a request head.

        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget(method, "https", "/", "api.test", protocol: null, new HttpHeaderCollection());

        // Assert
        violation.ShouldNotBeNull();
        violation.ShouldContain("':method'");
        violation.ShouldNotContain(method);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: An empty :method is not a valid request")]
    public void ValidateRequestTarget_OnEmptyMethod_ShouldReturnViolation()
    {
        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget(string.Empty, "https", "/", "api.test", protocol: null, new HttpHeaderCollection());

        // Assert
        violation.ShouldNotBeNull();
        violation.ShouldContain("':method'");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A :method longer than HttpMethod carries is refused, and one of 32 characters is not")]
    [InlineData(32, false)]
    [InlineData(33, true)]
    [InlineData(4096, true)]
    public void ValidateRequestTarget_OnMethodLength_ShouldRefuseOnlyWhatHttpMethodCannotCarry(int length, bool refused)
    {
        // Arrange — HttpMethod holds at most 32 characters; GetCanonicalizedValue throws past that.
        string method = new('M', length);

        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget(method, "https", "/", "api.test", protocol: null, new HttpHeaderCollection());

        // Assert
        if (refused)
        {
            violation.ShouldNotBeNull();
            violation.ShouldContain("':method'");
        }
        else
        {
            violation.ShouldBeNull();
            HttpMethod.GetCanonicalizedValue(method).Value.ShouldBe(method);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: Asterisk-form is exactly '*' and only for OPTIONS")]
    [InlineData("GET", "*")]
    [InlineData("CONNECT", "*")]
    [InlineData("GET", "*foo")]
    [InlineData("OPTIONS", "*foo")]
    [InlineData("OPTIONS", "*?x=1")]
    public void ValidateRequestTarget_OnAsteriskFormOutsideOptions_ShouldReturnViolation(string method, string path)
    {
        // Arrange — RFC 9110 §7.1, RFC 9112 §3.2.4. CONNECT here is an extended CONNECT, which carries a :path.

        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget(method, "https", path, "api.test", method == "CONNECT" ? "websocket" : null, new HttpHeaderCollection());

        // Assert
        violation.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: Ordinary targets, OPTIONS '*' and an extended CONNECT are valid request targets")]
    [InlineData("OPTIONS", "https", "*", "api.test", null)]
    [InlineData("OPTIONS", "https", "/", "api.test", null)]
    [InlineData("GET", "https", "/a*b", "api.test", null)]
    [InlineData("CONNECT", "https", "/chat", "api.test", "websocket")]
    public void ValidateRequestTarget_OnWellFormedTarget_ShouldReturnNull(string method, string scheme, string path, string authority, string? protocol)
    {
        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget(method, scheme, path, authority, protocol, new HttpHeaderCollection());

        // Assert
        violation.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A :path, :authority or Host with an octet other than VCHAR is not a valid request target")]
    [InlineData("GET", "/adminÿx", "api.test", null, "':path'")]
    [InlineData("GET", "/café", "api.test", null, "':path'")]
    [InlineData("GET", "/a b", "api.test", null, "':path'")]
    [InlineData("GET", "/", "api.test ", null, "':authority'")]
    [InlineData("GET", "/", "api.tést", null, "':authority'")]
    [InlineData("CONNECT", null, "upstream.tést:443", null, "':authority'")]
    [InlineData("GET", "/", null, "api.test ", "'host'")]
    [InlineData("GET", "/", "api.test", "\u0085api.test", "'host'")]
    public void ValidateRequestTarget_OnNonVisibleOctet_ShouldReturnViolation(string method, string? path, string? authority, string? host, string expected)
    {
        // Arrange — HPACK and QPACK keep obs-text (#1387), and HTTP/1.1 refuses it in its request line and Host (#1341).
        HttpHeaderCollection headers = new();

        if (host is not null)
        {
            headers[HttpHeaderKey.Host] = host;
        }

        // Act
        string? violation = HttpFieldNormalization.ValidateRequestTarget(method, method == "CONNECT" ? null : "https", path, authority, protocol: null, headers);

        // Assert
        violation.ShouldNotBeNull();
        violation.ShouldContain(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: Only a CONNECT with a non-empty :protocol is an extended CONNECT")]
    [InlineData("CONNECT", "websocket", true)]
    [InlineData("CONNECT", "", false)]
    [InlineData("CONNECT", null, false)]
    [InlineData("GET", "websocket", false)]
    [InlineData("GET", "", false)]
    [InlineData(null, "websocket", false)]
    public void IsExtendedConnect_ShouldRequireConnectAndNonEmptyProtocol(string? method, string? protocol, bool expected)
    {
        // Act
        bool isExtendedConnect = HttpFieldNormalization.IsExtendedConnect(method, protocol);

        // Assert
        isExtendedConnect.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A field name is valid only when it is a token")]
    [InlineData("x-trace", true)]
    [InlineData("Content-Type", true)]
    [InlineData("!#$%&'*+-.^_`|~09AZaz", true)] // every tchar class (RFC 9110 §5.6.2)
    [InlineData("", false)]                     // a token is 1*tchar
    [InlineData("X-Trace ", false)]             // whitespace before the colon
    [InlineData(" X-Trace", false)]             // obsolete line folding
    [InlineData("X\tTrace", false)]
    [InlineData(":path", false)]                // a pseudo-header is not a field name
    [InlineData("x:y", false)]
    [InlineData("x\r", false)]
    [InlineData("x\ny", false)]
    [InlineData("x\u0000", false)]
    [InlineData("(x)", false)]                  // a delimiter
    [InlineData("café", false)]            // outside US-ASCII
    public void IsValidFieldName_ShouldAcceptOnlyTokens(string name, bool expected)
    {
        // Act
        bool isValid = HttpFieldNormalization.IsValidFieldName(name);

        // Assert
        isValid.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A field value is valid without NUL, CR, LF, or whitespace at either end")]
    [InlineData("", true)]                            // an empty value
    [InlineData("abc", true)]
    [InlineData("a b\tc", true)]                      // SP and HTAB between visible characters
    [InlineData(" abc ", true)]             // a no-break space is obs-text, not whitespace
    [InlineData("café", true)]                   // obs-text (RFC 9110 §5.5)
    [InlineData("a\u000Bb", true)]                    // another CTL is IndexOfInvalidControlCharacter's rule
    [InlineData(" abc", false)]                       // RFC 9113 §8.2.1 / RFC 9114 §10.3
    [InlineData("abc ", false)]
    [InlineData("\tabc", false)]
    [InlineData("abc\t", false)]
    [InlineData(" ", false)]
    [InlineData("a\u0000b", false)]                   // RFC 9110 §5.5
    [InlineData("a\rb", false)]
    [InlineData("a\nb", false)]
    [InlineData("abc\r\nSet-Cookie: x=1", false)]     // response splitting
    [InlineData("\u0000", false)]
    public void IsValidFieldValue_ShouldRejectNulCrLfAndBoundaryWhitespace(string value, bool expected)
    {
        // Act
        bool isValid = HttpFieldNormalization.IsValidFieldValue(value);

        // Assert
        isValid.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: The first control character other than HTAB should be found")]
    [InlineData("", -1)]
    [InlineData("abc", -1)]
    [InlineData("a\tb", -1)]          // HTAB is allowed inside a value
    [InlineData(" abc ", -1)]         // boundary whitespace is not judged here
    [InlineData(" ÿ", -1)]  // obs-text
    [InlineData("\u0085", -1)]        // a C1 control is an obs-text octet, not a CTL
    [InlineData("\u0000", 0)]
    [InlineData("a\u0001b", 1)]
    [InlineData("ab\u0008", 2)]
    [InlineData("ab\n", 2)]
    [InlineData("a\u000Bb", 1)]       // vertical tab
    [InlineData("a\u000Cb", 1)]       // form feed
    [InlineData("abc\r", 3)]
    [InlineData("a\u001Fb", 1)]
    [InlineData("a\u007Fb", 1)]       // DEL
    [InlineData("a\tb\u0000c\rd", 3)] // the first one wins
    public void IndexOfInvalidControlCharacter_ShouldFindTheFirstControlOtherThanTab(string value, int expected)
    {
        // Act
        int index = HttpFieldNormalization.IndexOfInvalidControlCharacter(value);

        // Assert
        index.ShouldBe(expected);
    }
}
