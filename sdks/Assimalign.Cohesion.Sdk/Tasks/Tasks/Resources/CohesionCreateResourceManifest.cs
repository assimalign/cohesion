using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.Tasks;

/// <summary>
/// Creates a Cohesion resource manifest and the resource's strongly typed generated accessors.
/// </summary>
public sealed class CohesionCreateResourceManifest : Task
{
    private static readonly HashSet<string> BuiltInMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "AccessedTime", "CreatedTime", "DefiningProjectDirectory", "DefiningProjectExtension",
        "DefiningProjectFullPath", "DefiningProjectName", "Directory", "Extension", "Filename",
        "FullPath", "Identity", "ModifiedTime", "RecursiveDir", "RelativeDir", "RootDir"
    };

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while"
    };

    /// <summary>Gets or sets the resource manifest output path.</summary>
    [Required]
    public string ManifestOutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the generated resource accessor output path.</summary>
    [Required]
    public string ResourceSourceOutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the generated control-plane registration output path.</summary>
    [Required]
    public string ControlPlaneSourceOutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the consuming project path.</summary>
    [Required]
    public string ProjectFullPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the consuming project name.</summary>
    [Required]
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Gets or sets the consuming project's root namespace.</summary>
    [Required]
    public string RootNamespace { get; set; } = string.Empty;

    /// <summary>Gets or sets the consuming assembly name.</summary>
    [Required]
    public string AssemblyName { get; set; } = string.Empty;

    /// <summary>Gets or sets the consuming project's output type.</summary>
    [Required]
    public string OutputType { get; set; } = string.Empty;

    /// <summary>Gets or sets the consuming assembly output path.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the consuming executable apphost path.</summary>
    public string AppHostPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional resource name.</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the resource kind.</summary>
    [Required]
    public string ResourceKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional application name.</summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional application-model assembly name.</summary>
    public string ApplicationModelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional fully qualified area default control-plane type.</summary>
    public string ControlPlaneType { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the resource kind supports in-process composition.</summary>
    public bool Composable { get; set; } = true;

    /// <summary>Gets or sets the area default control-plane endpoint.</summary>
    [Required]
    public string ControlPlaneEndpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the area default control-plane path.</summary>
    [Required]
    public string ControlPlanePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional workload kind.</summary>
    public string WorkloadKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the default replica count.</summary>
    public int Replicas { get; set; } = 1;

    /// <summary>Gets or sets the optional maximum replica count.</summary>
    public string MaxReplicas { get; set; } = string.Empty;

    /// <summary>Gets or sets the graceful-stop budget in seconds.</summary>
    public int StopGraceSeconds { get; set; } = 30;

    /// <summary>Gets or sets the restart policy.</summary>
    public string RestartPolicy { get; set; } = "OnFailure";

    /// <summary>Gets or sets the declared endpoints.</summary>
    public ITaskItem[] Endpoints { get; set; } = [];

    /// <summary>Gets or sets the declared probes.</summary>
    public ITaskItem[] Probes { get; set; } = [];

    /// <summary>Gets or sets the declared mounts.</summary>
    public ITaskItem[] Mounts { get; set; } = [];

    /// <summary>Gets or sets the declared settings.</summary>
    public ITaskItem[] Settings { get; set; } = [];

    /// <summary>Gets or sets the declared resource references.</summary>
    public ITaskItem[] ResourceReferences { get; set; } = [];

    /// <summary>Gets or sets the manifests returned by referenced projects or packages.</summary>
    public ITaskItem[] ReferencedManifests { get; set; } = [];

    /// <summary>Gets or sets the kind-prefixed resource properties.</summary>
    public ITaskItem[] ResourceProperties { get; set; } = [];

    /// <summary>Gets the resolved resource name.</summary>
    [Output]
    public string ResolvedResourceName { get; private set; } = string.Empty;

    /// <summary>Gets the resolved application name.</summary>
    [Output]
    public string ResolvedApplicationName { get; private set; } = string.Empty;

    /// <summary>Gets the resolved resource kind.</summary>
    [Output]
    public string ResolvedResourceKind { get; private set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        if (!string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            Error(
                "COHSDK008",
                $"{ProjectName} has CohesionApplicationModel enabled but OutputType is '{OutputType}'; set OutputType to 'Exe'.");
            return false;
        }

        ResolvedResourceName = string.IsNullOrWhiteSpace(ResourceName)
            ? DeriveResourceName(ProjectName)
            : ResourceName.Trim();
        ResolvedApplicationName = string.IsNullOrWhiteSpace(ApplicationName)
            ? DeriveApplicationName(ProjectName)
            : Slug(ApplicationName);
        ResolvedResourceKind = ResourceKind.Trim();

        if (!IsRfc1123Label(ResolvedResourceName))
        {
            Log.LogError($"CohesionResourceName '{ResolvedResourceName}' must be an RFC 1123 label.");
        }
        if (!IsRfc1123Label(ResolvedApplicationName))
        {
            Log.LogError($"CohesionApplication '{ResolvedApplicationName}' must be an RFC 1123 label.");
        }
        if (string.IsNullOrWhiteSpace(ResolvedResourceKind))
        {
            Log.LogError("CohesionResourceKind is required when CohesionApplicationModel is enabled.");
        }
        if (string.IsNullOrWhiteSpace(ControlPlaneEndpoint) || string.IsNullOrWhiteSpace(ControlPlanePath))
        {
            Log.LogError(
                "CohesionControlPlaneEndpoint and CohesionControlPlanePath are required when " +
                "CohesionApplicationModel is enabled; area SDK defaults are delivered by design item 13.");
        }
        else if (!ControlPlanePath.StartsWith("/", StringComparison.Ordinal))
        {
            Log.LogError("CohesionControlPlanePath must be an absolute path beginning with '/'.");
        }
        if (!string.IsNullOrWhiteSpace(ControlPlaneType) && !IsNamespace(ControlPlaneType.Trim()))
        {
            Log.LogError(
                "CohesionResourceControlPlaneType must be a fully qualified C# type name when specified.");
        }
        if (!IsNamespace(RootNamespace))
        {
            Log.LogError($"Root namespace '{RootNamespace}' is not a valid C# namespace for Resource.g.cs.");
        }

        ValidateMetadata(Endpoints, "CohesionEndpoint", "Scheme", "ContainerPort", "DevPort", "Public", "Certificate", "Protocol");
        ValidateMetadata(Probes, "CohesionProbe", "Endpoint", "Http", "Tcp", "Exec", "Grpc", "None");
        ValidateMetadata(Mounts, "CohesionMount", "Kind", "ContainerPath", "Source", "Size");
        ValidateMetadata(Settings, "CohesionSetting", "Default", "Type");
        ValidateMetadata(ResourceReferences, "CohesionResourceReference", "Version", "Optional", "Endpoints");
        ValidateMetadata(ResourceProperties, "CohesionResourceProperty", "Value");

        List<ResourceEndpointModel> endpoints = ParseEndpoints();
        List<ResourceMountModel> mounts = ParseMounts();
        List<ResourceSettingModel> settings = ParseSettings();
        List<ResourceReferenceModel> references = ParseReferences();
        Dictionary<string, ResourceProbeModel> probes = ParseProbes(endpoints);
        Dictionary<string, string> properties = ParseProperties();

        EnsureUniqueIdentifiers(settings.Select(setting => setting.Key), "CohesionSetting");
        EnsureUniqueIdentifiers(references.Select(reference => reference.Resource), "CohesionResourceReference");

        int? maxReplicas = ParseOptionalPositiveInt(MaxReplicas, nameof(MaxReplicas));
        string workload = ResolveWorkload(mounts);
        bool composable = Composable;

        if (string.Equals(ResolvedResourceKind, "Composite", StringComparison.OrdinalIgnoreCase))
        {
            LiftComposite(references, endpoints, mounts, ref workload, ref maxReplicas, ref composable);
        }

        if (Replicas < 1)
        {
            Log.LogError("CohesionReplicas must be at least 1.");
        }
        if (maxReplicas is int maximum && maximum < Replicas)
        {
            Log.LogError("CohesionMaxReplicas cannot be less than CohesionReplicas.");
        }
        if (StopGraceSeconds < 5)
        {
            Log.LogError("CohesionStopGraceSeconds must be at least 5.");
        }
        if (!IsOneOf(RestartPolicy, "OnFailure", "Always", "Never"))
        {
            Log.LogError("CohesionRestartPolicy must be OnFailure, Always, or Never.");
        }
        if (!endpoints.Any(endpoint => string.Equals(endpoint.Name, ControlPlaneEndpoint, StringComparison.OrdinalIgnoreCase)))
        {
            Log.LogError($"Control-plane endpoint '{ControlPlaneEndpoint}' is not declared as a CohesionEndpoint.");
        }

        EnsureUniqueIdentifiers(endpoints.Select(endpoint => endpoint.Name), "generated endpoint");
        EnsureUniqueIdentifiers(mounts.Select(mount => mount.Name), "generated mount");
        EnsureGeneratedMemberNames(endpoints, mounts, settings, references);

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        var manifest = new ResourceManifestModel
        {
            Name = ResolvedResourceName,
            Kind = ResolvedResourceKind,
            Application = ResolvedApplicationName,
            ApplicationModel = string.IsNullOrWhiteSpace(ApplicationModelName)
                ? $"Assimalign.Cohesion.{ResolvedResourceKind}.ApplicationModel"
                : ApplicationModelName.Trim(),
            Artifact = new ResourceArtifactModel(
                AssemblyName,
                composable,
                Path.GetFullPath(ProjectFullPath),
                string.IsNullOrWhiteSpace(AppHostPath) ? TargetPath : AppHostPath),
            ControlPlane = new ResourceControlPlaneModel(ControlPlaneEndpoint, ControlPlanePath),
            Lifecycle = new ResourceLifecycleModel(
                workload,
                Replicas,
                maxReplicas,
                StopGraceSeconds,
                NormalizeChoice(RestartPolicy, "OnFailure", "Always", "Never"))
        };

        manifest.Endpoints.AddRange(endpoints);
        foreach ((string role, ResourceProbeModel probe) in probes)
        {
            manifest.Probes.Add(role, probe);
        }
        manifest.Mounts.AddRange(mounts);
        manifest.Settings.AddRange(settings);
        manifest.References.AddRange(references);
        foreach ((string key, string value) in properties)
        {
            manifest.Properties.Add(key, value);
        }

        try
        {
            ResourceManifestWriter.Write(ManifestOutputPath, manifest);
            ResourceSourceWriter.WriteResource(ResourceSourceOutputPath, RootNamespace, manifest);
            ResourceSourceWriter.WriteControlPlane(
                ControlPlaneSourceOutputPath,
                RootNamespace,
                ControlPlaneType.Trim(),
                manifest);
        }
        catch (IOException exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }

        Log.LogMessage(MessageImportance.Normal, $"Generated Cohesion resource manifest '{ManifestOutputPath}'.");
        return true;
    }

    private List<ResourceEndpointModel> ParseEndpoints()
    {
        var result = new List<ResourceEndpointModel>(Endpoints.Length);
        foreach (ITaskItem item in Endpoints)
        {
            string name = item.ItemSpec.Trim();
            string scheme = item.GetMetadata("Scheme").Trim();
            string protocol = item.GetMetadata("Protocol").Trim();
            if (protocol.Length == 0)
            {
                protocol = "tcp";
            }
            if (name.Length == 0 || scheme.Length == 0)
            {
                Log.LogError("Every CohesionEndpoint requires a non-empty Include and Scheme.");
                continue;
            }
            if (!Uri.CheckSchemeName(scheme))
            {
                Log.LogError($"CohesionEndpoint '{name}' Scheme '{scheme}' is not a valid URI scheme.");
            }
            if (!IsOneOf(protocol, "tcp", "udp"))
            {
                Log.LogError($"CohesionEndpoint '{name}' Protocol must be tcp or udp.");
            }
            int containerPort = ParsePort(item.GetMetadata("ContainerPort"), $"CohesionEndpoint '{name}' ContainerPort");
            int? devPort = ParseOptionalPort(item.GetMetadata("DevPort"), $"CohesionEndpoint '{name}' DevPort");
            bool isPublic = ParseBoolean(item.GetMetadata("Public"), $"CohesionEndpoint '{name}' Public");
            result.Add(new ResourceEndpointModel(
                name,
                scheme,
                protocol.ToLowerInvariant(),
                containerPort,
                devPort,
                isPublic,
                NullIfEmpty(item.GetMetadata("Certificate"))));
        }
        return result;
    }

    private List<ResourceMountModel> ParseMounts()
    {
        var result = new List<ResourceMountModel>(Mounts.Length);
        foreach (ITaskItem item in Mounts)
        {
            string name = item.ItemSpec.Trim();
            string kind = item.GetMetadata("Kind").Trim();
            if (name.Length == 0 || !IsOneOf(kind, "Volume", "Configuration", "Secret"))
            {
                Log.LogError($"CohesionMount '{name}' Kind must be Volume, Configuration, or Secret.");
                continue;
            }
            string? size = NullIfEmpty(item.GetMetadata("Size"));
            if (string.Equals(kind, "Volume", StringComparison.OrdinalIgnoreCase) && size is null)
            {
                Log.LogError($"CohesionMount '{name}' requires Size when Kind is Volume.");
            }
            string? source = NullIfEmpty(item.GetMetadata("Source"));
            if (source is not null && !string.Equals(kind, "Configuration", StringComparison.OrdinalIgnoreCase))
            {
                Log.LogError($"CohesionMount '{name}' Source is valid only when Kind is Configuration.");
            }
            else if (source is not null && !IsMountSource(source))
            {
                Log.LogError(
                    $"CohesionMount '{name}' Source must use parameter:<name>, <resource>:<key>, or literal:<value>.");
            }
            string path = item.GetMetadata("ContainerPath").Trim();
            if (path.Length == 0)
            {
                path = $"/cohesion/mounts/{name}";
            }
            else if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                Log.LogError($"CohesionMount '{name}' ContainerPath must be an absolute path beginning with '/'.");
            }
            result.Add(new ResourceMountModel(
                name,
                NormalizeChoice(kind, "Volume", "Configuration", "Secret"),
                path,
                source,
                size));
        }
        return result;
    }

    private List<ResourceSettingModel> ParseSettings()
    {
        var result = new List<ResourceSettingModel>(Settings.Length);
        foreach (ITaskItem item in Settings)
        {
            string key = item.ItemSpec.Trim();
            string type = item.GetMetadata("Type").Trim();
            if (type.Length == 0)
            {
                type = "string";
            }
            if (key.Length == 0)
            {
                Log.LogError("Every CohesionSetting requires a non-empty Include.");
                continue;
            }
            if (!TryNormalizeSettingType(type, out string normalizedType))
            {
                Log.LogError($"CohesionSetting '{key}' Type '{type}' is not a valid type name.");
                continue;
            }
            result.Add(new ResourceSettingModel(key, NullIfEmpty(item.GetMetadata("Default")), normalizedType));
        }
        return result;
    }

    private Dictionary<string, ResourceProbeModel> ParseProbes(IReadOnlyList<ResourceEndpointModel> endpoints)
    {
        var result = new Dictionary<string, ResourceProbeModel>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem item in Probes)
        {
            string role = item.ItemSpec.Trim().ToLowerInvariant();
            string endpoint = item.GetMetadata("Endpoint").Trim();
            if (!IsOneOf(role, "readiness", "liveness", "startup"))
            {
                Log.LogError($"CohesionProbe '{item.ItemSpec}' must be readiness, liveness, or startup.");
                continue;
            }
            string? http = NullIfEmpty(item.GetMetadata("Http"));
            if (http is not null && !http.StartsWith("/", StringComparison.Ordinal))
            {
                Log.LogError($"CohesionProbe '{role}' Http must be an absolute path beginning with '/'.");
            }
            bool? tcp = ParseOptionalTrueBoolean(item.GetMetadata("Tcp"), $"CohesionProbe '{role}' Tcp");
            string? execValue = NullIfEmpty(item.GetMetadata("Exec"));
            IReadOnlyList<string>? exec = execValue is null ? null : SplitList(execValue);
            if (exec is { Count: 0 })
            {
                Log.LogError($"CohesionProbe '{role}' Exec must declare a non-empty command.");
            }
            string? grpc = NullIfEmpty(item.GetMetadata("Grpc"));
            bool? none = ParseOptionalTrueBoolean(item.GetMetadata("None"), $"CohesionProbe '{role}' None");
            int mechanisms = (http is null ? 0 : 1) + (tcp is null ? 0 : 1) + (exec is null ? 0 : 1) +
                (grpc is null ? 0 : 1) + (none is null ? 0 : 1);
            if (mechanisms != 1)
            {
                Log.LogError($"CohesionProbe '{role}' must declare exactly one of Http, Tcp, Exec, Grpc, or None.");
            }
            bool requiresEndpoint = http is not null || tcp is not null || grpc is not null;
            if ((requiresEndpoint || endpoint.Length > 0) &&
                !endpoints.Any(candidate => string.Equals(candidate.Name, endpoint, StringComparison.OrdinalIgnoreCase)))
            {
                Log.LogError($"CohesionProbe '{role}' names undeclared endpoint '{endpoint}'.");
            }
            if (!result.TryAdd(role, new ResourceProbeModel(endpoint, http, tcp, exec, grpc, none)))
            {
                Log.LogError($"Only one CohesionProbe may be declared for role '{role}'.");
            }
        }
        return result;
    }

    private Dictionary<string, string> ParseProperties()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string prefix = ResolvedResourceKind.ToLowerInvariant() + ".";
        foreach (ITaskItem item in ResourceProperties)
        {
            string key = item.ItemSpec.Trim();
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length)
            {
                Error(
                    "COHSDK009",
                    $"CohesionResourceProperty '{key}' must be prefixed with this project's kind '{prefix}'.");
                continue;
            }
            if (!result.TryAdd(key, item.GetMetadata("Value")))
            {
                Log.LogError($"CohesionResourceProperty '{key}' is declared more than once.");
            }
        }
        return result;
    }

    private List<ResourceReferenceModel> ParseReferences()
    {
        var result = new List<ResourceReferenceModel>(ResourceReferences.Length);
        foreach (ITaskItem item in ResourceReferences)
        {
            string identity = item.ItemSpec.Trim();
            bool optional = ParseBoolean(item.GetMetadata("Optional"), $"CohesionResourceReference '{identity}' Optional");
            string? version = NullIfEmpty(item.GetMetadata("Version"));
            ITaskItem? resolved = ResolveManifestItem(identity);
            if (resolved is null)
            {
                Log.LogError($"CohesionResourceReference '{identity}' did not resolve to a cohesion/resource/v1 manifest.");
                continue;
            }
            if (string.Equals(resolved.GetMetadata("ApplicationModel"), "disabled", StringComparison.OrdinalIgnoreCase))
            {
                string project = NullIfEmpty(resolved.GetMetadata("ProjectName")) ?? Path.GetFileNameWithoutExtension(identity);
                Error("COHSDK001", $"{project} has CohesionApplicationModel disabled; set it to enabled to reference it");
                continue;
            }

            string manifestPath = NullIfEmpty(resolved.GetMetadata("ManifestPath")) ?? resolved.ItemSpec;
            if (!File.Exists(manifestPath) || !string.Equals(Path.GetExtension(manifestPath), ".json", StringComparison.OrdinalIgnoreCase))
            {
                Log.LogError($"CohesionResourceReference '{identity}' resolved manifest '{manifestPath}' does not exist.");
                continue;
            }

            try
            {
                string? resolvedVersion = version
                    ?? NullIfEmpty(resolved.GetMetadata("PackageVersion"))
                    ?? NullIfEmpty(resolved.GetMetadata("Version"));
                ResourceReferenceModel reference = ReadReferenceManifest(
                    manifestPath,
                    optional,
                    identity,
                    resolvedVersion,
                    string.Equals(Path.GetExtension(identity), ".csproj", StringComparison.OrdinalIgnoreCase));
                string[] selectedEndpoints = SplitList(item.GetMetadata("Endpoints"));
                if (selectedEndpoints.Length > 0)
                {
                    reference.Endpoints.RemoveAll(endpoint => !selectedEndpoints.Contains(endpoint.Name, StringComparer.OrdinalIgnoreCase));
                    foreach (string endpoint in selectedEndpoints)
                    {
                        if (!reference.Endpoints.Any(candidate => string.Equals(candidate.Name, endpoint, StringComparison.OrdinalIgnoreCase)))
                        {
                            Log.LogError($"CohesionResourceReference '{identity}' names missing endpoint '{endpoint}'.");
                        }
                    }
                }
                result.Add(reference);
            }
            catch (JsonException exception)
            {
                Log.LogError($"Cohesion resource manifest '{manifestPath}' is invalid JSON: {exception.Message}");
            }
            catch (InvalidDataException exception)
            {
                Log.LogError(exception.Message);
            }
            catch (IOException exception)
            {
                Log.LogError($"Could not read Cohesion resource manifest '{manifestPath}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                Log.LogError($"Could not read Cohesion resource manifest '{manifestPath}': {exception.Message}");
            }
        }
        return result;
    }

    private ITaskItem? ResolveManifestItem(string identity)
    {
        bool isProject = string.Equals(Path.GetExtension(identity), ".csproj", StringComparison.OrdinalIgnoreCase);
        string? expectedProject = isProject
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProjectFullPath)!, identity))
            : null;

        foreach (ITaskItem candidate in ReferencedManifests)
        {
            string? project = NullIfEmpty(candidate.GetMetadata("ProjectFullPath"));
            if (expectedProject is not null && project is not null && PathsEqual(expectedProject, project))
            {
                return candidate;
            }
            if (!isProject)
            {
                string? package = NullIfEmpty(candidate.GetMetadata("PackageId"))
                    ?? NullIfEmpty(candidate.GetMetadata("ReferenceIdentity"));
                if (string.Equals(package, identity, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private ResourceReferenceModel ReadReferenceManifest(
        string path,
        bool optional,
        string identity,
        string? version,
        bool isProjectReference)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        RequireObject(root, "root", path);
        if (!string.Equals(RequiredString(root, "schema", path), "cohesion/resource/v1", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Cohesion resource manifest '{path}' does not declare schema 'cohesion/resource/v1'.");
        }

        string resource = RequiredString(root, "name", path);
        string application = RequiredString(root, "application", path);
        _ = RequiredString(root, "kind", path);
        _ = RequiredString(root, "applicationModel", path);
        if (!IsRfc1123Label(resource) || !IsRfc1123Label(application))
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' has a name or application that is not an RFC 1123 label.");
        }
        JsonElement artifactElement = RequiredObject(root, "artifact", path);
        var artifact = new ResourceArtifactModel(
            RequiredString(artifactElement, "assembly", path),
            OptionalBoolean(artifactElement, "composable", path) ?? false,
            OptionalString(artifactElement, "project", path) ?? string.Empty,
            OptionalString(artifactElement, "apphost", path) ?? string.Empty);
        JsonElement lifecycleElement = RequiredObject(root, "lifecycle", path);
        string workload = RequiredString(lifecycleElement, "workload", path);
        if (!IsOneOf(workload, "Deployment", "StatefulSet", "Job", "DaemonSet"))
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' has unsupported lifecycle workload '{workload}'.");
        }
        int replicas = RequiredPositiveInteger(lifecycleElement, "replicas", path);
        int? maxReplicas = OptionalPositiveInteger(lifecycleElement, "maxReplicas", path);
        if (maxReplicas is int maximum && replicas > maximum)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' has replicas greater than maxReplicas.");
        }
        string restartPolicy = OptionalString(lifecycleElement, "restartPolicy", path) ?? "OnFailure";
        if (!IsOneOf(restartPolicy, "OnFailure", "Always", "Never"))
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' has unsupported restart policy '{restartPolicy}'.");
        }
        var lifecycle = new ResourceLifecycleModel(
            NormalizeChoice(workload, "Deployment", "StatefulSet", "Job", "DaemonSet"),
            replicas,
            maxReplicas,
            RequiredPositiveInteger(lifecycleElement, "stopGraceSeconds", path),
            NormalizeChoice(restartPolicy, "OnFailure", "Always", "Never"));
        var result = new ResourceReferenceModel
        {
            Resource = resource,
            Application = application,
            Optional = optional,
            Manifest = isProjectReference
                ? artifact.Assembly
                : version is null ? identity : $"{identity}@{version}",
            Artifact = artifact,
            Lifecycle = lifecycle
        };

        JsonElement endpointArray = RequiredArray(root, "endpoints", path);
        foreach (JsonElement endpoint in endpointArray.EnumerateArray())
        {
            RequireObject(endpoint, "endpoints[]", path);
            string endpointName = RequiredString(endpoint, "name", path);
            string protocol = OptionalString(endpoint, "protocol", path) ?? "tcp";
            if (!IsOneOf(protocol, "tcp", "udp"))
            {
                throw new InvalidDataException(
                    $"Cohesion resource manifest '{path}' endpoint '{endpointName}' has unsupported protocol '{protocol}'.");
            }
            result.Endpoints.Add(new ResourceEndpointModel(
                endpointName,
                RequiredString(endpoint, "scheme", path),
                protocol.ToLowerInvariant(),
                RequiredPort(endpoint, "containerPort", path),
                OptionalPort(endpoint, "devPort", path),
                OptionalBoolean(endpoint, "public", path) ?? false,
                OptionalString(endpoint, "certificate", path)));
        }
        JsonElement controlPlane = RequiredObject(root, "controlPlane", path);
        string controlPlaneEndpoint = RequiredString(controlPlane, "endpoint", path);
        string controlPlanePath = RequiredString(controlPlane, "path", path);
        if (!controlPlanePath.StartsWith("/", StringComparison.Ordinal) ||
            !result.Endpoints.Any(endpoint =>
                string.Equals(endpoint.Name, controlPlaneEndpoint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' has an invalid controlPlane endpoint or path.");
        }
        if (root.TryGetProperty("mounts", out JsonElement mounts))
        {
            if (mounts.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Cohesion resource manifest '{path}' member 'mounts' must be an array.");
            }
            foreach (JsonElement mount in mounts.EnumerateArray())
            {
                RequireObject(mount, "mounts[]", path);
                string mountName = RequiredString(mount, "name", path);
                string kind = RequiredString(mount, "kind", path);
                if (!IsOneOf(kind, "Volume", "Configuration", "Secret"))
                {
                    throw new InvalidDataException(
                        $"Cohesion resource manifest '{path}' mount '{mountName}' has unsupported kind '{kind}'.");
                }
                string? size = OptionalString(mount, "size", path);
                if (string.Equals(kind, "Volume", StringComparison.OrdinalIgnoreCase) && size is null)
                {
                    throw new InvalidDataException(
                        $"Cohesion resource manifest '{path}' volume mount '{mountName}' does not declare size.");
                }
                string? source = OptionalString(mount, "source", path);
                if (source is not null && !string.Equals(kind, "Configuration", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Cohesion resource manifest '{path}' mount '{mountName}' declares Source outside a Configuration mount.");
                }
                if (source is not null && !IsMountSource(source))
                {
                    throw new InvalidDataException(
                        $"Cohesion resource manifest '{path}' mount '{mountName}' has invalid Source '{source}'.");
                }
                result.Mounts.Add(new ResourceMountModel(
                    mountName,
                    NormalizeChoice(kind, "Volume", "Configuration", "Secret"),
                    RequiredString(mount, "containerPath", path),
                    source,
                    size));
            }
        }

        return result;
    }

    private void LiftComposite(
        IReadOnlyList<ResourceReferenceModel> references,
        List<ResourceEndpointModel> endpoints,
        List<ResourceMountModel> mounts,
        ref string workload,
        ref int? maxReplicas,
        ref bool composable)
    {
        bool stateful = mounts.Any(mount =>
            string.Equals(mount.Kind, "Volume", StringComparison.OrdinalIgnoreCase));
        foreach (ResourceReferenceModel reference in references)
        {
            if (!string.Equals(reference.Application, ResolvedApplicationName, StringComparison.Ordinal))
            {
                continue;
            }

            string memberName = CompositeMemberName(reference.Resource);
            if (string.Equals(reference.Lifecycle.Workload, "Job", StringComparison.OrdinalIgnoreCase))
            {
                Log.LogError($"Composite resource '{ResolvedResourceName}' cannot contain Job member '{reference.Resource}'.");
            }
            foreach (ResourceEndpointModel endpoint in reference.Endpoints)
            {
                endpoints.Add(endpoint with
                {
                    Name = $"{memberName}-{endpoint.Name}",
                    Certificate = endpoint.Certificate is null ? null : $"{memberName}-{endpoint.Certificate}"
                });
            }
            foreach (ResourceMountModel mount in reference.Mounts)
            {
                mounts.Add(mount with { Name = $"{memberName}-{mount.Name}" });
            }
            if (string.Equals(reference.Lifecycle.Workload, "StatefulSet", StringComparison.OrdinalIgnoreCase) ||
                reference.Mounts.Any(mount => string.Equals(mount.Kind, "Volume", StringComparison.OrdinalIgnoreCase)))
            {
                stateful = true;
            }
            if (reference.Lifecycle.MaxReplicas is int memberMax)
            {
                maxReplicas = maxReplicas is int current ? Math.Min(current, memberMax) : memberMax;
            }
            StopGraceSeconds = Math.Max(StopGraceSeconds, reference.Lifecycle.StopGraceSeconds);
            composable &= reference.Artifact.Composable;
        }
        workload = stateful ? "StatefulSet" : "Deployment";
    }

    private string CompositeMemberName(string resource)
    {
        string prefix = ResolvedApplicationName + "-";
        return resource.StartsWith(prefix, StringComparison.Ordinal) && resource.Length > prefix.Length
            ? resource.Substring(prefix.Length)
            : resource;
    }

    private string ResolveWorkload(IReadOnlyList<ResourceMountModel> mounts)
    {
        if (string.IsNullOrWhiteSpace(WorkloadKind))
        {
            return mounts.Any(mount => string.Equals(mount.Kind, "Volume", StringComparison.OrdinalIgnoreCase))
                ? "StatefulSet"
                : "Deployment";
        }
        if (!IsOneOf(WorkloadKind, "Deployment", "StatefulSet", "Job", "DaemonSet"))
        {
            Log.LogError("CohesionWorkloadKind must be Deployment, StatefulSet, Job, or DaemonSet.");
            return WorkloadKind;
        }
        return NormalizeChoice(WorkloadKind, "Deployment", "StatefulSet", "Job", "DaemonSet");
    }

    private void ValidateMetadata(ITaskItem[] items, string itemType, params string[] allowed)
    {
        var known = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem item in items)
        {
            foreach (string metadata in MetadataNames(item))
            {
                if (!BuiltInMetadata.Contains(metadata) && !known.Contains(metadata))
                {
                    Log.LogError($"Unknown metadata '{metadata}' on {itemType} '{item.ItemSpec}'.");
                }
            }
        }
    }

    private void EnsureUniqueIdentifiers(IEnumerable<string> names, string itemType)
    {
        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            string identifier = ResourceSourceWriter.Identifier(name);
            if (identifiers.TryGetValue(identifier, out string? existing))
            {
                Log.LogError($"{itemType} names '{existing}' and '{name}' both normalize to generated identifier '{identifier}'.");
            }
            else
            {
                identifiers.Add(identifier, name);
            }
        }
    }

    private void EnsureGeneratedMemberNames(
        IReadOnlyList<ResourceEndpointModel> endpoints,
        IReadOnlyList<ResourceMountModel> mounts,
        IReadOnlyList<ResourceSettingModel> settings,
        IReadOnlyList<ResourceReferenceModel> references)
    {
        EnsureDoesNotMatchEnclosingType(endpoints.Select(endpoint => endpoint.Name), "Endpoints", "CohesionEndpoint");
        EnsureDoesNotMatchEnclosingType(mounts.Select(mount => mount.Name), "Mounts", "CohesionMount");
        EnsureDoesNotMatchEnclosingType(settings.Select(setting => setting.Key), "Settings", "CohesionSetting");
        EnsureDoesNotMatchEnclosingType(
            references.Select(reference => reference.Resource),
            "References",
            "CohesionResourceReference");

        foreach (ResourceReferenceModel reference in references)
        {
            EnsureUniqueIdentifiers(
                reference.Endpoints.Select(endpoint => endpoint.Name),
                $"CohesionResourceReference '{reference.Resource}' endpoint");
            EnsureDoesNotMatchEnclosingType(
                reference.Endpoints.Select(endpoint => endpoint.Name),
                ResourceSourceWriter.Identifier(reference.Resource),
                $"CohesionResourceReference '{reference.Resource}' endpoint");
        }
    }

    private void EnsureDoesNotMatchEnclosingType(
        IEnumerable<string> names,
        string enclosingType,
        string itemType)
    {
        foreach (string name in names)
        {
            string identifier = ResourceSourceWriter.Identifier(name);
            if (string.Equals(identifier, enclosingType, StringComparison.Ordinal))
            {
                Log.LogError(
                    $"{itemType} name '{name}' normalizes to '{identifier}', which conflicts with its generated enclosing type.");
            }
        }
    }

    private int ParsePort(string value, string description)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            Log.LogError($"{description} must be between 1 and 65535.");
            return 1;
        }
        return port;
    }

    private int? ParseOptionalPort(string value, string description)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ParsePort(value, description);
    }

    private int? ParseOptionalPositiveInt(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
        {
            Log.LogError($"{description} must be a positive integer when specified.");
            return null;
        }
        return parsed;
    }

    private bool ParseBoolean(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (!bool.TryParse(value, out bool parsed))
        {
            Log.LogError($"{description} must be true or false.");
        }
        return parsed;
    }

    private bool? ParseOptionalTrueBoolean(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!bool.TryParse(value, out bool parsed))
        {
            Log.LogError($"{description} must be true or false.");
            return false;
        }
        if (!parsed)
        {
            Log.LogError($"{description} must be true when declared.");
        }
        return parsed;
    }

    private static string RequiredString(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Cohesion resource manifest '{path}' is missing required string '{property}'.");
        }
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be a string or null.");
        }
        return NullIfEmpty(value.GetString()!);
    }

    private static bool? OptionalBoolean(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be a Boolean or null.");
        }
        return value.GetBoolean();
    }

    private static int RequiredPositiveInteger(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int parsed) ||
            parsed < 1)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be a positive 32-bit integer.");
        }
        return parsed;
    }

    private static int? OptionalPositiveInteger(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed) || parsed < 1)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be a positive 32-bit integer or null.");
        }
        return parsed;
    }

    private static int RequiredPort(JsonElement element, string property, string path)
    {
        int port = RequiredPositiveInteger(element, property, path);
        if (port > 65535)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be between 1 and 65535.");
        }
        return port;
    }

    private static int? OptionalPort(JsonElement element, string property, string path)
    {
        int? port = OptionalPositiveInteger(element, property, path);
        if (port > 65535)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be between 1 and 65535 or null.");
        }
        return port;
    }

    private static JsonElement RequiredObject(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be an object.");
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{property}' must be an array.");
        }
        return value;
    }

    private static void RequireObject(JsonElement element, string description, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' member '{description}' must be an object.");
        }
    }

    private static IEnumerable<string> MetadataNames(ITaskItem item)
    {
        foreach (object? name in item.MetadataNames)
        {
            if (name is string text)
            {
                yield return text;
            }
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string DeriveResourceName(string projectName)
    {
        string[] segments = projectName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return Slug(segments.Length > 1 ? string.Join("-", segments.Skip(1)) : projectName);
    }

    private static string DeriveApplicationName(string projectName)
    {
        string[] segments = projectName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return Slug(segments.Length == 2 ? segments[0] : segments.Length >= 3 ? segments[1] : projectName);
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool separator = false;
        foreach (char character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && builder.Length > 0)
                {
                    builder.Append('-');
                }
                builder.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else
            {
                separator = true;
            }
        }
        return builder.ToString();
    }

    private static bool IsNamespace(string value)
    {
        return value.Split('.').All(IsIdentifier);
    }

    private static bool TryNormalizeSettingType(string value, out string normalized)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "string":
            case "system.string":
                normalized = "string";
                return true;
            case "int":
            case "int32":
            case "system.int32":
                normalized = "int";
                return true;
            case "bool":
            case "boolean":
            case "system.boolean":
                normalized = "bool";
                return true;
            case "timespan":
            case "system.timespan":
                normalized = "TimeSpan";
                return true;
            case "uri":
            case "system.uri":
                normalized = "Uri";
                return true;
        }

        string candidate = value.Trim();
        if (candidate.StartsWith("global::", StringComparison.Ordinal))
        {
            candidate = candidate.Substring(8);
        }
        string[] segments = candidate.Replace('+', '.').Split('.');
        if (segments.Length == 0 || !segments.All(IsIdentifier))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = candidate.Replace('+', '.');
        return true;
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            CSharpKeywords.Contains(value) ||
            !(IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }
        return value.Skip(1).All(character => IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '_');
    }

    private static bool IsAsciiLetter(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static bool IsRfc1123Label(string value)
    {
        if (value.Length is < 1 or > 63 || !IsLowerAsciiLetterOrDigit(value[0]) || !IsLowerAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }
        return value.All(character => IsLowerAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsLowerAsciiLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    private static bool IsMountSource(string source)
    {
        if (source.StartsWith("parameter:", StringComparison.Ordinal))
        {
            return source.Length > "parameter:".Length;
        }
        if (source.StartsWith("literal:", StringComparison.Ordinal))
        {
            return true;
        }

        int separator = source.IndexOf(':');
        return separator > 0 && separator < source.Length - 1;
    }

    private static bool IsOneOf(string value, params string[] choices)
    {
        return choices.Any(choice => string.Equals(value, choice, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeChoice(string value, params string[] choices)
    {
        return choices.First(choice => string.Equals(value, choice, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SplitList(string value)
    {
        return value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void Error(string code, string message)
    {
        Log.LogError(
            subcategory: null,
            errorCode: code,
            helpKeyword: null,
            file: ProjectFullPath,
            lineNumber: 0,
            columnNumber: 0,
            endLineNumber: 0,
            endColumnNumber: 0,
            message: message);
    }
}
