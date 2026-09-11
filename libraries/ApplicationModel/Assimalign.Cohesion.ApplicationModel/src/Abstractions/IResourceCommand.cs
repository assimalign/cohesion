using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Declares an application-owned mutation of a resource's desired state.</summary>
public interface IResourceCommand
{
    /// <summary>Gets the deterministic SHA-256 identity of kind, target identity, and canonical payload.</summary>
    string Id { get; }

    /// <summary>Gets the command kind accepted by the target's manifest.</summary>
    string Kind { get; }

    /// <summary>Gets the nonblank, provider-scoped ownership conflict key.</summary>
    string Key { get; }

    /// <summary>Gets the canonical UTF-8 JSON payload produced with explicit serialization metadata.</summary>
    ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Gets the target resource instance registered in the declaring application's graph.</summary>
    IApplicationResource Target { get; }

    /// <summary>Gets the application that declares and owns this command.</summary>
    ApplicationName Owner { get; }

    /// <summary>Gets whether rejection can be observed without blocking dependent resources.</summary>
    bool Optional { get; }
}
