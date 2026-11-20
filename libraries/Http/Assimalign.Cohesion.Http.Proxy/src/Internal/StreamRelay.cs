using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Copies bytes in both directions between the client's connection and the destination's, for an
/// upgraded exchange or a <c>CONNECT</c> tunnel, until one side closes.
/// </summary>
/// <remarks>
/// <para>
/// When the destination closes, nothing more can reach the client, so the relay ends. When the
/// client finishes sending, the destination may still be answering: the relay passes the end on as a
/// TCP half-close where the destination is a socket it can shut down, and keeps relaying toward the
/// client until the destination closes or the activity timeout runs out. A failure in either
/// direction ends both.
/// </para>
/// <para>
/// The relay owns both streams and disposes them when it ends; disposal also unblocks a read that
/// does not observe cancellation.
/// </para>
/// </remarks>
internal static class StreamRelay
{
    private const int bufferSize = 16 * 1024;

    public static async Task<StreamRelayResult> RelayAsync(Stream client, Stream destination, ActivityTimeout activity)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(activity.Token);
        Task<PumpOutcome> toDestination = PumpAsync(client, destination, activity, stop.Token);
        Task<PumpOutcome> toClient = PumpAsync(destination, client, activity, stop.Token);

        try
        {
            Task<PumpOutcome> first = await Task.WhenAny(toDestination, toClient).ConfigureAwait(false);
            PumpOutcome outcome = await first.ConfigureAwait(false);

            if (first == toDestination && outcome.Failure is null)
            {
                HalfClose(destination);
                await toClient.ConfigureAwait(false);
            }
            else
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
        }

        PumpOutcome sent = await toDestination.ConfigureAwait(false);
        PumpOutcome received = await toClient.ConfigureAwait(false);

        return new StreamRelayResult(sent.Bytes, received.Bytes, sent.Failure ?? received.Failure, activity.HasTimedOut);
    }

    private static void HalfClose(Stream destination)
    {
        if (destination is not NetworkStream network)
        {
            return;
        }

        try
        {
            network.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // The destination already reset the connection; the other direction ends with it.
        }
        catch (ObjectDisposedException)
        {
            // The other direction already ended the relay.
        }
    }

    private static async Task<PumpOutcome> PumpAsync(Stream source, Stream target, ActivityTimeout activity, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long total = 0;

        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return new PumpOutcome(total, null);
                }

                activity.Restart();
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                total += read;
                activity.Restart();
            }
        }
        catch (Exception exception)
        {
            // A direction ends on whatever its stream throws: a reset, a cancellation, or the other
            // direction disposing the stream. The exception is returned as the relay's failure rather
            // than lost on an unobserved task.
            return new PumpOutcome(total, exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private readonly record struct PumpOutcome(long Bytes, Exception? Failure);
}
