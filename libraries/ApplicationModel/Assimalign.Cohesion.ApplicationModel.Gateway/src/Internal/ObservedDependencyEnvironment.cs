using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal static class ObservedDependencyEnvironment
{
    public static void Apply(
        IResourceControlContext context,
        IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);

        ResourceManifest manifest = GetManifest(context.Model, context.Resource);
        if (manifest.References.Count == 0)
        {
            return;
        }

        var projected = new Dictionary<string, Projection>(StringComparer.Ordinal);

        foreach (ResourceManifestReference reference in manifest.References)
        {
            foreach (string endpointName in reference.Endpoints)
            {
                string dependencyName = reference.Resource.ToString();
                string urlVariable = ResourceEnvironment.Dependency(dependencyName, endpointName, "URL");
                var projection = new Projection(
                    reference.Application,
                    reference.Resource,
                    endpointName,
                    reference.Optional);

                if (projected.TryGetValue(urlVariable, out Projection previous))
                {
                    if (previous.Application == projection.Application
                        && previous.Resource == projection.Resource
                        && string.Equals(previous.Endpoint, projection.Endpoint, StringComparison.Ordinal))
                    {
                        if (previous.Optional && !projection.Optional)
                        {
                            projected[urlVariable] = projection;
                        }

                        continue;
                    }

                    throw new InvalidDataException(
                        $"Dependency endpoint '{projection.Application}/{projection.Resource}/{projection.Endpoint}' " +
                        $"collides with '{previous.Application}/{previous.Resource}/{previous.Endpoint}' " +
                        "after environment-name normalization.");
                }

                projected.Add(urlVariable, projection);
            }
        }

        foreach (Projection projection in projected.Values)
        {
            RemoveEndpoint(environment, projection.Resource.ToString(), projection.Endpoint);
        }

        foreach (Projection projection in projected.Values)
        {
            IApplicationResource? dependency = ResolveDependency(context, projection);
            if (dependency is null)
            {
                if (projection.Optional)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Resource '{context.Resource.Name}' cannot resolve required dependency " +
                    $"'{projection.Application}/{projection.Resource}' from its realized dependency graph.");
            }

            IReadOnlyList<ResourceEndpoint> observed = context.State.GetObservedEndpoints(dependency.Id);
            ResourceEndpoint endpoint = FindObservedEndpoint(
                context.Resource.Name,
                projection,
                observed);
            ApplyEndpoint(environment, projection.Resource.ToString(), endpoint);
        }
    }

    private static IApplicationResource? ResolveDependency(
        IResourceControlContext context,
        Projection projection)
    {
        for (int index = 0; index < context.Model.Descriptors.Count; index++)
        {
            ResourceManifest manifest = context.Model.Manifests[index];
            if (manifest.Application == projection.Application && manifest.Name == projection.Resource)
            {
                IApplicationResource dependency = context.Model.Descriptors[index].Resource;
                if (projection.Optional)
                {
                    return context.State.GetState(dependency.Id) == ResourceLifecycle.Running
                        ? dependency
                        : null;
                }

                foreach (IApplicationResource admitted in context.Dependencies)
                {
                    if (ReferenceEquals(admitted, dependency))
                    {
                        return dependency;
                    }
                }

                throw new InvalidOperationException(
                    $"Resource '{context.Resource.Name}' has required dependency " +
                    $"'{projection.Application}/{projection.Resource}', but its inferred dependency edge " +
                    "was not admitted by the gateway.");
            }
        }

        return null;
    }

    private static ResourceManifest GetManifest(
        IApplicationModel model,
        IApplicationResource resource)
    {
        if (model.Descriptors.Count != model.Manifests.Count)
        {
            throw new InvalidOperationException(
                "The application model must contain one manifest for every resource descriptor.");
        }

        for (int index = 0; index < model.Descriptors.Count; index++)
        {
            if (ReferenceEquals(model.Descriptors[index].Resource, resource))
            {
                return model.Manifests[index];
            }
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' is not part of the application model being realized.");
    }

    private static ResourceEndpoint FindObservedEndpoint(
        ResourceName dependent,
        Projection projection,
        IReadOnlyList<ResourceEndpoint> observed)
    {
        foreach (ResourceEndpoint endpoint in observed)
        {
            if (string.Equals(endpoint.Name, projection.Endpoint, StringComparison.Ordinal))
            {
                return endpoint;
            }
        }

        throw new InvalidOperationException(
            $"Resource '{dependent}' requires endpoint '{projection.Endpoint}' from dependency " +
            $"'{projection.Application}/{projection.Resource}', but the Running dependency did not report it in observed state.");
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
                $"Dependency '{dependency}' reached Running without a complete observed address for endpoint '{endpoint.Name}'.");
        }

        var address = new EndpointAddress(endpoint.Scheme, endpoint.Host, endpoint.Port);
        GatewayEnvironmentVariables.Set(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint.Name, "URL"),
            address.ToString());
        GatewayEnvironmentVariables.Set(
            environment,
            ResourceEnvironment.Dependency(dependency, endpoint.Name, "HOST"),
            address.Host);
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
        string Endpoint,
        bool Optional);
}
