using System;

using Assimalign.Cohesion.Web.Authentication;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Grafts the JWT bearer scheme verbs onto <see cref="AuthenticationBuilder"/>, so registering
/// bearer authentication reads identically wherever the builder came from:
/// <c>builder.AddAuthentication(...).AddJwtBearer(...)</c>.
/// </summary>
public static class AuthenticationBuilderJwtBearerExtensions
{
    extension(AuthenticationBuilder builder)
    {
        /// <summary>
        /// Registers a JWT bearer authentication scheme under the default scheme name
        /// (<see cref="JwtBearerDefaults.AuthenticationScheme"/>).
        /// </summary>
        /// <param name="configure">An optional callback to configure the bearer options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
        public AuthenticationBuilder AddJwtBearer(Action<JwtBearerOptions>? configure = null)
            => builder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, configure);

        /// <summary>
        /// Registers a JWT bearer authentication scheme.
        /// </summary>
        /// <param name="scheme">The scheme name.</param>
        /// <param name="configure">An optional callback to configure the bearer options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="scheme"/> is <see langword="null"/> or whitespace.</exception>
        /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
        public AuthenticationBuilder AddJwtBearer(string scheme, Action<JwtBearerOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

            JwtBearerOptions options = new();
            configure?.Invoke(options);

            return builder.AddScheme(new AuthenticationScheme(
                scheme,
                options.DisplayName,
                () => JwtBearerAuthentication.CreateHandler(options)));
        }
    }
}
