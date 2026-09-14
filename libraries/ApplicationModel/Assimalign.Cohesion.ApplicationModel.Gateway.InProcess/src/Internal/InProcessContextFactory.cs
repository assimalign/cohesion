using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Resources;

using HostingResourceMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

internal sealed class InProcessContextFactory
{
    private const string ConfigurationSectionToken = "<Section>";
    private static readonly string ConfigurationPrefix = GetConfigurationPrefix();

    private readonly string _stateDirectory;
    private readonly LocalPortStore _ports;

    internal InProcessContextFactory(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _ports = new LocalPortStore(_stateDirectory);
    }

    internal async Task<InProcessMemberConfiguration> CreateAsync(
        IResourceControlContext control,
        InProcessPlanCompilation compilation,
        ResourceContext outerContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(outerContext);

        ResourceManifest manifest = (control.Resource as IManifestResource)?.Manifest
            ?? throw new InvalidOperationException(
                $"Resource '{control.Resource.Name}' has no enabled resource manifest.");

        ResourcePlan plan = compilation.Plan;
        var declared = new ResourceEndpoint[plan.Container.Ports.Count];
        for (int index = 0; index < declared.Length; index++)
        {
            PortBinding port = plan.Container.Ports[index];
            ResourceManifestEndpoint manifestEndpoint = manifest.Endpoints.First(
                endpoint => string.Equals(endpoint.Name, port.Endpoint, StringComparison.Ordinal));
            declared[index] = new ResourceEndpoint(
                port.Endpoint,
                manifestEndpoint.Scheme,
                Port: 0,
                manifestEndpoint.Public);
        }

        var ambientEnvironment = new Dictionary<string, string>(
            compilation.AmbientValues,
            StringComparer.Ordinal)
        {
            [ResourceEnvironment.Gateway] = "inprocess",
            [ResourceEnvironment.ContentRoot] = compilation.Artifact.ContentRootPath,
        };
        IReadOnlyList<ResourceEndpoint> allocated = await _ports
            .ResolveAsync(
                control.Model.Name,
                control.Resource.Name,
                declared,
                ambientEnvironment,
                cancellationToken)
            .ConfigureAwait(false);

        string member = MemberName(control.Model.Name, control.Resource.Name);
        ResourceEndpoint[] observed = RemapOuterEndpoints(
            member,
            allocated,
            outerContext);
        EnsureLoopbackEndpoints(control.Resource.Name, observed);
        Dictionary<string, Uri> endpoints = ToEndpointDictionary(observed);
        Dictionary<string, HostingResourceMount> mounts = CreateMounts(
            control,
            compilation,
            member,
            outerContext);
        Dictionary<string, string> settings = CreateSettings(compilation.AmbientValues);
        Dictionary<string, Uri> references = CreateReferences(
            control,
            compilation.ObservedDependencies,
            member,
            outerContext);

        await new LocalMountMaterializer(_stateDirectory).MaterializeTrustBundleAsync(control.Model.Name,
            control.Resource.Name, compilation.Inputs.TrustBundle, ambientEnvironment, cancellationToken).ConfigureAwait(false);

        var resourceContext = new ResourceContext(
            applicationName: control.Model.Name.ToString(),
            resourceName: control.Resource.Name.ToString(),
            environmentName: control.Model.Environment.Name.ToString(),
            gatewayName: "inprocess",
            contentRootPath: compilation.Artifact.ContentRootPath,
            endpoints: endpoints,
            mounts: mounts,
            settings: settings,
            references: references,
            bootstrapCredential: compilation.Inputs.BootstrapCredential,
            applicationTrustKey: compilation.Inputs.ApplicationTrustKey,
            ambientValues: CreateAmbientValues(ambientEnvironment),
            endpointCertificates: plan.Container.Ports.Where(port => !string.IsNullOrEmpty(port.Certificate))
                .ToDictionary(port => port.Endpoint, port => port.Certificate, StringComparer.Ordinal));

        return new InProcessMemberConfiguration(
            control,
            compilation.Artifact,
            resourceContext,
            observed,
            ResolveProbe(plan, manifest, "startup", "readyz"),
            ResolveProbe(plan, manifest, "readiness", "readyz"),
            ResolveProbe(plan, manifest, "liveness", "livez"),
            ParseRestartPolicy(manifest.Lifecycle.RestartPolicy, control.Resource.Name));
    }

    internal async Task DeleteAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken)
    {
        await _ports.DeleteAsync(application, resource, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        string resourceDirectory = GetResourceStateDirectory(application, resource);
        if (Directory.Exists(resourceDirectory))
        {
            Directory.Delete(resourceDirectory, recursive: true);
        }
    }

    private ResourceEndpoint[] RemapOuterEndpoints(
        string member,
        IReadOnlyList<ResourceEndpoint> allocated,
        ResourceContext outerContext)
    {
        var result = new ResourceEndpoint[allocated.Count];
        for (int index = 0; index < result.Length; index++)
        {
            ResourceEndpoint endpoint = allocated[index];
            if (TryFindEndpoint(
                outerContext.Endpoints,
                $"{member}-{endpoint.Name}",
                out Uri? outer))
            {
                if (!outer.IsLoopback)
                {
                    throw new InvalidOperationException(
                        $"In-process resource '{member}' endpoint '{endpoint.Name}' must use a loopback address, but the outer composite supplied '{outer}'.");
                }

                result[index] = new ResourceEndpoint(
                    endpoint.Name,
                    outer.Scheme,
                    outer.Port,
                    endpoint.IsPublic,
                    outer.IdnHost);
            }
            else
            {
                result[index] = endpoint;
            }
        }
        return result;
    }

    private Dictionary<string, HostingResourceMount> CreateMounts(
        IResourceControlContext control,
        InProcessPlanCompilation compilation,
        string member,
        ResourceContext outerContext)
    {
        var mounts = new Dictionary<string, HostingResourceMount>(StringComparer.OrdinalIgnoreCase);
        foreach (MountBinding mount in compilation.Plan.Container.Mounts)
        {
            if (TryFindMount(outerContext, member, mount.Mount, out HostingResourceMount? outer))
            {
                mounts.Add(mount.Mount, outer);
                continue;
            }

            if (!compilation.Inputs.Mounts.TryGetValue(mount.Mount, out ResourceMountInput? input))
            {
                throw new InvalidOperationException(
                    $"Resource '{control.Resource.Name}' has no resolved input for mount '{mount.Mount}'.");
            }
            if (!input.IsResolved)
            {
                throw new InvalidOperationException(
                    input.UnresolvedReason
                    ?? $"Mount '{mount.Mount}' on resource '{control.Resource.Name}' is unresolved.");
            }

            ResourceManifestMount? manifestMount = FindManifestMount(control.Resource, mount.Mount);
            if (manifestMount?.Kind is ResourceMountKind.Volume)
            {
                string claimPath = GetClaimPath(
                    control.Model.Name,
                    control.Resource.Name,
                    mount.Mount);
                Directory.CreateDirectory(claimPath);
                mounts.Add(mount.Mount, new HostingResourceMount(claimPath));
                continue;
            }

            mounts.Add(
                mount.Mount,
                HostingResourceMount.FromBytes(input.Content.Span));
        }
        return mounts;
    }

    private static Dictionary<string, string> CreateSettings(
        IReadOnlyDictionary<string, string> environment)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in environment)
        {
            if (!name.StartsWith(ConfigurationPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string key = name[ConfigurationPrefix.Length..]
                .Replace("__", ":", StringComparison.Ordinal);
            settings[key] = value;
        }
        return settings;
    }

    private static Dictionary<string, string?> CreateAmbientValues(
        IReadOnlyDictionary<string, string> environment)
    {
        var values = new Dictionary<string, string?>(environment.Count, StringComparer.Ordinal);
        foreach ((string name, string value) in environment)
        {
            values.Add(name, value);
        }
        return values;
    }

    private static Dictionary<string, Uri> CreateReferences(
        IResourceControlContext control,
        IReadOnlyList<ResourceDependencyObservation> observations,
        string member,
        ResourceContext outerContext)
    {
        var references = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (ResourceDependencyObservation observation in observations)
        {
            if (observation.Optional
                && observation.State is not ResourceLifecycle.Running
                && observation.State is not ResourceLifecycle.Degraded)
            {
                continue;
            }

            foreach (string endpointName in observation.RequestedEndpoints)
            {
                ResourceEndpoint? observed = FindObservedEndpoint(observation.Endpoints, endpointName);
                if (observed is ResourceEndpoint value && value.Host is not null)
                {
                    AddReference(
                        references,
                        observation.Resource.ToString(),
                        endpointName,
                        ToUri(value));
                    continue;
                }

                string? composite = outerContext.ResourceName;
                if (!string.IsNullOrWhiteSpace(composite)
                    && TryFindOuterReference(
                        outerContext,
                        composite,
                        MemberName(control.Model.Name, observation.Resource),
                        endpointName,
                        out Uri? remapped))
                {
                    if (!remapped.IsLoopback)
                    {
                        throw new InvalidOperationException(
                            $"In-process composite member reference '{observation.Resource}:{endpointName}' "
                            + $"must use a loopback address, but the outer composite supplied '{remapped}'.");
                    }

                    AddReference(
                        references,
                        observation.Resource.ToString(),
                        endpointName,
                        remapped);
                }
            }
        }

        return references;
    }

    private static void EnsureLoopbackEndpoints(
        ResourceName resource,
        IReadOnlyList<ResourceEndpoint> endpoints)
    {
        foreach (ResourceEndpoint endpoint in endpoints)
        {
            Uri address = ToUri(endpoint);
            if (!address.IsLoopback)
            {
                throw new InvalidOperationException(
                    $"In-process resource '{resource}' endpoint '{endpoint.Name}' must use a loopback address, "
                    + $"but resolved to '{address}'.");
            }
        }
    }

    private static bool TryFindOuterReference(
        ResourceContext outerContext,
        string composite,
        string member,
        string endpoint,
        [NotNullWhen(true)]
        out Uri? address)
    {
        string compositeEndpoint = $"{member}-{endpoint}";
        return outerContext.TryGetReference(composite, compositeEndpoint, out address)
            || outerContext.TryGetReference(
                NormalizeName(composite),
                NormalizeName(compositeEndpoint),
                out address);
    }

    private static InProcessProbeConfiguration ResolveProbe(
        ResourcePlan plan,
        ResourceManifest manifest,
        string role,
        string defaultOperation)
    {
        ProbeMapping? configured = plan.Container.Probes.FirstOrDefault(
            probe => string.Equals(probe.Role, role, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
        {
            return new InProcessProbeConfiguration(configured, IsDefaultControlPlane: false);
        }

        string prefix = manifest.ControlPlane.Path.TrimEnd('/');
        return new InProcessProbeConfiguration(
            new ProbeMapping(
                role,
                manifest.ControlPlane.Endpoint,
                ProbeKind.Http,
                $"{prefix}/{defaultOperation}",
                Array.Empty<string>()),
            IsDefaultControlPlane: true);
    }

    private static RestartPolicy ParseRestartPolicy(string value, ResourceName resource)
    {
        if (Enum.TryParse(value, ignoreCase: false, out RestartPolicy policy)
            && Enum.IsDefined(policy))
        {
            return policy;
        }

        throw new InvalidDataException(
            $"Resource '{resource}' declares unsupported restart policy '{value}'. "
            + "Expected OnFailure, Always, or Never.");
    }

    private static Dictionary<string, Uri> ToEndpointDictionary(
        IReadOnlyList<ResourceEndpoint> endpoints)
    {
        var result = new Dictionary<string, Uri>(endpoints.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ResourceEndpoint endpoint in endpoints)
        {
            result.Add(endpoint.Name, ToUri(endpoint));
        }
        return result;
    }

    private static Uri ToUri(ResourceEndpoint endpoint) =>
        new UriBuilder(endpoint.Scheme, endpoint.Host ?? "127.0.0.1", endpoint.Port).Uri;

    private static ResourceEndpoint? FindObservedEndpoint(
        IReadOnlyList<ResourceEndpoint> endpoints,
        string name)
    {
        for (int index = 0; index < endpoints.Count; index++)
        {
            if (string.Equals(endpoints[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return endpoints[index];
            }
        }
        return null;
    }

    private static ResourceManifestMount? FindManifestMount(
        IApplicationResource resource,
        string name) =>
        (resource as IManifestResource)?.Manifest.Mounts.FirstOrDefault(
            mount => string.Equals(mount.Name, name, StringComparison.Ordinal));

    private bool TryFindMount(
        ResourceContext outerContext,
        string member,
        string mount,
        [NotNullWhen(true)]
        out HostingResourceMount? value)
    {
        string composite = outerContext.ResourceName ?? string.Empty;
        string[] candidates =
        [
            $"{composite}-{member}-{mount}",
            $"{member}-{mount}",
        ];
        foreach ((string name, HostingResourceMount candidate) in outerContext.Mounts)
        {
            for (int index = 0; index < candidates.Length; index++)
            {
                if (EquivalentName(name, candidates[index]))
                {
                    value = candidate;
                    return true;
                }
            }
        }
        value = null;
        return false;
    }

    private static bool TryFindEndpoint(
        IReadOnlyDictionary<string, Uri> endpoints,
        string name,
        [NotNullWhen(true)]
        out Uri? address)
    {
        foreach ((string candidate, Uri value) in endpoints)
        {
            if (EquivalentName(candidate, name))
            {
                address = value;
                return true;
            }
        }
        address = null;
        return false;
    }

    private string GetClaimPath(
        ApplicationName application,
        ResourceName resource,
        string mount)
    {
        string resourceDirectory = GetResourceStateDirectory(application, resource);
        string mountDirectory = GetContainedChildPath(
            resourceDirectory,
            "mounts",
            "Mount root");
        return GetContainedChildPath(mountDirectory, mount, "Mount");
    }

    private string GetResourceStateDirectory(
        ApplicationName application,
        ResourceName resource)
    {
        string applicationDirectory = GetContainedChildPath(
            _stateDirectory,
            application.ToString(),
            "Application");
        return GetContainedChildPath(
            applicationDirectory,
            resource.ToString(),
            "Resource");
    }

    private static string GetContainedChildPath(
        string root,
        string child,
        string description)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(fullRoot, child));
        string rooted = fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(
            rooted,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{description} name '{child}' cannot be used as an in-process state path.");
        }
        return path;
    }

    private static void AddReference(
        IDictionary<string, Uri> references,
        string resource,
        string endpoint,
        Uri address)
    {
        string key = $"{resource}:{endpoint}";
        if (references.TryGetValue(key, out Uri? existing)
            && existing != address)
        {
            throw new InvalidOperationException(
                $"In-process reference '{key}' resolved to conflicting endpoints.");
        }
        references[key] = address;
    }

    private static string MemberName(ApplicationName application, ResourceName resource) =>
        MemberName(application.ToString(), resource.ToString());

    private static string MemberName(string application, string resource)
    {
        string prefix = application + "-";
        return resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? resource[prefix.Length..]
            : resource;
    }

    private static bool EquivalentName(string left, string right) =>
        string.Equals(
            NormalizeName(left),
            NormalizeName(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value)
    {
        Span<char> buffer = value.Length <= 256
            ? stackalloc char[value.Length]
            : new char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            buffer[length++] = char.IsAsciiLetterOrDigit(character)
                ? char.ToUpperInvariant(character)
                : '_';
        }
        return new string(buffer[..length]);
    }

    private static string GetConfigurationPrefix()
    {
        int tokenIndex = ResourceEnvironment.ConfigurationPattern.IndexOf(
            ConfigurationSectionToken,
            StringComparison.Ordinal);
        return tokenIndex < 0
            ? throw new InvalidOperationException(
                $"Resource environment pattern '{ResourceEnvironment.ConfigurationPattern}' has no "
                + $"'{ConfigurationSectionToken}' token.")
            : ResourceEnvironment.ConfigurationPattern[..tokenIndex];
    }
}
