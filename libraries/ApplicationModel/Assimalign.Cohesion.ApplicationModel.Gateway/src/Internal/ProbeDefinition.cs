using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class ProbeDefinition : IProbeSpec
{
    public ProbeDefinition(
        ProbeKind kind,
        string? endpoint,
        EndpointAddress? address,
        string? path,
        IReadOnlyList<string> command)
    {
        Kind = kind;
        Endpoint = endpoint;
        Address = address;
        Path = path;

        var copy = new string[command.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = command[index];
        }

        Command = new ReadOnlyCollection<string>(copy);
    }

    public ProbeKind Kind { get; }

    public string? Endpoint { get; }

    public EndpointAddress? Address { get; }

    public string? Path { get; }

    public IReadOnlyList<string> Command { get; }
}
