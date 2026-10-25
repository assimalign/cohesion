using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Reports durable logical intent and its current instance convergence.</summary>
public sealed record ResourceStateOperation
{
    /// <summary>Gets the operation identity.</summary>
    public Guid Id { get; init; }
    /// <summary>Gets the logical target.</summary>
    public ResourceStateAddress Address { get; init; } = null!;
    /// <summary>Gets the accepted desired revision.</summary>
    public long Revision { get; init; }
    /// <summary>Gets the effective desired value accepted for this revision.</summary>
    public ResourceDesiredValue Desired { get; init; } = null!;
    /// <summary>Gets the producer-defined delivery semantic.</summary>
    public string Scope { get; init; } = string.Empty;
    /// <summary>Gets the continuing reconciliation state.</summary>
    public ResourceStateOperationStatus Status { get; init; }
    /// <summary>Gets the platform inventory generation used by this observation.</summary>
    public string InventoryRevision { get; init; } = string.Empty;
    /// <summary>Gets the current scale target.</summary>
    public int DesiredInstances { get; init; }
    /// <summary>Gets whether the inventory was complete.</summary>
    public bool InventoryComplete { get; init; }
    /// <summary>Gets process-specific outcomes.</summary>
    public IReadOnlyList<ResourceStateInstanceProgress> Instances { get; init; } = Array.Empty<ResourceStateInstanceProgress>();
    /// <summary>Gets the time at which this topology and progress were observed.</summary>
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>Identifies logical state reconciliation progress.</summary>
public enum ResourceStateOperationStatus
{
    /// <summary>Intent is durably stored.</summary>
    Accepted,
    /// <summary>Instances are being reconciled.</summary>
    Reconciling,
    /// <summary>Every required process in this complete inventory applied the revision.</summary>
    Converged,
    /// <summary>Some required targets could not apply the revision.</summary>
    Degraded,
    /// <summary>A newer desired revision replaced this operation.</summary>
    Superseded,
    /// <summary>Scale is zero and future instances will receive the revision.</summary>
    Deferred,
    /// <summary>A shared backend outcome cannot be established safely.</summary>
    Indeterminate
}
