using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Application-builder verbs for binding resources realized by another application.
/// </summary>
public static class ApplicationBuilderExternalExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Declares and binds a build-produced external resource.</summary>
        /// <param name="declaration">The generated application-boundary declaration.</param>
        /// <param name="configure">Selects a gateway, endpoint, file, or contributed resolver.</param>
        /// <returns>The external resource descriptor.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/>, <paramref name="declaration"/>, or
        /// <paramref name="configure"/> is <see langword="null"/>.
        /// </exception>
        public IApplicationResourceDescriptor RemoteReference(
            ExternalResourceDeclaration declaration,
            Action<RemoteReferenceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(declaration);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new RemoteReferenceOptions();
            configure(options);
            return builder.AddExternal(declaration, options.Resolver);
        }

        /// <summary>Declares and binds a manifest-less external resource.</summary>
        /// <param name="name">The external resource name.</param>
        /// <param name="configure">Selects a gateway, endpoint, file, or contributed resolver.</param>
        /// <returns>The external resource descriptor.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
        public IApplicationResourceDescriptor RemoteReference(
            string name,
            Action<RemoteReferenceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new RemoteReferenceOptions();
            configure(options);
            var declaration = new ExternalResourceDeclaration(
                (ResourceName)name,
                (ApplicationName)name,
                options.EndpointNames,
                optional: false);
            return builder.AddExternal(declaration, options.Resolver);
        }
    }
}
