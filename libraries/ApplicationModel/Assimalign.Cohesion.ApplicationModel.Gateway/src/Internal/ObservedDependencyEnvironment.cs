using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal static class ObservedDependencyEnvironment
{
    public static void Apply(
        IReadOnlyList<ResourceDependencyObservation> observations,
        IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(environment);

        var projected = new Dictionary<string, Projection>(StringComparer.Ordinal);
        foreach (ResourceDependencyObservation observation in observations)
        {
            foreach (string endpointName in observation.RequestedEndpoints)
            {
                ResourceEndpoint? endpoint = observation.Optional
                    && observation.State is not ResourceLifecycle.Running
                    && observation.State is not ResourceLifecycle.Degraded
                        ? null
                        : FindObservedEndpoint(observation, endpointName);
                string dependency = observation.Resource.ToString();
                string urlVariable = ResourceEnvironment.Dependency(dependency, endpointName, "URL");
                var projection = new Projection(
                    observation.Application,
                    observation.Resource,
                    endpointName,
                    endpoint);

                if (projected.TryGetValue(urlVariable, out Projection previous))
                {
                    if (previous.Application == projection.Application
                        && previous.Resource == projection.Resource
                        && string.Equals(previous.EndpointName, endpointName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new InvalidDataException(
                        $"Dependency endpoint '{projection.Application}/{projection.Resource}/{endpointName}' " +
                        $"collides with '{previous.Application}/{previous.Resource}/{previous.EndpointName}' " +
                        "after environment-name normalization.");
                }

                projected.Add(urlVariable, projection);
            }
        }

        foreach (Projection projection in projected.Values)
        {
            string dependency = projection.Resource.ToString();
            RemoveEndpoint(environment, dependency, projection.EndpointName);
            if (projection.Endpoint is ResourceEndpoint endpoint)
            {
                ApplyEndpoint(environment, dependency, endpoint);
            }
        }
    }

    private static ResourceEndpoint FindObservedEndpoint(
        ResourceDependencyObservation observation,
        string endpointName)
    {
        foreach (ResourceEndpoint endpoint in observation.Endpoints)
        {
            if (string.Equals(endpoint.Name, endpointName, StringComparison.Ordinal))
            {
                return endpoint;
            }
        }

        throw new InvalidOperationException(
            $"Dependency '{observation.Application}/{observation.Resource}' reached " +
            $"'{observation.State}' without requested endpoint '{endpointName}' in observed state.");
    }

    private static void RemoveEndpoint(
        IDictionary<string, string> environment,
        string dependency,
        string endpoint)
    {
        GatewayEnvironmentVariables.Remove(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint, "URL"));
        GatewayEnvironmentVariables.Remove(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint, "HOST"));
        GatewayEnvironmentVariables.Remove(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint, "PORT"));
        GatewayEnvironmentVariables.Remove(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint, "SCHEME"));
    }

    private static void ApplyEndpoint(
        IDictionary<string, string> environment,
        string dependency,
        ResourceEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host)
            || string.IsNullOrWhiteSpace(endpoint.Scheme)
            || endpoint.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Dependency '{dependency}' reached Running without a complete observed address " +
                $"for endpoint '{endpoint.Name}'.");
        }

        Uri address = Uri.CreateEndpoint(endpoint.Scheme, endpoint.Host, endpoint.Port);
        GatewayEnvironmentVariables.Set(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint.Name, "URL"),
            address.ToEndpointString());
        GatewayEnvironmentVariables.Set(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint.Name, "HOST"),
            address.IdnHost);
        GatewayEnvironmentVariables.Set(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint.Name, "PORT"),
            address.Port.ToString(CultureInfo.InvariantCulture));
        GatewayEnvironmentVariables.Set(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint.Name, "SCHEME"),
            address.Scheme);
    }

    private readonly record struct Projection(
        ApplicationName Application,
        ResourceName Resource,
        string EndpointName,
        ResourceEndpoint? Endpoint);
}
