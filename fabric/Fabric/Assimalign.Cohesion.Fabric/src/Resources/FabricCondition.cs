namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One condition of a resource, such as <c>Ready</c> or <c>Available</c>.
/// </summary>
/// <param name="Type">The condition type.</param>
/// <param name="Status">Whether the condition holds.</param>
/// <param name="Reason">A short PascalCase cause, such as a <see cref="FaultCode"/> name or <c>TierNotReady</c>.</param>
/// <param name="Message">A sentence that names the resource or the cause.</param>
/// <param name="ObservedGeneration">The generation the condition was observed at, if it differs from the status's.</param>
/// <param name="Source">For a composite, the role of the private child the cause came from, such as <c>store tier</c>. The child is named by its role, never its address.</param>
/// <param name="Platform">The platform's own words for the cause, kept verbatim; <see langword="null"/> for a cause that is not a platform's.</param>
public sealed record FabricCondition(
    string Type,
    ConditionStatus Status,
    string? Reason = null,
    string? Message = null,
    long? ObservedGeneration = null,
    string? Source = null,
    PlatformDetail? Platform = null);
