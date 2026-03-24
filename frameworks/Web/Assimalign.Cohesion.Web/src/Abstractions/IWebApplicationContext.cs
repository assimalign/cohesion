using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationContext
{
    /// <summary>
    /// A collection of servers that are hosting the web application. This allows for multiple servers to be used for 
    /// load balancing or other purposes, and provides a way to access information about each server, such as its configuration and status.
    /// </summary>
    IEnumerable<IWebApplicationServer> Servers { get; }

    /// <summary>
    /// 
    /// </summary>
    IServiceProvider? ServiceProvider { get; }
}
