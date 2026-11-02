using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The immutable model of one gateway's application: what the gateway deploys, and the parent it
/// joins, as validated manifest data.
/// </summary>
[DebuggerDisplay("{Name}")]
public sealed class ApplicationModel : IApplicationModel
{
    // The parameter of Create that every validation failure blames.
    private const string manifestParameter = "manifest";

    private ApplicationModel(ApplicationModelManifest manifest)
    {
        Manifest = manifest;
    }

    /// <inheritdoc/>
    public GatewayName Name => Manifest.Name;

    /// <inheritdoc/>
    public GatewayName? Parent => Manifest.Parent;

    /// <inheritdoc/>
    public ApplicationGatewayRole Role => Parent is null ? ApplicationGatewayRole.Root : ApplicationGatewayRole.Composite;

    /// <inheritdoc/>
    public ApplicationModelManifest Manifest { get; }

    /// <summary>
    /// Creates a builder that declares the application a gateway runs.
    /// </summary>
    /// <param name="name">The name of the gateway the model is for.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    public static IApplicationModelBuilder CreateBuilder(GatewayName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(name));
        }

        return new ApplicationModelBuilder(name);
    }

    /// <summary>
    /// Creates a gateway's model from manifest data, such as the manifest a child gateway sends when
    /// it joins its parent.
    /// </summary>
    /// <remarks>
    /// The rules are the ones composition enforces: the manifest names its gateway, and a parent
    /// other than itself; a resource's provider and name are unique; a dependency names another of
    /// the gateway's resources, at most once per resource, with unique permissions; the dependencies
    /// form no cycle, because they order deployment; a resource's setting names are unique, each
    /// setting has a value or an output but not both, and an output belongs to a resource the
    /// setting's resource depends on or to one of the parent gateway's resources; and a resource's
    /// extensions are unique per platform and name, each with a JSON value. The model keeps a
    /// read-only copy, so later changes to <paramref name="manifest"/>'s lists do not reach it.
    /// </remarks>
    /// <param name="manifest">The gateway's manifest.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The manifest breaks a rule; the message names the entry.</exception>
    public static ApplicationModel Create(ApplicationModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(manifest));
        }

        if (manifest.Parent is { } parent)
        {
            if (parent.Value is null)
            {
                throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(manifest));
            }

            if (parent == manifest.Name)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationParentSelf, manifest.Name), nameof(manifest));
            }
        }

        if (manifest.Resources is null)
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationManifestListMissing, manifest.Name), nameof(manifest));
        }

        // First pass: every resource, so a dependency can name one declared after it.
        var resources = new HashSet<(ProviderName Provider, ResourceName Name)>();
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

            if (!resources.Add((resource.Provider, resource.Name)))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceDuplicate, manifest.Name, resource.Provider, resource.Name), nameof(manifest));
            }
        }

        // Second pass: check each resource against the first, and copy it read-only.
        var copied = new ApplicationResourceManifest[manifest.Resources.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            copied[index] = Copy(manifest, manifest.Resources[index], resources);
        }

        var copy = new ApplicationModelManifest(manifest.Name, manifest.Parent, Array.AsReadOnly(copied));
        ThrowIfCyclic(copy);
        return new ApplicationModel(copy);
    }

    private static ApplicationResourceManifest Copy(
        ApplicationModelManifest manifest,
        ApplicationResourceManifest resource,
        HashSet<(ProviderName Provider, ResourceName Name)> resources)
    {
        GatewayName gateway = manifest.Name;
        if (resource.Dependencies is null || resource.Settings is null || resource.Extensions is null)
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationManifestListMissing, gateway), manifestParameter);
        }

        var targets = new HashSet<(ProviderName Provider, ResourceName Name)>();
        var dependencies = new ApplicationResourceDependency[resource.Dependencies.Count];
        for (int index = 0; index < dependencies.Length; index++)
        {
            ApplicationResourceDependency? dependency = resource.Dependencies[index];
            if (dependency is null || dependency.Permissions is null)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationManifestEntryNull, gateway), manifestParameter);
            }

            if (dependency.Gateway == gateway && dependency.Provider == resource.Provider && dependency.Name == resource.Name)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationDependencySelf, resource.Provider, resource.Name), manifestParameter);
            }

            // A dependency orders deployment, and a gateway orders only its own resources.
            if (dependency.Gateway != gateway || !resources.Contains((dependency.Provider, dependency.Name)))
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationDependencyUnknown, resource.Provider, resource.Name, gateway, dependency.Provider, dependency.Name, dependency.Gateway),
                    manifestParameter);
            }

            if (!targets.Add((dependency.Provider, dependency.Name)))
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationDependencyDuplicate, resource.Provider, resource.Name, gateway, dependency.Provider, dependency.Name, dependency.Gateway),
                    manifestParameter);
            }

            var permissions = new Permission[dependency.Permissions.Count];
            var named = new HashSet<Permission>();
            for (int position = 0; position < permissions.Length; position++)
            {
                Permission permission = dependency.Permissions[position];
                if (permission.Value is null)
                {
                    throw new ArgumentException(ErrorMessages.PermissionMissing, manifestParameter);
                }

                if (!named.Add(permission))
                {
                    throw new ArgumentException(Format(ErrorMessages.ApplicationPermissionDuplicate, resource.Provider, resource.Name, permission), manifestParameter);
                }

                permissions[position] = permission;
            }

            dependencies[index] = new ApplicationResourceDependency(dependency.Gateway, dependency.Provider, dependency.Name, Array.AsReadOnly(permissions));
        }

        var names = new HashSet<SettingName>();
        var settings = new ApplicationResourceSetting[resource.Settings.Count];
        for (int index = 0; index < settings.Length; index++)
        {
            ApplicationResourceSetting? setting = resource.Settings[index];
            if (setting is null)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationManifestEntryNull, gateway), manifestParameter);
            }

            if (setting.Name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.SettingNameMissing, manifestParameter);
            }

            if ((setting.Value is null) == (setting.Output is null))
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationSettingValueMissing, setting.Name, resource.Provider, resource.Name, gateway),
                    manifestParameter);
            }

            if (!names.Add(setting.Name))
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationSettingDuplicate, resource.Provider, resource.Name, gateway, setting.Name),
                    manifestParameter);
            }

            if (setting.Output is { } output)
            {
                ThrowIfUnresolvable(manifest, resource, setting, output, targets);
            }

            settings[index] = setting;
        }

        var declared = new HashSet<(PlatformName Platform, ExtensionName Name)>();
        var extensions = new ApplicationResourceExtension[resource.Extensions.Count];
        for (int index = 0; index < extensions.Length; index++)
        {
            ApplicationResourceExtension? extension = resource.Extensions[index];
            if (extension is null)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationManifestEntryNull, gateway), manifestParameter);
            }

            if (extension.Platform.Value is null)
            {
                throw new ArgumentException(ErrorMessages.PlatformNameMissing, manifestParameter);
            }

            if (extension.Name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ExtensionNameMissing, manifestParameter);
            }

            if (extension.Value.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationExtensionValueMissing, extension.Name, extension.Platform, resource.Provider, resource.Name, gateway),
                    manifestParameter);
            }

            if (!declared.Add((extension.Platform, extension.Name)))
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationExtensionDuplicate, resource.Provider, resource.Name, gateway, extension.Name, extension.Platform),
                    manifestParameter);
            }

            // A copy, so the model does not depend on the document the value came from.
            extensions[index] = extension with { Value = extension.Value.Clone() };
        }

        return new ApplicationResourceManifest(
            resource.Provider,
            resource.Name,
            Array.AsReadOnly(dependencies),
            Array.AsReadOnly(settings),
            Array.AsReadOnly(extensions));
    }

    // An output exists once its resource is deployed. One of the gateway's own resources is deployed
    // first only when it is a dependency; one of the parent's resources the parent reports when the
    // gateway joins it. A gateway gets no other gateway's outputs.
    private static void ThrowIfUnresolvable(
        ApplicationModelManifest manifest,
        ApplicationResourceManifest resource,
        ApplicationResourceSetting setting,
        ApplicationResourceOutput output,
        HashSet<(ProviderName Provider, ResourceName Name)> targets)
    {
        GatewayName gateway = manifest.Name;
        if (output.Gateway.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, manifestParameter);
        }

        if (output.Provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, manifestParameter);
        }

        if (output.Name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, manifestParameter);
        }

        if (output.Output.Value is null)
        {
            throw new ArgumentException(ErrorMessages.OutputNameMissing, manifestParameter);
        }

        if (output.Gateway != gateway)
        {
            if (output.Gateway != manifest.Parent)
            {
                throw new ArgumentException(
                    Format(ErrorMessages.ApplicationSettingGatewayUnknown, setting.Name, resource.Provider, resource.Name, gateway, output.Output, output.Provider, output.Name, output.Gateway),
                    manifestParameter);
            }

            return;
        }

        if (output.Provider == resource.Provider && output.Name == resource.Name)
        {
            throw new ArgumentException(
                Format(ErrorMessages.ApplicationSettingOutputSelf, setting.Name, resource.Provider, resource.Name, output.Output),
                manifestParameter);
        }

        if (!targets.Contains((output.Provider, output.Name)))
        {
            throw new ArgumentException(
                Format(ErrorMessages.ApplicationSettingNotDependency, setting.Name, resource.Provider, resource.Name, gateway, output.Provider, output.Name, output.Gateway),
                manifestParameter);
        }
    }

    // Dependencies order deployment, so they may not form a cycle. A depth-first walk that meets a
    // resource still on its path has found one.
    private static void ThrowIfCyclic(ApplicationModelManifest manifest)
    {
        var dependencies = new Dictionary<(ProviderName Provider, ResourceName Name), IReadOnlyList<ApplicationResourceDependency>>();
        foreach (ApplicationResourceManifest resource in manifest.Resources)
        {
            dependencies.Add((resource.Provider, resource.Name), resource.Dependencies);
        }

        // A resource is absent until the walk reaches it, false while it is on the walk's path, and
        // true once everything it depends on has been walked.
        var walked = new Dictionary<(ProviderName Provider, ResourceName Name), bool>();
        var path = new List<(ProviderName Provider, ResourceName Name)>();
        var next = new List<int>();
        foreach (ApplicationResourceManifest resource in manifest.Resources)
        {
            var start = (resource.Provider, resource.Name);
            if (walked.ContainsKey(start))
            {
                continue;
            }

            walked[start] = false;
            path.Add(start);
            next.Add(0);
            while (path.Count > 0)
            {
                int top = path.Count - 1;
                IReadOnlyList<ApplicationResourceDependency> edges = dependencies[path[top]];
                if (next[top] == edges.Count)
                {
                    walked[path[top]] = true;
                    path.RemoveAt(top);
                    next.RemoveAt(top);
                    continue;
                }

                ApplicationResourceDependency edge = edges[next[top]++];
                var target = (edge.Provider, edge.Name);
                if (!walked.TryGetValue(target, out bool done))
                {
                    walked[target] = false;
                    path.Add(target);
                    next.Add(0);
                }
                else if (!done)
                {
                    var cycle = new List<string>();
                    for (int index = path.IndexOf(target); index < path.Count; index++)
                    {
                        cycle.Add(Describe(manifest.Name, path[index]));
                    }

                    cycle.Add(Describe(manifest.Name, target));
                    throw new ArgumentException(Format(ErrorMessages.ApplicationDependencyCycle, string.Join(" -> ", cycle)), manifestParameter);
                }
            }
        }
    }

    private static string Describe(GatewayName gateway, (ProviderName Provider, ResourceName Name) resource)
    {
        return string.Concat(gateway.ToString(), "/", resource.Provider.ToString(), "/", resource.Name.ToString());
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}
