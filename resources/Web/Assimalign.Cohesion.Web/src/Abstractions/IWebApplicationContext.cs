using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationContext
{
    /// <summary>
    /// Represents the pipeline of middleware components that are executed 
    /// in order to process incoming HTTP requests and generate responses.
    /// </summary>
    IWebApplicationPipeline Pipeline { get; }

    /// <summary>
    /// A collection of servers that are hosting the web application. This allows for multiple servers to be used for 
    /// load balancing or other purposes, and provides a way to access information about each server, such as its configuration and status.
    /// </summary>
    IEnumerable<IWebServer> Servers { get; }

    /// <summary>
    /// A collection of features that are available in the web application context. These features can include things like authentication,
    /// </summary>
    IHttpFeatureCollection Features { get; }
}
