using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting.Internal;

namespace Assimalign.Cohesion.LogSpace.Hosting;

/// <summary>
/// Hosts a LogSpace application and its ordered service lifecycle.
/// </summary>
public sealed class LogSpaceApplication : Host<LogSpaceApplicationContext>, ILogSpaceApplication
{
    private readonly LogSpaceApplicationContext _context;

    internal LogSpaceApplication(
        LogSpaceApplicationOptions options,
        LogSpaceApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override LogSpaceApplicationContext Context => _context;

    ILogSpaceApplicationContext ILogSpaceApplication.Context => _context;

    Task ILogSpaceApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task ILogSpaceApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a LogSpace application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the LogSpace application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static LogSpaceApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(LogSpaceApplication).Assembly);
    }

    internal static LogSpaceApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new LogSpaceApplicationBuilder(args, resourceAssembly);
    }
}
