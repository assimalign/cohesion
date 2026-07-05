namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents how severe a <see cref="TokenValidationDiagnostic" /> is.
/// </summary>
/// <remarks>
/// <see cref="Error" /> is deliberately the zero value: severity is always supplied
/// explicitly at construction, but if a defaulted value ever leaks it must read as the
/// <em>most</em> severe interpretation — fail closed. This mirrors the protocol branch's
/// <c>ProtocolValidationSeverity</c>; the two branches keep independent copies because the
/// token branch must not reference the protocol branch.
/// </remarks>
public enum TokenValidationSeverity
{
    /// <summary>
    /// The finding makes the validated token invalid.
    /// </summary>
    Error = 0,

    /// <summary>
    /// The finding is suspicious or non-conformant but does not invalidate the token.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// The finding is informational.
    /// </summary>
    Information = 2
}
