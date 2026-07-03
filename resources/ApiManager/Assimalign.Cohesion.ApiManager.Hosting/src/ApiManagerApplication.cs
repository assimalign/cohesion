using System;

namespace Assimalign.Cohesion.ApiManager.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ApiManager.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the API gateway and management plane resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class ApiManagerApplication : Host<ApiManagerApplicationContext>
{
    private readonly ApiManagerApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiManagerApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ApiManagerApplication(ApiManagerApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new ApiManagerApplicationContext(options, new IHostService[]
        {
            new GatewayEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override ApiManagerApplicationContext Context => _context;
}