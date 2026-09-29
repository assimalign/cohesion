using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Registers ConfigurationStore resources as gateway mount sources, on an application builder or
/// on an application-set member.
/// </summary>
public static partial class ConfigurationStoreOrchestrationExtensions
{
    private const string configurationStoreKind = "ConfigurationStore";

    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Makes a ConfigurationStore resource of the application the source of every
        /// <c>&lt;store&gt;:&lt;namespace&gt;</c> Configuration mount that names it.
        /// </summary>
        /// <param name="store">The ConfigurationStore resource, as returned by the verb that added it.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="store"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="store"/> is manifest-backed with a kind other than <c>ConfigurationStore</c>,
        /// or its name is not a valid mount-source name.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A provider of another type is already registered for the store's name in
        /// <see cref="ApplicationProviders.Sources"/>.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The verb sets <c>builder.Providers.Sources[store name]</c> to a
        /// <see cref="ConfigurationStoreSourceProvider"/> and changes nothing else.
        /// </para>
        /// <para>
        /// The verb is idempotent: calling it again for the same store keeps the existing registration.
        /// It never silently replaces a registration it did not make — a different provider under the
        /// store's name is an <see cref="InvalidOperationException"/>. <see cref="IApplicationBuilder.Build"/>
        /// copies the registration into the built model and checks that the store is a ConfigurationStore
        /// resource of this application.
        /// </para>
        /// </remarks>
        public IApplicationBuilder UseConfigurationStore(IApplicationResourceDescriptor store)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(store);

            IApplicationResource resource = store.Resource;
            if (resource is IManifestResource { Manifest: ResourceManifest manifest })
            {
                EnsureConfigurationStoreKind(manifest, application: null, nameof(store));
            }

            Register(builder.Providers, resource.Name);
            return builder;
        }
    }

    extension(IApplicationProviderBuilder application)
    {
        /// <summary>
        /// Makes the named ConfigurationStore resource of the application the source of every
        /// <c>&lt;store&gt;:&lt;namespace&gt;</c> Configuration mount that names it.
        /// </summary>
        /// <param name="store">The ConfigurationStore resource's name.</param>
        /// <returns>This provider-registration surface.</returns>
        /// <exception cref="ArgumentNullException">The application surface is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="store"/> is blank, is not a valid mount-source name, or names a resource
        /// whose manifest kind is not <c>ConfigurationStore</c>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A provider of another type is already registered for the store's name in
        /// <see cref="ApplicationProviders.Sources"/>.
        /// </exception>
        /// <remarks>
        /// <para>
        /// This is the form an application-set member uses, because a member's resources come from
        /// its imported model rather than from descriptors:
        /// </para>
        /// <code>
        /// Application.CreateSet(new LocalGateway(options), args)
        ///     .AddApplication(Applications.AppA, appa =&gt; appa.UseConfigurationStore("appa-configuration"));
        /// </code>
        /// <para>
        /// It registers exactly what the descriptor overload registers, with the same idempotence and
        /// conflict rules, on the surface it is called on only: an application-set member gets the
        /// registrations of its own callback and never another member's. When the surface knows the
        /// resource's manifest (<see cref="IApplicationProviderBuilder.TryGetResourceManifest"/>), its kind
        /// is checked now; either way, provider validation checks — when the set binds the member's
        /// registrations, or at <see cref="IApplicationBuilder.Build"/> — that the store is a
        /// ConfigurationStore resource of this application and not another application's.
        /// </para>
        /// <para>
        /// <see cref="IApplicationBuilder"/> does not extend <see cref="IApplicationProviderBuilder"/>, so
        /// an application built in code uses the descriptor overload, which returns the builder for
        /// chaining builder verbs. Both forms write the same <see cref="ApplicationProviders"/>.
        /// </para>
        /// </remarks>
        public IApplicationProviderBuilder UseConfigurationStore(ResourceName store)
        {
            ArgumentNullException.ThrowIfNull(application);
            ArgumentException.ThrowIfNullOrWhiteSpace(store.Value, nameof(store));

            if (application.TryGetResourceManifest(store, out ResourceManifest? manifest))
            {
                // The lookup also finds another application's resource (an external or a Local
                // --realize closure), so the message names the resource's owner, not the surface's.
                EnsureConfigurationStoreKind(manifest, manifest.Application, nameof(store));
            }

            Register(application.Providers, store);
            return application;
        }
    }

    private static void Register(ApplicationProviders providers, ResourceName store)
    {
        IDictionary<string, IResourceSourceProvider> sources = providers.Sources;
        string source = store.ToString();
        if (sources.TryGetValue(source, out IResourceSourceProvider? existing))
        {
            if (existing is ConfigurationStoreSourceProvider)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Mount source '{source}' is already registered with provider '{existing.GetType().Name}'. " +
                $"Remove that registration before calling UseConfigurationStore for resource '{source}'.");
        }

        sources[source] = new ConfigurationStoreSourceProvider();
    }

    private static void EnsureConfigurationStoreKind(
        ResourceManifest manifest,
        ApplicationName? application,
        string paramName)
    {
        if (string.Equals(manifest.Kind, configurationStoreKind, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string owner = application is ApplicationName name && !string.IsNullOrEmpty(name.ToString())
            ? $" of application '{name}'"
            : string.Empty;
        throw new ArgumentException(
            $"Resource '{manifest.Name}'{owner} is kind '{manifest.Kind}'. UseConfigurationStore binds " +
            $"{configurationStoreKind} resources only.",
            paramName);
    }
}
