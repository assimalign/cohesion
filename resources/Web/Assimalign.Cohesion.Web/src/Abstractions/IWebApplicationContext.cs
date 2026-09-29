using System.Collections.Generic;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using System.IO;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationContext
{
    /// <summary>
    /// Represents the base directory where your application is running
    /// </summary>
    FileSystemPath? ContentRootPath { get; }

    /// <summary>
    /// Represents the pipeline of middleware components that are executed 
    /// in order to process incoming HTTP requests and generate responses.
    /// </summary>
    IEnumerable <IWebApplicationMiddleware> Middleware { get; }

    /// <summary>
    /// Gets the application servers in lifecycle registration order.
    /// </summary>
    /// <remarks>
    /// The collection exposes the original Web server instances even when the Hosting runtime
    /// uses an internal adapter to participate in its host lifecycle.
    /// </remarks>
    IEnumerable<IWebApplicationServer> Servers { get; }

    /// <summary>
    /// A collection of features that are available in the web application context.
    /// </summary>
    IEnumerable<IHttpFeature> Features { get; }
}
