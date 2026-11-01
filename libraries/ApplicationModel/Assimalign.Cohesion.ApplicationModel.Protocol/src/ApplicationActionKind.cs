using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The operations an action accepts: its capability. One invocation requests exactly one of them,
/// as an <see cref="ApplicationActionOperation"/>.
/// </summary>
/// <remarks>
/// There is no delete operation. A resource is never deleted through its control plane: it is
/// marked deleted and its platform removes it later, so no action can destroy what it manages.
/// </remarks>
[Flags]
public enum ApplicationActionKind
{
    /// <summary>No operation. No action may declare it.</summary>
    None = 0,

    /// <summary>Observes the instance without changing it.</summary>
    Read = 1,

    /// <summary>Changes the instance.</summary>
    Write = 2,

    /// <summary>Both operations: a control whose read reports what a write set.</summary>
    ReadWrite = Read | Write,
}
