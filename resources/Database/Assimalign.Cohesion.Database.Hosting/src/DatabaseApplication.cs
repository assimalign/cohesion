using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>A complete database composition with ordered hosting and explicit product ownership.</summary>
/// <remarks>Services start before servers. Disposal stops the host, disposes owned services and engines, then infrastructure.</remarks>
public sealed class DatabaseApplication : Host<DatabaseApplicationContext>, IDatabaseApplication
{
    private readonly DatabaseApplicationContext _context;
    private readonly DatabaseApplicationOwnership _ownership;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private bool _startAttempted;

    /// <summary>Builds an application from copied legacy borrowed inputs and local default infrastructure.</summary>
    /// <param name="options">The host settings and caller-owned inputs.</param>
    public DatabaseApplication(DatabaseApplicationOptions options)
        : this(new DatabaseApplicationBuilder(options).BuildComposition()) { }

    internal DatabaseApplication(DatabaseApplicationComposition composition) : base(composition.Options)
    {
        _context = composition.Context;
        _ownership = composition.Ownership;
        var services = new List<IHostService>(composition.Options.Services);
        foreach (IDatabaseServer server in composition.Context.Servers)
        {
            services.Add(new DatabaseServerHostService(server));
        }

        _context.SetHostedServices(services.AsReadOnly());
    }

    /// <summary>Gets final infrastructure and the fixed runtime engine/server registry.</summary>
    public override DatabaseApplicationContext Context => _context;

    /// <summary>Creates a builder with default settings and no command-line arguments.</summary>
    /// <returns>A new builder.</returns>
    public static DatabaseApplicationBuilder CreateBuilder() => new(new DatabaseApplicationOptions());

    /// <summary>Captures application arguments for configuration loaded at Build.</summary>
    /// <param name="args">The arguments, copied before returning.</param>
    /// <returns>A new builder honoring the existing enabled-resource host integration when present.</returns>
    public static DatabaseApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(DatabaseApplication).Assembly;
        return new DatabaseApplicationBuilder(new DatabaseApplicationOptions(), resourceAssembly, args);
    }

    /// <summary>Creates a builder from host settings and borrowed legacy inputs.</summary>
    /// <param name="options">The options copied at Build.</param>
    /// <returns>A new builder.</returns>
    public static DatabaseApplicationBuilder CreateBuilder(DatabaseApplicationOptions options) => new(options);

    /// <inheritdoc />
    protected override Task OnStartingAsync(CancellationToken cancellationToken = default)
    {
        if (_disposeTask is not null)
        {
            throw new ObjectDisposedException(nameof(DatabaseApplication));
        }

        if (_startAttempted)
        {
            throw new InvalidOperationException("A database application supports only one start lifecycle.");
        }

        _startAttempted = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsync(bool disposing)
    {
        if (!disposing)
        {
            return ValueTask.CompletedTask;
        }

        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        try
        {
            await base.DisposeAsync(true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        await _ownership.DisposeAsync(failures).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            throw new AggregateException("Database application disposal failed.", failures);
        }
    }

    IDatabaseApplicationContext IDatabaseApplication.Context => _context;
    Task IDatabaseApplication.StartAsync(CancellationToken cancellationToken) => ((IHost)this).StartAsync(cancellationToken);
    Task IDatabaseApplication.StopAsync(CancellationToken cancellationToken) => ((IHost)this).StopAsync(cancellationToken);
}
