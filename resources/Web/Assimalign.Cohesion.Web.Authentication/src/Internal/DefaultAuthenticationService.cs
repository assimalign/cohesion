using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Default <see cref="IAuthenticationService"/> implementation. Reads the live
/// <see cref="AuthenticationOptions"/> per request so schemes registered after the service was
/// created are still resolved, caches per-request handlers on <see cref="IHttpContext.Items"/>,
/// and records the default-scheme authenticate result on an <see cref="IAuthenticationResultFeature"/>.
/// </summary>
internal sealed class DefaultAuthenticationService : IAuthenticationService
{
    // Per-request handler cache key prefix. Handlers are cached on context.Items so an
    // authenticate followed by a challenge in the same request reuses one initialized instance.
    private const string HandlerItemPrefix = "Assimalign.Cohesion.Web.Authentication.Handler:";

    private readonly AuthenticationOptions _options;

    public DefaultAuthenticationService(AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public string Name => nameof(IAuthenticationService);

    /// <inheritdoc />
    public string? DefaultAuthenticateScheme => _options.ResolveDefaultAuthenticateScheme();

    /// <inheritdoc />
    public async Task<AuthenticateResult> AuthenticateAsync(IHttpContext context, string? scheme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string schemeName = ResolveScheme(scheme, _options.ResolveDefaultAuthenticateScheme(), "authenticate");
        IAuthenticationHandler handler = await GetHandlerAsync(context, schemeName, cancellationToken).ConfigureAwait(false);

        AuthenticateResult result = await handler.AuthenticateAsync(cancellationToken).ConfigureAwait(false);

        // Surface the default-scheme result on the result feature so downstream code can inspect
        // how the principal was established. Only the default authenticate scheme populates it.
        if (string.Equals(schemeName, _options.ResolveDefaultAuthenticateScheme(), StringComparison.Ordinal))
        {
            IAuthenticationResultFeature feature = context.Features.Get<IAuthenticationResultFeature>()
                ?? InstallResultFeature(context);
            feature.AuthenticateResult = result;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task ChallengeAsync(IHttpContext context, string? scheme, AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string schemeName = ResolveScheme(scheme, _options.ResolveDefaultChallengeScheme(), "challenge");
        IAuthenticationHandler handler = await GetHandlerAsync(context, schemeName, cancellationToken).ConfigureAwait(false);
        await handler.ChallengeAsync(properties, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ForbidAsync(IHttpContext context, string? scheme, AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string schemeName = ResolveScheme(scheme, _options.ResolveDefaultForbidScheme(), "forbid");
        IAuthenticationHandler handler = await GetHandlerAsync(context, schemeName, cancellationToken).ConfigureAwait(false);
        await handler.ForbidAsync(properties, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SignInAsync(IHttpContext context, string? scheme, ClaimsPrincipal user, AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        string schemeName = ResolveScheme(scheme, _options.ResolveDefaultSignInScheme(), "sign in");
        IAuthenticationHandler handler = await GetHandlerAsync(context, schemeName, cancellationToken).ConfigureAwait(false);

        if (handler is not IAuthenticationSignInHandler signInHandler)
        {
            throw new InvalidOperationException(
                $"Scheme '{schemeName}' does not support sign-in; its handler is not an {nameof(IAuthenticationSignInHandler)}.");
        }

        await signInHandler.SignInAsync(user, properties, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SignOutAsync(IHttpContext context, string? scheme, AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string schemeName = ResolveScheme(scheme, _options.ResolveDefaultSignOutScheme(), "sign out");
        IAuthenticationHandler handler = await GetHandlerAsync(context, schemeName, cancellationToken).ConfigureAwait(false);

        if (handler is not IAuthenticationSignInHandler signInHandler)
        {
            throw new InvalidOperationException(
                $"Scheme '{schemeName}' does not support sign-out; its handler is not an {nameof(IAuthenticationSignInHandler)}.");
        }

        await signInHandler.SignOutAsync(properties, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveScheme(string? requested, string? fallback, string verb)
    {
        string? schemeName = requested ?? fallback;
        if (string.IsNullOrWhiteSpace(schemeName))
        {
            throw new InvalidOperationException(
                $"No scheme was specified for {verb} and no matching default scheme is configured.");
        }

        return schemeName;
    }

    private async Task<IAuthenticationHandler> GetHandlerAsync(IHttpContext context, string schemeName, CancellationToken cancellationToken)
    {
        string itemKey = HandlerItemPrefix + schemeName;
        if (context.Items.TryGetValue(itemKey, out object? cached) && cached is IAuthenticationHandler existing)
        {
            return existing;
        }

        AuthenticationScheme scheme = _options.GetScheme(schemeName)
            ?? throw new InvalidOperationException($"Scheme '{schemeName}' is not registered.");

        IAuthenticationHandler handler = scheme.CreateHandler();
        await handler.InitializeAsync(scheme, context, cancellationToken).ConfigureAwait(false);
        context.Items[itemKey] = handler;
        return handler;
    }

    private static IAuthenticationResultFeature InstallResultFeature(IHttpContext context)
    {
        AuthenticationResultFeature feature = new();
        context.Features.Set<IAuthenticationResultFeature>(feature);
        return feature;
    }
}
