using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpAltServiceTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should format an h3 alt-value with same-host authority and ma")]
    public void Format_OnHttp3WithMaxAge_ShouldEmitSameHostAltValue()
    {
        // Arrange
        HttpAltService service = HttpAltService.Http3(host: null, port: 443, maxAgeSeconds: 86400);

        // Act
        string formatted = service.Format();

        // Assert
        formatted.ShouldBe("h3=\":443\"; ma=86400");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should format an explicit host, port, ma and persist")]
    public void Format_OnExplicitHostWithPersist_ShouldEmitFullAltValue()
    {
        // Arrange
        HttpAltService service = new("h3", "alt.example.com", 8443, maxAgeSeconds: 3600, persist: true);

        // Act
        string formatted = service.Format();

        // Assert
        formatted.ShouldBe("h3=\"alt.example.com:8443\"; ma=3600; persist=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should omit ma when max-age is absent")]
    public void Format_OnNoMaxAge_ShouldOmitMaParameter()
    {
        // Arrange
        HttpAltService service = HttpAltService.Http3(host: null, port: 443);

        // Act
        string formatted = service.Format();

        // Assert
        formatted.ShouldBe("h3=\":443\"");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpAltService: Should round-trip a single alt-value")]
    [InlineData("h3=\":443\"; ma=86400")]
    [InlineData("h3=\":443\"")]
    [InlineData("h2=\"alt.example.com:8000\"; ma=3600; persist=1")]
    [InlineData("h3=\"[::1]:8443\"; ma=60")]
    public void TryParse_Then_Format_ShouldRoundTrip(string altValue)
    {
        // Act
        bool parsed = HttpAltService.TryParse(altValue, out HttpAltService service);

        // Assert
        parsed.ShouldBeTrue();
        service.Format().ShouldBe(altValue);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should parse the alt-authority host, port, ma and persist")]
    public void TryParse_OnFullAltValue_ShouldExposeTypedMembers()
    {
        // Act
        bool parsed = HttpAltService.TryParse("h3=\"alt.example.com:8443\"; ma=3600; persist=1", out HttpAltService service);

        // Assert
        parsed.ShouldBeTrue();
        service.ProtocolId.ShouldBe("h3");
        service.Host.ShouldBe("alt.example.com");
        service.Port.ShouldBe(8443);
        service.MaxAgeSeconds.ShouldBe(3600);
        service.Persist.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should treat an empty alt-authority host as same-host")]
    public void TryParse_OnEmptyHost_ShouldYieldNullHost()
    {
        // Act
        bool parsed = HttpAltService.TryParse("h3=\":443\"", out HttpAltService service);

        // Assert
        parsed.ShouldBeTrue();
        service.Host.ShouldBeNull();
        service.Port.ShouldBe(443);
        service.MaxAgeSeconds.ShouldBeNull();
        service.Persist.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should ignore unrecognized parameters")]
    public void TryParse_OnUnknownParameter_ShouldIgnoreIt()
    {
        // Act
        bool parsed = HttpAltService.TryParse("h3=\":443\"; ma=60; foo=bar", out HttpAltService service);

        // Assert
        parsed.ShouldBeTrue();
        service.Port.ShouldBe(443);
        service.MaxAgeSeconds.ShouldBe(60);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpAltService: Should reject malformed alt-values")]
    [InlineData("")]
    [InlineData("h3")]                    // no '=' and no authority
    [InlineData("h3=:443")]               // alt-authority not quoted
    [InlineData("h3=\"443\"")]            // missing ':' — no port
    [InlineData("h3=\":\"")]              // empty port
    [InlineData("h3=\":notaport\"")]      // non-numeric port
    [InlineData("=\":443\"")]             // empty protocol-id
    [InlineData("h3=\":443\" ma=60")]     // missing ';' before parameter
    public void TryParse_OnMalformedInput_ShouldReturnFalse(string altValue)
    {
        // Act
        bool parsed = HttpAltService.TryParse(altValue, out _);

        // Assert
        parsed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should parse the case-sensitive clear token")]
    public void TryParseHeader_OnClear_ShouldSignalClear()
    {
        // Act
        bool parsed = HttpAltService.TryParseHeader("clear", out IReadOnlyList<HttpAltService> services, out bool isClear);

        // Assert
        parsed.ShouldBeTrue();
        isClear.ShouldBeTrue();
        services.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should not treat a differently-cased Clear as the clear token")]
    public void TryParseHeader_OnMixedCaseClear_ShouldNotSignalClear()
    {
        // RFC 7838 §3 — "clear" is case-sensitive.
        // Act
        bool parsed = HttpAltService.TryParseHeader("Clear", out _, out bool isClear);

        // Assert
        parsed.ShouldBeFalse();
        isClear.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should parse and round-trip a multi-value header")]
    public void TryParseHeader_OnMultipleAltValues_ShouldRoundTrip()
    {
        // Arrange
        const string header = "h3=\":443\"; ma=3600, h2=\"alt.example.com:8000\"";

        // Act
        bool parsed = HttpAltService.TryParseHeader(header, out IReadOnlyList<HttpAltService> services, out bool isClear);

        // Assert
        parsed.ShouldBeTrue();
        isClear.ShouldBeFalse();
        services.Count.ShouldBe(2);
        services[0].ProtocolId.ShouldBe("h3");
        services[0].Port.ShouldBe(443);
        services[1].ProtocolId.ShouldBe("h2");
        services[1].Host.ShouldBe("alt.example.com");
        services[1].Port.ShouldBe(8000);

        HttpAltService.FormatHeader(services).ShouldBe(header);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should not split on a comma inside the quoted alt-authority")]
    public void TryParseHeader_OnCommaInsideQuotes_ShouldNotSplit()
    {
        // A comma inside the quoted alt-authority is part of the value, not a list separator.
        // Act
        bool parsed = HttpAltService.TryParseHeader("h3=\"weird,host:443\"", out IReadOnlyList<HttpAltService> services, out _);

        // Assert
        parsed.ShouldBeTrue();
        services.Count.ShouldBe(1);
        services[0].Host.ShouldBe("weird,host");
        services[0].Port.ShouldBe(443);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should format an empty list as the empty string")]
    public void FormatHeader_OnEmptyList_ShouldBeEmpty()
    {
        // Act
        string formatted = HttpAltService.FormatHeader(System.Array.Empty<HttpAltService>());

        // Assert
        formatted.ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpAltService: Should compare alternatives by value")]
    public void Equals_OnSameMembers_ShouldBeEqual()
    {
        // Arrange
        HttpAltService left = HttpAltService.Http3(host: null, port: 443, maxAgeSeconds: 60);
        HttpAltService right = HttpAltService.Http3(host: null, port: 443, maxAgeSeconds: 60);

        // Assert
        left.ShouldBe(right);
        (left == right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }
}
