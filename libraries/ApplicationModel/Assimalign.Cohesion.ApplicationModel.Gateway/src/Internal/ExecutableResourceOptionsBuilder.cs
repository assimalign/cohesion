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

            if (!names.Add(endpoint.Name))
            {
                throw new InvalidOperationException($"Executable endpoint '{endpoint.Name}' is declared more than once.");
            }
        }
    }
}
