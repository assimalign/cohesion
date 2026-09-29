namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Classifies an authenticated caller of a gateway control plane.
/// </summary>
public enum ApplicationCallerKind
{
    /// <summary>
    /// Another application's gateway.
    /// </summary>
    Peer = 0,

    /// <summary>
    /// A developer or tool acting for one.
    /// </summary>
    Developer,
}
