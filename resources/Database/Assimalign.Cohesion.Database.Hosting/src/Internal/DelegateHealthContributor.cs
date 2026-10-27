using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("DelegateHealthContributor {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.Internal.DelegateHealthContributor.DebugView))]
internal sealed class DelegateHealthContributor : IHealthContributor
{
    private readonly ResourceHealthCheck _check;

    internal DelegateHealthContributor(string name, ResourceHealthCheck check)
    {
        Name = name;
        _check = check;
    }

    public string Name { get; }

    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        return _check.Invoke(cancellationToken);
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Hosting.Internal.DelegateHealthContributor _target;

        public DebugView(global::Assimalign.Cohesion.Database.Hosting.Internal.DelegateHealthContributor value)
        {
            _target = value;
        }

        public string Name => _target.Name;
    }
}
