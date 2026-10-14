
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// Configures a Cohesion Web application host.
/// </summary>
/// <remarks>
/// Web application servers use serial lifecycle execution so they start in registration order
/// and stop in reverse order. Setting either inherited concurrency option causes
/// <see cref="WebApplicationBuilder.Build"/> to reject the configuration.
/// </remarks>
public class WebApplicationOptions : HostOptions<WebApplicationContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebApplicationOptions"/> class.
    /// </summary>
    public WebApplicationOptions()
    {
    }

    /// <summary>
    /// Gets or sets the content root: the directory configuration files (<c>appsettings.json</c>)
    /// are read from and the web root is resolved against.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/>, an orchestrated resource uses its ambient resource context's
    /// content root and a plain application uses <see cref="AppContext.BaseDirectory"/>.
    /// </remarks>
    public FileSystemPath? ContentRootPath { get; set; }

    /// <summary>
    /// Gets or sets the web root: the directory static web assets are served from. A relative
    /// path is resolved against the content root.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/>, the web root is <c>wwwroot</c> under the content root if that
    /// directory exists, and the application has no web root otherwise. Static files are never
    /// served from the content root itself, which holds configuration files and binaries.
    /// </remarks>
    public FileSystemPath? WebRootPath { get; set; }
}
