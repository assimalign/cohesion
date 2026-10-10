using System;
using System.IO;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Composes authentication for a web application: the <see cref="AuthenticationOptions"/> the runtime
/// reads (default-scheme selections and the scheme registry) and the data-protection provider that
/// handler packages derive ticket protectors from, so the request-path handlers never touch key material
/// or configuration.
/// </summary>
/// <remarks>
/// <para>
/// Applications receive one in the <c>builder.Services.AddAuthentication(authentication => ...)</c>
/// callback and chain the scheme verbs the handler packages graft onto it (<c>AddCookie</c> from
/// <c>Assimalign.Cohesion.Web.Authentication.Cookie</c>, <c>AddJwtBearer</c> from
/// <c>Assimalign.Cohesion.Web.Authentication.Bearer</c>). That verb is a component integration
/// (<c>Properties/ComponentIntegrations.cs</c>, owner decision 34): it creates the builder, runs the
/// callback, calls <see cref="Build"/>, and registers the resulting <see cref="IAuthenticationService"/>
/// as an <c>IHttpFeature</c> singleton. A composition surface without a service container registers
/// <see cref="Build"/>'s result through <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// <para>
/// This type lives with the scheme model, not in <c>Web.Hosting</c>: handler packages extend it, and
/// nothing in the Web area may depend on the hosting runtime. Composition stays dependency-free: schemes
/// are registered as values and no service container is involved.
/// </para>
/// </remarks>
public sealed class AuthenticationBuilder
{
    private readonly AuthenticationOptions _options = new();
    private IDataProtectionProvider? _dataProtectionProvider;
    private bool _isDataProtectionProviderResolved;

    /// <summary>
    /// Initializes a builder with no schemes, no default schemes, and the default data-protection
    /// provider.
    /// </summary>
    public AuthenticationBuilder()
    {
    }

    /// <summary>
    /// Gets the authentication options being composed (default-scheme selections and the scheme
    /// registry).
    /// </summary>
    public AuthenticationOptions Options => _options;

    /// <summary>
    /// Gets the data-protection provider scheme verbs derive ticket protectors from. Unless
    /// <see cref="UseDataProtection"/> supplied one, a file-system-backed rotating key ring rooted at
    /// <c>DataProtection-Keys</c> under <see cref="AppContext.BaseDirectory"/> is created on first use.
    /// </summary>
    /// <remarks>
    /// Reading the provider fixes it, whoever reads it: a scheme verb derives its protector from it at
    /// registration, and the builder cannot tell that read from any other, so <see cref="UseDataProtection"/>
    /// must run before the first read, by a scheme or by a caller sharing the key ring with another
    /// feature.
    /// </remarks>
    public IDataProtectionProvider DataProtectionProvider
    {
        get
        {
            _isDataProtectionProviderResolved = true;
            return _dataProtectionProvider ??= CreateDefaultProvider();
        }
    }

    /// <summary>
    /// Sets the data-protection provider scheme verbs derive ticket protectors from, for example a shared
    /// key ring or one rooted at the host's content root. Call it before registering the schemes that use
    /// it.
    /// </summary>
    /// <param name="dataProtectionProvider">The application's data-protection provider.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataProtectionProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DataProtectionProvider"/> was already read, by a scheme registered earlier or by a caller,
    /// and <paramref name="dataProtectionProvider"/> is a different provider; a scheme that derived its
    /// protector from the earlier provider would keep sealing tickets with it.
    /// </exception>
    public AuthenticationBuilder UseDataProtection(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        if (_isDataProtectionProviderResolved && !ReferenceEquals(_dataProtectionProvider, dataProtectionProvider))
        {
            throw new InvalidOperationException(
                "The data-protection provider has already been read, by a registered scheme or by a caller of " +
                "AuthenticationBuilder.DataProtectionProvider. Call UseDataProtection before registering the " +
                "schemes that derive ticket protectors from it and before reading DataProtectionProvider.");
        }

        _dataProtectionProvider = dataProtectionProvider;
        return this;
    }

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

    /// <summary>
    /// Builds the authentication service over this builder's <see cref="Options"/>.
    /// </summary>
    /// <remarks>
    /// The service reads the options live, so it resolves the schemes and default-scheme selections as
    /// they stand when a request needs them.
    /// </remarks>
    /// <returns>The authentication service, an <c>IHttpFeature</c> for the application to register.</returns>
    public IAuthenticationService Build()
    {
        return AuthenticationService.Create(_options);
    }

    private static IDataProtectionProvider CreateDefaultProvider()
    {
        string keysDirectory = Path.Combine(AppContext.BaseDirectory, "DataProtection-Keys");

        // Fully qualified: the DataProtectionProvider property on this type shadows the static
        // factory class of the same name inside the type body.
        return Security.DataProtection.DataProtectionProvider.Create(KeyRepository.CreateFileSystem(keysDirectory));
    }
}
