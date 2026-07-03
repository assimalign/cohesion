using System;

namespace Assimalign.Cohesion.SecretStore.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.SecretStore.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the secret store resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class SecretStoreApplication : Host<SecretStoreApplicationContext>
{
    private readonly SecretStoreApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretStoreApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public SecretStoreApplication(SecretStoreApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new SecretStoreApplicationContext(options, new IHostService[]
        {
            new SecretsEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override SecretStoreApplicationContext Context => _context;
}