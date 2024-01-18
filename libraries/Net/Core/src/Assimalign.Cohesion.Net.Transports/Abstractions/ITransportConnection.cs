using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

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
    /// 
    /// </summary>
    void Abort();
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ValueTask AbortAsync();
}