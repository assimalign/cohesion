using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MediaHub;
using Assimalign.Cohesion.MediaHub.Hosting.Internal;

namespace Assimalign.Cohesion.MediaHub.Hosting;

/// <summary>
/// Hosts a MediaHub application and its ordered service lifecycle.
/// </summary>
public sealed class MediaHubApplication : Host<MediaHubApplicationContext>, IMediaHubApplication
{
    private readonly MediaHubApplicationContext _context;

    internal MediaHubApplication(
        MediaHubApplicationOptions options,
        MediaHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override MediaHubApplicationContext Context => _context;

    /// <inheritdoc />
    protected override async ValueTask DisposeAsync(bool disposing)
    {
        await base.DisposeAsync(disposing).ConfigureAwait(false);
        if (disposing)
        {
            // The application owns its service provider: disposing it releases every service a
            // registered factory created, after the host has stopped them. Instance registrations
            // stay with their callers.
            await _context.DisposeServiceProviderAsync().ConfigureAwait(false);
        }
    }

    IMediaHubApplicationContext IMediaHubApplication.Context => _context;

    Task IMediaHubApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IMediaHubApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a media hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the media hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static MediaHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(MediaHubApplication).Assembly);
    }

    internal static MediaHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new MediaHubApplicationBuilder(args, resourceAssembly);
    }
}
