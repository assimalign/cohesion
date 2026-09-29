using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

/// <summary>
/// Provides the ConfigurationStore application environment and runtime composition.
/// </summary>
public sealed class ConfigurationStoreApplicationContext : HostContext, IConfigurationStoreApplicationContext
{
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;

    internal ConfigurationStoreApplicationContext(string environmentName, System.IO.FileSystemPath? contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        // The opt-in resource runner asserts that the host content root equals the ambient resource content root,
        // so an enabled resource seeds it from the ambient context; a plain application keeps it unset.
        _environment = new HostEnvironment(environmentName) { ContentRootPath = contentRootPath };
    }

    /// <summary>
    /// Gets the configured application content root.
    /// </summary>
    public FileSystemPath? ContentRootPath => Environment.ContentRootPath;

    /// <summary>
    /// Gets the host environment for this application.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services in registration and startup order.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);
        _hostedServices = hostedServices;
    }
}
