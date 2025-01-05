namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;

public enum TcpConnectionTraceCode
{
    /// <summary>
    /// Paused Connection.
    /// </summary>
    Paused = SocketTraceCode.Paused,
    /// <summary>
    /// Connection Reset.
    /// </summary>
    Reset = SocketTraceCode.Reset,
    /// <summary>
    /// Connection Error.
    /// </summary>
    Error = SocketTraceCode.Error,
    /// <summary>
    /// Connection resumed.
    /// </summary>
    Resumed = SocketTraceCode.Resumed,
    /// <summary>
    /// Sender has finished sending data.
    /// </summary>
    Finished = SocketTraceCode.Finished

}
