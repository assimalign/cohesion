using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting.Internal;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

/// <summary>
/// Hosts a Rezolvr application and its ordered service lifecycle.
/// </summary>
public sealed class RezolvrApplication : Host<RezolvrApplicationContext>, IRezolvrApplication
{
    private readonly RezolvrApplicationContext _context;

    internal RezolvrApplication(
        RezolvrApplicationOptions options,
        RezolvrApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override RezolvrApplicationContext Context => _context;

    IRezolvrApplicationContext IRezolvrApplication.Context => _context;

    Task IRezolvrApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IRezolvrApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a Rezolvr application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the Rezolvr application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static RezolvrApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(RezolvrApplication).Assembly);
    }

    internal static RezolvrApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new RezolvrApplicationBuilder(args, resourceAssembly);
    }
}
