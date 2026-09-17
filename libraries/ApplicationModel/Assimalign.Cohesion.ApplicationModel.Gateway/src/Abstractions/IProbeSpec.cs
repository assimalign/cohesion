using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Describes one platform-independent process probe consumed by the local gateway.
/// </summary>
/// <remarks>
/// A probe can target a named resource endpoint, carry an absolute address for a
/// manifest-less executable, or run a command. Implementations should populate only the
/// properties required by <see cref="Kind"/>.
/// </remarks>
public interface IProbeSpec
{
    /// <summary>Gets the mechanism used to evaluate the probe.</summary>
    ProbeKind Kind { get; }

    /// <summary>Gets the logical resource endpoint targeted by the probe, when applicable.</summary>
    string? Endpoint { get; }

    /// <summary>Gets the absolute address targeted by a manifest-less executable probe, when applicable.</summary>
    Uri? Address { get; }

    /// <summary>Gets the HTTP path, when the probe uses a named endpoint.</summary>
    string? Path { get; }

    /// <summary>Gets the executable and arguments for an exec probe.</summary>
    IReadOnlyList<string> Command { get; }
}
