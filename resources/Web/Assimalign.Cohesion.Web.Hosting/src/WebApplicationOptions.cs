
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Hosting;

public class WebApplicationOptions : HostOptions<WebApplicationContext>
{
    public WebApplicationOptions()
    {
    }

    /// <summary>
    /// Gets the allowed-hosts (host filtering) configuration. Filtering is disabled while
    /// <see cref="HostFilteringOptions.AllowedHosts"/> is empty (the default, match-any);
    /// adding patterns opts in, compiling the allowlist once at pipeline build and enforcing
    /// it as the pipeline's first-position middleware.
    /// </summary>
    public HostFilteringOptions HostFiltering { get; } = new();
}
