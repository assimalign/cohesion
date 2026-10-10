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
