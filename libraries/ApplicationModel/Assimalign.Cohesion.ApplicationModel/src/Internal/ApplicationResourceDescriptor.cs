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
    private readonly List<ApplicationResourceDescriptor> _targets = [];
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

        // A new dependency must not lead back here: dependencies order deployment, so a cycle
        // could never start.
        List<ApplicationResourceDescriptor>? path = target.FindPath(this);
        if (path is not null)
        {
            path.Insert(0, this);
            throw new ArgumentException(Format(ErrorMessages.ApplicationDependencyCycle, Describe(path)), nameof(resource));
        }

        _dependencies.Add(new ApplicationResourceDependency(gateway, target.Provider, target.Name, Union([], permissions)));
        _targets.Add(target);
        return this;
    }

    public ApplicationResourceOutput Output(OutputName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.OutputNameMissing, nameof(name));
        }

        return new ApplicationResourceOutput(Set.Name, Provider, Name, name);
    }

    public ApplicationResourceManifest ToManifest()
    {
        return new ApplicationResourceManifest(Provider, Name, Array.AsReadOnly(_dependencies.ToArray()));
    }

    // The dependency path from this resource to another, both included, or null when this resource
    // does not depend on it, directly or through others.
    private List<ApplicationResourceDescriptor>? FindPath(ApplicationResourceDescriptor destination)
    {
        var previous = new Dictionary<ApplicationResourceDescriptor, ApplicationResourceDescriptor?> { [this] = null };
        var pending = new Stack<ApplicationResourceDescriptor>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            ApplicationResourceDescriptor current = pending.Pop();
            if (ReferenceEquals(current, destination))
            {
                var path = new List<ApplicationResourceDescriptor>();
                for (ApplicationResourceDescriptor? step = current; step is not null; step = previous[step])
                {
                    path.Add(step);
                }

                path.Reverse();
                return path;
            }

            foreach (ApplicationResourceDescriptor next in current._targets)
            {
                if (previous.TryAdd(next, current))
                {
                    pending.Push(next);
                }
            }
        }

        return null;
    }

    private static string Describe(List<ApplicationResourceDescriptor> path)
    {
        var names = new string[path.Count];
        for (int index = 0; index < names.Length; index++)
        {
            names[index] = string.Concat(path[index].Set.Name.ToString(), "/", path[index].Provider.ToString(), "/", path[index].Name.ToString());
        }

        return string.Join(" -> ", names);
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
