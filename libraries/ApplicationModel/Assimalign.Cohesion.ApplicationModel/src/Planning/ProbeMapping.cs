using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Maps one manifest probe role to its platform-neutral container probe configuration.
/// </summary>
public sealed record ProbeMapping
{
    /// <summary>Initializes a probe mapping.</summary>
    /// <param name="role">The probe role: <c>readiness</c>, <c>liveness</c>, or <c>startup</c>.</param>
    /// <param name="endpoint">The manifest endpoint against which the probe runs, when applicable.</param>
    /// <param name="kind">The probe mechanism.</param>
    /// <param name="value">The HTTP path or gRPC service, when applicable.</param>
    /// <param name="command">The exec command arguments, when applicable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public ProbeMapping(
        string role,
        string? endpoint,
        ProbeKind kind,
        string? value,
        IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        Role = role;
        Endpoint = endpoint;
        Kind = kind;
        Value = value;

        var commandCopy = new string[command.Count];
        for (int index = 0; index < command.Count; index++)
        {
            commandCopy[index] = command[index];
        }

        Command = new ReadOnlyCollection<string>(commandCopy);
    }

    /// <summary>Gets the probe role.</summary>
    public string Role { get; }

    /// <summary>Gets the endpoint name, when the probe uses an endpoint.</summary>
    public string? Endpoint { get; }

    /// <summary>Gets the probe mechanism.</summary>
    public ProbeKind Kind { get; }

    /// <summary>Gets the HTTP path or gRPC service, when applicable.</summary>
    public string? Value { get; }

    /// <summary>Gets the immutable exec command, or an empty list for non-exec probes.</summary>
    public IReadOnlyList<string> Command { get; }
}
