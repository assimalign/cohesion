using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

// One set's resources and child gateways, and the rules for adding them. The application builder
// (the root set) and each child set own one.
[DebuggerDisplay("{Owner.Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationSetComposition
{
    private readonly List<ApplicationResourceDescriptor> _resources = [];
    private readonly List<ApplicationSet> _gateways = [];

    public ApplicationSetComposition(IApplicationSet owner, ApplicationHierarchy hierarchy)
    {
        Owner = owner;
        Hierarchy = hierarchy;
        Resources = _resources.AsReadOnly();
        Gateways = _gateways.AsReadOnly();
    }

    public IApplicationSet Owner { get; }

    public ApplicationHierarchy Hierarchy { get; }

    public IReadOnlyCollection<IApplicationResourceDescriptor> Resources { get; }

    public IReadOnlyCollection<IApplicationSet> Gateways { get; }

    public IApplicationResourceDescriptor AddResource(ProviderName provider, ResourceName name)
    {
        if (provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(provider));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
        }

        Hierarchy.ThrowIfBuilt();

        foreach (ApplicationResourceDescriptor resource in _resources)
        {
            if (resource.Provider == provider && resource.Name == name)
            {
                throw new InvalidOperationException(Format(ErrorMessages.ApplicationResourceDuplicate, Owner.Name, provider, name));
            }
        }

        var descriptor = new ApplicationResourceDescriptor(this, provider, name);
        _resources.Add(descriptor);
        Hierarchy.Resources.Add((Owner.Name, provider, name), descriptor);
        return descriptor;
    }

    public IApplicationSet AddGateway(GatewayName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(name));
        }

        Hierarchy.ThrowIfBuilt();

        if (!Hierarchy.GatewayNames.Add(name))
        {
            throw new InvalidOperationException(Format(ErrorMessages.ApplicationGatewayDuplicate, name));
        }

        var gateway = new ApplicationSet(name, Owner, Hierarchy);
        _gateways.Add(gateway);
        return gateway;
    }

    public ApplicationManifest GetManifest()
    {
        var resources = new ApplicationResourceManifest[_resources.Count];
        for (int index = 0; index < resources.Length; index++)
        {
            resources[index] = _resources[index].ToManifest();
        }

        var gateways = new ApplicationManifest[_gateways.Count];
        for (int index = 0; index < gateways.Length; index++)
        {
            gateways[index] = _gateways[index].GetManifest();
        }

        return new ApplicationManifest(Owner.Name, Array.AsReadOnly(resources), Array.AsReadOnly(gateways));
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationSetComposition _composition;

        public DebugView(ApplicationSetComposition composition)
        {
            _composition = composition;
        }

        public IApplicationSet Owner => _composition.Owner;

        public IReadOnlyCollection<IApplicationResourceDescriptor> Resources => _composition.Resources;

        public IReadOnlyCollection<IApplicationSet> Gateways => _composition.Gateways;
    }
}
