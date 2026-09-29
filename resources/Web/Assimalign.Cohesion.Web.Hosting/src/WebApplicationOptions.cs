
using System;
using System.Collections.Generic;
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
}
