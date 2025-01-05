using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class SocketPipeReceiver : SocketPipeAsyncArgs
{
    public SocketPipeReceiver(PipeScheduler pipeScheduler)
           : base(pipeScheduler) { }

    public ValueTask<SocketPipeResult> ReceiveAsync(Socket socket, Memory<byte> buffer)
    {
        SetBuffer(buffer);

        if (socket.ReceiveAsync(this))
        {
            return new ValueTask<SocketPipeResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketPipeResult>(new SocketPipeResult(bytesTransferred))
            : new ValueTask<SocketPipeResult>(new SocketPipeResult(CreateException(error)));
    }
}