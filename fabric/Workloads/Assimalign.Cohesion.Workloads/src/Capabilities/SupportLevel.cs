namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// How a platform implements one portable field.
/// </summary>
public enum SupportLevel
{
    /// <summary>
    /// The platform implements the field itself.
    /// </summary>
    Native,

    /// <summary>
    /// The driver approximates the field. Allowed only where the environment's policy permits
    /// emulation, and reported as <c>Emulated=True</c>.
    /// </summary>
    Emulated,

    /// <summary>
    /// The platform cannot honor the field. Always a validation error.
    /// </summary>
    Unsupported,
}
