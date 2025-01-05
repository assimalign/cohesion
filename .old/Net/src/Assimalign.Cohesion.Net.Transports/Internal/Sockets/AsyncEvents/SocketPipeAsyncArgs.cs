using System;
using System.Threading;
using System.Threading.Tasks.Sources;
using System.Net.Sockets;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class SocketPipeAsyncArgs : SocketAsyncEventArgs, IValueTaskSource<SocketPipeResult>
{
    private static readonly Action<object?> continuationCompleted = _ => { };
    private Action<object?>? continuation;
    private readonly PipeScheduler pipeScheduler;


    public SocketPipeAsyncArgs(PipeScheduler? pipeScheduler) : base(unsafeSuppressExecutionContextFlow: true)
    {
        if (pipeScheduler is null)
        {
            throw new ArgumentNullException(nameof(pipeScheduler));
        }

        this.pipeScheduler = pipeScheduler;
    }

    protected override void OnCompleted(SocketAsyncEventArgs eventArgs)
    {
        var continuationReference = continuation;
        var continuationState = UserToken;

        if (continuationReference != null || (continuationReference = Interlocked.CompareExchange(ref continuation, continuationCompleted, null)) != null)
        {
            UserToken = null;
            continuation = continuationCompleted; // in case someone's polling IsCompleted
            pipeScheduler.Schedule(continuationReference, continuationState);
        }
    }

    public SocketPipeResult GetResult(short token)
    {
        continuation = null;

        if (SocketError != SocketError.Success)
        {
            return new SocketPipeResult(CreateException(SocketError));
        }

        return new SocketPipeResult(BytesTransferred);
    }

    protected static SocketException CreateException(SocketError socketError)
    {
        return new SocketException((int)socketError);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        return !ReferenceEquals(continuation, continuationCompleted) ? ValueTaskSourceStatus.Pending :
                SocketError == SocketError.Success ? ValueTaskSourceStatus.Succeeded :
                ValueTaskSourceStatus.Faulted;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        UserToken = state;
        var prevContinuation = Interlocked.CompareExchange(ref this.continuation, continuation, null);
        if (ReferenceEquals(prevContinuation, continuationCompleted))
        {
            UserToken = null;
            ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: true);
        }
    }
}
