using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the database resource. Composition-only:
/// it wraps the composed wire-protocol servers as host services on the
/// <c>Assimalign.Cohesion.Hosting</c> execution menu and integrates them — plus any
/// additional services — with the host lifecycle.
/// </summary>
/// <remarks>
/// Registration order is the composition root's additional services first
/// (<see cref="DatabaseApplicationOptions.Services"/>), then one endpoint host
/// service per registered server (<see cref="DatabaseApplicationOptions.Servers"/>).
/// Because a host starts services in registration order and stops them in reverse,
/// the servers start last and drain first. Engines take no part in the lifecycle:
/// they are data machines — operational from creation, durably flushed and closed
/// by whichever composition root created and disposes them. Compose an application
/// through <see cref="CreateBuilder()"/> (the builder-first surface — model
/// packages register engines and servers on the root's
/// <see cref="IDatabaseApplicationBuilder"/> seam) or construct it directly from
/// fully populated <see cref="DatabaseApplicationOptions"/>.
/// </remarks>
public sealed class DatabaseApplication : Host<DatabaseApplicationContext>, IDatabaseApplication
{
    private readonly DatabaseApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public DatabaseApplication(DatabaseApplicationOptions options)
        : this(options, options is null ? null! : new DatabaseApplicationContext(options))
    {
    }

    internal DatabaseApplication(DatabaseApplicationOptions options, DatabaseApplicationContext context) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        var services = new List<IHostService>();

        // The composition root's additional services start first and stop last
        // (after the servers have drained).
        foreach (IHostService service in options.Services)
        {
            services.Add(service);
        }

        // The wire-protocol servers register last: a host starts services in
        // registration order and stops them in reverse, so every server starts
        // last and drains first.
        foreach (IDatabaseServer server in options.Servers)
        {
            services.Add(new DatabaseServerHostService(server));
        }

        context.SetHostedServices(services);
        _context = context;
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override DatabaseApplicationContext Context => _context;

    /// <summary>
    /// Creates a builder for composing a database application — the entry point of
    /// the area's builder pattern (mirrors <c>WebApplication.CreateBuilder()</c>).
    /// Model packages register their engines and servers on the returned builder
    /// through the root's <see cref="IDatabaseApplicationBuilder"/> seam.
    /// </summary>
    /// <returns>A new application builder over default options.</returns>
    public static DatabaseApplicationBuilder CreateBuilder()
    {
        return CreateBuilder(new DatabaseApplicationOptions());
    }

    /// <summary>
    /// Creates a builder for composing a database application over the specified
    /// options.
    /// </summary>
    /// <param name="options">The application options the builder composes into.</param>
    /// <returns>A new application builder over <paramref name="options"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static DatabaseApplicationBuilder CreateBuilder(DatabaseApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new DatabaseApplicationBuilder(options);
    }

    IDatabaseApplicationContext IDatabaseApplication.Context => _context;

    Task IDatabaseApplication.StartAsync(CancellationToken cancellationToken)
    {
        return ((IHost)this).StartAsync(cancellationToken);
    }

    Task IDatabaseApplication.StopAsync(CancellationToken cancellationToken)
    {
        return ((IHost)this).StopAsync(cancellationToken);
    }
}
