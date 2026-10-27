namespace Assimalign.Cohesion.Database.Execution;

/// <summary>Describes one operator chosen by a model's planner.</summary>
/// <remarks>This is descriptive metadata; operator names and details belong to the model.</remarks>
public sealed class DatabasePlanNode
{
    /// <summary>Gets the node's identifier within the description.</summary>
    public required int NodeId { get; init; }

    /// <summary>Gets the parent identifier, or null for a root.</summary>
    public int? ParentId { get; init; }

    /// <summary>Gets the model's operator name.</summary>
    public required string Operator { get; init; }

    /// <summary>Gets the target object, or null for an operator without a target.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the chosen index name, or null when no index is used.</summary>
    public string? Index { get; init; }

    /// <summary>Gets model-specific details, including seek bounds when present.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets the model's candidate-row estimate, or null when unavailable. Details identify incomplete bounds.</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>Gets observed candidate work for an executed description, or null when unmeasured.</summary>
    public long? ActualRows { get; init; }
}
