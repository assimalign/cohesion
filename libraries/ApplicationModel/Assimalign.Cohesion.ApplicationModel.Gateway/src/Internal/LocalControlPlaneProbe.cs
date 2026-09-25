using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

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

    public Uri? Address => _probe.Address;

    public string? Path => _probe.Path;

    public IReadOnlyList<string> Command => _probe.Command;

    internal ReadOnlyMemory<byte> BootstrapCredential { get; }
}
