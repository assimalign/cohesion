using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>The transport-independent outcome returned by a resource command client.</summary>
/// <param name="Status">Whether the target applied or rejected the declaration.</param>
/// <param name="Detail">The target's named explanation.</param>
/// <param name="Result">The optional area-defined result; never included in discovery exports.</param>
public sealed record ResourceCommandResult(
    ResourceCommandStatus Status,
    string Detail,
    ReadOnlyMemory<byte> Result = default);
