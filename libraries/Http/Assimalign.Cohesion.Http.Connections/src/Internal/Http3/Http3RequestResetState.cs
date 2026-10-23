using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Owns the first reset decision for one request stream, from its head read through exchange cleanup.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("Http3RequestResetState")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class Http3RequestResetState
{
    private int _resetClaimed;

    /// <summary>
    /// Claims reporting and the coded driver abort together, before either can invoke a listener or
    /// cancellation callback. Later reset paths cannot report again or replace the winning code.
    /// </summary>
    internal bool TryClaimReset() => Interlocked.CompareExchange(ref _resetClaimed, 1, 0) == 0;

    private sealed class DebugView
    {
        public DebugView(Http3RequestResetState value)
        {
        }
    }
}
