using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Contains one gateway-issued, process-bound state application.</summary>
/// <param name="Address">The logical state slot.</param>
/// <param name="Incarnation">The platform deployment incarnation.</param>
/// <param name="ProcessStartId">The verified target process.</param>
/// <param name="Revision">The durable desired revision.</param>
/// <param name="ValueDigest">The canonical desired value digest.</param>
/// <param name="ContractDigest">The required advertised contract digest.</param>
/// <param name="Fence">The current durable coordinator fence.</param>
/// <param name="Desired">The desired value.</param>
public sealed record ResourceStateDelivery(ResourceStateAddress Address, Guid Incarnation, Guid ProcessStartId,
    long Revision, string ValueDigest, string ContractDigest, long Fence, ResourceDesiredValue Desired);
