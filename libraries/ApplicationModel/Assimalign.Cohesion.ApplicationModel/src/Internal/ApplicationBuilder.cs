using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationBuilder"/>. Maintains the working resource collection
/// and descriptor graph, the selected gateway, and validates the graph at <see cref="Build"/>.
/// </summary>
internal sealed class ApplicationBuilder : IApplicationBuilder
{
    private readonly GatewayCommandLineOptions _options;
    private readonly ApplicationEnvironment _environment;
    private readonly ApplicationResourceCollection _resources = new();
    private readonly List<ApplicationResourceDescriptor> _descriptors = new();
    private ApplicationName? _name;
    private IApplicationGateway? _gateway;

    public ApplicationBuilder()
        : this((ApplicationName)"application", Array.Empty<string>())
    {
    }

    public ApplicationBuilder(string[] args)
    {
        _options = GatewayCommandLineOptions.Parse(args);
        _environment = _options.Environment is null
            ? ApplicationEnvironment.FromHost()
            : ApplicationEnvironment.FromName(_options.Environment);
    }

    public ApplicationBuilder(ApplicationName name, string[] args)
        : this(args)
    {
        _name = name;
    }

    public IApplicationEnvironment Environment => _environment;

    public GatewayRunMode RunMode => _options.RunMode;

    public ResourceName? RequestedGateway => _options.Gateway is null
        ? default(ResourceName?)
        : (ResourceName)_options.Gateway;

    public IApplicationBuilder UseName(ApplicationName name)
    {
        _name = name;
        return this;
    }

    public IApplicationResourceDescriptor AddResource(IApplicationResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Enforces resource-name uniqueness; throws before the descriptor is created.
        _resources.Add(resource);

        var descriptor = new ApplicationResourceDescriptor(resource);
        _descriptors.Add(descriptor);
        return descriptor;
    }

    public IApplicationResourceDescriptor AddResource(ResourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return AddResource(manifest, new ResourceOptions());
    }

    public IApplicationResourceDescriptor AddResource<TOptions>(ResourceManifest manifest, TOptions options)
        where TOptions : class, IResourceOptions
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        return AddResource(new GenericPlannedResource<TOptions>(manifest, options));
    }

    public IApplicationResourceDescriptor AddResource(Func<IApplicationModel, IApplicationResource> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Hand the factory a snapshot of what has been declared so far.
        IApplicationModel snapshot = BuildModel(validate: false);
        IApplicationResource resource = configure(snapshot);
        return AddResource(resource);
    }

    public IApplicationBuilder UseGateway(IApplicationGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        return this;
    }

    public IApplication Build()
    {
        if (_descriptors.Count == 0)
        {
            ApplicationName emptyApplicationName = ResolveName(_environment);
            throw new InvalidOperationException(
                $"every reference crossed an application boundary; declare CohesionApplication or reference a resource of {emptyApplicationName}");
        }

        if (_gateway is null)
        {
            throw new InvalidOperationException(
                "No IApplicationGateway selected. Call UseGateway(...) or UseLocalGateway() before Build().");
        }

        if (_options.Gateway is not null &&
            !string.Equals(_options.Gateway, _gateway.Name.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Gateway '{_options.Gateway}' was requested, but gateway '{_gateway.Name}' was selected.");
        }

        IApplicationModel model = BuildModel(validate: true);
        return new CohesionApplication(model, _gateway);
    }

    private CohesionApplicationModel BuildModel(bool validate)
    {
        ApplicationResourceDescriptor[] authoringDescriptors = _descriptors.ToArray();
        ApplicationResourceDescriptor[] descriptors = authoringDescriptors;
        ApplicationName name = ResolveName(_environment);
        ResourceManifest[] manifests = CreateManifests(authoringDescriptors, name);

        if (validate)
        {
            ValidateApplicationName(name);
            ValidateManifests(authoringDescriptors, manifests);
            descriptors = MergeManifestDependencies(authoringDescriptors, manifests);
            ValidateGraph(descriptors);
        }

        ResourceName gatewayIdentity = _gateway is not null
            ? _gateway.Name
            : _options.Gateway is not null
                ? (ResourceName)_options.Gateway
                : (ResourceName)"unselected";
        ResourcePlan[] plans = validate
            ? CreatePlans(descriptors, manifests, gatewayIdentity)
            : Array.Empty<ResourcePlan>();

        return new CohesionApplicationModel(
            name,
            _environment,
            descriptors,
            manifests,
            plans,
            _options.RunMode,
            gatewayIdentity,
            _options.Adopt,
            _options.RestartOrphans);
    }

    private static void ValidateManifests(
        IReadOnlyList<ApplicationResourceDescriptor> descriptors,
        IReadOnlyList<ResourceManifest> manifests)
    {
        for (int index = 0; index < descriptors.Count; index++)
        {
            ResourceManifest manifest;
            try
            {
                manifest = manifests[index].Validate();
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException(
                    $"Resource '{descriptors[index].Resource.Name}' has an invalid manifest: {exception.Message}",
                    exception);
            }

            if (manifest.Name != descriptors[index].Resource.Name)
            {
                throw new InvalidOperationException(
                    $"Resource '{descriptors[index].Resource.Name}' exposes manifest '{manifest.Name}'. " +
                    "A resource and its manifest must have the same name.");
            }
        }
    }

    private static ApplicationResourceDescriptor[] MergeManifestDependencies(
        IReadOnlyList<ApplicationResourceDescriptor> descriptors,
        IReadOnlyList<ResourceManifest> manifests)
    {
        var copies = new Dictionary<IApplicationResourceDescriptor, ApplicationResourceDescriptor>(
            descriptors.Count,
            ReferenceEqualityComparer.Instance);
        var merged = new ApplicationResourceDescriptor[descriptors.Count];
        var byManifestIdentity = new Dictionary<(ApplicationName Application, ResourceName Resource), ApplicationResourceDescriptor>();

        for (int index = 0; index < descriptors.Count; index++)
        {
            var copy = new ApplicationResourceDescriptor(descriptors[index].Resource);
            copies.Add(descriptors[index], copy);
            merged[index] = copy;

            ResourceManifest manifest = manifests[index];
            if (!byManifestIdentity.TryAdd((manifest.Application, manifest.Name), copy))
            {
                throw new InvalidOperationException(
                    $"Application model contains more than one manifest for '{manifest.Application}/{manifest.Name}'.");
            }
        }

        for (int index = 0; index < descriptors.Count; index++)
        {
            ApplicationResourceDescriptor source = descriptors[index];
            ApplicationResourceDescriptor target = merged[index];

            foreach (IApplicationResourceDescriptor dependency in source.Dependencies)
            {
                target.DependsOn(copies.TryGetValue(dependency, out ApplicationResourceDescriptor? copy)
                    ? copy
                    : dependency);
            }

            foreach (ResourceManifestReference reference in manifests[index].References)
            {
                if (byManifestIdentity.TryGetValue(
                        (reference.Application, reference.Resource),
                        out ApplicationResourceDescriptor? dependency))
                {
                    if (!reference.Optional)
                    {
                        target.DependsOn(dependency);
                    }

                    continue;
                }

                if (!reference.Optional && reference.Application == manifests[index].Application)
                {
                    throw new InvalidOperationException(
                        $"Resource '{source.Resource.Name}' requires reference " +
                        $"'{reference.Application}/{reference.Resource}', but it is not present in the application model. " +
                        "Add or bind that resource, or mark the reference optional.");
                }
            }
        }

        return merged;
    }

    private ResourcePlan[] CreatePlans(
        IReadOnlyList<ApplicationResourceDescriptor> descriptors,
        IReadOnlyList<ResourceManifest> manifests,
        ResourceName gateway)
    {
        var manifestByIdentity = new Dictionary<(ApplicationName Application, ResourceName Resource), ResourceManifest>(
            manifests.Count);
        foreach (ResourceManifest manifest in manifests)
        {
            manifestByIdentity.Add((manifest.Application, manifest.Name), manifest);
        }

        var plans = new ResourcePlan[descriptors.Count];
        for (int index = 0; index < descriptors.Count; index++)
        {
            ApplicationResourceDescriptor descriptor = descriptors[index];
            ResourceManifest manifest = manifests[index];
            var references = new Dictionary<string, ResourceManifest>(StringComparer.Ordinal);

            foreach (ResourceManifestReference reference in manifest.References)
            {
                if (manifestByIdentity.TryGetValue(
                        (reference.Application, reference.Resource),
                        out ResourceManifest? referencedManifest))
                {
                    references.TryAdd(referencedManifest.Name.ToString(), referencedManifest);
                }
            }

            IPlannedResource? plannedResource = descriptor.Resource as IPlannedResource;
            IResourceOptions options = plannedResource is not null
                ? plannedResource.Options
                : new ResourceOptions();
            var context = new PlanContext(manifest, options, _environment, references);

            ResourcePlan plan = plannedResource is not null
                ? plannedResource.CreatePlan(context)
                : GenericPlanner.CreatePlan(context);

            ResourcePlanValidator.Validate(plan, context);
            WritePlanningDiagnostic(descriptor.Resource, plannedResource, gateway);
            plans[index] = plan;
        }

        return plans;
    }

    private static void WritePlanningDiagnostic(
        IApplicationResource resource,
        IPlannedResource? plannedResource,
        ResourceName gateway)
    {
        string plannerName = plannedResource?.PlannerName ?? nameof(GenericPlanner);
        if (string.IsNullOrWhiteSpace(plannerName)
            || plannerName.Contains('\r')
            || plannerName.Contains('\n'))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' returned an invalid {nameof(IPlannedResource)}.{nameof(IPlannedResource.PlannerName)}. " +
                $"Use a non-empty, single-line label such as 'Database planner' or '{nameof(GenericPlanner)}'.");
        }

        if (string.Equals(plannerName, nameof(GenericPlanner), StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{resource.Name}: {nameof(GenericPlanner)}");
            return;
        }

        Console.Error.WriteLine($"{resource.Name}: {plannerName} → {gateway} compiler");
    }

    private static ResourceManifest[] CreateManifests(
        IReadOnlyList<ApplicationResourceDescriptor> descriptors,
        ApplicationName application)
    {
        var manifests = new ResourceManifest[descriptors.Count];
        for (int index = 0; index < descriptors.Count; index++)
        {
            IApplicationResource resource = descriptors[index].Resource;
            if (resource is IManifestResource manifestResource)
            {
                ResourceManifest manifest = manifestResource.Manifest ?? throw new InvalidOperationException(
                    $"Manifest resource '{resource.Name}' returned a null manifest.");
                manifests[index] = ResourceManifestSnapshot.Create(manifest);
            }
            else
            {
                manifests[index] = CreateLegacyManifest(resource, application);
            }
        }

        return manifests;
    }

    private static ResourceManifest CreateLegacyManifest(
        IApplicationResource resource,
        ApplicationName application)
    {
        ResourceManifestEndpoint[] endpoints = CreateLegacyEndpoints(resource);
        ResourceManifestMount[] mounts = CreateLegacyMounts(resource);
        bool hasVolume = false;
        for (int index = 0; index < mounts.Length; index++)
        {
            hasVolume |= mounts[index].Kind == ResourceMountKind.Volume;
        }

        string artifact = resource is IExecutableResource executable &&
            !string.IsNullOrWhiteSpace(executable.Artifact)
                ? executable.Artifact
                : resource.Name.ToString();
        IReadOnlyDictionary<string, string> environment = resource is IExecutableResource environmentResource
            ? CopyDictionary(environmentResource.EnvironmentVariables)
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

        return new ResourceManifest
        {
            Name = resource.Name,
            Kind = "Legacy",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.ApplicationModel.Legacy",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = string.IsNullOrWhiteSpace(artifact) ? "legacy" : artifact,
            },
            Endpoints = new ReadOnlyCollection<ResourceManifestEndpoint>(endpoints),
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = endpoints[0].Name,
                Path = "/cohesion/v1",
            },
            Mounts = new ReadOnlyCollection<ResourceManifestMount>(mounts),
            EnvironmentVariables = environment,
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = hasVolume ? WorkloadKind.StatefulSet : WorkloadKind.Deployment,
            },
        };
    }

    private static ResourceManifestEndpoint[] CreateLegacyEndpoints(IApplicationResource resource)
    {
        if (resource is not IEndpointResource endpointResource || endpointResource.Endpoints.Count == 0)
        {
            // cohesion/resource/v1 requires every manifest to bind a control plane to a
            // declared endpoint. This sentinel exists only for the temporary legacy-resource
            // compatibility path; it is not added to the original resource descriptor.
            return
            [
                new ResourceManifestEndpoint
                {
                    Name = "control",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 1,
                },
            ];
        }

        var endpoints = new ResourceManifestEndpoint[endpointResource.Endpoints.Count];
        for (int index = 0; index < endpoints.Length; index++)
        {
            ResourceEndpoint endpoint = endpointResource.Endpoints[index];
            endpoints[index] = new ResourceManifestEndpoint
            {
                Name = endpoint.Name,
                Scheme = endpoint.Scheme,
                Protocol = string.Equals(endpoint.Scheme, "udp", StringComparison.OrdinalIgnoreCase)
                    ? "udp"
                    : "tcp",
                ContainerPort = endpoint.Port == 0 ? 1 : endpoint.Port,
                Public = endpoint.IsPublic,
            };
        }

        return endpoints;
    }

    private static ResourceManifestMount[] CreateLegacyMounts(IApplicationResource resource)
    {
        if (resource is not IMountResource mountResource)
        {
            return Array.Empty<ResourceManifestMount>();
        }

        var mounts = new ResourceManifestMount[mountResource.Mounts.Count];
        for (int index = 0; index < mounts.Length; index++)
        {
            ResourceMount mount = mountResource.Mounts[index];
            mounts[index] = new ResourceManifestMount
            {
                Name = mount.Name,
                ContainerPath = mount.Path,
                Kind = mount.Kind,
                Size = mount.Kind == ResourceMountKind.Volume ? "1Gi" : null,
            };
        }

        return mounts;
    }

    private static IReadOnlyDictionary<string, string> CopyDictionary(
        IReadOnlyDictionary<string, string> source)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in source)
        {
            copy.Add(key, value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static void ValidateGraph(IReadOnlyList<ApplicationResourceDescriptor> descriptors)
    {
        var present = new HashSet<IApplicationResourceDescriptor>(descriptors);

        foreach (ApplicationResourceDescriptor descriptor in descriptors)
        {
            foreach (IApplicationResourceDescriptor dependency in descriptor.Dependencies)
            {
                if (!present.Contains(dependency))
                {
                    throw new InvalidOperationException(
                        $"Resource '{descriptor.Resource.Name}' depends on '{dependency.Resource.Name}', which is not part of the application.");
                }
            }
        }

        // Depth-first cycle detection. 1 == on the current stack, 2 == fully explored.
        var state = new Dictionary<IApplicationResourceDescriptor, int>(present.Count);

        foreach (ApplicationResourceDescriptor descriptor in descriptors)
        {
            Visit(descriptor, state);
        }

        static void Visit(IApplicationResourceDescriptor node, Dictionary<IApplicationResourceDescriptor, int> state)
        {
            if (state.TryGetValue(node, out int status))
            {
                if (status == 1)
                {
                    throw new InvalidOperationException(
                        $"A dependency cycle was detected involving resource '{node.Resource.Name}'.");
                }

                if (status == 2)
                {
                    return;
                }
            }

            state[node] = 1;

            foreach (IApplicationResourceDescriptor dependency in node.Dependencies)
            {
                Visit(dependency, state);
            }

            state[node] = 2;
        }
    }

    private ApplicationName ResolveName(IApplicationEnvironment environment)
    {
        if (_name is ApplicationName configured)
        {
            return configured;
        }

        if (!environment.IsDevelopment)
        {
            return default;
        }

        string? entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        return Slugify(entryAssemblyName);
    }

    private static void ValidateApplicationName(ApplicationName name)
    {
        string? value = name.ToString();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                "An RFC1123 application name is required. Pass it to Application.CreateBuilder(ApplicationName, args) or call UseName(...) before Build(); only unnamed Development builders use the entry-assembly fallback.");
        }

        if (value.Length > 63 ||
            !IsAsciiLetterOrDigit(value[0]) ||
            !IsAsciiLetterOrDigit(value[^1]))
        {
            ThrowInvalidApplicationName(value);
        }

        foreach (char character in value)
        {
            if (!IsAsciiLetterOrDigit(character) && character != '-')
            {
                ThrowInvalidApplicationName(value);
            }
        }
    }

    private static ApplicationName Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "application";
        }

        Span<char> buffer = value.Length <= 256
            ? stackalloc char[value.Length]
            : new char[value.Length];
        int length = 0;
        bool lastWasSeparator = true;

        foreach (char input in value)
        {
            char character = char.ToLowerInvariant(input);
            if (IsAsciiLetterOrDigit(character))
            {
                if (length == 63)
                {
                    break;
                }

                buffer[length++] = character;
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && length < 63)
            {
                buffer[length++] = '-';
                lastWasSeparator = true;
            }
        }

        while (length > 0 && buffer[length - 1] == '-')
        {
            length--;
        }

        return length == 0 ? "application" : new string(buffer[..length]);
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static void ThrowInvalidApplicationName(string value)
    {
        throw new InvalidOperationException(
            $"Application name '{value}' is invalid. Use an RFC1123 label containing 1-63 lowercase letters, numbers, or '-' characters, beginning and ending with a letter or number.");
    }
}
