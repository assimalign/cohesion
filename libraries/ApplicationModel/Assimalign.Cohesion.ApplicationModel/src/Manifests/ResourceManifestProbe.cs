using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one readiness, liveness, or startup probe.
/// </summary>
/// <remarks>
/// A generated manifest sets exactly one of <see cref="Http"/>, <see cref="Tcp"/>,
/// <see cref="Exec"/>, <see cref="Grpc"/>, or <see cref="None"/>.
/// </remarks>
public sealed record ResourceManifestProbe
{
    /// <summary>
    /// Gets the logical endpoint against which the probe runs, when applicable.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Gets the HTTP request path for an HTTP probe.
    /// </summary>
    public string? Http { get; init; }

    /// <summary>
    /// Gets whether the probe performs a TCP connection check.
    /// </summary>
    public bool? Tcp { get; init; }

    /// <summary>
    /// Gets the command and arguments for an executable probe.
    /// </summary>
    public IReadOnlyList<string>? Exec { get; init; }

    /// <summary>
    /// Gets the optional service name for a gRPC probe.
    /// </summary>
    public string? Grpc { get; init; }

    /// <summary>
    /// Gets whether probing is explicitly disabled for this role.
    /// </summary>
    public bool? None { get; init; }
}
