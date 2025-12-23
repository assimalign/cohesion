using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Threading;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;

public sealed class TcpTransportConnection : ISingleStreamTransportConnection
{
    private readonly TransportId _transportId;
    private readonly ConnectionId _id;
    private readonly SocketTransportConnectionContext _context;
    private readonly TransportPipeline _pipeline;

    private bool _isOpen;

    internal TcpTransportConnection(
        SocketTransportConnectionContext context,
        TransportPipeline pipeline,
        TransportId transportId)
    {
        _id = ConnectionId.New();
        _context = context;
        _pipeline = pipeline;
        _transportId = transportId;
    }

    public ConnectionId Id => _id;
    public TransportId TransportId => _transportId;
    public TransportProtocol Protocol { get; } = TransportProtocol.Tcp;
    public ConnectionState State => _context.State;

    public void Abort()
    {
        _context.Abort();
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        return _context.AbortAsync();
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return _context.DisposeAsync();
    }

    public TcpTransportConnectionContext Open()
    {
        return OpenAsync().GetAwaiter().GetResult();
    }

    public async ValueTask<TcpTransportConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        InvalidOperationException.ThrowIf(_isOpen, "he connection is already open.");

        if (!(_isOpen = ThreadPool.UnsafeQueueUserWorkItem(_context, false)))
        {
            throw new Exception();
        }

        var context = new TcpTransportConnectionContext(_context);

        var task = _pipeline?.ExecuteAsync(
            this,
            context,
            cancellationToken);

        if (task is not null)
        {
            await task;
        }

        return context;
    }

    ITransportConnectionContext ISingleStreamTransportConnection.Open()
    {
        return Open();
    }
    async ValueTask<ITransportConnectionContext> ISingleStreamTransportConnection.OpenAsync(CancellationToken cancellationToken)
    {
        return await OpenAsync(cancellationToken);
    }
}
