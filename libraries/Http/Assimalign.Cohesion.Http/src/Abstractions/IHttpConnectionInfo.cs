using System;
using System.Net;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IHttpConnectionInfo
{
    /// <summary>
    /// The remote port connecting.
    /// </summary>
    int RemotePort { get; }

    /// <summary>
    /// The remote IP address of the server or client.
    /// </summary>
    IPAddress RemoteIp { get; }

    /// <summary>
    /// 
    /// </summary>
    int LocalPort { get; }

    /// <summary>
    /// 
    /// </summary>
    IPAddress LocalIp { get; }
}
