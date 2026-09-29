using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Provides the shared immutable base for identity token formats, normalized onto the root
/// canonical model. Concrete token packages (JWT, SAML) derive from this base and pin their
/// document format through the constructor.
/// </summary>
/// <remarks>
/// The base snapshots the descriptor defensively at construction: audiences and properties
/// are copied, and claims are materialized into an immutable root
/// <see cref="IdentityClaimCollection" />. It does not access the root's internal
/// materialization helper (the token branch is not granted that visibility), so it reproduces
/// the fail-closed property rule — an undefined property value is rejected — inline.
/// </remarks>
public abstract class IdentityToken : IIdentityToken
{
    private readonly string[] _audiences;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityToken" /> class by snapshotting
    /// the provided descriptor.
    /// </summary>
    /// <param name="kind">The token document format, supplied by the derived type.</param>
    /// <param name="descriptor">The normalized token contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when an audience entry is null or whitespace, when a claim entry is null, or
    /// when a property name is blank or a property value is undefined.
    /// </exception>
    protected IdentityToken(IdentityTokenKind kind, IdentityTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = kind;
        Protocol = descriptor.Protocol;
        Id = descriptor.Id;
        Subject = descriptor.Subject;
        Issuer = descriptor.Issuer;
        TokenType = descriptor.TokenType;
        RawData = descriptor.RawData;
        IssuedAt = descriptor.IssuedAt;
        NotBefore = descriptor.NotBefore;
        ExpiresAt = descriptor.ExpiresAt;
        AuthenticationContext = descriptor.AuthenticationContext;

        _audiences = CopyAudiences(descriptor.Audiences);
        Audiences = new ReadOnlyCollection<string>(_audiences);
        Claims = descriptor.Claims.Count == 0
            ? IdentityClaimCollection.Empty
            : new IdentityClaimCollection(descriptor.Claims);
        Properties = SnapshotProperties(descriptor.Properties);
    }

    /// <inheritdoc />
    public IdentityTokenKind Kind { get; }

    /// <inheritdoc />
    public AuthenticationProtocol Protocol { get; }

    /// <inheritdoc />
    public string? Id { get; }

    /// <inheritdoc />
    public SubjectIdentifier? Subject { get; }

    /// <inheritdoc />
    public string? Issuer { get; }

    /// <inheritdoc />
    public string? TokenType { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Audiences { get; }

    /// <inheritdoc />
    public DateTimeOffset? IssuedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset? NotBefore { get; }

    /// <inheritdoc />
    public DateTimeOffset? ExpiresAt { get; }

    /// <inheritdoc />
    public AuthenticationContext? AuthenticationContext { get; }

    /// <inheritdoc />
    public IIdentityClaimCollection Claims { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public string? RawData { get; }

    /// <summary>
    /// Validates the token's protocol-neutral data rules: issuer match, audience membership,
    /// and the primary temporal window, against the provided expectations. This is the only
    /// genuinely format-agnostic validation — signature verification and format-specific
    /// windows (a SAML bearer confirmation window) belong to the concrete token package. The
    /// clock skew is applied to the caller-supplied instant, so extreme wire timestamps
    /// diagnose rather than throw.
    /// </summary>
    /// <param name="options">The validation expectations.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public TokenValidationResult Validate(IdentityTokenValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<TokenValidationDiagnostic>();

        if (options.ExpectedIssuer is not null &&
            !string.Equals(Issuer, options.ExpectedIssuer, StringComparison.Ordinal))
        {
            diagnostics.Add(new TokenValidationDiagnostic(
                TokenValidationSeverity.Error,
                TokenValidationCodes.IssuerMismatch,
                "The token issuer does not match the expected issuer.",
                member: nameof(Issuer)));
        }

        if (options.ExpectedAudience is not null && !ContainsAudience(options.ExpectedAudience))
        {
            diagnostics.Add(new TokenValidationDiagnostic(
                TokenValidationSeverity.Error,
                TokenValidationCodes.AudienceMismatch,
                "The token is not intended for the expected audience.",
                member: nameof(Audiences)));
        }

        if (ExpiresAt is { } expiresAt && options.ValidateAt - options.ClockSkew >= expiresAt)
        {
            diagnostics.Add(new TokenValidationDiagnostic(
                TokenValidationSeverity.Error,
                TokenValidationCodes.Expired,
                "The token is expired.",
                member: nameof(ExpiresAt)));
        }

        if (NotBefore is { } notBefore && options.ValidateAt + options.ClockSkew < notBefore)
        {
            diagnostics.Add(new TokenValidationDiagnostic(
                TokenValidationSeverity.Error,
                TokenValidationCodes.NotYetValid,
                "The token is not yet valid.",
                member: nameof(NotBefore)));
        }

        return diagnostics.Count == 0 ? TokenValidationResult.Success : new TokenValidationResult(diagnostics);
    }

    private bool ContainsAudience(string audience)
    {
        for (var index = 0; index < _audiences.Length; index++)
        {
            if (string.Equals(_audiences[index], audience, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] CopyAudiences(IList<string> audiences)
    {
        ArgumentNullException.ThrowIfNull(audiences);

        if (audiences.Count == 0)
        {
            return Array.Empty<string>();
        }

        var copy = new string[audiences.Count];
        for (var index = 0; index < audiences.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audiences[index], nameof(audiences));
            copy[index] = audiences[index];
        }

        return copy;
    }

    private static IReadOnlyDictionary<string, IdentityClaimValue> SnapshotProperties(
        IDictionary<string, IdentityClaimValue> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (properties.Count == 0)
        {
            return ReadOnlyDictionary<string, IdentityClaimValue>.Empty;
        }

        // Reproduce the root's fail-closed property rule without the internal helper: a blank
        // name or an undefined value is a materialization error, not silent data.
        var copy = new Dictionary<string, IdentityClaimValue>(properties.Count, StringComparer.Ordinal);
        foreach (var (name, value) in properties)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(properties));

            if (value.IsUndefined)
            {
                throw new ArgumentException(
                    $"The token property '{name}' must not have an undefined value. Use IdentityClaimValue.Null for explicit null.",
                    nameof(properties));
            }

            copy[name] = value;
        }

        return new ReadOnlyDictionary<string, IdentityClaimValue>(copy);
    }
}
