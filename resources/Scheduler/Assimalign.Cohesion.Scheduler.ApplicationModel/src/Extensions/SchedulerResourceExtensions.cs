using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Scheduler.ApplicationModel;

/// <summary>Composition extensions for Scheduler manifests.</summary>
public static class SchedulerResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed Scheduler resource.</summary>
        /// <param name="manifest">The build-produced Scheduler manifest.</param>
        /// <param name="options">Optional singleton planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IApplicationResourceDescriptor AddScheduler(
            ResourceManifest manifest,
            SchedulerResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return builder.AddResource(new SchedulerResource(manifest, options));
        }
    }
}
