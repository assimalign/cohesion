using System;
using System.ComponentModel;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Antiforgery.Internal;

namespace Assimalign.Cohesion.Web.Antiforgery;

/// <summary>
/// The factory behind <c>builder.Services.AddAntiforgery(...)</c>. Applications call that verb, not this
/// type.
/// </summary>
/// <remarks>
/// <para>
/// The verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>): every compilation
/// that references both this package and <c>Assimalign.Cohesion.DependencyInjection</c>, as every Web
/// application does through <c>Web.Hosting</c>, receives both <see cref="CreateFeature(Action{HttpAntiforgeryOptions})"/>
/// overloads as <c>AddAntiforgery</c> on <c>IServiceProviderBuilder</c>. The verb registers the feature
/// the factory returns as an <see cref="IHttpFeature"/> singleton (owner decision 34, #1380).
/// </para>
/// <para>
/// The type is public only because the generated verb, compiled into the application, calls it. It is
/// the static-factory shape because the verb's configuration is optional and one overload takes the
/// application's data-protection provider, which the builder template's single configure callback
/// cannot carry.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AntiforgeryComponents
{
    // The purpose chain the data-protection protector is derived for. It is part of the token format:
    // changing it invalidates every outstanding token, so a new chain needs a new version segment.
    private const string purpose = "Assimalign.Cohesion.Web.Antiforgery";
    private const string purposeVersion = "v1";

    /// <summary>
    /// Creates the application's antiforgery feature with tokens protected by a per-process random key.
    /// Use this form for development only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without a data-protection provider, tokens are signed with a random key generated when the
    /// application starts: tokens minted before a restart stop validating, and instances behind a load
    /// balancer reject each other's tokens. For any deployed application, use the overload that takes an
    /// <see cref="IDataProtectionProvider"/>, or set <see cref="HttpAntiforgeryOptions.Protector"/> in
    /// <paramref name="configure"/>.
    /// </para>
    /// <para>
    /// Registering again replaces the earlier registration: the last <c>AddAntiforgery</c> call supplies
    /// the service every exchange carries and <c>UseAntiforgery</c> validates with.
    /// </para>
    /// </remarks>
    /// <param name="configure">An optional callback to configure the token names, cookie attributes, and protector.</param>
    /// <returns>The antiforgery feature, registered by the verb as an <see cref="IHttpFeature"/> singleton.</returns>
    public static IHttpFeature CreateFeature(Action<HttpAntiforgeryOptions>? configure = null)
    {
        return Create(dataProtectionProvider: null, configure);
    }

    /// <summary>
    /// Creates the application's antiforgery feature with tokens protected by the application's
    /// data-protection key ring, so they survive restarts and validate on every instance that shares the
    /// key repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service protects tokens with a protector derived from <paramref name="dataProtectionProvider"/>
    /// for the antiforgery purpose, so no other payload the same key ring protects (an authentication
    /// ticket, for example) is accepted as a token. To share the key ring cookie authentication uses, pass
    /// the same provider to both, for example to <c>AuthenticationBuilder.UseDataProtection</c>. A
    /// protector set explicitly in <paramref name="configure"/> takes precedence over the provider.
    /// </para>
    /// <para>
    /// Registering again replaces the earlier registration: the last <c>AddAntiforgery</c> call supplies
    /// the service every exchange carries and <c>UseAntiforgery</c> validates with.
    /// </para>
    /// </remarks>
    /// <param name="dataProtectionProvider">The application's data-protection provider.</param>
    /// <param name="configure">An optional callback to configure the token names, cookie attributes, and protector.</param>
    /// <returns>The antiforgery feature, registered by the verb as an <see cref="IHttpFeature"/> singleton.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataProtectionProvider"/> is <see langword="null"/>.</exception>
    public static IHttpFeature CreateFeature(
        IDataProtectionProvider dataProtectionProvider,
        Action<HttpAntiforgeryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        return Create(dataProtectionProvider, configure);
    }

    private static AntiforgeryFeature Create(
        IDataProtectionProvider? dataProtectionProvider,
        Action<HttpAntiforgeryOptions>? configure)
    {
        HttpAntiforgeryOptions options = new();
        configure?.Invoke(options);

        // An explicitly configured protector wins. Otherwise the application's key ring protects tokens,
        // and only without one does the engine fall back to its per-process random key.
        if (options.Protector is null && dataProtectionProvider is not null)
        {
            options.Protector = new DataProtectionAntiforgeryProtector(
                dataProtectionProvider.CreateProtector(purpose, purposeVersion));
        }

        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        return new AntiforgeryFeature(antiforgery, options);
    }
}
