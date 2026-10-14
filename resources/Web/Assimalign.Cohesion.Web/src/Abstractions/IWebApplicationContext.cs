using System.Collections.Generic;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using System.IO;

/// <summary>
/// The composed Web application as its middleware sees it at pipeline build: its content and web
/// roots, middleware, servers, and request features.
/// </summary>
public interface IWebApplicationContext
{
    /// <summary>
    /// Represents the base directory where your application is running
    /// </summary>
    FileSystemPath? ContentRootPath { get; }

    /// <summary>
    /// Gets the directory the application serves static web assets from, or
    /// <see langword="null"/> when the application has no web root.
    /// </summary>
    /// <remarks>
    /// The hosting runtime resolves it against <see cref="ContentRootPath"/>: <c>wwwroot</c> by
    /// default, present only when that directory exists. It is never the content root itself or
    /// the process working directory, which hold the application's configuration files and
    /// binaries.
    /// </remarks>
    FileSystemPath? WebRootPath { get; }

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
