namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents how severe a <see cref="ProtocolValidationDiagnostic" /> is.
/// </summary>
/// <remarks>
/// The member values are stable and pinned by test. <see cref="Error" /> is deliberately
/// the zero value: severity is always supplied explicitly at construction, but if a
/// defaulted value ever leaks it must read as the <em>most</em> severe interpretation —
/// fail closed — and <c>severity &lt;= ProtocolValidationSeverity.Warning</c> naturally
/// expresses "at least this severe".
/// </remarks>
public enum ProtocolValidationSeverity
{
    /// <summary>
    /// The finding makes the validated artifact invalid.
    /// </summary>
    Error = 0,

    /// <summary>
    /// The finding is suspicious or non-conformant but does not invalidate the artifact.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// The finding is informational.
    /// </summary>
    Information = 2
}
