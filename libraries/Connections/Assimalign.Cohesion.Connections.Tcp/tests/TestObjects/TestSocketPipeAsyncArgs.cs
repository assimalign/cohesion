using System.IO.Pipelines;

using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Tests.TestObjects;

/// <summary>
/// Exposes the socket-completion path of <see cref="SocketPipeAsyncArgs"/> so a test can drive it
/// without a socket: <see cref="CompleteOperation"/> runs exactly what the runtime runs when a pending
/// socket operation finishes.
/// </summary>
internal sealed class TestSocketPipeAsyncArgs : SocketPipeAsyncArgs
{
    public TestSocketPipeAsyncArgs(PipeScheduler pipeScheduler)
        : base(pipeScheduler)
    {
    }

    public void CompleteOperation() => OnCompleted(this);
}
