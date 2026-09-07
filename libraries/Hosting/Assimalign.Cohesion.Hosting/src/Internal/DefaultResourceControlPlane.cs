using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting;

internal sealed class DefaultResourceControlPlane : IResourceControlPlane
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<string> _acceptedCommandKinds;
    private readonly Dictionary<string, IHealthContributor> _contributors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EndpointAddress> _observedEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private IHost? _host;

    internal DefaultResourceControlPlane(IEnumerable<string> acceptedCommandKinds)
    {
        ArgumentNullException.ThrowIfNull(acceptedCommandKinds);

        _acceptedCommandKinds = acceptedCommandKinds
            .Select(static kind =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(kind);
                return kind;
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> AcceptedCommandKinds => _acceptedCommandKinds;

    public IReadOnlyDictionary<string, EndpointAddress> ObservedEndpoints
    {
        get
        {
            lock (_sync)
            {
                return new ReadOnlyDictionary<string, EndpointAddress>(
                    new Dictionary<string, EndpointAddress>(_observedEndpoints, StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    public void AddHealthContributor(IHealthContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        ArgumentException.ThrowIfNullOrWhiteSpace(contributor.Name);

        lock (_sync)
        {
            if (!_contributors.TryAdd(contributor.Name, contributor))
            {
                throw new InvalidOperationException(
                    $"A resource health contributor named '{contributor.Name}' is already registered.");
            }
        }
    }

    public void ObserveEndpoint(string name, EndpointAddress address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_sync)
        {
            _observedEndpoints[name] = address;
        }
    }

    public ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default)
        => CheckContributorsAsync(cancellationToken);

    public ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default)
        => CheckContributorsAsync(cancellationToken);

    public ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default)
        => CheckContributorsAsync(cancellationToken);

    public void AttachHost(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        lock (_sync)
        {
            if (_host is not null && !ReferenceEquals(_host, host))
            {
                throw new InvalidOperationException("The resource control plane is already attached to a host.");
            }

            _host = host;
        }
    }

    public ValueTask RequestStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHost host;
        lock (_sync)
        {
            host = _host
                ?? throw new InvalidOperationException("The resource control plane is not attached to a host.");
        }

        host.Context.Shutdown();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            $"Resource command kind '{command.Kind}' is not implemented by this control plane.");
    }

    private async ValueTask<ResourceHealthReport> CheckContributorsAsync(CancellationToken cancellationToken)
    {
        IHealthContributor[] contributors;
        lock (_sync)
        {
            contributors = _contributors.Values.ToArray();
        }

        var results = new Dictionary<string, HealthContribution>(contributors.Length, StringComparer.Ordinal);
        HealthStatus status = HealthStatus.Healthy;

        foreach (IHealthContributor contributor in contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HealthContribution contribution;
            try
            {
                contribution = await contributor.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                contribution = HealthContribution.Unhealthy(exception.Message);
            }

            results.Add(contributor.Name, contribution);
            if ((int)contribution.Status < (int)status)
            {
                status = contribution.Status;
            }
        }

        return new ResourceHealthReport(
            status,
            new ReadOnlyDictionary<string, HealthContribution>(results));
    }
}
