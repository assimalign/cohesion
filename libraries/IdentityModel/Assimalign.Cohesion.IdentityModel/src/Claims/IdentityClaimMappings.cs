using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Defines the default cross-protocol claim-name mappings: wire claim and attribute names
/// (LDAP/X.500 OID URNs, WS-Federation claim URIs) that are <em>strictly equivalent</em> to a
/// canonical claim type in <see cref="IdentityClaimTypes" />. OpenID Connect standard claims
/// need no mapping — the canonical vocabulary is the IANA JWT names they already use.
/// </summary>
/// <remarks>
/// <para>
/// The table holds only strict equivalences — profile-attested identity data whose meaning is
/// the same on both sides. Identifier-shaped attributes are deliberately excluded so subject
/// identity flows only through the family's pinned NameID recipes, never the mapper: no entry
/// targets <c>sub</c> (or any structural claim), and <c>nameidentifier</c>,
/// <c>eduPersonPrincipalName</c> (reassignable per eduPerson, so never a <c>sub</c>),
/// <c>eduPersonTargetedID</c>, and <c>upn</c> stay unmapped. Ambiguous or vendor-shaped names
/// are likewise excluded — the WS-Federation <c>…/claims/name</c> URI (often a UPN or account
/// name in practice, not a display name), LDAP <c>cn</c>, vendor role/group URIs, and
/// <c>eduPersonAffiliation</c> (an affiliation vocabulary, not group membership). Deployments
/// that know their provider's semantics opt in through
/// <see cref="IdentityClaimMapperDescriptor.CustomMappings" />.
/// </para>
/// <para>
/// Keys and targets compare ordinally. Bare basic-format attribute names (<c>mail</c>,
/// <c>givenName</c>) are excluded from the defaults — they collide with the open extension-claim
/// namespace and are custom-mapping territory.
/// </para>
/// </remarks>
public static class IdentityClaimMappings
{
    /// <summary>
    /// Gets the default wire-name to canonical-type mappings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Default { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Email address: RFC 4524 mail, PKCS#9 emailAddress, WS-Federation emailaddress.
        ["urn:oid:0.9.2342.19200300.100.1.3"] = IdentityClaimTypes.Email,
        ["urn:oid:1.2.840.113549.1.9.1"] = IdentityClaimTypes.Email,
        ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"] = IdentityClaimTypes.Email,

        // Given (first) name: X.520 givenName, WS-Federation givenname.
        ["urn:oid:2.5.4.42"] = IdentityClaimTypes.GivenName,
        ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname"] = IdentityClaimTypes.GivenName,

        // Family (last) name: X.520 surname, WS-Federation surname.
        ["urn:oid:2.5.4.4"] = IdentityClaimTypes.FamilyName,
        ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname"] = IdentityClaimTypes.FamilyName,

        // Full display name: inetOrgPerson displayName. (LDAP cn and the WS-Federation
        // …/claims/name URI are deliberately excluded — both are ambiguous in practice.)
        ["urn:oid:2.16.840.1.113730.3.1.241"] = IdentityClaimTypes.Name,

        // Phone number: X.520 telephoneNumber. Values pass through byte-identical — the
        // mapper never reformats (no E.164 coercion).
        ["urn:oid:2.5.4.20"] = IdentityClaimTypes.PhoneNumber,

        // End-user locale: inetOrgPerson preferredLanguage.
        ["urn:oid:2.16.840.1.113730.3.1.39"] = IdentityClaimTypes.Locale,
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
