using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Host Information.
/// </summary>
public interface IHostContext
{
    /// <summary>
    /// The unique identifier for the host.
    /// </summary>
    HostId HostId { get; }

    /// <summary>
    /// 
    /// </summary>
    HostState State { get; }

    /// <summary>
    /// 
    /// </summary>
    FileSystemPath? ContentRootPath { get; }

    /// <summary>
    /// The host environment information.
    /// </summary>
    IHostEnvironment Environment { get; }

    /// <summary>
    /// A collection of hosted services.
    /// </summary>
    IEnumerable<IHostService> HostedServices { get; }

    /// <summary>
    /// The Host Service Provider.
    /// </summary>
    IServiceProvider? ServiceProvider { get; }

    /// <summary>
    /// Signals the host to shutdown
    /// </summary>
    void Shutdown();
}