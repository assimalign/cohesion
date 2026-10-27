using System.Collections.Generic;
using System.Diagnostics;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

// A child gateway's set. The root set is the application builder.
[DebuggerDisplay("{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationSet : IApplicationSet
{
    private readonly ApplicationSetComposition _composition;

    public ApplicationSet(GatewayName name, IApplicationSet parent, ApplicationHierarchy hierarchy)
    {
        Name = name;
        Parent = parent;
        _composition = new ApplicationSetComposition(this, hierarchy);
    }

    public GatewayName Name { get; }

    public IApplicationSet? Parent { get; }

    public IReadOnlyCollection<IApplicationResourceDescriptor> Resources => _composition.Resources;

    public IReadOnlyCollection<IApplicationSet> Gateways => _composition.Gateways;

    public IApplicationResourceDescriptor AddResource(ProviderName provider, ResourceName name)
    {
        return _composition.AddResource(provider, name);
    }

    public IApplicationSet AddGateway(GatewayName name)
    {
        return _composition.AddGateway(name);
    }

    public ApplicationManifest GetManifest()
    {
        return _composition.GetManifest();
    }

    private sealed class DebugView
    {
        private readonly ApplicationSet _set;

        public DebugView(ApplicationSet set)
        {
            _set = set;
        }

        public GatewayName Name => _set.Name;

        public IApplicationSet? Parent => _set.Parent;

        public IReadOnlyCollection<IApplicationResourceDescriptor> Resources => _set.Resources;

        public IReadOnlyCollection<IApplicationSet> Gateways => _set.Gateways;
    }
}
