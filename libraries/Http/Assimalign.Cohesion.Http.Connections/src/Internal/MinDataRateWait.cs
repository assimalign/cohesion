using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Runs one operation the peer can stall — a response write or flush, or the wait for flow-control
/// credit — bounded by what is left of a <see cref="MinDataRateGate"/>'s allowance (#1384).
/// </summary>
/// <remarks>
/// <para>
/// The deadline is armed only when the operation actually waits. The operation starts with the token of
/// the gate's reusable deadline (<see cref="MinDataRateGate.RentDeadline"/>), which nothing has armed
/// yet, and the caller's token reaches it through a registration on the caller's token for the
/// operation's run, which reuses that token's node. One that completes synchronously — a pipe write below
/// the pause threshold, which is nearly every write — therefore costs no timer, no linked source and no
/// allocation. Only an operation still pending arms the deadline
/// (<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>), charges the measured wait to the gate,
/// and resets the deadline for the next operation. The gate creates its deadline once, at its first
/// bounded operation, and replaces it only after it fired, or after the caller's token cancelled it, since
/// a cancelled source cannot be reset.
/// </para>
/// <para>
/// A writer credits the octets it hands to the transport before it waits for them
/// (<see cref="MinDataRateGate.Record"/> with no wait), as Kestrel's write timing does: the peer must then
/// accept them within the grace period plus the octets over the rate. Crediting only after the operation
/// would hold a peer that is reading a large write at a healthy pace to the bare grace period. Crediting
/// is why a writer hands a large write over in pieces of at most <see cref="MaxWriteSize"/> octets: each
/// piece's credit is earned only once the transport has taken the pieces before it.
/// </para>
/// </remarks>
internal static class MinDataRateWait
{
    /// <summary>
    /// The most octets a response held to a rate hands to the transport per wait. A body, or a large
    /// write of a streamed one, is written in pieces of this size, each credited as it is handed over, so
    /// a peer that never reads is reclaimed within the grace period plus, over the rate, the octets the
    /// transport buffers and one piece, not plus the whole write. HTTP/2 caps its DATA frames at it for
    /// the same reason, whatever <c>SETTINGS_MAX_FRAME_SIZE</c> the peer advertises.
    /// </summary>
    public const int MaxWriteSize = 16 * 1024;

    /// <summary>
    /// Runs <paramref name="operation"/> bounded by what is left of <paramref name="gate"/>'s allowance.
    /// </summary>
    /// <typeparam name="TState">The operation's arguments.</typeparam>
    /// <param name="gate">The gate whose allowance bounds the operation and is charged for its wait.</param>
    /// <param name="state">The operation's arguments, passed through so the operation can be a static lambda.</param>
    /// <param name="operation">The operation, given its arguments and the token it must observe.</param>
    /// <param name="cancellationToken">The caller's token, which still cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> once the operation completed; <see langword="false"/> when the allowance was
    /// already spent, so the operation never started, or ran out while it waited, so it was cancelled. The
    /// caller then fails the transfer.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller's token cancelled the operation.</exception>
    public static ValueTask<bool> TryRunAsync<TState>(MinDataRateGate gate, TState state, Func<TState, CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller already gave up: the operation observes the caller's token alone and fails at
            // once, as it would unbounded, waiting on nothing the gate should be charged for.
            return AwaitUnboundedAsync(operation(state, cancellationToken));
        }

        if (!gate.TryGetOperationTimeout(out TimeSpan timeout))
        {
            return new ValueTask<bool>(false);
        }

        CancellationTokenSource deadline = gate.RentDeadline();

        // The caller's token cancels the operation through the deadline for as long as the operation
        // runs, a cancellation that lands inside a write that completes synchronously included, as the
        // unbounded operation would observe it. A token that cannot be cancelled registers nothing, and a
        // registration on one that can reuses the token's own node.
        CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static source => ((CancellationTokenSource)source!).Cancel(),
            deadline);
        bool waiting = false;

        try
        {
            ValueTask pending = operation(state, deadline.Token);

            if (pending.IsCompleted)
            {
                pending.GetAwaiter().GetResult();
                return new ValueTask<bool>(true);
            }

            waiting = true;
            return WaitAsync(gate, deadline, registration, timeout, pending, cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        finally
        {
            if (!waiting)
            {
                registration.Dispose();
                gate.ReturnDeadline(deadline);
            }
        }
    }

    private static ValueTask<bool> AwaitUnboundedAsync(ValueTask operation)
    {
        if (operation.IsCompleted)
        {
            operation.GetAwaiter().GetResult();
            return new ValueTask<bool>(true);
        }

        return AwaitAsync(operation);

        static async ValueTask<bool> AwaitAsync(ValueTask operation)
        {
            await operation.ConfigureAwait(false);
            return true;
        }
    }

    private static async ValueTask<bool> WaitAsync(MinDataRateGate gate, CancellationTokenSource deadline, CancellationTokenRegistration registration, TimeSpan timeout, ValueTask pending, CancellationToken cancellationToken)
    {
        long start = gate.TimeProvider.GetTimestamp();

        try
        {
            deadline.CancelAfter(timeout);
            await pending.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken != cancellationToken)
        {
            // The caller's token reached the operation through the deadline: report it as the caller's
            // cancellation, which is what the operation run unbounded would have thrown.
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        finally
        {
            registration.Dispose();
            gate.Record(gate.TimeProvider.GetTimestamp() - start, bytesTransferred: 0);
            gate.ReturnDeadline(deadline);
        }
    }
}
