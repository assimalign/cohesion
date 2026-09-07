using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalControlPlaneProbe : IProbeSpec
{
    private readonly IProbeSpec _probe;

    internal LocalControlPlaneProbe(
        IProbeSpec probe,
        ReadOnlyMemory<byte> bootstrapCredential)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        BootstrapCredential = bootstrapCredential.ToArray();
    }

    public ProbeKind Kind => _probe.Kind;

    public string? Endpoint => _probe.Endpoint;

    public EndpointAddress? Address => _probe.Address;

    public string? Path => _probe.Path;

    public IReadOnlyList<string> Command => _probe.Command;

    internal ReadOnlyMemory<byte> BootstrapCredential { get; }
}
