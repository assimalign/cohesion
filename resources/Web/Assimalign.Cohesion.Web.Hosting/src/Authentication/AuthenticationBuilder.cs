using System;
using System.IO;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;

namespace Assimalign.Cohesion.Web.Hosting;

/// <summary>
/// The builder-time surface for registering authentication schemes on a web application. It is the
/// single place authentication is composed: it owns the <see cref="AuthenticationOptions"/> the
/// runtime reads, and it constructs the data-protection provider that seals cookie tickets — so
/// the request-path handler packages never touch key material or configuration.
/// </summary>
/// <remarks>
/// Obtain one from <c>builder.AddAuthentication(...)</c> and chain <see cref="AddCookie(string, Action{CookieAuthenticationOptions})"/>
/// and <see cref="AddJwtBearer(string, Action{JwtBearerOptions})"/> onto it.
/// </remarks>
public sealed class AuthenticationBuilder
{
    private const string CookiePurpose = "Assimalign.Cohesion.Web.Authentication.Cookie";
    private const string CookiePurposeVersion = "v1";

    private readonly AuthenticationOptions _options;
    private readonly HostEnvironment _environment;
    private IDataProtectionProvider? _dataProtectionProvider;

    internal AuthenticationBuilder(
        AuthenticationOptions options,
        HostEnvironment environment,
        IDataProtectionProvider? dataProtectionProvider)
    {
        _options = options;
        _environment = environment;
        _dataProtectionProvider = dataProtectionProvider;
    }

    /// <summary>
    /// Gets the authentication options being composed (default-scheme selections and the scheme
    /// registry).
    /// </summary>
    public AuthenticationOptions Options => _options;

    /// <summary>
    /// Registers a pre-built scheme.
    /// </summary>
    /// <param name="scheme">The scheme to register.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is <see langword="null"/>.</exception>
    public AuthenticationBuilder AddScheme(AuthenticationScheme scheme)
    {
        _options.AddScheme(scheme);
        return this;
    }

    /// <summary>
    /// Registers a cookie authentication scheme under the default scheme name
    /// (<see cref="CookieAuthenticationDefaults.AuthenticationScheme"/>).
    /// </summary>
    /// <param name="configure">An optional callback to configure the cookie options.</param>
    /// <returns>This builder, for chaining.</returns>
    public AuthenticationBuilder AddCookie(Action<CookieAuthenticationOptions>? configure = null)
        => AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, configure);

    /// <summary>
    /// Registers a cookie authentication scheme.
    /// </summary>
    /// <param name="scheme">The scheme name.</param>
    /// <param name="configure">An optional callback to configure the cookie options.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="scheme"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
    public AuthenticationBuilder AddCookie(string scheme, Action<CookieAuthenticationOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        CookieAuthenticationOptions options = new();
        configure?.Invoke(options);

        // The ticket protector is the crypto seam: derive it here (composition root) from the
        // rotating key ring, scoped per scheme so two cookie schemes cannot read each other's
        // tickets. The handler never sees key material.
        options.TicketProtector ??= GetDataProtectionProvider()
            .CreateProtector(CookiePurpose, scheme, CookiePurposeVersion);

        _options.AddScheme(new AuthenticationScheme(
            scheme,
            options.DisplayName,
            () => CookieAuthentication.CreateHandler(options)));

        return this;
    }

    /// <summary>
    /// Registers a JWT bearer authentication scheme under the default scheme name
    /// (<see cref="JwtBearerDefaults.AuthenticationScheme"/>).
    /// </summary>
    /// <param name="configure">An optional callback to configure the bearer options.</param>
    /// <returns>This builder, for chaining.</returns>
    public AuthenticationBuilder AddJwtBearer(Action<JwtBearerOptions>? configure = null)
        => AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, configure);

    /// <summary>
    /// Registers a JWT bearer authentication scheme.
    /// </summary>
    /// <param name="scheme">The scheme name.</param>
    /// <param name="configure">An optional callback to configure the bearer options.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="scheme"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
    public AuthenticationBuilder AddJwtBearer(string scheme, Action<JwtBearerOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        JwtBearerOptions options = new();
        configure?.Invoke(options);

        _options.AddScheme(new AuthenticationScheme(
            scheme,
            options.DisplayName,
            () => JwtBearerAuthentication.CreateHandler(options)));

        return this;
    }

    private IDataProtectionProvider GetDataProtectionProvider()
        => _dataProtectionProvider ??= CreateDefaultProvider(_environment);

    private static IDataProtectionProvider CreateDefaultProvider(HostEnvironment environment)
    {
        string? root = environment.ContentRootPath?.ToString();
        string keysDirectory = Path.Combine(
            string.IsNullOrEmpty(root) ? AppContext.BaseDirectory : root,
            "DataProtection-Keys");

        return DataProtectionProvider.Create(KeyRepository.CreateFileSystem(keysDirectory));
    }
}
