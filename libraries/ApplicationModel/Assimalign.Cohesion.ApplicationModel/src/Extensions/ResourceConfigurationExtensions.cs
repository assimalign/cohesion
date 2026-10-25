using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Declares a resource's deployment configuration and its platform extension data on the resource's descriptor.
/// </summary>
/// <remarks>
/// The members are generic over the descriptor type, so an area descriptor such as <c>IWebResourceDescriptor</c> keeps
/// its type and the area's own verbs still chain after them. Only manifest-backed resources this application realizes
/// carry either: a reference to another application's resource is configured by that application's code.
/// </remarks>
public static class ResourceConfigurationExtensions
{
    extension<TDescriptor>(TDescriptor descriptor)
        where TDescriptor : IApplicationResourceDescriptor
    {
        /// <summary>Adds an RFC 7396 merge patch for one configuration section to the resource's deployment layer.</summary>
        /// <param name="section">The section path the resource's runtime surface declares, such as <c>Web:Cors</c>.</param>
        /// <param name="patch">The merge patch as JSON text. JSON <c>null</c> resets the section to the resource's own value.</param>
        /// <returns>The descriptor, for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Patches for one section compose in call order. <see cref="IApplicationBuilder.Build"/> validates each section's
        /// patch against the runtime surface the resource's build described, and fails with the resource and the path
        /// named for an unknown section, an unknown member, or a value of the wrong type.
        /// </para>
        /// <para>
        /// The gateway delivers the layer as the deployment record's <c>deployment.json</c>. It is part of the resource's
        /// deployment content, so changing it starts a new deployment generation, which discards management overrides.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">The descriptor or <paramref name="patch"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="section"/> is the default value, or <paramref name="patch"/> is not JSON.</exception>
        /// <exception cref="InvalidOperationException">
        /// The descriptor belongs to a built model, or its resource is not a manifest-backed resource this application realizes.
        /// </exception>
        public TDescriptor Configure(ResourceSectionPath section, string patch)
        {
            ArgumentNullException.ThrowIfNull(patch);
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(patch);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(ErrorMessages.ResourceConfigurationPatchInvalid, nameof(patch), exception);
            }

            Authoring(descriptor, section).Configure(section, node);
            return descriptor;
        }

        /// <summary>Adds an RFC 7396 merge patch for one configuration section to the resource's deployment layer.</summary>
        /// <param name="section">The section path the resource's runtime surface declares, such as <c>Web:Cors</c>.</param>
        /// <param name="patch">The merge patch. JSON <c>null</c> resets the section to the resource's own value.</param>
        /// <returns>The descriptor, for chaining.</returns>
        /// <remarks>Behaves as the JSON-text overload.</remarks>
        /// <exception cref="ArgumentNullException">The descriptor is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="section"/> or <paramref name="patch"/> is the default value.</exception>
        /// <exception cref="InvalidOperationException">
        /// The descriptor belongs to a built model, or its resource is not a manifest-backed resource this application realizes.
        /// </exception>
        public TDescriptor Configure(ResourceSectionPath section, JsonElement patch)
        {
            if (patch.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException(ErrorMessages.ResourceConfigurationPatchInvalid, nameof(patch));
            }

            Authoring(descriptor, section).Configure(section, JsonNode.Parse(patch.GetRawText()));
            return descriptor;
        }

        /// <summary>Stores platform extension data on the resource, replacing any value under the same key.</summary>
        /// <param name="key">The platform-qualified key, such as <c>kubernetes/hpa</c>.</param>
        /// <param name="value">The data.</param>
        /// <returns>The descriptor, for chaining.</returns>
        /// <remarks>
        /// Platform packages build typed verbs, such as <c>ConfigureHpa</c>, on this member. The data is copied into the
        /// resource's plan; the gateway named by the key's platform part compiles it and refuses a key it does not know,
        /// and every other gateway ignores it.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The descriptor is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="value"/> is the default value.</exception>
        /// <exception cref="InvalidOperationException">
        /// The descriptor belongs to a built model, or its resource is not a manifest-backed resource this application realizes.
        /// </exception>
        public TDescriptor SetExtension(ResourceExtensionKey key, JsonElement value)
        {
            if (key.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceExtensionKeyInvalid, nameof(key));
            }

            if (value.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException(ErrorMessages.ResourceExtensionValueInvalid, nameof(value));
            }

            Authoring(descriptor).SetExtension(key, value);
            return descriptor;
        }

        /// <summary>Reads the platform extension data stored on the resource.</summary>
        /// <param name="key">The platform-qualified key.</param>
        /// <param name="value">The data, when the resource carries it.</param>
        /// <returns>Whether the resource carries data under <paramref name="key"/>.</returns>
        /// <remarks>A built descriptor reads its plan's extensions; an authoring descriptor reads what has been stored so far.</remarks>
        /// <exception cref="ArgumentNullException">The descriptor is null.</exception>
        public bool TryGetExtension(ResourceExtensionKey key, out JsonElement value)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (key.Value is not null)
            {
                if (descriptor.Plan is ResourcePlan plan)
                {
                    return plan.Extensions.TryGetValue(key.Value, out value);
                }

                if (descriptor.Resource is PlannedResource planned)
                {
                    return planned.TryGetExtension(key, out value);
                }
            }

            value = default;
            return false;
        }
    }

    private static PlannedResource Authoring(IApplicationResourceDescriptor descriptor, ResourceSectionPath section)
    {
        if (section.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceSectionPathInvalid, nameof(section));
        }

        return Authoring(descriptor);
    }

    private static PlannedResource Authoring(IApplicationResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Plan is not null)
        {
            throw new InvalidOperationException(ErrorMessages.ResourceConfigurationAfterBuild);
        }

        return descriptor.Resource is PlannedResource planned and not IExternalResource
            ? planned
            : throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture, ErrorMessages.ResourceConfigurationUnsupported, descriptor.Resource.Name));
    }
}
