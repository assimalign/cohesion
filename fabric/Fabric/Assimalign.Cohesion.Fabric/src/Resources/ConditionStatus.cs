namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Whether a <see cref="FabricCondition"/> holds.
/// </summary>
public enum ConditionStatus
{
    /// <summary>
    /// The reconcile could not determine the condition.
    /// </summary>
    Unknown,

    /// <summary>
    /// The condition holds.
    /// </summary>
    True,

    /// <summary>
    /// The condition does not hold.
    /// </summary>
    False,
}
