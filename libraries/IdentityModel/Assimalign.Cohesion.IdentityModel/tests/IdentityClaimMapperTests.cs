using System;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the cross-protocol claim mapper: the default-table invariants, the
/// custom-mapping layering (override, suppression, chained fixed points, cycle rejection), the
/// provenance-preservation rules, and idempotency.
/// </summary>
public sealed class IdentityClaimMapperTests
{
    private const string MailOid = "urn:oid:0.9.2342.19200300.100.1.3";
    private const string WsFedEmail = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: No default mapping targets a structural claim")]
    public void DefaultTable_ShouldNeverTargetStructuralClaims()
    {
        // Subject and envelope claims flow only through the pinned recipes, never the mapper.
        string[] structural =
        [
            IdentityClaimTypes.Subject,
            IdentityClaimTypes.Issuer,
            IdentityClaimTypes.Audience,
            IdentityClaimTypes.ExpirationTime,
            IdentityClaimTypes.IssuedAt,
            IdentityClaimTypes.NotBefore,
            IdentityClaimTypes.JwtId,
        ];

        foreach (var (name, target) in IdentityClaimMappings.Default)
        {
            structural.ShouldNotContain(target, $"'{name}' must not map onto a structural claim");
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: A mapped claim is re-typed with its value byte-identical")]
    public void Canonicalize_WhenClaimMapped_ShouldRetypeWithIdenticalValue()
    {
        var claims = Collection(new IdentityClaim(MailOid, "user@example.com"));

        var canonical = claims.Canonicalize();

        canonical.TryGet(IdentityClaimTypes.Email, out var email).ShouldBeTrue();
        email!.Value.AsString().ShouldBe("user@example.com");
        canonical.Contains(MailOid).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: Unmapped claims pass through as the same instances")]
    public void Canonicalize_WhenNothingMaps_ShouldReturnTheInputCollection()
    {
        var claims = Collection(
            new IdentityClaim("custom-claim", "value"),
            new IdentityClaim(IdentityClaimTypes.Email, "user@example.com"));

        var canonical = claims.Canonicalize();

        ReferenceEquals(canonical, claims).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: Canonicalizing twice equals canonicalizing once")]
    public void Canonicalize_WhenAppliedTwice_ShouldBeIdempotent()
    {
        var claims = Collection(
            new IdentityClaim(MailOid, "user@example.com"),
            new IdentityClaim("urn:oid:2.5.4.42", "Ada"));

        var once = claims.Canonicalize();
        var twice = once.Canonicalize();

        ReferenceEquals(twice, once).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: Two wire names for one concept yield duplicate claims")]
    public void Canonicalize_WhenTwoWireNamesMapToOneType_ShouldYieldDuplicateClaims()
    {
        // The canonical multi-value representation: never merged, insertion order preserved,
        // provenance disambiguating the sources.
        var claims = Collection(
            new IdentityClaim(MailOid, "a@example.com"),
            new IdentityClaim(WsFedEmail, "b@example.com"));

        var canonical = claims.Canonicalize();

        canonical.GetAll(IdentityClaimTypes.Email)
            .Select(claim => claim.Value.AsString())
            .ShouldBe(new[] { "a@example.com", "b@example.com" });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: Branch-recorded provenance is preserved verbatim")]
    public void Canonicalize_WhenProvenanceHasOriginalType_ShouldPreserveItVerbatim()
    {
        var provenance = new IdentityClaimProvenance(
            AuthenticationProtocol.Saml2,
            originalType: MailOid,
            originalNameFormat: "uri",
            originalFriendlyName: "mail");
        var claims = Collection(new IdentityClaim(MailOid, "user@example.com", provenance: provenance));

        var canonical = claims.Canonicalize();

        canonical.TryGet(IdentityClaimTypes.Email, out var email).ShouldBeTrue();
        ReferenceEquals(email!.Provenance, provenance).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: A null OriginalType is filled with the pre-mapping name")]
    public void Canonicalize_WhenProvenanceLacksOriginalType_ShouldFillItAndPreserveEveryOtherField()
    {
        var claims = Collection(new IdentityClaim(
            MailOid,
            "user@example.com",
            provenance: new IdentityClaimProvenance(
                AuthenticationProtocol.Saml2,
                originalIssuer: "https://idp",
                originalValueType: "xs:string",
                originalNameFormat: "uri",
                originalFriendlyName: "mail")));

        var canonical = claims.Canonicalize();

        // The fill is lossless: only OriginalType is added; every other field survives.
        canonical.TryGet(IdentityClaimTypes.Email, out var email).ShouldBeTrue();
        email!.Provenance!.OriginalType.ShouldBe(MailOid);
        email.Provenance.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        email.Provenance.OriginalIssuer.ShouldBe("https://idp");
        email.Provenance.OriginalValueType.ShouldBe("xs:string");
        email.Provenance.OriginalNameFormat.ShouldBe("uri");
        email.Provenance.OriginalFriendlyName.ShouldBe("mail");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: A provenance-less claim gains an honest Unknown provenance")]
    public void Canonicalize_WhenClaimHasNoProvenance_ShouldMintUnknownProvenance()
    {
        var claims = Collection(new IdentityClaim(MailOid, "user@example.com"));

        var canonical = claims.Canonicalize();

        canonical.TryGet(IdentityClaimTypes.Email, out var email).ShouldBeTrue();
        email!.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.Unknown);
        email.Provenance.OriginalType.ShouldBe(MailOid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: A custom mapping wins over the default")]
    public void Canonicalize_WhenCustomOverridesDefault_ShouldApplyTheCustom()
    {
        var descriptor = new IdentityClaimMapperDescriptor();
        descriptor.CustomMappings[MailOid] = IdentityClaimTypes.PreferredUsername;
        var mapper = new IdentityClaimMapper(descriptor);

        var canonical = Collection(new IdentityClaim(MailOid, "ada")).Canonicalize(mapper);

        canonical.Contains(IdentityClaimTypes.PreferredUsername).ShouldBeTrue();
        canonical.Contains(IdentityClaimTypes.Email).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: An identity entry suppresses a single default")]
    public void Canonicalize_WhenIdentityEntrySuppressesDefault_ShouldPassThrough()
    {
        var descriptor = new IdentityClaimMapperDescriptor();
        descriptor.CustomMappings[MailOid] = MailOid;
        var mapper = new IdentityClaimMapper(descriptor);

        var claims = Collection(new IdentityClaim(MailOid, "internal-id-123"));
        var canonical = claims.Canonicalize(mapper);

        ReferenceEquals(canonical, claims).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: Chained mappings resolve to their fixed point")]
    public void Canonicalize_WhenMappingsChain_ShouldResolveToFixedPoint()
    {
        // vendor -> urn:oid mail -> email resolves at materialization, so one pass suffices.
        var descriptor = new IdentityClaimMapperDescriptor();
        descriptor.CustomMappings["vendor-mail"] = MailOid;
        var mapper = new IdentityClaimMapper(descriptor);

        var canonical = Collection(new IdentityClaim("vendor-mail", "user@example.com")).Canonicalize(mapper);

        canonical.Contains(IdentityClaimTypes.Email).ShouldBeTrue();
        canonical.Contains(MailOid).ShouldBeFalse();
        canonical.Contains("vendor-mail").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: A cyclic mapping chain fails materialization")]
    public void Construct_WhenMappingChainCyclic_ShouldThrow()
    {
        var descriptor = new IdentityClaimMapperDescriptor { IncludeDefaultMappings = false };
        descriptor.CustomMappings["a"] = "b";
        descriptor.CustomMappings["b"] = "a";

        Should.Throw<IdentityModelException>(() => new IdentityClaimMapper(descriptor));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: A mapping targeting a structural claim fails materialization")]
    public void Construct_WhenMappingTargetsSub_ShouldThrow()
    {
        var descriptor = new IdentityClaimMapperDescriptor();
        descriptor.CustomMappings["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"] =
            IdentityClaimTypes.Subject;

        Should.Throw<IdentityModelException>(() => new IdentityClaimMapper(descriptor));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Mapper: Defaults can be excluded wholesale")]
    public void Canonicalize_WhenDefaultsExcluded_ShouldApplyCustomsOnly()
    {
        var descriptor = new IdentityClaimMapperDescriptor { IncludeDefaultMappings = false };
        descriptor.CustomMappings["vendor-mail"] = IdentityClaimTypes.Email;
        var mapper = new IdentityClaimMapper(descriptor);

        var claims = Collection(
            new IdentityClaim(MailOid, "untouched@example.com"),
            new IdentityClaim("vendor-mail", "mapped@example.com"));
        var canonical = claims.Canonicalize(mapper);

        canonical.Contains(MailOid).ShouldBeTrue();
        canonical.GetString(IdentityClaimTypes.Email).ShouldBe("mapped@example.com");
    }

    private static IIdentityClaimCollection Collection(params IIdentityClaim[] claims)
        => new IdentityClaimCollection(claims);
}
