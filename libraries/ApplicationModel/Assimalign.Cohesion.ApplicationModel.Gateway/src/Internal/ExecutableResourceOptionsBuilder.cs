using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class ExecutableResourceOptionsBuilder : IExecutableResourceOptionsBuilder
{
    private readonly List<ResourceEndpoint> _endpoints = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public string? ReadyMarker { get; private set; }

    public IProbeSpec? ReadinessProbe { get; private set; }

    public IProbeSpec? StartupProbe { get; private set; }

    public IProbeSpec? LivenessProbe { get; private set; }

    public RestartPolicy RestartPolicy { get; private set; } = RestartPolicy.OnFailure;

    public IReadOnlyList<ResourceEndpoint> Endpoints => _endpoints;

    public IReadOnlyDictionary<string, string> Environment => _environment;

    public IExecutableResourceOptionsBuilder UseReadyMarker(string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        ReadyMarker = marker;
        return this;
    }

    public IExecutableResourceOptionsBuilder UseReadinessProbe(IProbeSpec probe)
    {
        ReadinessProbe = probe ?? throw new ArgumentNullException(nameof(probe));
        return this;
    }

    public IExecutableResourceOptionsBuilder UseStartupProbe(IProbeSpec probe)
    {
        StartupProbe = probe ?? throw new ArgumentNullException(nameof(probe));
        return this;
    }

    public IExecutableResourceOptionsBuilder UseLivenessProbe(IProbeSpec probe)
    {
        LivenessProbe = probe ?? throw new ArgumentNullException(nameof(probe));
        return this;
    }

    public IExecutableResourceOptionsBuilder UseRestartPolicy(RestartPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "The restart policy is not defined.");
        }

        RestartPolicy = policy;
        return this;
    }

    public IExecutableResourceOptionsBuilder AddEndpoint(ResourceEndpoint endpoint)
    {
        _endpoints.Add(endpoint);
        return this;
    }

    public IExecutableResourceOptionsBuilder AddEnvironment(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _environment.Add(name, value);
        return this;
    }

    public void Validate()
    {
        if (ReadyMarker is null
            && (ReadinessProbe is null || ReadinessProbe.Kind == ProbeKind.None))
        {
            throw new InvalidOperationException(
                "A manifest-less executable must declare a readiness probe or stdout ready marker.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ResourceEndpoint endpoint in _endpoints)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Scheme);

            if (endpoint.Port is < 0 or > 65535)
            {
                throw new InvalidOperationException(
                    $"Executable endpoint '{endpoint.Name}' has invalid port '{endpoint.Port}'; " +
                    "expected zero for allocation or a value from 1 through 65535.");
            }

            if (!names.Add(endpoint.Name))
            {
                throw new InvalidOperationException($"Executable endpoint '{endpoint.Name}' is declared more than once.");
            }
        }

        ValidateProbe("readiness", ReadinessProbe, names);
        ValidateProbe("startup", StartupProbe, names);
        ValidateProbe("liveness", LivenessProbe, names);
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
                $"The executable {role} probe has unsupported kind '{probe.Kind}'.");
        }

        if (probe.Kind is ProbeKind.Http or ProbeKind.Tcp && probe.Address is null &&
            (string.IsNullOrWhiteSpace(probe.Endpoint) || !endpointNames.Contains(probe.Endpoint)))
        {
            throw new InvalidOperationException(
                $"The executable {role} probe endpoint '{probe.Endpoint}' does not name a declared endpoint.");
        }

        if (probe.Kind == ProbeKind.Exec &&
            (probe.Command.Count == 0 || string.IsNullOrWhiteSpace(probe.Command[0])))
        {
            throw new InvalidOperationException(
                $"The executable {role} exec probe must declare a non-empty command.");
        }

        if (probe.Kind == ProbeKind.Grpc)
        {
            throw new InvalidOperationException(
                $"The executable {role} probe cannot use gRPC because IProbeSpec does not carry a gRPC service name.");
        }
    }
}
