using System;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>Exposes a <see cref="CorsPolicyStore"/> as a live section of the resource's deployment record.</summary>
public static class CorsPolicyStoreExtensions
{
    private static readonly ResourceConfigurationPath _defaultPath = "Web:Cors";

    extension(CorsPolicyStore store)
    {
        /// <summary>Creates the live <c>Web:Cors</c> configuration section over this store.</summary>
        /// <remarks>
        /// The store's configuration when this is called becomes the section's baseline, so call it after the
        /// application has configured the store. Register the section in the resource's services before the
        /// host is built, and install the same store with <c>UseDynamicCors</c>.
        /// </remarks>
        /// <returns>A section whose deployment, claim and override layers replace the store's policies.</returns>
        /// <exception cref="ArgumentNullException">The store is null.</exception>
        public ResourceConfigurationSection AsResourceConfiguration() => store.AsResourceConfiguration(_defaultPath);

        /// <summary>Creates a live configuration section over this store at a path of the application's choosing.</summary>
        /// <remarks>
        /// The store's configuration when this is called becomes the section's baseline. A value the store
        /// refuses, such as one that omits a named policy endpoints may use, leaves every published policy in
        /// force and reports the store's reason in the section's status.
        /// </remarks>
        /// <param name="path">The configuration path, for an application that publishes more than one store.</param>
        /// <returns>A section whose deployment, claim and override layers replace the store's policies.</returns>
        /// <exception cref="ArgumentNullException">The store is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is the default value.</exception>
        public ResourceConfigurationSection AsResourceConfiguration(ResourceConfigurationPath path)
        {
            ArgumentNullException.ThrowIfNull(store);
            return ResourceConfigurationSection.Create(
                path,
                CorsJsonContext.Default.CorsConfiguration,
                ResourceConfigurationMutability.Live,
                store.GetConfiguration(),
                configuration => Replace(store, configuration));
        }
    }

    private static string? Replace(CorsPolicyStore store, CorsConfiguration configuration)
    {
        try
        {
            store.Replace(configuration);
            return null;
        }
        catch (ArgumentException exception)
        {
            // The store validates the whole replacement before it publishes any of it. Its messages name
            // policies, origins and headers, which the section's authenticated status may carry.
            return "cors-invalid: " + exception.Message;
        }
    }
}
