using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.ApiManager.Hosting.Internal;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ApiManager.Hosting;

/// <summary>
/// Hosts an ApiManager application and its ordered service lifecycle.
/// </summary>
public sealed class ApiManagerApplication : Host<ApiManagerApplicationContext>, IApiManagerApplication
{
    private readonly ApiManagerApplicationContext _context;

    internal ApiManagerApplication(
        ApiManagerApplicationOptions options,
        ApiManagerApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override ApiManagerApplicationContext Context => _context;

    IApiManagerApplicationContext IApiManagerApplication.Context => _context;

    Task IApiManagerApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IApiManagerApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for an API manager application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the API manager application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static ApiManagerApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(ApiManagerApplication).Assembly);
    }

    internal static ApiManagerApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new ApiManagerApplicationBuilder(args, resourceAssembly);
    }
}
