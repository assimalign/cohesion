using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[DebuggerDisplay("{Provider}/{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationResourceDescriptor : IApplicationResourceDescriptor
{
    private readonly ApplicationSetComposition _composition;
    private readonly List<ApplicationResourceDependency> _dependencies = [];
    private readonly ReadOnlyCollection<ApplicationResourceDependency> _dependenciesView;

    public ApplicationResourceDescriptor(ApplicationSetComposition composition, ProviderName provider, ResourceName name)
    {
        _composition = composition;
        _dependenciesView = _dependencies.AsReadOnly();
        Provider = provider;
        Name = name;
    }

    public IApplicationSet Set => _composition.Owner;

    public ProviderName Provider { get; }

    public ResourceName Name { get; }

    public IReadOnlyList<ApplicationResourceDependency> Dependencies => _dependenciesView;

    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource, params ResourcePermission[] permissions)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(permissions);

        if (ReferenceEquals(resource, this))
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationDependencySelf, Provider, Name), nameof(resource));
        }

        // Only a descriptor this hierarchy created can be located again from the manifest.
        if (resource is not ApplicationResourceDescriptor target || target._composition.Hierarchy != _composition.Hierarchy)
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationDependencyForeign, resource.Provider, resource.Name), nameof(resource));
        }

        foreach (ResourcePermission permission in permissions)
        {
            if (permission.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourcePermissionMissing, nameof(permissions));
            }
        }

        _composition.Hierarchy.ThrowIfBuilt();

        GatewayName gateway = target.Set.Name;
        for (int index = 0; index < _dependencies.Count; index++)
        {
            ApplicationResourceDependency existing = _dependencies[index];
            if (existing.Gateway == gateway && existing.Provider == target.Provider && existing.Name == target.Name)
            {
                _dependencies[index] = existing with { Permissions = Union(existing.Permissions, permissions) };
                return this;
            }
        }

        _dependencies.Add(new ApplicationResourceDependency(gateway, target.Provider, target.Name, Union([], permissions)));
        return this;
    }

    public ApplicationResourceManifest ToManifest()
    {
        return new ApplicationResourceManifest(Provider, Name, Array.AsReadOnly(_dependencies.ToArray()));
    }

    private static ReadOnlyCollection<ResourcePermission> Union(IReadOnlyList<ResourcePermission> existing, ResourcePermission[] added)
    {
        var permissions = new List<ResourcePermission>(existing);
        foreach (ResourcePermission permission in added)
        {
            if (!permissions.Contains(permission))
            {
                permissions.Add(permission);
            }
        }

        return permissions.AsReadOnly();
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationResourceDescriptor _descriptor;

        public DebugView(ApplicationResourceDescriptor descriptor)
        {
            _descriptor = descriptor;
        }

        public IApplicationSet Set => _descriptor.Set;

        public ProviderName Provider => _descriptor.Provider;

        public ResourceName Name => _descriptor.Name;

        public IReadOnlyList<ApplicationResourceDependency> Dependencies => _descriptor.Dependencies;
    }
}
