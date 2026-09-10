using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.Gateway.Tasks;

/// <summary>
/// Reads resolved resource manifests and generates a gateway's build-time composition surface.
/// </summary>
public sealed class CohesionCreateResourceVerbs : Task
{
    /// <summary>Gets or sets the generated C# output path.</summary>
    [Required]
    public string SourceOutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the gateway project path used to resolve relative references.</summary>
    [Required]
    public string ProjectFullPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the application name compiled into the gateway.</summary>
    [Required]
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Gets or sets direct project and manifest-package references.</summary>
    public ITaskItem[] ResourceReferences { get; set; } = [];

    /// <summary>Gets or sets project and package manifests in the boundary-aware closure.</summary>
    public ITaskItem[] ReferencedManifests { get; set; } = [];

    /// <summary>Gets or sets area-specific typed resource verb metadata.</summary>
    public ITaskItem[] ResourceKinds { get; set; } = [];

    /// <summary>Gets or sets gateway providers contributed by platform packages.</summary>
    public ITaskItem[] GatewayProviders { get; set; } = [];

    /// <summary>Gets or sets the semicolon-delimited providers selected by the gateway project.</summary>
    [Required]
    public string Gateways { get; set; } = string.Empty;

    /// <summary>Gets or sets whether project-referenced composable resources run in process.</summary>
    public bool InProcessEnabled { get; set; }

    /// <summary>Gets or sets mount-source target-kind to client-package mappings.</summary>
    public ITaskItem[] ClientKinds { get; set; } = [];

    /// <summary>Gets the application-model packages named by referenced manifests.</summary>
    [Output]
    public ITaskItem[] RequiredApplicationModels { get; private set; } = [];

    /// <summary>Gets the client packages required by protected mount sources.</summary>
    [Output]
    public ITaskItem[] RequiredClientPackages { get; private set; } = [];

    /// <summary>
    /// Gets the enabled, composable project resources whose content is required by generated
    /// in-process bindings.
    /// </summary>
    [Output]
    public ITaskItem[] InProcessProjectReferences { get; private set; } = [];

    /// <inheritdoc />
    public override bool Execute()
    {
        string application = ApplicationName.Trim();
        if (!IsRfc1123Label(application))
        {
            Error($"CohesionApplicationName '{ApplicationName}' must be an RFC 1123 label.");
            return false;
        }

        try
        {
            List<GatewayManifest> manifests = ReadManifests();
            MarkDirectReferences(manifests);
            AssignMemberNames(manifests, application);

            List<GatewayResourceKind> resourceKinds = ReadResourceKinds();
            List<GatewayProvider> providers = ReadProviders();
            List<GatewayClientKind> clientKinds = ReadClientKinds();
            if (Log.HasLoggedErrors)
            {
                return false;
            }

            List<GatewayManifest> resources = SortResources(
                manifests.Where(manifest => string.Equals(
                    manifest.Application,
                    application,
                    StringComparison.OrdinalIgnoreCase)).ToList());
            ResolveInProcessBindings(resources);
            List<GatewayExternal> externals = CreateExternals(manifests, resources, application);
            List<GatewayManifest> applications = manifests
                .Where(manifest => manifest.IsDirect &&
                    !string.IsNullOrWhiteSpace(manifest.ProjectPath) &&
                    !string.IsNullOrWhiteSpace(manifest.AppHostPath) &&
                    string.Equals(manifest.Kind, "Composite", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(manifest.Application, application, StringComparison.OrdinalIgnoreCase))
                .GroupBy(manifest => manifest.Application, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(manifest => manifest.Application, StringComparer.Ordinal)
                .ToList();
            ValidateGeneratedMemberNames(externals, applications);
            if (Log.HasLoggedErrors)
            {
                return false;
            }

            RequiredApplicationModels = manifests
                .Select(manifest => manifest.ApplicationModel)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => (ITaskItem)new TaskItem(value))
                .ToArray();
            RequiredClientPackages = ResolveClientPackages(manifests, clientKinds)
                .Select(value => (ITaskItem)new TaskItem(value))
                .ToArray();
            InProcessProjectReferences = resources
                .Where(manifest => manifest.InProcessBinding is not null)
                .Select(CreateInProcessProjectReference)
                .ToArray();

            GatewaySourceWriter.Write(
                SourceOutputPath,
                application,
                manifests,
                resources,
                externals,
                applications,
                resourceKinds,
                providers);
            Log.LogMessage(MessageImportance.Normal, $"Generated Cohesion gateway source '{SourceOutputPath}'.");
            return !Log.HasLoggedErrors;
        }
        catch (JsonException exception)
        {
            Log.LogError($"A referenced Cohesion resource manifest is invalid JSON: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            Log.LogError(exception.Message);
        }
        catch (IOException exception)
        {
            Log.LogError($"Could not generate the Cohesion gateway source: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.LogError($"Could not generate the Cohesion gateway source: {exception.Message}");
        }

        return false;
    }

    private List<GatewayManifest> ReadManifests()
    {
        var manifests = new Dictionary<(string Application, string Resource), GatewayManifest>(
            new ManifestIdentityComparer());
        foreach (ITaskItem item in ReferencedManifests)
        {
            if (string.Equals(item.GetMetadata("ApplicationModel"), "disabled", StringComparison.OrdinalIgnoreCase))
            {
                string project = Value(item.GetMetadata("ProjectName"))
                    ?? Path.GetFileNameWithoutExtension(item.ItemSpec);
                Error($"{project} has CohesionApplicationModel disabled; set it to enabled to reference it", "COHSDK001");
                continue;
            }

            string path = Value(item.GetMetadata("ManifestPath")) ?? item.ItemSpec;
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GatewayManifest manifest = ReadManifest(path, item);
            var identity = (manifest.Application, manifest.Name);
            if (manifests.TryGetValue(identity, out GatewayManifest? existing))
            {
                if (string.IsNullOrWhiteSpace(existing.ProjectPath) && !string.IsNullOrWhiteSpace(manifest.ProjectPath))
                {
                    manifests[identity] = manifest;
                }
            }
            else
            {
                manifests.Add(identity, manifest);
            }
        }

        return manifests.Values
            .OrderBy(manifest => manifest.Application, StringComparer.Ordinal)
            .ThenBy(manifest => manifest.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static GatewayManifest ReadManifest(string path, ITaskItem item)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        RequireObject(root, "root", path);
        if (!string.Equals(RequiredString(root, "schema", path), "cohesion/resource/v1", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Cohesion resource manifest '{path}' does not declare schema 'cohesion/resource/v1'.");
        }

        JsonElement artifact = RequiredObject(root, "artifact", path);
        JsonElement controlPlane = RequiredObject(root, "controlPlane", path);
        var manifest = new GatewayManifest
        {
            Path = Path.GetFullPath(path),
            Name = RequiredString(root, "name", path),
            Application = RequiredString(root, "application", path),
            Kind = RequiredString(root, "kind", path),
            ApplicationModel = RequiredString(root, "applicationModel", path),
            RawJson = root.GetRawText(),
            Composable = OptionalBoolean(artifact, "composable", path),
            ControlPlaneEndpoint = RequiredString(controlPlane, "endpoint", path),
            ControlPlanePath = RequiredString(controlPlane, "path", path),
            ProjectPath = Value(item.GetMetadata("ProjectFullPath"))
                ?? OptionalString(artifact, "project", path)
                ?? string.Empty,
            ProjectName = Value(item.GetMetadata("ProjectName")) ?? string.Empty,
            RootNamespace = Value(item.GetMetadata("RootNamespace")) ?? string.Empty,
            TargetPath = Value(item.GetMetadata("TargetPath")) ?? string.Empty,
            AppHostPath = Value(item.GetMetadata("AppHostPath"))
                ?? OptionalString(artifact, "apphost", path)
                ?? string.Empty,
            ReferenceIdentity = Value(item.GetMetadata("ReferenceIdentity"))
                ?? Value(item.GetMetadata("PackageId"))
                ?? item.ItemSpec,
            IsDirect = false,
            IsDirectProjectReference = false
        };

        foreach (JsonElement endpoint in RequiredArray(root, "endpoints", path).EnumerateArray())
        {
            RequireObject(endpoint, "endpoints[]", path);
            manifest.Endpoints.Add(new GatewayManifestEndpoint(RequiredString(endpoint, "name", path)));
        }
        if (!manifest.ControlPlanePath.StartsWith("/", StringComparison.Ordinal) ||
            !manifest.Endpoints.Any(endpoint => string.Equals(
                endpoint.Name,
                manifest.ControlPlaneEndpoint,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Cohesion resource manifest '{path}' has an invalid controlPlane endpoint or path.");
        }
        foreach (JsonElement mount in RequiredArray(root, "mounts", path).EnumerateArray())
        {
            RequireObject(mount, "mounts[]", path);
            manifest.Mounts.Add(new GatewayManifestMount(
                RequiredString(mount, "name", path),
                RequiredString(mount, "kind", path),
                OptionalString(mount, "source", path)));
        }
        foreach (JsonElement reference in RequiredArray(root, "references", path).EnumerateArray())
        {
            RequireObject(reference, "references[]", path);
            string[] endpoints = RequiredArray(reference, "endpoints", path)
                .EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : throw new InvalidDataException(
                        $"Cohesion resource manifest '{path}' reference endpoints must be strings."))
                .ToArray();
            manifest.References.Add(new GatewayManifestReference(
                RequiredString(reference, "resource", path),
                RequiredString(reference, "application", path),
                endpoints,
                OptionalBoolean(reference, "optional", path)));
        }

        return manifest;
    }

    private void MarkDirectReferences(IReadOnlyList<GatewayManifest> manifests)
    {
        var projects = new HashSet<string>(PathComparer());
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(ProjectFullPath))!;
        foreach (ITaskItem reference in ResourceReferences)
        {
            if (string.Equals(Path.GetExtension(reference.ItemSpec), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projects.Add(Path.GetFullPath(reference.ItemSpec, baseDirectory));
            }
            else
            {
                packages.Add(reference.ItemSpec);
            }
        }

        foreach (GatewayManifest manifest in manifests)
        {
            manifest.IsDirectProjectReference = !string.IsNullOrWhiteSpace(manifest.ProjectPath) &&
                projects.Contains(Path.GetFullPath(manifest.ProjectPath));
            manifest.IsDirect = manifest.IsDirectProjectReference ||
                packages.Contains(manifest.ReferenceIdentity);
        }
    }

    private void ResolveInProcessBindings(IEnumerable<GatewayManifest> manifests)
    {
        if (!InProcessEnabled)
        {
            return;
        }

        foreach (GatewayManifest manifest in manifests.Where(candidate =>
            !string.IsNullOrWhiteSpace(candidate.ProjectPath) && candidate.Composable))
        {
            if (string.IsNullOrWhiteSpace(manifest.RootNamespace))
            {
                throw new InvalidDataException(
                    $"In-process resource '{manifest.Application}/{manifest.Name}' did not report its RootNamespace.");
            }
            if (string.IsNullOrWhiteSpace(manifest.TargetPath))
            {
                throw new InvalidDataException(
                    $"In-process resource '{manifest.Application}/{manifest.Name}' did not report its TargetPath.");
            }

            string projectPath = Path.GetFullPath(manifest.ProjectPath);
            string projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidDataException(
                    $"In-process resource '{manifest.Application}/{manifest.Name}' has an invalid project path '{manifest.ProjectPath}'.");
            string targetPath = Path.IsPathFullyQualified(manifest.TargetPath)
                ? Path.GetFullPath(manifest.TargetPath)
                : Path.GetFullPath(manifest.TargetPath, projectDirectory);
            if (!File.Exists(targetPath))
            {
                throw new InvalidDataException(
                    $"In-process resource '{manifest.Application}/{manifest.Name}' target assembly '{targetPath}' does not exist.");
            }

            (string entryPointType, string entryAssemblyName) = ReadEntryPoint(targetPath, manifest);
            manifest.InProcessBinding = new GatewayInProcessBinding(
                manifest.RootNamespace,
                entryPointType,
                entryAssemblyName,
                manifest.Name,
                targetPath,
                projectPath);
        }
    }

    private static ITaskItem CreateInProcessProjectReference(GatewayManifest manifest)
    {
        GatewayInProcessBinding binding = manifest.InProcessBinding
            ?? throw new InvalidOperationException(
                $"Resource '{manifest.Application}/{manifest.Name}' has no in-process binding.");
        var item = new TaskItem(binding.ProjectPath);
        item.SetMetadata("TargetPath", binding.TargetPath);
        item.SetMetadata("ResourceName", binding.ResourceName);
        return item;
    }

    private static (string EntryPointType, string AssemblyName) ReadEntryPoint(
        string targetPath,
        GatewayManifest manifest)
    {
        using FileStream stream = File.OpenRead(targetPath);
        using var peReader = new PEReader(stream);
        CorHeader? corHeader = peReader.PEHeaders.CorHeader;
        if (!peReader.HasMetadata || corHeader is null ||
            (corHeader.Flags & CorFlags.NativeEntryPoint) != 0 ||
            corHeader.EntryPointTokenOrRelativeVirtualAddress == 0)
        {
            throw new InvalidDataException(
                $"In-process resource '{manifest.Application}/{manifest.Name}' target assembly '{targetPath}' has no managed entry point.");
        }

        MetadataReader reader = peReader.GetMetadataReader();
        EntityHandle entryPoint = MetadataTokens.EntityHandle(corHeader.EntryPointTokenOrRelativeVirtualAddress);
        if (entryPoint.Kind != HandleKind.MethodDefinition)
        {
            throw new InvalidDataException(
                $"In-process resource '{manifest.Application}/{manifest.Name}' target assembly '{targetPath}' has an invalid managed entry point token.");
        }

        MethodDefinition method = reader.GetMethodDefinition((MethodDefinitionHandle)entryPoint);
        string entryPointType = FullTypeName(reader, method.GetDeclaringType());
        string assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
        return (entryPointType, assemblyName);
    }

    private static string FullTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return FullTypeName(reader, declaringType) + "+" + name;
        }

        string typeNamespace = reader.GetString(type.Namespace);
        return typeNamespace.Length == 0 ? name : typeNamespace + "." + name;
    }

    private void AssignMemberNames(IReadOnlyList<GatewayManifest> manifests, string application)
    {
        var members = new Dictionary<string, GatewayManifest>(StringComparer.Ordinal);
        foreach (GatewayManifest manifest in manifests)
        {
            string member = GatewaySourceWriter.FriendlyManifestName(manifest);
            if (members.TryGetValue(member, out GatewayManifest? existing) &&
                (!string.Equals(existing.Application, manifest.Application, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(existing.Name, manifest.Name, StringComparison.OrdinalIgnoreCase)))
            {
                member = GatewaySourceWriter.Identifier(manifest.Application) + GatewaySourceWriter.Identifier(manifest.Name);
            }
            if (members.ContainsKey(member))
            {
                Error($"Referenced manifests normalize to duplicate generated member '{member}'.");
                continue;
            }

            manifest.MemberName = member;
            members.Add(member, manifest);
        }

        if (!manifests.Any(manifest => string.Equals(manifest.Application, application, StringComparison.OrdinalIgnoreCase)))
        {
            Log.LogWarning(
                $"Sdk.Gateway found no resource manifest owned by application '{application}'; Build() will reject a zero-resource application.");
        }
    }

    private List<GatewayResourceKind> ReadResourceKinds()
    {
        var result = new List<GatewayResourceKind>(ResourceKinds.Length);
        foreach (ITaskItem item in ResourceKinds)
        {
            string applicationModel = item.GetMetadata("ApplicationModel").Trim();
            string optionsType = item.GetMetadata("OptionsType").Trim();
            string addMethod = item.GetMetadata("AddMethod").Trim();
            if (applicationModel.Length == 0 || optionsType.Length == 0 || addMethod.Length == 0)
            {
                Log.LogError($"CohesionGatewayResourceKind '{item.ItemSpec}' requires ApplicationModel, OptionsType, and AddMethod metadata.");
                continue;
            }
            result.Add(new GatewayResourceKind(item.ItemSpec, applicationModel, optionsType, addMethod));
        }
        return result;
    }

    private List<GatewayProvider> ReadProviders()
    {
        var selected = new HashSet<string>(
            Gateways.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<GatewayProvider>(GatewayProviders.Length);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (ITaskItem item in GatewayProviders)
        {
            string name = (Value(item.GetMetadata("Name")) ?? item.ItemSpec).Trim();
            if (!selected.Contains(name) && !selected.Contains(item.ItemSpec))
            {
                continue;
            }
            string gatewayType = item.GetMetadata("GatewayType").Trim();
            string optionsType = item.GetMetadata("OptionsType").Trim();
            string? commandLineApplyMethod = Value(item.GetMetadata("CommandLineApplyMethod"));
            if (name.Length == 0 || gatewayType.Length == 0 || optionsType.Length == 0)
            {
                Log.LogError($"CohesionGatewayProvider '{item.ItemSpec}' requires Name, GatewayType, and OptionsType metadata.");
                continue;
            }
            if (!names.Add(name))
            {
                Log.LogError($"Cohesion gateway provider name '{name}' is contributed more than once.");
                continue;
            }

            string member = GatewaySourceWriter.Identifier(item.ItemSpec);
            if (!members.Add(member))
            {
                Log.LogError($"Cohesion gateway providers normalize to duplicate generated member '{member}'.");
                continue;
            }
            if (!bool.TryParse(Value(item.GetMetadata("RequiresJit")) ?? "false", out bool requiresJit))
            {
                Log.LogError($"CohesionGatewayProvider '{item.ItemSpec}' RequiresJit must be true or false.");
                continue;
            }

            result.Add(new GatewayProvider(
                member,
                name,
                gatewayType,
                optionsType,
                commandLineApplyMethod,
                requiresJit));
        }

        if (result.Count == 0)
        {
            Log.LogError(
                "Sdk.Gateway did not receive a CohesionGatewayProvider item. " +
                "Select a package through CohesionGateways and restore the project.");
        }
        foreach (string name in selected)
        {
            if (!result.Any(provider => string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider.MemberName, GatewaySourceWriter.Identifier(name), StringComparison.Ordinal)))
            {
                Log.LogError(
                    $"Cohesion gateway '{name}' was selected by CohesionGateways, but its package did not contribute " +
                    "a matching CohesionGatewayProvider item. Restore the project and verify the platform package.");
            }
        }
        return result.OrderBy(provider => provider.Name, StringComparer.Ordinal).ToList();
    }

    private List<GatewayClientKind> ReadClientKinds()
    {
        var result = new List<GatewayClientKind>(ClientKinds.Length);
        foreach (ITaskItem item in ClientKinds)
        {
            string packageId = item.GetMetadata("PackageId").Trim();
            if (packageId.Length == 0)
            {
                Log.LogError($"CohesionGatewayClientKind '{item.ItemSpec}' requires PackageId metadata.");
                continue;
            }
            result.Add(new GatewayClientKind(item.ItemSpec, packageId));
        }
        return result;
    }

    private static List<GatewayManifest> SortResources(List<GatewayManifest> resources)
    {
        var byIdentity = resources.ToDictionary(
            manifest => (manifest.Application, manifest.Name),
            manifest => manifest,
            new ManifestIdentityComparer());
        var result = new List<GatewayManifest>(resources.Count);
        var visited = new HashSet<(string Application, string Resource)>(new ManifestIdentityComparer());
        var active = new HashSet<(string Application, string Resource)>(new ManifestIdentityComparer());

        foreach (GatewayManifest resource in resources.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            Visit(resource);
        }
        return result;

        void Visit(GatewayManifest resource)
        {
            var identity = (resource.Application, resource.Name);
            if (visited.Contains(identity) || !active.Add(identity))
            {
                return;
            }

            foreach (GatewayManifestReference reference in resource.References.OrderBy(value => value.Resource, StringComparer.Ordinal))
            {
                if (byIdentity.TryGetValue((reference.Application, reference.Resource), out GatewayManifest? dependency))
                {
                    Visit(dependency);
                }
            }
            active.Remove(identity);
            visited.Add(identity);
            result.Add(resource);
        }
    }

    private static List<GatewayExternal> CreateExternals(
        IReadOnlyList<GatewayManifest> manifests,
        IReadOnlyList<GatewayManifest> resources,
        string application)
    {
        var byIdentity = manifests.ToDictionary(
            manifest => (manifest.Application, manifest.Name),
            manifest => manifest,
            new ManifestIdentityComparer());
        var externals = new Dictionary<(string Application, string Resource), GatewayExternal>(
            new ManifestIdentityComparer());

        foreach (GatewayManifest resource in resources)
        {
            foreach (GatewayManifestReference reference in resource.References)
            {
                if (string.Equals(reference.Application, application, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var identity = (reference.Application, reference.Resource);
                if (!externals.TryGetValue(identity, out GatewayExternal? external))
                {
                    byIdentity.TryGetValue(identity, out GatewayManifest? target);
                    string member = target is null
                        ? GatewaySourceWriter.Identifier(reference.Application) + GatewaySourceWriter.Identifier(reference.Resource)
                        : GatewaySourceWriter.FriendlyManifestName(target);
                    external = new GatewayExternal
                    {
                        MemberName = member,
                        Resource = reference.Resource,
                        Application = reference.Application,
                        Optional = reference.Optional,
                        Manifest = target
                    };
                    externals.Add(identity, external);
                }
                else
                {
                    external.Optional &= reference.Optional;
                }
                foreach (string endpoint in reference.Endpoints)
                {
                    external.Endpoints.Add(endpoint);
                }
            }
        }

        foreach (GatewayExternal external in externals.Values)
        {
            if (external.Manifest is null)
            {
                continue;
            }

            var visited = new HashSet<(string Application, string Resource)>(new ManifestIdentityComparer());
            Visit(external.Manifest);

            void Visit(GatewayManifest manifest)
            {
                if (!visited.Add((manifest.Application, manifest.Name)))
                {
                    return;
                }
                external.Closure.Add(manifest);
                foreach (GatewayManifestReference reference in manifest.References)
                {
                    if (string.Equals(reference.Application, external.Application, StringComparison.OrdinalIgnoreCase) &&
                        byIdentity.TryGetValue((reference.Application, reference.Resource), out GatewayManifest? dependency))
                    {
                        Visit(dependency);
                    }
                }
            }
        }

        return externals.Values.OrderBy(value => value.MemberName, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> ResolveClientPackages(
        IReadOnlyList<GatewayManifest> manifests,
        IReadOnlyList<GatewayClientKind> clientKinds)
    {
        var kinds = clientKinds.ToDictionary(value => value.Kind, value => value.PackageId, StringComparer.OrdinalIgnoreCase);
        var resources = manifests
            .GroupBy(manifest => manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GatewayManifest manifest in manifests)
        {
            foreach (GatewayManifestMount mount in manifest.Mounts)
            {
                if (mount.Source is null)
                {
                    continue;
                }
                int separator = mount.Source.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }
                string targetName = mount.Source[..separator];
                if (resources.TryGetValue(targetName, out GatewayManifest? target) &&
                    kinds.TryGetValue(target.Kind, out string? packageId))
                {
                    packages.Add(packageId);
                }
            }
        }
        return packages.OrderBy(value => value, StringComparer.Ordinal);
    }

    private void ValidateGeneratedMemberNames(
        IReadOnlyList<GatewayExternal> externals,
        IReadOnlyList<GatewayManifest> applications)
    {
        var externalMembers = new Dictionary<string, GatewayExternal>(StringComparer.Ordinal);
        foreach (GatewayExternal external in externals)
        {
            if (externalMembers.TryGetValue(external.MemberName, out GatewayExternal? existing))
            {
                Error(
                    $"External resources '{existing.Application}/{existing.Resource}' and " +
                    $"'{external.Application}/{external.Resource}' normalize to duplicate generated member " +
                    $"'{external.MemberName}'.");
                continue;
            }

            externalMembers.Add(external.MemberName, external);
        }

        var applicationMembers = new Dictionary<string, GatewayManifest>(StringComparer.Ordinal);
        foreach (GatewayManifest application in applications)
        {
            string member = GatewaySourceWriter.Identifier(application.Application);
            if (applicationMembers.TryGetValue(member, out GatewayManifest? existing))
            {
                Error(
                    $"Gateway applications '{existing.Application}' and '{application.Application}' normalize " +
                    $"to duplicate generated member '{member}'.");
                continue;
            }

            applicationMembers.Add(member, application);
        }
    }

    private static JsonElement RequiredObject(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Cohesion resource manifest '{path}' member '{property}' must be an object.");
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Cohesion resource manifest '{path}' member '{property}' must be an array.");
        }
        return value;
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
            throw new InvalidDataException($"Cohesion resource manifest '{path}' member '{property}' must be a string or null.");
        }
        return Value(value.GetString());
    }

    private static bool OptionalBoolean(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Cohesion resource manifest '{path}' member '{property}' must be a Boolean.");
        }
        return value.GetBoolean();
    }

    private static void RequireObject(JsonElement element, string description, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Cohesion resource manifest '{path}' member '{description}' must be an object.");
        }
    }

    private static string? Value(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private static StringComparer PathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    private void Error(string message, string? code = null)
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

    private sealed class ManifestIdentityComparer : IEqualityComparer<(string Application, string Resource)>
    {
        public bool Equals(
            (string Application, string Resource) left,
            (string Application, string Resource) right)
        {
            return string.Equals(left.Application, right.Application, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Resource, right.Resource, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Application, string Resource) value)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Application),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Resource));
        }
    }
}
