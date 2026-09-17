using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// Configures a resource-program invocation created by
/// <see cref="WebApplicationTestFactory.FromProgram{TProgram}(WebApplicationProgramTestFactoryOptions)"/>.
/// </summary>
public sealed class WebApplicationProgramTestFactoryOptions
{
    /// <summary>
    /// Gets or sets the invocation context. When null, the factory creates an isolated context
    /// with a reserved loopback <c>http</c> endpoint.
    /// </summary>
    public ResourceContext? ResourceContext { get; set; }

    /// <summary>Gets or sets the command-line arguments passed to the resource entry point.</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>Gets or sets the readiness timeout. Defaults to 30 seconds.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the graceful-shutdown timeout. Defaults to 30 seconds.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the delay between readiness attempts. Defaults to 50 milliseconds.</summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}
