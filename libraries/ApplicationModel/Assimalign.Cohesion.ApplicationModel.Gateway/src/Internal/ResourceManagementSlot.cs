using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed record ResourceManagementSlot
{
    public ResourceStateAddress Address { get; init; } = null!;
    public ResourceDesiredValue Desired { get; init; } = null!;
    public ResourceDesiredValue? Baseline { get; init; }
    public bool HasOverride { get; init; }
    public string ContractDigest { get; init; } = string.Empty;
    public string ValueDigest { get; init; } = string.Empty;
    public long Revision { get; init; }
    public Guid OperationId { get; init; }
    public Guid LeaseOwner { get; init; }
    public long Fence { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
}
