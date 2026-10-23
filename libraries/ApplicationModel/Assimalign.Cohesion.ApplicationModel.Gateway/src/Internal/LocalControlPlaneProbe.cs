using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[global::System.Diagnostics.DebuggerDisplay("LocalControlPlaneProbe")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly LocalControlPlaneProbe _target;

        public DebugView(LocalControlPlaneProbe value)
        {
            _target = value;
        }

        public ProbeKind Kind => _target.Kind;
        public string? Endpoint => _target.Endpoint;
        public Uri? Address => _target.Address;
        public string? Path => _target.Path;
        public IReadOnlyList<string> Command => _target.Command;
    }
}
