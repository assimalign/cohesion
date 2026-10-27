using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The immutable model of an application: what its gateway deploys, as validated manifest data.
/// </summary>
[DebuggerDisplay("{Name}")]
public sealed class ApplicationModel : IApplicationModel
{
    // The parameter of Create that every validation failure blames.
    private const string manifestParameter = "manifest";

    private ApplicationModel(ApplicationManifest manifest, ApplicationManifest deployment)
    {
        Manifest = manifest;
        Deployment = deployment;
    }

    /// <inheritdoc/>
    public GatewayName Name => Deployment.Name;

    /// <inheritdoc/>
    public ApplicationManifest Manifest { get; }

    /// <inheritdoc/>
    public ApplicationManifest Deployment { get; }

    /// <summary>
    /// Creates the model of the root gateway from manifest data.
    /// </summary>
    /// <param name="manifest">The manifest of the hierarchy.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The manifest breaks a rule; the message names the entry.</exception>
    public static ApplicationModel Create(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return Create(manifest, manifest.Name);
    }

    /// <summary>
    /// Creates the model of one gateway of a hierarchy from manifest data, such as the deployment
    /// manifest a child gateway received as JSON.
    /// </summary>
    /// <remarks>
    /// The rules are the ones composition enforces: gateway names are unique across the hierarchy;
    /// a resource's provider and name are unique within its gateway; a dependency names a resource the
    /// manifest declares, never its own resource, and at most once per resource; a dependency's
    /// permissions are unique. The model keeps a read-only copy, so later changes to
    /// <paramref name="manifest"/>'s lists do not reach it.
    /// </remarks>
    /// <param name="manifest">The manifest of the whole hierarchy, from the root gateway down.</param>
    /// <param name="gateway">The gateway the model is for.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The manifest breaks a rule, the message naming the entry; or it declares no gateway named
    /// <paramref name="gateway"/>.
    /// </exception>
    public static ApplicationModel Create(ApplicationManifest manifest, GatewayName gateway)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (gateway.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(gateway));
        }

        var resources = new HashSet<(GatewayName Gateway, ProviderName Provider, ResourceName Name)>();
        Index(manifest, [], resources);
        ApplicationManifest copy = Copy(manifest, resources);
        ApplicationManifest deployment = Find(copy, gateway)
            ?? throw new ArgumentException(Format(ErrorMessages.ApplicationModelGatewayUnknown, gateway), nameof(gateway));

        return new ApplicationModel(copy, deployment);
    }

    private static ApplicationManifest? Find(ApplicationManifest manifest, GatewayName gateway)
    {
        if (manifest.Name == gateway)
        {
            return manifest;
        }

        foreach (ApplicationManifest child in manifest.Gateways)
        {
            ApplicationManifest? found = Find(child, gateway);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // First pass: every gateway and resource, so a dependency can point anywhere in the hierarchy.
    private static void Index(
        ApplicationManifest manifest,
        HashSet<GatewayName> gateways,
        HashSet<(GatewayName Gateway, ProviderName Provider, ResourceName Name)> resources)
    {
        if (manifest.Name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(manifest));
        }

        // A repeated name also stops a manifest that contains itself.
        if (!gateways.Add(manifest.Name))
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationGatewayDuplicate, manifest.Name), nameof(manifest));
        }

        if (manifest.Resources is null || manifest.Gateways is null)
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationManifestListMissing, manifest.Name), nameof(manifest));
        }

        foreach (ApplicationResourceManifest? resource in manifest.Resources)
        {
            if (resource is null)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationManifestEntryNull, manifest.Name), nameof(manifest));
            }

            if (resource.Provider.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(manifest));
            }

            if (resource.Name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(manifest));
            }

            if (!resources.Add((manifest.Name, resource.Provider, resource.Name)))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceDuplicate, manifest.Name, resource.Provider, resource.Name), nameof(manifest));
            }
        }

        foreach (ApplicationManifest? gateway in manifest.Gateways)
        {
            if (gateway is null)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationManifestEntryNull, manifest.Name), nameof(manifest));
            }

            Index(gateway, gateways, resources);
        }
    }

    // Second pass: check each dependency against the index, and copy the hierarchy read-only.
    private static ApplicationManifest Copy(
        ApplicationManifest manifest,
        HashSet<(GatewayName Gateway, ProviderName Provider, ResourceName Name)> resources)
    {
        var copiedResources = new ApplicationResourceManifest[manifest.Resources.Count];
        for (int index = 0; index < copiedResources.Length; index++)
        {
            copiedResources[index] = Copy(manifest.Name, manifest.Resources[index], resources);
        }

        var copiedGateways = new ApplicationManifest[manifest.Gateways.Count];
        for (int index = 0; index < copiedGateways.Length; index++)
        {
            copiedGateways[index] = Copy(manifest.Gateways[index], resources);
        }

        return new ApplicationManifest(manifest.Name, Array.AsReadOnly(copiedResources), Array.AsReadOnly(copiedGateways));
    }

    private static ApplicationResourceManifest Copy(
        GatewayName gateway,
        ApplicationResourceManifest resource,
        HashSet<(GatewayName Gateway, ProviderName Provider, ResourceName Name)> resources)
    {
        if (resource.Dependencies is null)
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationManifestListMissing, gateway), manifestParameter);
        }

        var targets = new HashSet<(GatewayName Gateway, ProviderName Provider, ResourceName Name)>();
        var dependencies = new ApplicationResourceDependency[resource.Dependencies.Count];
        for (int index = 0; index < dependencies.Length; index++)
        {
            ApplicationResourceDependency? dependency = resource.Dependencies[index];
            if (dependency is null || dependency.Permissions is null)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationManifestEntryNull, gateway), manifestParameter);
            }

            var target = (dependency.Gateway, dependency.Provider, dependency.Name);
            if (target == (gateway, resource.Provider, resource.Name))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationDependencySelf, resource.Provider, resource.Name), manifestParameter);
            }

            if (!resources.Contains(target))
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationDependencyUnknown, resource.Provider, resource.Name, gateway, dependency.Provider, dependency.Name, dependency.Gateway),
                    manifestParameter);
            }

            if (!targets.Add(target))
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationDependencyDuplicate, resource.Provider, resource.Name, gateway, dependency.Provider, dependency.Name, dependency.Gateway),
                    manifestParameter);
            }

            var permissions = new ResourcePermission[dependency.Permissions.Count];
            var named = new HashSet<ResourcePermission>();
            for (int position = 0; position < permissions.Length; position++)
            {
                ResourcePermission permission = dependency.Permissions[position];
                if (permission.Value is null)
                {
                    throw new ArgumentException(ErrorMessages.ResourcePermissionMissing, manifestParameter);
                }

                if (!named.Add(permission))
                {
                    throw new ArgumentException(Format(ErrorMessages.ApplicationPermissionDuplicate, resource.Provider, resource.Name, permission), manifestParameter);
                }

                permissions[position] = permission;
            }

            dependencies[index] = new ApplicationResourceDependency(dependency.Gateway, dependency.Provider, dependency.Name, Array.AsReadOnly(permissions));
        }

        return new ApplicationResourceManifest(resource.Provider, resource.Name, Array.AsReadOnly(dependencies));
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}
