using System;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// One wait on the peer bounded by what is left of a <see cref="MinDataRateGate"/>'s allowance: a
/// response write or flush the peer can stall by withholding flow-control credit or by not reading
/// (#1384). The waiter passes <see cref="Token"/> to the blocking operation, reports the outcome with
/// <see cref="Complete"/>, and disposes the wait.
/// </summary>
/// <remarks>
/// <para>
/// Without a gate the wait is the caller's token alone and allocates nothing, so a response held to no
/// rate keeps its fast path. With one, the operation is bounded by a timeout on the gate's clock linked to
/// the caller's token: two <see cref="CancellationTokenSource"/> instances per wait, as the request-body
/// reads already pay.
/// </para>
/// <para>
/// A writer credits the octets it hands to the transport before it waits for them
/// (<see cref="MinDataRateGate.Record"/> with no wait), as Kestrel's write timing does: the peer must then
/// accept them within the grace period plus the octets over the rate. Crediting only after the operation
/// would hold a peer that is reading a large write at a healthy pace to the bare grace period.
/// </para>
/// </remarks>
internal readonly struct MinDataRateWait : IDisposable
{
    /// <summary>
    /// The most octets a response held to a rate hands to the transport per wait. A buffered body is
    /// written in pieces of this size, each credited as it is handed over, so a peer that never reads is
    /// reclaimed within the grace period plus one piece over the rate, not plus the whole body.
    /// </summary>
    public const int MaxWriteSize = 16 * 1024;

    private readonly MinDataRateGate? _gate;
    private readonly CancellationTokenSource? _timeout;
    private readonly CancellationTokenSource? _linked;
    private readonly CancellationToken _cancellationToken;
    private readonly long _start;

    private MinDataRateWait(MinDataRateGate? gate, CancellationTokenSource? timeout, CancellationTokenSource? linked, CancellationToken cancellationToken, long start)
    {
        _gate = gate;
        _timeout = timeout;
        _linked = linked;
        _cancellationToken = cancellationToken;
        _start = start;
    }

    /// <summary>
    /// Gets the token the bounded operation observes: the caller's, linked to the allowance's timeout
    /// when a gate applies.
    /// </summary>
    public CancellationToken Token => _linked?.Token ?? _timeout?.Token ?? _cancellationToken;

    /// <summary>
    /// Gets a value indicating whether the wait ran out of allowance: the timeout fired, and not the
    /// caller's own token.
    /// </summary>
    public bool TimedOut => _timeout is { IsCancellationRequested: true } && !_cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Begins a wait bounded by what is left of <paramref name="gate"/>'s allowance.
    /// </summary>
    /// <param name="gate">The gate, or <see langword="null"/> when the transfer is held to no rate.</param>
    /// <param name="cancellationToken">The caller's token, which still cancels the operation.</param>
    /// <param name="wait">When this method returns <see langword="true"/>, the wait to run the operation under.</param>
    /// <returns>
    /// <see langword="false"/> when the allowance is already spent, so the peer has fallen below the rate
    /// and the caller fails the transfer without waiting.
    /// </returns>
    public static bool TryBegin(MinDataRateGate? gate, CancellationToken cancellationToken, out MinDataRateWait wait)
    {
        if (gate is null)
        {
            wait = new MinDataRateWait(null, null, null, cancellationToken, 0);
            return true;
        }

        if (!gate.TryGetOperationTimeout(out TimeSpan timeout))
        {
            wait = default;
            return false;
        }

        CancellationTokenSource timeoutSource = new(timeout, gate.TimeProvider);
        CancellationTokenSource? linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token)
            : null;

        wait = new MinDataRateWait(gate, timeoutSource, linked, cancellationToken, gate.TimeProvider.GetTimestamp());
        return true;
    }

    /// <summary>
    /// Charges the time the wait took against the allowance. Called once the operation completes, or once
    /// it is cancelled for running out of allowance.
    /// </summary>
    public void Complete()
    {
        _gate?.Record(_gate.TimeProvider.GetTimestamp() - _start, bytesTransferred: 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _linked?.Dispose();
        _timeout?.Dispose();
    }
}
