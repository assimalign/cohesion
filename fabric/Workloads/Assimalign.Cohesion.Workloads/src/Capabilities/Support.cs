namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// How a platform implements one portable field, and why.
/// </summary>
/// <param name="Level">The support level.</param>
/// <param name="Note">What the emulation or the gap means in practice, if anything.</param>
public readonly record struct Support(SupportLevel Level, string? Note = null)
{
    /// <summary>
    /// Gets native support.
    /// </summary>
    public static Support Native { get; } = new(SupportLevel.Native);

    /// <summary>
    /// Gets no support.
    /// </summary>
    public static Support Unsupported { get; } = new(SupportLevel.Unsupported);

    /// <summary>
    /// Creates emulated support.
    /// </summary>
    /// <param name="note">What the emulation means in practice.</param>
    /// <returns>The support.</returns>
    public static Support Emulated(string note) => new(SupportLevel.Emulated, note);
}
