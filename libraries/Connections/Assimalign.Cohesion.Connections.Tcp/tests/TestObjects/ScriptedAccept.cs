using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tcp.Tests.TestObjects;

/// <summary>
/// Stands in for a listener's accept through its internal seam. Each call fails with the next scripted
/// error, or with one error forever, and otherwise accepts on the real listening socket. Every call is
/// recorded with its <see cref="Stopwatch"/> timestamp.
/// </summary>
/// <remarks>
/// Running out of descriptors or buffers cannot be produced reliably in a test, so this is how the
/// listener's back-off is driven.
/// </remarks>
internal sealed class ScriptedAccept
{
    private readonly ConcurrentQueue<SocketError> _failures;
    private readonly ConcurrentQueue<long> _attempts = new();
    private readonly SocketError? _permanentFailure;

    public ScriptedAccept(params SocketError[] failures)
    {
        _failures = new ConcurrentQueue<SocketError>(failures);
    }

    private ScriptedAccept(SocketError permanentFailure)
    {
        _failures = new ConcurrentQueue<SocketError>();
        _permanentFailure = permanentFailure;
    }

    /// <summary>The calls made so far.</summary>
    public int Attempts => _attempts.Count;

    /// <summary>When each call was made, as <see cref="Stopwatch.GetTimestamp"/> values, in order.</summary>
    public IReadOnlyList<long> AttemptTimestamps => _attempts.ToArray();

    /// <summary>Creates an accept that fails every call with <paramref name="error"/>.</summary>
    public static ScriptedAccept FailingForever(SocketError error) => new(error);

    /// <summary>Queues more failures for the calls that follow.</summary>
    public void Fail(params SocketError[] failures)
    {
        foreach (SocketError failure in failures)
        {
            _failures.Enqueue(failure);
        }
    }

    /// <summary>The seam itself: the listener calls this in place of its own accept.</summary>
    public ValueTask<Socket> AcceptAsync(Socket listenerSocket, CancellationToken cancellationToken)
    {
        _attempts.Enqueue(Stopwatch.GetTimestamp());

        if (_permanentFailure is { } permanentFailure)
        {
            return ValueTask.FromException<Socket>(new SocketException((int)permanentFailure));
        }

        if (_failures.TryDequeue(out SocketError failure))
        {
            return ValueTask.FromException<Socket>(new SocketException((int)failure));
        }

        return listenerSocket.AcceptAsync(cancellationToken);
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> calls have been made, or until <paramref name="accept"/>
    /// completes, whichever comes first.
    /// </summary>
    public async Task WaitForAttemptsAsync(int count, Task accept, CancellationToken cancellationToken)
    {
        while (_attempts.Count < count && !accept.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }
}
