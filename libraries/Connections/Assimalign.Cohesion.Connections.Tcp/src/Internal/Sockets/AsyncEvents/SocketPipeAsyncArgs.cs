using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// A reusable socket operation that is its own value-task source: an awaiter registers one
/// continuation per operation, and the socket's completion schedules it on the transport scheduler.
/// </summary>
/// <remarks>
/// <para>
/// The awaiter and the socket completion race: either may run first, and both may run at once.
/// The awaiter publishes its continuation's state and then the continuation itself, with a full
/// fence between them (the compare-exchange). The completion path therefore reads the state only
/// after it has observed the continuation, and with acquire semantics, so a continuation it schedules
/// always travels with its own state.
/// </para>
/// <para>
/// Reading the state first (#1093) let the completion read a null state, lose the race to a
/// registration, and then schedule the newly registered continuation with that null state. An
/// async method's continuation is the runtime's state-machine callback, which rejects any state that
/// is not its state machine with "An unexpected state object was encountered", on a thread-pool
/// thread, where the exception terminates the process.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SocketPipeAsyncArgs {Count,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal class SocketPipeAsyncArgs : SocketAsyncEventArgs, IValueTaskSource<SocketPipeResult>
{
    private static readonly Action<object?> _continuationCompleted = _ => { };
    private readonly PipeScheduler _pipeScheduler;
    private Action<object?>? _continuation;
    private object? _continuationState;

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
        // Observe the continuation first; mark the operation completed if none is registered yet.
        Action<object?>? continuation = Volatile.Read(ref _continuation)
            ?? Interlocked.CompareExchange(ref _continuation, _continuationCompleted, null);

        if (continuation is null)
        {
            // Completed before an awaiter registered: the awaiter finds the completed marker and runs
            // its continuation itself.
            return;
        }

        // Only now read the state: it was published before the continuation we observed.
        object? continuationState = _continuationState;
        _continuationState = null;
        Volatile.Write(ref _continuation, _continuationCompleted); // in case someone polls IsCompleted
        _pipeScheduler.Schedule(continuation, continuationState);
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
        return !ReferenceEquals(Volatile.Read(ref _continuation), _continuationCompleted) ? ValueTaskSourceStatus.Pending :
                SocketError == SocketError.Success ? ValueTaskSourceStatus.Succeeded :
                ValueTaskSourceStatus.Faulted;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        // Publish the state before the continuation; the compare-exchange is the fence the
        // completion path relies on.
        _continuationState = state;
        Action<object?>? previous = Interlocked.CompareExchange(ref this._continuation, continuation, null);
        if (ReferenceEquals(previous, _continuationCompleted))
        {
            // The operation completed before this registration: run the continuation here.
            _continuationState = null;
            ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: true);
        }
    }

    private sealed class DebugView
    {
        private readonly SocketPipeAsyncArgs _target;

        public DebugView(SocketPipeAsyncArgs value)
        {
            _target = value;
        }

        public global::System.Net.Sockets.Socket? AcceptSocket => _target.AcceptSocket;
        public byte[]? Buffer => _target.Buffer;
        public global::System.Collections.Generic.IList<global::System.ArraySegment<byte>>? BufferList => _target.BufferList;
        public int BytesTransferred => _target.BytesTransferred;
        public global::System.Exception? ConnectByNameError => _target.ConnectByNameError;
        public global::System.Net.Sockets.Socket? ConnectSocket => _target.ConnectSocket;
        public int Count => _target.Count;
        public bool DisconnectReuseSocket => _target.DisconnectReuseSocket;
        public global::System.Net.Sockets.SocketAsyncOperation LastOperation => _target.LastOperation;
        public global::System.Memory<byte> MemoryBuffer => _target.MemoryBuffer;
        public int Offset => _target.Offset;
        public global::System.Net.Sockets.IPPacketInformation ReceiveMessageFromPacketInfo => _target.ReceiveMessageFromPacketInfo;
        public global::System.Net.EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public global::System.Net.Sockets.SendPacketsElement[]? SendPacketsElements => _target.SendPacketsElements;
        public global::System.Net.Sockets.TransmitFileOptions SendPacketsFlags => _target.SendPacketsFlags;
        public int SendPacketsSendSize => _target.SendPacketsSendSize;
        public global::System.Net.Sockets.SocketError SocketError => _target.SocketError;
        public global::System.Net.Sockets.SocketFlags SocketFlags => _target.SocketFlags;
        public object? UserToken => _target.UserToken;
    }
}
