using System;

namespace Assimalign.Cohesion.Web.Authentication.Certificate;

/// <summary>Grafts certificate schemes onto the builder supplied by <c>builder.Services.AddAuthentication</c>.</summary>
public static partial class AuthenticationBuilderCertificateExtensions
{
    extension(AuthenticationBuilder builder)
    {
        /// <summary>Registers certificate authentication with the default scheme name.</summary>
        /// <param name="configure">An optional callback configuring application validation and mapping.</param>
        /// <returns>The builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">The builder is null.</exception>
        /// <exception cref="InvalidOperationException">The scheme is already registered.</exception>
        public AuthenticationBuilder AddCertificate(Action<CertificateAuthenticationOptions>? configure = null)
            => builder.AddCertificate(CertificateAuthenticationDefaults.AuthenticationScheme, configure);

        /// <summary>Registers a named certificate scheme, snapshotting its options at registration.</summary>
        /// <param name="scheme">The scheme name, also used as the default identity's authentication type.</param>
        /// <param name="configure">An optional callback configuring application validation and mapping.</param>
        /// <returns>The builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">The builder is null.</exception>
        /// <exception cref="ArgumentException">The scheme name is null or whitespace.</exception>
        /// <exception cref="InvalidOperationException">The scheme is already registered.</exception>
        public AuthenticationBuilder AddCertificate(string scheme, Action<CertificateAuthenticationOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

            CertificateAuthenticationOptions options = new();
            configure?.Invoke(options);
            CertificateAuthenticationOptions snapshot = new()
            {
                DisplayName = options.DisplayName,
                OnCertificateValidated = options.OnCertificateValidated,
            };

            return builder.AddScheme(new AuthenticationScheme(
                scheme,
                snapshot.DisplayName,
                () => CertificateAuthentication.CreateHandler(snapshot)));
        }
    }
}
