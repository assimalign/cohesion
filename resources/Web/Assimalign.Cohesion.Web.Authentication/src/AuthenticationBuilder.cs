using System;
using System.IO;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The builder-time surface for registering authentication schemes on a web application. It owns
/// the <see cref="AuthenticationOptions"/> the runtime reads and carries the data-protection
/// provider that handler packages derive ticket protectors from — so the request-path handlers
/// never touch key material or configuration.
/// </summary>
/// <remarks>
/// <para>
/// Obtain one from <c>builder.AddAuthentication(...)</c> and chain the scheme verbs the handler
/// packages graft onto it (<c>AddCookie</c> from <c>Assimalign.Cohesion.Web.Authentication.Cookie</c>,
/// <c>AddJwtBearer</c> from <c>Assimalign.Cohesion.Web.Authentication.Bearer</c>).
/// </para>
/// <para>
/// This type deliberately lives with the scheme model, not in <c>Web.Hosting</c>: handler packages
/// extend it, and nothing in the Web area may depend on the hosting/runtime module. Composition
/// stays dependency-free — schemes are registered as values, the service is attached as a typed
/// feature, and no service container is involved.
/// </para>
/// </remarks>
public sealed class AuthenticationBuilder
{
    private readonly AuthenticationOptions _options;
    private IDataProtectionProvider? _dataProtectionProvider;

    internal AuthenticationBuilder(AuthenticationOptions options, IDataProtectionProvider? dataProtectionProvider)
    {
        _options = options;
        _dataProtectionProvider = dataProtectionProvider;
    }

    /// <summary>
    /// Gets the authentication options being composed (default-scheme selections and the scheme
    /// registry).
    /// </summary>
    public AuthenticationOptions Options => _options;

    /// <summary>
    /// Gets the data-protection provider scheme verbs derive ticket protectors from. When no
    /// provider was supplied to <c>AddAuthentication</c>, a file-system-backed rotating key ring
    /// rooted at <c>DataProtection-Keys</c> under <see cref="AppContext.BaseDirectory"/> is created
    /// on first use. Supply a provider explicitly to control key placement (for example, a shared
    /// key ring or a host-content-root path).
    /// </summary>
    public IDataProtectionProvider DataProtectionProvider
        => _dataProtectionProvider ??= CreateDefaultProvider();

    /// <summary>
    /// Registers a pre-built scheme.
    /// </summary>
    /// <param name="scheme">The scheme to register.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
    public AuthenticationBuilder AddScheme(AuthenticationScheme scheme)
    {
        _options.AddScheme(scheme);
        return this;
    }

    private static IDataProtectionProvider CreateDefaultProvider()
    {
        string keysDirectory = Path.Combine(AppContext.BaseDirectory, "DataProtection-Keys");

        // Fully qualified: the DataProtectionProvider property on this type shadows the static
        // factory class of the same name inside the type body.
        return Security.DataProtection.DataProtectionProvider.Create(KeyRepository.CreateFileSystem(keysDirectory));
    }
}
