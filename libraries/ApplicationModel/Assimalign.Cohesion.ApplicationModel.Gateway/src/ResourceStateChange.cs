using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Requests a conditional logical state replacement with durable retry identity.</summary>
/// <param name="RequestId">The client's stable request identity.</param>
/// <param name="Address">The target state slot.</param>
/// <param name="ExpectedRevision">The revision to replace; null means create-if-absent.</param>
/// <param name="Desired">The desired value.</param>
/// <param name="ClearOverride">Whether to restore the current declared baseline instead of using Desired.</param>
public sealed record ResourceStateChange(Guid RequestId, ResourceStateAddress Address, long? ExpectedRevision, ResourceDesiredValue Desired, bool ClearOverride = false);
