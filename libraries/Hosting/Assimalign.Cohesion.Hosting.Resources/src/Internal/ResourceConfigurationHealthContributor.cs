using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Reports whether this instance runs the configuration its deployment record describes. The text is fixed
/// because the unauthenticated probes publish it; the reasons are on the authenticated configuration status.
/// </summary>
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceConfigurationHealthContributor : IHealthContributor
{
    internal const string ContributorName = "cohesion.configuration";
    internal const string AppliedDescription = "The deployment record is applied.";
    internal const string PendingDescription = "A configuration section refused the deployment record or is waiting for a restart.";

    private readonly Func<ResourceConfigurationStatus> _status;

    public ResourceConfigurationHealthContributor(Func<ResourceConfigurationStatus> status)
    {
        _status = status;
    }

    public string Name => ContributorName;

    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int rejected = 0;
        int restartRequired = 0;
        foreach (ResourceConfigurationSectionStatus section in _status().Sections)
        {
            if (section.State == ResourceConfigurationState.Rejected)
            {
                rejected++;
            }
            else if (section.State == ResourceConfigurationState.RestartRequired)
            {
                restartRequired++;
            }
        }

        if (rejected == 0 && restartRequired == 0)
        {
            return ValueTask.FromResult(HealthContribution.Healthy(AppliedDescription));
        }

        return ValueTask.FromResult(HealthContribution.Degraded(PendingDescription, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["rejected"] = rejected,
            ["restartRequired"] = restartRequired,
        }));
    }

    private sealed class DebugView
    {
        private readonly ResourceConfigurationHealthContributor _target;

        public DebugView(ResourceConfigurationHealthContributor value)
        {
            _target = value;
        }

        public string Name => _target.Name;
    }
}
