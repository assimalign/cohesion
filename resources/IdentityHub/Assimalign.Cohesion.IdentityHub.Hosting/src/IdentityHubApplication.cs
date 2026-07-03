using System;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the identity provider resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class IdentityHubApplication : Host<IdentityHubApplicationContext>
{
    private readonly IdentityHubApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityHubApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public IdentityHubApplication(IdentityHubApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new IdentityHubApplicationContext(options, new IHostService[]
        {
            new IdentityEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override IdentityHubApplicationContext Context => _context;
}