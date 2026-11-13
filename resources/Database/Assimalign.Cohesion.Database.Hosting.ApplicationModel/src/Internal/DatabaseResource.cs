using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
            // The instance contract discovers contributors synchronously, but engine inventory is
            // asynchronous. Bridge off the caller's synchronization context, as Hosting's Build does.
            // Refresh on each discovery so opens, closes and pending reopens remain represented.
            var observations = Task.Run(() => _context.GetHealthAsync().AsTask()).GetAwaiter().GetResult();
            return observations.Select(observation => (IApplicationInstanceHealthContributor)
                new DatabaseHealthContributor(_context, observation.Name)).ToArray();
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
