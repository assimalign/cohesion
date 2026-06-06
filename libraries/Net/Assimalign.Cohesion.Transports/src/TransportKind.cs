using System;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents the type of transport being used.
/// </summary>
[Flags]
public enum TransportKind
{
    /// <summary>
    /// 
    /// </summary>
    Client = 1,

    /// <summary>
    /// 
    /// </summary>
    Server = 2
}
