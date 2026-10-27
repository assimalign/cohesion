using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The operations an action accepts, or the one operation an invocation requests.
/// </summary>
/// <remarks>
/// There is no delete operation. A resource is never deleted through its control plane: it is
/// marked deleted and its platform removes it later, so no action can destroy what it manages.
/// </remarks>
[Flags]
public enum ResourceActionKind
{
    /// <summary>No operation. Neither an action nor an invocation may declare it.</summary>
    None = 0,

    /// <summary>Observes the instance without changing it.</summary>
    Read = 1,

    /// <summary>Changes the instance.</summary>
    Write = 2,

    /// <summary>Both operations: a control whose read reports what a write set.</summary>
    ReadWrite = Read | Write,
}
