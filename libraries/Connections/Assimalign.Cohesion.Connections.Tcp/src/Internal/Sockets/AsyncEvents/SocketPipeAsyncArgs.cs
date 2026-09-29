using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

internal class SocketPipeAsyncArgs : SocketAsyncEventArgs, IValueTaskSource<SocketPipeResult>
{
    private static readonly Action<object?> _continuationCompleted = _ => { };
    private Action<object?>? _continuation;
    private readonly PipeScheduler _pipeScheduler;


    public SocketPipeAsyncArgs(PipeScheduler? pipeScheduler) : base(unsafeSuppressExecutionContextFlow: true)
    {
        if (pipeScheduler is null)
        {
            throw new ArgumentNullException(nameof(pipeScheduler));
        }

        this._pipeScheduler = pipeScheduler;
    }

    protected override void OnCompleted(SocketAsyncEventArgs eventArgs)
    {
        var continuationReference = _continuation;
        var continuationState = UserToken;

        if (continuationReference != null || (continuationReference = Interlocked.CompareExchange(ref _continuation, _continuationCompleted, null)) != null)
        {
            UserToken = null;
            _continuation = _continuationCompleted; // in case someone's polling IsCompleted
            _pipeScheduler.Schedule(continuationReference, continuationState);
        }
    }

    public SocketPipeResult GetResult(short token)
    {
        _continuation = null;

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
        return !ReferenceEquals(_continuation, _continuationCompleted) ? ValueTaskSourceStatus.Pending :
                SocketError == SocketError.Success ? ValueTaskSourceStatus.Succeeded :
                ValueTaskSourceStatus.Faulted;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        UserToken = state;
        var prevContinuation = Interlocked.CompareExchange(ref this._continuation, continuation, null);
        if (ReferenceEquals(prevContinuation, _continuationCompleted))
        {
            UserToken = null;
            ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: true);
        }
    }
}
