using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Internal;

/// <summary>
/// One running instance of a Database resource: the application this process runs, with the actions
/// the application added.
/// </summary>
[System.Diagnostics.DebuggerDisplay("DatabaseResource")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DatabaseResource : IApplicationInstance
{
    private readonly DatabaseApplicationContext _context;

    public DatabaseResource(ResourceName name, DeploymentId? deployment, IEnumerable<IApplicationInstanceAction> addedActions,
        DatabaseApplicationContext context)
    {
        _context = context;
        Name = name;
        Deployment = deployment;
        Actions = [.. addedActions];
    }

    public ProviderName Provider { get; } = "Database";

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment { get; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; }

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors
    {
        get
        {
            return _context.HealthComponentNames.Select(name => (IApplicationInstanceHealthContributor)
                new DatabaseHealthContributor(_context, name)).ToArray();
        }
    }

    private sealed class DebugView
    {
        private readonly DatabaseResource _resource;

        public DebugView(DatabaseResource resource) { _resource = resource; }

        public ProviderName Provider => _resource.Provider;
        public ResourceName Name => _resource.Name;
        public InstanceKey Key => _resource.Key;
        public DeploymentId? Deployment => _resource.Deployment;
        public IReadOnlyCollection<IApplicationInstanceAction> Actions => _resource.Actions;
    }
}
