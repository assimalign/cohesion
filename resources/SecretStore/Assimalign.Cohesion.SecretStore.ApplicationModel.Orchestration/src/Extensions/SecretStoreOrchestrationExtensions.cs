using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Registers SecretStore resources as gateway providers, on an application builder or on an
/// application-set member.
/// </summary>
public static partial class SecretStoreOrchestrationExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Makes a SecretStore resource of the application the source of every
        /// <c>&lt;store&gt;:&lt;key&gt;</c> Secret mount (secret values and endpoint certificates) that
        /// names it, and resolves the declared sources of <c>secretstore.add-secret</c> commands at
        /// delivery time.
        /// </summary>
        /// <param name="store">The SecretStore resource, as returned by the verb that added it.</param>
        /// <returns>
        /// A handle that can also make the store the application's certificate authority
        /// (<see cref="SecretStoreProviderBuilder.AsCertificateAuthority"/>) or trusted-issuer store
        /// (<see cref="SecretStoreProviderBuilder.AsTrustStore"/>).
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="store"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="store"/> is manifest-backed with a kind other than <c>SecretStore</c>, or its
        /// name is not a valid mount-source name.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A provider of another type is already registered for the store's name in
        /// <see cref="ApplicationProviders.Sources"/>, or a resolver of another type is already
        /// registered for <c>secretstore.add-secret</c> in <see cref="ApplicationProviders.CommandInputs"/>.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The verb sets <c>builder.Providers.Sources[store name]</c> to a
        /// <see cref="SecretStoreSourceProvider"/> and adds one <see cref="SecretStoreAddSecretInputResolver"/>
        /// to <c>builder.Providers.CommandInputs</c>; the resolver is store-agnostic, so registering a
        /// second store does not add a second resolver.
        /// </para>
        /// <para>
        /// The verb is idempotent: calling it again for the same store keeps the existing
        /// registrations and returns a new handle. It never silently replaces a registration it did not
        /// make — a different provider under the store's name, or a different resolver for
        /// <c>secretstore.add-secret</c>, is an <see cref="InvalidOperationException"/>.
        /// <see cref="IApplicationBuilder.Build"/> copies the registrations into the built model and
        /// checks that the store is a SecretStore resource of this application.
        /// </para>
        /// </remarks>
        public SecretStoreProviderBuilder UseSecretStore(IApplicationResourceDescriptor store)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(store);

            IApplicationResource resource = store.Resource;
            if (resource is IManifestResource { Manifest: ResourceManifest manifest })
            {
                EnsureSecretStoreKind(manifest, application: null, nameof(store));
            }

            return Register(() => builder.Providers, resource.Name);
        }
    }

    extension(IApplicationProviderBuilder application)
    {
        /// <summary>
        /// Makes the named SecretStore resource of the application the source of every
        /// <c>&lt;store&gt;:&lt;key&gt;</c> Secret mount (secret values and endpoint certificates) that
        /// names it, and resolves the declared sources of <c>secretstore.add-secret</c> commands at
        /// delivery time.
        /// </summary>
        /// <param name="store">The SecretStore resource's name.</param>
        /// <returns>
        /// A handle that can also make the store the application's certificate authority
        /// (<see cref="SecretStoreProviderBuilder.AsCertificateAuthority"/>) or trusted-issuer store
        /// (<see cref="SecretStoreProviderBuilder.AsTrustStore"/>).
        /// </returns>
        /// <exception cref="ArgumentNullException">The application surface is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="store"/> is blank, is not a valid mount-source name, or names a resource
        /// whose manifest kind is not <c>SecretStore</c>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A provider of another type is already registered for the store's name in
        /// <see cref="ApplicationProviders.Sources"/>, or a resolver of another type is already
        /// registered for <c>secretstore.add-secret</c> in <see cref="ApplicationProviders.CommandInputs"/>.
        /// </exception>
        /// <remarks>
        /// <para>
        /// This is the form an application-set member uses, because a member's resources come from
        /// its imported model rather than from descriptors:
        /// </para>
        /// <code>
        /// Application.CreateSet(new LocalGateway(options), args)
        ///     .AddApplication(Applications.Platform, platform =&gt; platform
        ///         .UseSecretStore("platform-secrets")
        ///         .AsCertificateAuthority()
        ///         .AsTrustStore());
        /// </code>
        /// <para>
        /// It registers exactly what the descriptor overload registers, with the same idempotence and
        /// conflict rules, on the surface it is called on only: an application-set member gets the
        /// registrations of its own callback and never another member's. When the surface knows the
        /// resource's manifest (<see cref="IApplicationProviderBuilder.TryGetResourceManifest"/>), its kind
        /// is checked now; either way, provider validation checks — when the set binds the member's
        /// registrations, or at <see cref="IApplicationBuilder.Build"/> — that the store is a SecretStore
        /// resource of this application and not another application's.
        /// </para>
        /// <para>
        /// <see cref="IApplicationBuilder"/> does not extend <see cref="IApplicationProviderBuilder"/>, so
        /// an application built in code uses the descriptor overload, which returns the same
        /// <see cref="SecretStoreProviderBuilder"/> handle; the default builder implements both interfaces
        /// and can be cast to call this form. Both forms make the same registrations.
        /// </para>
        /// </remarks>
        public SecretStoreProviderBuilder UseSecretStore(ResourceName store)
        {
            ArgumentNullException.ThrowIfNull(application);
            ArgumentException.ThrowIfNullOrWhiteSpace(store.Value, nameof(store));

            if (application.TryGetResourceManifest(store, out ResourceManifest? manifest))
            {
                // The lookup also finds another application's resource (an external or a Local
                // --realize closure), so the message names the resource's owner, not the surface's.
                EnsureSecretStoreKind(manifest, manifest.Application, nameof(store));
            }

            return Register(() => application.Providers, store);
        }
    }

    private static SecretStoreProviderBuilder Register(Func<ApplicationProviders> current, ResourceName store)
    {
        ApplicationProviders providers = current();
        // Every conflict is checked before anything is written, so a refused call leaves the
        // registrations exactly as it found them.
        string source = store.ToString();
        bool registerSource = true;
        if (providers.Sources.TryGetValue(source, out IResourceSourceProvider? existing))
        {
            if (existing is not SecretStoreSourceProvider)
            {
                throw new InvalidOperationException(
                    $"Mount source '{source}' is already registered with provider '{existing.GetType().Name}'. " +
                    $"Remove that registration before calling UseSecretStore for resource '{source}'.");
            }

            registerSource = false;
        }

        bool registerResolver = !HasAddSecretResolver(providers.CommandInputs);
        if (registerSource)
        {
            providers.Sources[source] = new SecretStoreSourceProvider();
        }

        if (registerResolver)
        {
            providers.CommandInputs.Add(new SecretStoreAddSecretInputResolver());
        }

        return new SecretStoreProviderBuilder(current, store);
    }

    private static void EnsureSecretStoreKind(ResourceManifest manifest, ApplicationName? application, string paramName)
    {
        if (string.Equals(manifest.Kind, SecretStoreProtocol.ResourceKind, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string owner = application is ApplicationName name && !string.IsNullOrEmpty(name.ToString())
            ? $" of application '{name}'"
            : string.Empty;
        throw new ArgumentException(
            $"Resource '{manifest.Name}'{owner} is kind '{manifest.Kind}'. UseSecretStore binds " +
            $"{SecretStoreProtocol.ResourceKind} resources only.",
            paramName);
    }

    private static bool HasAddSecretResolver(IList<IResourceCommandInputResolver> resolvers)
    {
        foreach (IResourceCommandInputResolver candidate in resolvers)
        {
            if (!string.Equals(candidate.CommandKind, SecretStoreProtocol.AddSecretCommandKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate is SecretStoreAddSecretInputResolver)
            {
                return true;
            }

            throw new InvalidOperationException(
                $"Command kind '{SecretStoreProtocol.AddSecretCommandKind}' already has input resolver " +
                $"'{candidate.GetType().Name}'. Remove that registration before calling UseSecretStore, which " +
                $"registers {nameof(SecretStoreAddSecretInputResolver)}.");
        }

        return false;
    }
}
