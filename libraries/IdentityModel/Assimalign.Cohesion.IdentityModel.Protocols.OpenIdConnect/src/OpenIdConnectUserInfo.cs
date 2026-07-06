using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents a UserInfo response (OpenID Connect Core §5.3). Subject presence and the
/// ID-token subject match are <see cref="Validate" /> diagnostics, not guards, so
/// non-conformant responses stay diagnosable.
/// </summary>
public sealed class OpenIdConnectUserInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectUserInfo" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The response contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when an extension claim's type collides with the subject claim, or a claims
    /// source entry is null.
    /// </exception>
    public OpenIdConnectUserInfo(OpenIdConnectUserInfoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Subject = descriptor.Subject;
        Issuer = descriptor.Issuer;
        RawDocument = descriptor.RawDocument;
        ClaimsSources = SnapshotSources(descriptor.ClaimsSources);
        Claims = BuildClaims(descriptor);
    }

    /// <summary>
    /// Gets the subject (<c>sub</c>), as the raw wire string.
    /// </summary>
    public string? Subject { get; }

    /// <summary>
    /// Gets the issuer the response was obtained from.
    /// </summary>
    public string? Issuer { get; }

    /// <summary>
    /// Gets every claim the response asserts, with OpenID Connect provenance.
    /// </summary>
    public IIdentityClaimCollection Claims { get; }

    /// <summary>
    /// Gets the unresolved aggregated and distributed claims references. They never
    /// appear in <see cref="Claims" />.
    /// </summary>
    public IReadOnlyList<OpenIdConnectClaimsSource> ClaimsSources { get; }

    /// <summary>
    /// Gets the as-received response document, when retained.
    /// </summary>
    public string? RawDocument { get; }

    /// <summary>
    /// Validates the response per Core §5.3.2: the subject must be present, and when the
    /// caller supplies the ID token's subject the two must match exactly — claims must
    /// not be used otherwise.
    /// </summary>
    /// <param name="expectedSubject">The ID token's subject, when correlating.</param>
    /// <returns>The validation findings.</returns>
    public ProtocolValidationResult Validate(string? expectedSubject = null)
    {
        var diagnostics = new List<ProtocolValidationDiagnostic>();

        if (Subject is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.MissingRequiredMember,
                "A UserInfo response must carry the sub claim.",
                member: IdentityClaimTypes.Subject));
        }
        else if (expectedSubject is not null &&
            !string.Equals(Subject, expectedSubject, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.SubjectMismatch,
                "The UserInfo subject does not match the ID token's subject.",
                member: IdentityClaimTypes.Subject));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private static IReadOnlyList<OpenIdConnectClaimsSource> SnapshotSources(IList<OpenIdConnectClaimsSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            return Array.Empty<OpenIdConnectClaimsSource>();
        }

        var snapshot = new OpenIdConnectClaimsSource[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            snapshot[index] = sources[index]
                ?? throw new IdentityModelException("Claims source lists must not contain null entries.");
        }

        return new ReadOnlyCollection<OpenIdConnectClaimsSource>(snapshot);
    }

    private static IIdentityClaimCollection BuildClaims(OpenIdConnectUserInfoDescriptor descriptor)
    {
        var provenance = new IdentityClaimProvenance(
            AuthenticationProtocol.OpenIdConnect,
            originalIssuer: descriptor.Issuer);

        var claims = new List<IIdentityClaim>();

        if (descriptor.Subject is not null)
        {
            claims.Add(new IdentityClaim(IdentityClaimTypes.Subject, descriptor.Subject, descriptor.Issuer, provenance));
        }

        foreach (var claim in descriptor.AdditionalClaims)
        {
            ArgumentNullException.ThrowIfNull(claim, nameof(descriptor));

            if (string.Equals(claim.Type, IdentityClaimTypes.Subject, StringComparison.Ordinal))
            {
                throw new IdentityModelException(
                    "The extension claim 'sub' collides with the typed Subject member. Set the typed member instead.");
            }

            claims.Add(claim);
        }

        return new IdentityClaimCollection(claims);
    }
}
