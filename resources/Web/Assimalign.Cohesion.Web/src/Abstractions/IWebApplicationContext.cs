using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationContext : IHostContext
{
    /// <summary>
    /// A collection of servers that are hosting the web application. This allows for multiple servers to be used for 
    /// load balancing or other purposes, and provides a way to access information about each server, such as its configuration and status.
    /// </summary>
    IEnumerable<IWebApplicationServer> Servers { get; }
}
