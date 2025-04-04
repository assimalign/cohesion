using System;
using System.Net;

namespace Assimalign.Cohesion.Http;

public interface IHttpConnectionInfo
{

    /// <summary>
    /// 
    /// </summary>
    int RemotePort { get; }

    /// <summary>
    /// 
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
