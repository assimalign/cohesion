using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class ContainerResourceOptionsBuilder : IContainerResourceOptionsBuilder
{
    private readonly List<ResourceEndpoint> _endpoints = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public IProbeSpec? ReadinessProbe { get; private set; }

    public IProbeSpec? StartupProbe { get; private set; }

    public IProbeSpec? LivenessProbe { get; private set; }

    public RestartPolicy RestartPolicy { get; private set; } = RestartPolicy.OnFailure;

    public IReadOnlyList<ResourceEndpoint> Endpoints => _endpoints;

    public IReadOnlyDictionary<string, string> Environment => _environment;

    public IContainerResourceOptionsBuilder UseReadinessProbe(IProbeSpec probe)
    {
        ReadinessProbe = probe ?? throw new ArgumentNullException(nameof(probe));
        return this;
    }

    public IContainerResourceOptionsBuilder UseStartupProbe(IProbeSpec probe)
    {
        StartupProbe = probe ?? throw new ArgumentNullException(nameof(probe));
        return this;
    }

    public IContainerResourceOptionsBuilder UseLivenessProbe(IProbeSpec probe)
    {
        LivenessProbe = probe ?? throw new ArgumentNullException(nameof(probe));
        return this;
    }

    public IContainerResourceOptionsBuilder UseRestartPolicy(RestartPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "The restart policy is not defined.");
        }

        RestartPolicy = policy;
        return this;
    }

    public IContainerResourceOptionsBuilder AddEndpoint(ResourceEndpoint endpoint)
    {
        _endpoints.Add(endpoint);
        return this;
    }

    public IContainerResourceOptionsBuilder AddEnvironment(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _environment.Add(name, value);
        return this;
    }

    public void Validate()
    {
        if (_endpoints.Count == 0)
        {
            throw new InvalidOperationException("A container resource must declare at least one endpoint.");
        }

        if (ReadinessProbe is null || ReadinessProbe.Kind == ProbeKind.None)
        {
            throw new InvalidOperationException("A container resource must declare a readiness probe.");
        }

        var endpointNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ResourceEndpoint endpoint in _endpoints)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Scheme);

            if (endpoint.Port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"Container endpoint '{endpoint.Name}' has invalid port '{endpoint.Port}'; expected a value from 1 through 65535.");
            }

            if (!endpointNames.Add(endpoint.Name))
            {
                throw new InvalidOperationException($"Container endpoint '{endpoint.Name}' is declared more than once.");
            }
        }

        ValidateProbe("readiness", ReadinessProbe, endpointNames);
        ValidateProbe("startup", StartupProbe, endpointNames);
        ValidateProbe("liveness", LivenessProbe, endpointNames);
    }

    private static void ValidateProbe(
        string role,
        IProbeSpec? probe,
        IReadOnlySet<string> endpointNames)
    {
        if (probe is null)
        {
            return;
        }

        if (!Enum.IsDefined(probe.Kind))
        {
            throw new InvalidOperationException(
                $"The container {role} probe has unsupported kind '{probe.Kind}'.");
        }

        if (probe.Address is not null)
        {
            throw new InvalidOperationException(
                $"The container {role} probe must target a declared endpoint, not an absolute address.");
        }

        switch (probe.Kind)
        {
            case ProbeKind.Http:
                ValidateNetworkEndpoint(role, probe, endpointNames);
                if (string.IsNullOrWhiteSpace(probe.Path)
                    || !probe.Path.StartsWith("/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The container {role} HTTP probe must declare an absolute path.");
                }

                break;

            case ProbeKind.Tcp:
                ValidateNetworkEndpoint(role, probe, endpointNames);
                break;

            case ProbeKind.Exec:
                if (probe.Command.Count == 0 || string.IsNullOrWhiteSpace(probe.Command[0]))
                {
                    throw new InvalidOperationException(
                        $"The container {role} exec probe must declare a non-empty command.");
                }

                break;

            case ProbeKind.None:
                break;

            case ProbeKind.Grpc:
                throw new InvalidOperationException(
                    $"The container {role} probe cannot use gRPC because IProbeSpec does not carry a gRPC service name.");
        }
    }

    private static void ValidateNetworkEndpoint(
        string role,
        IProbeSpec probe,
        IReadOnlySet<string> endpointNames)
    {
        if (string.IsNullOrWhiteSpace(probe.Endpoint)
            || !endpointNames.Contains(probe.Endpoint))
        {
            throw new InvalidOperationException(
                $"The container {role} probe endpoint '{probe.Endpoint}' does not name a declared endpoint.");
        }
    }
}
