using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A redirect URI is where an authorization code is sent, so what it may be is a security rule.
/// </summary>
public class IdentityClientRedirectTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept https and loopback http for every platform")]
    [InlineData("https://orders.example/signin")]
    [InlineData("https://orders.example:8443/signin?tenant=a")]
    [InlineData("http://localhost/callback")]
    [InlineData("http://localhost:5173/callback")]
    [InlineData("http://127.0.0.1:7000/callback")]
    [InlineData("http://[::1]:7000/callback")]
    public void Constructor_WithHttpsOrLoopbackHttp_ShouldCreateForEveryPlatform(string uri)
    {
        // Act
        IdentityClientRedirect web = IdentityClientRedirect.Web(uri);
        IdentityClientRedirect singlePage = IdentityClientRedirect.SinglePage(uri);
        IdentityClientRedirect native = IdentityClientRedirect.Native(uri);

        // Assert
        web.Uri.ShouldBe(uri);
        web.Platform.ShouldBe(IdentityClientPlatform.Web);
        singlePage.Platform.ShouldBe(IdentityClientPlatform.SinglePage);
        native.Platform.ShouldBe(IdentityClientPlatform.Native);
        web.ToString().ShouldBe(uri);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse http to a host that is not loopback")]
    [InlineData("http://orders.example/signin")]
    [InlineData("http://loopback/callback")]
    [InlineData("http://192.168.1.10/callback")]
    [InlineData("http://localhost.example/callback")]
    [InlineData("http://localhost./callback")]
    public void Constructor_WithHttpToARemoteHost_ShouldThrowArgumentException(string uri)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClientRedirect.Web(uri));

        // Assert
        exception.ParamName.ShouldBe("uri");
        IdentityClientRedirect.TryCreate(uri, IdentityClientPlatform.Web, out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a wildcard, a fragment, user information, and a relative or malformed URI")]
    [InlineData("")]
    [InlineData("https://*.orders.example/signin")]
    [InlineData("https://orders.example/*")]
    [InlineData("https://orders.example/signin#token")]
    [InlineData("https://user:password@orders.example/signin")]
    [InlineData("/signin")]
    [InlineData("orders.example/signin")]
    [InlineData("https://orders.example/sign in")]
    [InlineData("https://órdenes.example/signin")]
    [InlineData("https:///signin")]
    [InlineData("https:/orders.example/signin")]
    [InlineData("https://@orders.example/signin")]
    [InlineData("https://orders.example\\@attacker.example/signin")]
    [InlineData("https://orders.example/<signin>")]
    [InlineData("https://orders.example/sign|in")]
    public void Constructor_WithAUriOutsideTheRules_ShouldThrowArgumentException(string uri)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClientRedirect.Native(uri));

        // Assert
        exception.ParamName.ShouldBe("uri");
        IdentityClientRedirect.TryCreate(uri, IdentityClientPlatform.Native, out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept a private-use scheme for a native application only")]
    [InlineData("com.example.orders:/callback")]
    [InlineData("com.example.orders://callback")]
    public void Constructor_WithAPrivateUseScheme_ShouldCreateForNativeOnly(string uri)
    {
        // Act
        IdentityClientRedirect native = IdentityClientRedirect.Native(uri);

        // Assert
        native.Uri.ShouldBe(uri);
        Should.Throw<ArgumentException>(() => IdentityClientRedirect.Web(uri));
        Should.Throw<ArgumentException>(() => IdentityClientRedirect.SinglePage(uri));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a scheme that is not a reversed domain name, even for a native application")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("myapp://callback")]
    [InlineData("ftp://orders.example/signin")]
    public void Constructor_WithASchemeThatHoldsNoDot_ShouldThrowArgumentException(string uri)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClientRedirect.Native(uri));

        // Assert
        exception.ParamName.ShouldBe("uri");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a null URI and an undefined platform")]
    public void Constructor_WithANullUriOrAnUndefinedPlatform_ShouldThrow()
    {
        // Act
        ArgumentNullException nullUri = Should.Throw<ArgumentNullException>(() => IdentityClientRedirect.Web(null!));
        ArgumentOutOfRangeException none = Should.Throw<ArgumentOutOfRangeException>(
            () => new IdentityClientRedirect("https://orders.example/signin", IdentityClientPlatform.None));
        ArgumentOutOfRangeException undefined = Should.Throw<ArgumentOutOfRangeException>(
            () => new IdentityClientRedirect("https://orders.example/signin", (IdentityClientPlatform)42));

        // Assert
        nullUri.ParamName.ShouldBe("uri");
        none.ParamName.ShouldBe("platform");
        undefined.ParamName.ShouldBe("platform");
        IdentityClientRedirect.TryCreate(null, IdentityClientPlatform.Web, out _).ShouldBeFalse();
        IdentityClientRedirect.TryCreate("https://orders.example/signin", IdentityClientPlatform.None, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept the longest redirect URI and refuse one longer")]
    public void Constructor_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        const string prefix = "https://orders.example/";
        string longest = prefix + new string('a', IdentityClientRedirect.MaxLength - prefix.Length);
        string tooLong = longest + "a";

        // Act
        bool atLimit = IdentityClientRedirect.TryCreate(longest, IdentityClientPlatform.Web, out IdentityClientRedirect redirect);
        bool beyond = IdentityClientRedirect.TryCreate(tooLong, IdentityClientPlatform.Web, out _);

        // Assert
        IdentityClientRedirect.MaxLength.ShouldBe(512);
        atLimit.ShouldBeTrue();
        redirect.Uri.ShouldBe(longest);
        beyond.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the URI exactly as written")]
    public void Constructor_WithAUriANormalizerWouldChange_ShouldKeepItAsWritten()
    {
        // Arrange: a parser would lower the host's case and drop the default port.
        const string uri = "https://Orders.Example:443/SignIn";

        // Act
        IdentityClientRedirect redirect = IdentityClientRedirect.Web(uri);

        // Assert
        redirect.Uri.ShouldBe(uri);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare the URI exactly and the platform")]
    public void Equals_WithTheSameUriAndPlatform_ShouldBeEqual()
    {
        // Arrange
        IdentityClientRedirect redirect = IdentityClientRedirect.Web("https://orders.example/signin");

        // Act
        bool same = redirect == IdentityClientRedirect.Web("https://orders.example/signin");
        bool otherCase = redirect == IdentityClientRedirect.Web("https://orders.example/SignIn");
        bool otherPlatform = redirect == IdentityClientRedirect.SinglePage("https://orders.example/signin");

        // Assert
        same.ShouldBeTrue();
        otherCase.ShouldBeFalse();
        otherPlatform.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no URI")]
    public void Default_ShouldHoldNoUri()
    {
        // Act
        IdentityClientRedirect redirect = default;

        // Assert
        redirect.Uri.ShouldBeNull();
        redirect.Platform.ShouldBe(IdentityClientPlatform.None);
        redirect.ToString().ShouldBeEmpty();
    }
}
