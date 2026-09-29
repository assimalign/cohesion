using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Testing;

/// <summary>
/// Options controlling a <see cref="DatabaseApplicationTestFactory"/> invocation.
/// </summary>
public sealed class DatabaseApplicationTestFactoryOptions
{
    /// <summary>
    /// Gets or sets the invocation context. When null, the factory creates an isolated test
    /// context with loopback <c>db</c> and <c>admin</c> endpoints plus a temporary
    /// <c>data</c> mount and bootstrap credential. It removes the mount on disposal.
    /// </summary>
    public ResourceContext? ResourceContext { get; set; }

    /// <summary>Gets or sets the command-line arguments passed to the resource entry point.</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>
    /// Gets or sets how long <see cref="IDatabaseApplicationTestFactory.StartAsync"/> waits for
    /// <c>/readyz</c>. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how long disposal waits for graceful control-plane shutdown. Defaults to
    /// 30 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the delay between control-plane connection attempts. Defaults to 50
    /// milliseconds.
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}
