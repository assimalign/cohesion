using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// 
/// </remarks>
public interface ITransportConnection : IThreadPoolWorkItem, IDisposable
{
    /// <summary>
    /// Specifies whether the connection is connected to a remote host.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 
    /// </summary>
    object? ConnectionData { get; }

    /// <summary>
    /// 
    /// </summary>
    ProtocolType Protocol { get; }

    /// <summary>
    /// Represents the current state of the pipeline.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// A pipe to send and receive data from either client or server.
    /// </summary>
    ITransportConnectionPipe Pipe { get; }

    /// <summary>
    /// 
    /// </summary>
    EndPoint LocalEndPoint { get; }

    /// <summary>
    /// 
    /// </summary>
    EndPoint RemoteEndPoint { get; }

    /// <summary>
    /// Aborts the connection.
    /// </summary>
    void Abort();

    /// <summary>
    /// Asynchronously aborts the connection.
    /// </summary>
    /// <returns></returns>
    ValueTask AbortAsync();
}