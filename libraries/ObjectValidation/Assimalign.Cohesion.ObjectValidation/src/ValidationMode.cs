namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// Specifies whether the validator continues to the next validation item after an item fails, or stops.
/// </summary>
/// <remarks>
/// The mode decides between items (members), not how many of one item's chained rules run: that is
/// <see cref="IValidationContext.ContinueThroughValidationChain"/>.
/// </remarks>
public enum ValidationMode
{
    /// <summary>
    /// Evaluates every <see cref="IValidationItem"/> and reports each one that fails. The default.
    /// </summary>
    Cascade = 0,

    /// <summary>
    /// Evaluates no further <see cref="IValidationItem"/> once one has failed, so only the first failing
    /// item, in the order the profile declares its members, is reported.
    /// </summary>
    Stop = 1
}