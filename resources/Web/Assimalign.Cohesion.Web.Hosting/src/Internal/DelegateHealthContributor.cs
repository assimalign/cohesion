using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("DelegateHealthContributor {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
        private readonly DelegateHealthContributor _target;

        public DebugView(DelegateHealthContributor value)
        {
            _target = value;
        }

        public string Name => _target.Name;
    }
}
