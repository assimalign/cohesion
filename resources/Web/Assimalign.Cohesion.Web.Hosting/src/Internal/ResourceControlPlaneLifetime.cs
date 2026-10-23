using System;
using System.Threading;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceControlPlaneLifetime")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceControlPlaneLifetime : IDisposable
{
    private readonly BootstrapTokenVerifier? _verifier;
    private int _disposed;

    internal ResourceControlPlaneLifetime(BootstrapTokenVerifier? verifier)
    {
        _verifier = verifier;
        verifier?.Retain();
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _verifier?.Release();
        }
    }

    private sealed class DebugView
    {
        public DebugView(ResourceControlPlaneLifetime value)
        {
        }
    }
}
