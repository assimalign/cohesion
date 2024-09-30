using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Host Information.
/// </summary>
public interface IHostContext
{
    /// <summary>
    /// 
    /// </summary>
    HostState State { get; }
    /// <summary>
    /// 
    /// </summary>
    string? ContentRootPath { get; }
    /// <summary>
    /// 
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