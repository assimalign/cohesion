using Assimalign.Cohesion.Internal;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public class QuicTransportContext : ITransportContext
{
    internal QuicTransportContext(QuicTransportConnection connection)
    {
        Connection = ThrowHelper.ThrowIfNull(connection);
    }

    /// <summary>
    /// 
    /// </summary>
    public QuicTransportConnection Connection { get; }
    ITransportConnection ITransportContext.Connection => Connection;

    public IServiceProvider? ServiceProvider => throw new NotImplementedException();


    /// <summary>
    /// Sets the connection pipe of the existing connection.
    /// </summary>
    /// <param name="pipe"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        _connection.Pipe = ThrowHelper.ThrowIfNull(pipe);
    }

    /// <summary>
    /// Sets connection data for the existing connection.
    /// </summary>
    /// <param name="data"></param>
    public void SetConnectionData(object? data)
    {
        _connection.ConnectionData = data;
    }
}
