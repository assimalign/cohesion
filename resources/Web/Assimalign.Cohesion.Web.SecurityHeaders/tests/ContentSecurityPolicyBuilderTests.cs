using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// The Content Security Policy builder: serialization order and form, replacement in place, source
/// normalization, and the grammar checks that reject a policy that would not parse the way it reads.
/// </summary>
public class ContentSecurityPolicyBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: Should serialize directives in the order they were set")]
    public void Create_StrictPolicy_ShouldSerializeInOrder()
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp
            .DefaultSrc(sources => sources.Self())
            .ScriptSrc(sources => sources.Self().Nonce().StrictDynamic().UnsafeInline())
            .StyleSrc(sources => sources.Self().Host("https://fonts.googleapis.com"))
            .ImgSrc(sources => sources.Self().Scheme("data").Scheme("https:"))
            .ObjectSrc(sources => sources.None())
            .BaseUri(sources => sources.None())
            .FormAction(sources => sources.Self())
            .UpgradeInsecureRequests()
            .ReportTo("csp-endpoint")
            .ReportUri("/csp-reports"));

        // Assert
        policy.ToString().ShouldBe(
            "default-src 'self'; script-src 'self' 'nonce-{nonce}' 'strict-dynamic' 'unsafe-inline'; " +
            "style-src 'self' https://fonts.googleapis.com; img-src 'self' data: https:; object-src 'none'; " +
            "base-uri 'none'; form-action 'self'; upgrade-insecure-requests; report-to csp-endpoint; report-uri /csp-reports");
        policy.UsesNonce.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: Every keyword source should serialize with its quotes")]
    public void Create_KeywordSources_ShouldSerializeQuoted()
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Self().UnsafeInline().UnsafeEval().UnsafeHashes().StrictDynamic().ReportSample().WasmUnsafeEval()));

        // Assert
        policy.ToString().ShouldBe(
            "script-src 'self' 'unsafe-inline' 'unsafe-eval' 'unsafe-hashes' 'strict-dynamic' 'report-sample' 'wasm-unsafe-eval'");
        policy.UsesNonce.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: Every fetch directive should serialize under its CSP name")]
    public void Create_FetchDirectives_ShouldUseTheirCspNames()
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp
            .ChildSrc(sources => sources.Self())
            .ConnectSrc(sources => sources.Self())
            .FontSrc(sources => sources.Self())
            .FrameSrc(sources => sources.Self())
            .ManifestSrc(sources => sources.Self())
            .MediaSrc(sources => sources.Self())
            .ScriptSrcAttr(sources => sources.None())
            .ScriptSrcElem(sources => sources.Self())
            .StyleSrcAttr(sources => sources.None())
            .StyleSrcElem(sources => sources.Self())
            .WorkerSrc(sources => sources.Self()));

        // Assert
        policy.ToString().ShouldBe(
            "child-src 'self'; connect-src 'self'; font-src 'self'; frame-src 'self'; manifest-src 'self'; media-src 'self'; " +
            "script-src-attr 'none'; script-src-elem 'self'; style-src-attr 'none'; style-src-elem 'self'; worker-src 'self'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: Setting a directive again should replace it in place")]
    public void Create_DirectiveSetTwice_ShouldReplaceInPlace()
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp
            .DefaultSrc(sources => sources.Self())
            .ScriptSrc(sources => sources.Self())
            .DefaultSrc(sources => sources.None()));

        // Assert
        policy.ToString().ShouldBe("default-src 'none'; script-src 'self'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A source added twice should serialize once")]
    public void Create_DuplicateSources_ShouldSerializeOnce()
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp
            .ScriptSrc(sources => sources.Self().Nonce().Self().Nonce().Host("https://cdn.example.com").Host("https://cdn.example.com")));

        // Assert
        policy.ToString().ShouldBe("script-src 'self' 'nonce-{nonce}' https://cdn.example.com");
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A scheme source should be normalized to lowercase with its colon")]
    [InlineData("https", "https:")]
    [InlineData("HTTPS:", "https:")]
    [InlineData("data", "data:")]
    [InlineData("blob:", "blob:")]
    [InlineData("web+app", "web+app:")]
    public void Scheme_ValidScheme_ShouldNormalize(string scheme, string expected)
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp.ImgSrc(sources => sources.Scheme(scheme)));

        // Assert
        policy.ToString().ShouldBe($"img-src {expected}");
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: An invalid scheme should be rejected")]
    [InlineData("")]
    [InlineData(":")]
    [InlineData("1https")]
    [InlineData("ht tps")]
    [InlineData("https://")]
    public void Scheme_InvalidScheme_ShouldThrow(string scheme)
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ImgSrc(sources => sources.Scheme(scheme)));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A valid host source should be accepted as written")]
    [InlineData("example.com")]
    [InlineData("*.example.com")]
    [InlineData("*")]
    [InlineData("https://cdn.example.com")]
    [InlineData("https://cdn.example.com:8443")]
    [InlineData("wss://*.example.com:*")]
    [InlineData("https://example.com/scripts/")]
    [InlineData("https://example.com/a%2Cb/app.js")]
    [InlineData("example.com.")]
    [InlineData("192.0.2.10:8080")]
    public void Host_ValidSource_ShouldBeAccepted(string source)
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources.Host(source)));

        // Assert
        policy.ToString().ShouldBe($"script-src {source}");
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A host source that could split or rewrite the policy should be rejected")]
    [InlineData("")]
    [InlineData("example.com; script-src *")]
    [InlineData("example.com, default-src *")]
    [InlineData("example.com example.org")]
    [InlineData("example.com\r\nX-Injected: 1")]
    [InlineData("'self'")]
    [InlineData("self")]
    [InlineData("UNSAFE-INLINE")]
    [InlineData("https:")]
    [InlineData("https://")]
    [InlineData("exa_mple.com")]
    [InlineData("example..com")]
    [InlineData("example.com:port")]
    [InlineData("example.com/a;b")]
    [InlineData("example.com/%zz")]
    [InlineData("https://example.com/?q=1")]
    [InlineData("[::1]")]
    [InlineData("exämple.com")]
    public void Host_InvalidSource_ShouldThrow(string source)
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources.Host(source)));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A hash source should serialize with its algorithm")]
    public void Hash_ValidDigest_ShouldSerialize()
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources
            .Hash(ContentSecurityPolicyHashAlgorithm.Sha256, "RFWPLDbv2BY+rCkDzsE+0fr8ylGr2R2faWMhq4lfEQc=")
            .Hash(ContentSecurityPolicyHashAlgorithm.Sha384, "abc_-")
            .Hash(ContentSecurityPolicyHashAlgorithm.Sha512, "Zm9v")));

        // Assert
        policy.ToString().ShouldBe(
            "script-src 'sha256-RFWPLDbv2BY+rCkDzsE+0fr8ylGr2R2faWMhq4lfEQc=' 'sha384-abc_-' 'sha512-Zm9v'");
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A digest outside the base64 grammar should be rejected")]
    [InlineData("")]
    [InlineData("abc===")]
    [InlineData("ab'c")]
    [InlineData("abc def")]
    [InlineData("=abc")]
    public void Hash_InvalidDigest_ShouldThrow(string digest)
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources
            .Hash(ContentSecurityPolicyHashAlgorithm.Sha256, digest)));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: An undefined hash algorithm should be rejected")]
    public void Hash_UndefinedAlgorithm_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources
            .Hash((ContentSecurityPolicyHashAlgorithm)9, "Zm9v")));

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: 'none' beside another source should be rejected")]
    public void None_WithOtherSources_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources.None().Self()));

        // Assert
        act.ShouldThrow<ArgumentException>().Message.ShouldContain("'none'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: An empty source list should be rejected")]
    public void SourceList_Empty_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => { }));

        // Assert
        act.ShouldThrow<ArgumentException>().Message.ShouldContain("None()");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A policy with no directive should be rejected")]
    public void Build_NoDirective_ShouldThrow()
    {
        // Arrange
        ContentSecurityPolicyBuilder builder = new();

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: Sandbox should serialize its lowercased, distinct tokens")]
    public void Sandbox_Tokens_ShouldSerialize()
    {
        // Arrange & Act
        ContentSecurityPolicy withTokens = ContentSecurityPolicy.Create(csp => csp.Sandbox("allow-scripts", "ALLOW-FORMS", "allow-scripts"));
        ContentSecurityPolicy bare = ContentSecurityPolicy.Create(csp => csp.Sandbox());

        // Assert
        withTokens.ToString().ShouldBe("sandbox allow-scripts allow-forms");
        bare.ToString().ShouldBe("sandbox");
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A sandbox keyword outside the token grammar should be rejected")]
    [InlineData("allow scripts")]
    [InlineData("allow-scripts;")]
    [InlineData("")]
    public void Sandbox_InvalidToken_ShouldThrow(string token)
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.Sandbox(token));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: An invalid reporting target should be rejected")]
    [InlineData("csp endpoint")]
    [InlineData("csp;endpoint")]
    [InlineData("")]
    public void ReportTo_InvalidEndpointName_ShouldThrow(string endpointName)
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ReportTo(endpointName));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: A report URL that could split the policy should be rejected")]
    [InlineData("/reports; script-src *")]
    [InlineData("/reports,/other")]
    [InlineData("/re ports")]
    [InlineData("")]
    public void ReportUri_InvalidUri_ShouldThrow(string uri)
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ReportUri(uri));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: report-uri should require at least one URL")]
    public void ReportUri_NoUri_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.ReportUri());

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: An unmodeled directive should serialize through the escape hatch")]
    public void Directive_Unmodeled_ShouldSerialize()
    {
        // Arrange & Act
        ContentSecurityPolicy policy = ContentSecurityPolicy.Create(csp => csp
            .Directive("Require-Trusted-Types-For", "'script'")
            .Directive("block-all-mixed-content"));

        // Assert
        policy.ToString().ShouldBe("require-trusted-types-for 'script'; block-all-mixed-content");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: frame-ancestors should be refused, because framing owns it")]
    public void Directive_FrameAncestors_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.Directive("frame-ancestors", "'self'"));

        // Assert
        act.ShouldThrow<ArgumentException>().Message.ShouldContain("Framing");
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: An escape-hatch value or name outside the CSP grammar should be rejected")]
    [InlineData("trusted-types", "a; script-src *")]
    [InlineData("trusted-types", "a, b")]
    [InlineData("trusted-types", "a\r\nX-Injected: 1")]
    [InlineData("trusted-types", "pölicy")]
    [InlineData("trusted types", "a")]
    [InlineData("trusted_types", "a")]
    public void Directive_InvalidNameOrValue_ShouldThrow(string name, string value)
    {
        // Arrange & Act
        Action act = () => ContentSecurityPolicy.Create(csp => csp.Directive(name, value));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - CSP: Null callbacks and arguments should throw ArgumentNullException")]
    public void Create_NullArguments_ShouldThrow()
    {
        // Arrange & Act
        Action create = () => ContentSecurityPolicy.Create(null!);
        Action sourceList = () => ContentSecurityPolicy.Create(csp => csp.ScriptSrc(null!));
        Action host = () => ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources.Host(null!)));
        Action directive = () => ContentSecurityPolicy.Create(csp => csp.Directive(null!));

        // Assert
        create.ShouldThrow<ArgumentNullException>();
        sourceList.ShouldThrow<ArgumentNullException>();
        host.ShouldThrow<ArgumentNullException>();
        directive.ShouldThrow<ArgumentNullException>();
    }
}
