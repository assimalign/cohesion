using System;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Authentication.Cookie;

/// <summary>
/// Grafts the cookie scheme verbs onto <see cref="AuthenticationBuilder"/>, so registering cookie
/// authentication reads identically wherever the builder came from:
/// <c>builder.AddAuthentication(...).AddCookie(...)</c>.
/// </summary>
/// <remarks>
/// The ticket protector is derived here — at composition time, from the builder's data-protection
/// provider, scoped per scheme so two cookie schemes cannot read each other's tickets. The
/// request-path handler never sees key material.
/// </remarks>
public static class AuthenticationBuilderCookieExtensions
{
    private const string CookiePurpose = "Assimalign.Cohesion.Web.Authentication.Cookie";
    private const string CookiePurposeVersion = "v1";

    extension(AuthenticationBuilder builder)
    {
        /// <summary>
        /// Registers a cookie authentication scheme under the default scheme name
        /// (<see cref="CookieAuthenticationDefaults.AuthenticationScheme"/>).
        /// </summary>
        /// <param name="configure">An optional callback to configure the cookie options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
        public AuthenticationBuilder AddCookie(Action<CookieAuthenticationOptions>? configure = null)
            => builder.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, configure);

        /// <summary>
        /// Registers a cookie authentication scheme.
        /// </summary>
        /// <param name="scheme">The scheme name.</param>
        /// <param name="configure">An optional callback to configure the cookie options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="scheme"/> is <see langword="null"/> or whitespace.</exception>
        /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
        public AuthenticationBuilder AddCookie(string scheme, Action<CookieAuthenticationOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

            CookieAuthenticationOptions options = new();
            configure?.Invoke(options);

            // The ticket protector is the crypto seam: derive it from the builder's rotating key
            // ring, scoped per scheme so two cookie schemes cannot read each other's tickets.
            options.TicketProtector ??= builder.DataProtectionProvider
                .CreateProtector(CookiePurpose, scheme, CookiePurposeVersion);

            return builder.AddScheme(new AuthenticationScheme(
                scheme,
                options.DisplayName,
                () => CookieAuthentication.CreateHandler(options)));
        }
    }
}
