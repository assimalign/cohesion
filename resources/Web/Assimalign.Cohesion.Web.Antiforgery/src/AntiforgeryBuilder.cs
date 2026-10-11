using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Antiforgery.Internal;

namespace Assimalign.Cohesion.Web.Antiforgery;

/// <summary>
/// Composes the application's antiforgery feature: the <see cref="HttpAntiforgeryOptions"/> (token names,
/// cookie attributes, and protector) and the data-protection key ring that protects tokens.
/// </summary>
/// <remarks>
/// <para>
/// Applications receive one in the <c>builder.Services.AddAntiforgery(antiforgery => ...)</c> callback, for
/// example <c>antiforgery => antiforgery.UseDataProtection(dataProtectionProvider)</c>. That verb is a
/// component integration (<c>Properties/ComponentIntegrations.cs</c>, owner decisions 34 and 39): it creates
/// the builder, runs the optional callback, calls <see cref="Build"/>, and registers the resulting
/// <see cref="IHttpAntiforgeryFeature"/> as an <see cref="IHttpFeature"/> singleton. A composition surface
/// without a service container registers <see cref="Build"/>'s result through
/// <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// <para>
/// Called bare, <c>AddAntiforgery()</c> protects tokens with a random key generated when the application
/// starts: tokens minted before a restart stop validating, and instances behind a load balancer reject each
/// other's tokens. Use that form for development only. A deployed application calls
/// <see cref="UseDataProtection"/>, or sets <see cref="HttpAntiforgeryOptions.Protector"/> on
/// <see cref="Options"/>.
/// </para>
/// <para>
/// Registering again replaces the earlier registration: the last <c>AddAntiforgery</c> call supplies the
/// service every exchange carries and <c>UseAntiforgery</c> validates with.
/// </para>
/// </remarks>
public sealed class AntiforgeryBuilder
{
    // The purpose chain the data-protection protector is derived for. It is part of the token format:
    // changing it invalidates every outstanding token, so a new chain needs a new version segment.
    private const string purpose = "Assimalign.Cohesion.Web.Antiforgery";
    private const string purposeVersion = "v1";

    private readonly HttpAntiforgeryOptions _options = new();
    private IDataProtectionProvider? _dataProtectionProvider;

    /// <summary>
    /// Initializes a builder with the default token names and cookie attributes, no protector, and no
    /// data-protection provider.
    /// </summary>
    public AntiforgeryBuilder()
    {
    }

    /// <summary>
    /// Gets the antiforgery options being composed: the token names, cookie attributes, and protector.
    /// </summary>
    /// <remarks>
    /// The service <see cref="Build"/> creates reads these options as they stand, so configure them before
    /// it runs and do not reuse the builder for another registration.
    /// </remarks>
    public HttpAntiforgeryOptions Options => _options;

    /// <summary>
    /// Protects tokens with the application's data-protection key ring, so they survive restarts and
    /// validate on every instance that shares the key repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service protects tokens with a protector derived from <paramref name="dataProtectionProvider"/>
    /// for the antiforgery purpose, so no other payload the same key ring protects (an authentication
    /// ticket, for example) is accepted as a token. To share the key ring cookie authentication uses, pass the
    /// same provider to both, for example to <c>AuthenticationBuilder.UseDataProtection</c>.
    /// </para>
    /// <para>
    /// A protector set explicitly on <see cref="Options"/> takes precedence over the provider. A later call
    /// replaces the provider an earlier call supplied.
    /// </para>
    /// </remarks>
    /// <param name="dataProtectionProvider">The application's data-protection provider.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataProtectionProvider"/> is <see langword="null"/>.</exception>
    public AntiforgeryBuilder UseDataProtection(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _dataProtectionProvider = dataProtectionProvider;
        return this;
    }

    /// <summary>
    /// Builds the antiforgery feature over <see cref="Options"/>.
    /// </summary>
    /// <remarks>
    /// An explicitly configured <see cref="HttpAntiforgeryOptions.Protector"/> wins. Otherwise a provider
    /// supplied through <see cref="UseDataProtection"/> protects tokens, and only without one does the
    /// engine fall back to its per-process random key.
    /// </remarks>
    /// <returns>The antiforgery feature, an <see cref="IHttpFeature"/> for the application to register.</returns>
    public IHttpAntiforgeryFeature Build()
    {
        if (_options.Protector is null && _dataProtectionProvider is not null)
        {
            _options.Protector = new DataProtectionAntiforgeryProtector(
                _dataProtectionProvider.CreateProtector(purpose, purposeVersion));
        }

        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(_options);
        return new AntiforgeryFeature(antiforgery, _options);
    }
}
