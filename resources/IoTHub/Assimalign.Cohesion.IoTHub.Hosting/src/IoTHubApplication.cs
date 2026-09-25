using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IoTHub;
using Assimalign.Cohesion.IoTHub.Hosting.Internal;

namespace Assimalign.Cohesion.IoTHub.Hosting;

/// <summary>
/// Hosts an IoTHub application and its ordered service lifecycle.
/// </summary>
public sealed class IoTHubApplication : Host<IoTHubApplicationContext>, IIoTHubApplication
{
    private readonly IoTHubApplicationContext _context;

    internal IoTHubApplication(
        IoTHubApplicationOptions options,
        IoTHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override IoTHubApplicationContext Context => _context;

    IIoTHubApplicationContext IIoTHubApplication.Context => _context;

    Task IIoTHubApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IIoTHubApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for an IoT hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the IoT hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IoTHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(IoTHubApplication).Assembly);
    }

    internal static IoTHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new IoTHubApplicationBuilder(args, resourceAssembly);
    }
}
