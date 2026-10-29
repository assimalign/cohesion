using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[DebuggerDisplay("{Provider}/{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationResourceDescriptor : IApplicationResourceDescriptor
{
    private readonly ApplicationSetComposition _composition;
    private readonly List<ApplicationResourceDependency> _dependencies = [];
    private readonly List<ApplicationResourceDescriptor> _targets = [];
    private readonly List<ApplicationResourceSetting> _settings = [];
    private readonly List<ApplicationResourceExtension> _extensions = [];
    private readonly ReadOnlyCollection<ApplicationResourceDependency> _dependenciesView;
    private readonly ReadOnlyCollection<ApplicationResourceSetting> _settingsView;
    private readonly ReadOnlyCollection<ApplicationResourceExtension> _extensionsView;

    public ApplicationResourceDescriptor(ApplicationSetComposition composition, ProviderName provider, ResourceName name)
    {
        _composition = composition;
        _dependenciesView = _dependencies.AsReadOnly();
        _settingsView = _settings.AsReadOnly();
        _extensionsView = _extensions.AsReadOnly();
        Provider = provider;
        Name = name;
    }

    public IApplicationSet Set => _composition.Owner;

    public ProviderName Provider { get; }

    public ResourceName Name { get; }

    public IReadOnlyList<ApplicationResourceDependency> Dependencies => _dependenciesView;

    public IReadOnlyList<ApplicationResourceSetting> Settings => _settingsView;

    public IReadOnlyList<ApplicationResourceExtension> Extensions => _extensionsView;

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

    public IApplicationResourceDescriptor WithSetting(SettingName name, string value)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.SettingNameMissing, nameof(name));
        }

        ArgumentNullException.ThrowIfNull(value);

        _composition.Hierarchy.ThrowIfBuilt();

        AddOrReplace(new ApplicationResourceSetting(name, value, null));
        return this;
    }

    public IApplicationResourceDescriptor WithSetting(SettingName name, ApplicationResourceOutput output)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.SettingNameMissing, nameof(name));
        }

        ArgumentNullException.ThrowIfNull(output);

        if (output.Output.Value is null)
        {
            throw new ArgumentException(ErrorMessages.OutputNameMissing, nameof(output));
        }

        if (output.Gateway == Set.Name && output.Provider == Provider && output.Name == Name)
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationSettingOutputSelf, name, Provider, Name, output.Output), nameof(output));
        }

        if (!_composition.Hierarchy.Resources.TryGetValue((output.Gateway, output.Provider, output.Name), out ApplicationResourceDescriptor? target))
        {
            throw new ArgumentException(
                Format(ErrorMessages.ApplicationSettingOutputUnknown, name, Provider, Name, output.Provider, output.Name, output.Gateway),
                nameof(output));
        }

        // The value exists only once the other resource is deployed, so this one depends on it. A
        // dependency that would close a cycle throws here, before the setting is recorded.
        DependsOn(target);
        AddOrReplace(new ApplicationResourceSetting(name, null, output));
        return this;
    }

    public IApplicationResourceDescriptor WithExtension(PlatformName platform, ExtensionName name, JsonElement value)
    {
        if (platform.Value is null)
        {
            throw new ArgumentException(ErrorMessages.PlatformNameMissing, nameof(platform));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ExtensionNameMissing, nameof(name));
        }

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(ErrorMessages.ExtensionValueMissing, nameof(value));
        }

        _composition.Hierarchy.ThrowIfBuilt();

        // A copy outlives the document the caller may dispose. A platform and name declared again
        // keep their place and take the new value.
        var extension = new ApplicationResourceExtension(platform, name, value.Clone());
        for (int index = 0; index < _extensions.Count; index++)
        {
            if (_extensions[index].Platform == platform && _extensions[index].Name == name)
            {
                _extensions[index] = extension;
                return this;
            }
        }

        _extensions.Add(extension);
        return this;
    }

    public ApplicationResourceManifest ToManifest()
    {
        return new ApplicationResourceManifest(
            Provider,
            Name,
            Array.AsReadOnly(_dependencies.ToArray()),
            Array.AsReadOnly(_settings.ToArray()),
            Array.AsReadOnly(_extensions.ToArray()));
    }

    // A name set again keeps its place and takes the new value.
    private void AddOrReplace(ApplicationResourceSetting setting)
    {
        for (int index = 0; index < _settings.Count; index++)
        {
            if (_settings[index].Name == setting.Name)
            {
                _settings[index] = setting;
                return;
            }
        }

        _settings.Add(setting);
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

        public IReadOnlyList<ApplicationResourceSetting> Settings => _descriptor.Settings;

        public IReadOnlyList<ApplicationResourceExtension> Extensions => _descriptor.Extensions;
    }
}
