using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The portable, platform-neutral description of a built Cohesion application model.
/// </summary>
public sealed class ApplicationModelDocument
{
    /// <summary>The application-model schema supported by this contract.</summary>
    public const string CurrentSchema = "cohesion/model/v1";

    /// <summary>
    /// Initializes an application-model document.
    /// </summary>
    /// <param name="schema">The application-model schema.</param>
    /// <param name="application">The application name.</param>
    /// <param name="environment">The target environment.</param>
    /// <param name="gateway">The selected gateway identity.</param>
    /// <param name="owner">The application owner identity.</param>
    /// <param name="mode">The requested gateway run mode.</param>
    /// <param name="adopt">Whether foreign-owned resources may be adopted.</param>
    /// <param name="restartOrphans">Whether orphaned local resources may be restarted.</param>
    /// <param name="resources">The model resources in declaration order.</param>
    /// <param name="commands">Optional declarative commands; absent documents retain an empty collection.</param>
    /// <exception cref="ArgumentNullException">
    /// A required string or <paramref name="resources"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidDataException"><paramref name="commands"/> contains a null entry.</exception>
    [JsonConstructor]
    public ApplicationModelDocument(
        string schema,
        string application,
        string environment,
        string gateway,
        string owner,
        string mode,
        bool adopt,
        bool restartOrphans,
        IReadOnlyList<ApplicationModelResourceDocument> resources,
        IReadOnlyList<ApplicationModelCommandDocument>? commands = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(resources);

        var resourceCopy = new ApplicationModelResourceDocument[resources.Count];
        for (int index = 0; index < resourceCopy.Length; index++)
        {
            resourceCopy[index] = resources[index];
        }

        Schema = schema;
        Application = application;
        Environment = environment;
        Gateway = gateway;
        Owner = owner;
        Mode = mode;
        Adopt = adopt;
        RestartOrphans = restartOrphans;
        Resources = new ReadOnlyCollection<ApplicationModelResourceDocument>(resourceCopy);
        var commandCopy = new ApplicationModelCommandDocument[commands?.Count ?? 0];
        for (int index = 0; index < commandCopy.Length; index++)
        {
            ApplicationModelCommandDocument command = commands![index];
            if (command is null)
            {
                throw new InvalidDataException("Application-model commands must not contain null entries.");
            }
            commandCopy[index] = command with { Payload = command.Payload.ToArray() };
        }
        Commands = Array.AsReadOnly(commandCopy);
    }

    /// <summary>Gets the application-model schema.</summary>
    public string Schema { get; }

    /// <summary>Gets the application name.</summary>
    public string Application { get; }

    /// <summary>Gets the target environment.</summary>
    public string Environment { get; }

    /// <summary>Gets the selected gateway identity.</summary>
    public string Gateway { get; }

    /// <summary>Gets the application owner identity.</summary>
    public string Owner { get; }

    /// <summary>Gets the requested gateway run mode.</summary>
    public string Mode { get; }

    /// <summary>Gets a value indicating whether foreign-owned resources may be adopted.</summary>
    public bool Adopt { get; }

    /// <summary>Gets a value indicating whether orphaned local resources may be restarted.</summary>
    public bool RestartOrphans { get; }

    /// <summary>Gets the model resources in declaration order.</summary>
    public IReadOnlyList<ApplicationModelResourceDocument> Resources { get; }

    /// <summary>Gets portable desired commands; command payloads must contain no secrets.</summary>
    public IReadOnlyList<ApplicationModelCommandDocument> Commands { get; }

    /// <summary>
    /// Creates a portable document from a built application model.
    /// </summary>
    /// <param name="model">The built model.</param>
    /// <returns>The corresponding application-model document.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="model"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The model's descriptor, manifest, and plan collections do not align.
    /// </exception>
    public static ApplicationModelDocument Create(IApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Descriptors.Count != model.Manifests.Count ||
            model.Descriptors.Count != model.Plans.Count)
        {
            throw new InvalidOperationException(
                "The application model cannot be described because its descriptor, manifest, and plan counts differ.");
        }

        var resources = new ApplicationModelResourceDocument[model.Descriptors.Count];
        for (int index = 0; index < resources.Length; index++)
        {
            IApplicationResourceDescriptor descriptor = model.Descriptors[index];
            var dependencies = new string[descriptor.Dependencies.Count];
            for (int dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
            {
                dependencies[dependencyIndex] = descriptor.Dependencies[dependencyIndex].Resource.Name.ToString();
            }

            resources[index] = new ApplicationModelResourceDocument(
                descriptor.Resource.Name.ToString(),
                new ReadOnlyCollection<string>(dependencies),
                model.Manifests[index],
                model.Plans[index],
                descriptor.Resource is ExternalResource external
                    ? ExternalResourceDocumentConverter.Create(external)
                    : null);
        }

        var commands = new ApplicationModelCommandDocument[model.Commands.Count];
        for (int index = 0; index < commands.Length; index++)
        {
            IResourceCommand command = model.Commands[index];
            commands[index] = new ApplicationModelCommandDocument(command.Id, command.Kind,
                command.Key, command.Target.Name.ToString(), command.Owner.ToString(), command.Payload, command.Optional);
        }

        return new ApplicationModelDocument(
            CurrentSchema,
            model.Name.ToString(),
            model.Environment.Name.ToString(),
            model.GatewayIdentity.ToString(),
            model.Owner,
            model.RunMode.ToString().ToLowerInvariant(),
            model.Adopt,
            model.RestartOrphans,
            new ReadOnlyCollection<ApplicationModelResourceDocument>(resources), commands);
    }

    /// <summary>
    /// Parses and validates an application-model document from JSON text.
    /// </summary>
    /// <param name="json">The JSON document.</param>
    /// <returns>The parsed application-model document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is malformed or JSON <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The document violates the application-model contract.</exception>
    /// <exception cref="InvalidOperationException">A resource plan violates its manifest contract.</exception>
    public static ApplicationModelDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ApplicationModelDocument? document = JsonSerializer.Deserialize(
            json,
            ApplicationModelDocumentJsonContext.Default.ApplicationModelDocument);

        return ValidateDeserialized(document);
    }

    /// <summary>
    /// Loads and validates an application-model document from a readable stream.
    /// The method does not close the stream.
    /// </summary>
    /// <param name="stream">The readable JSON stream.</param>
    /// <returns>The loaded application-model document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The stream does not contain a valid JSON document.</exception>
    /// <exception cref="InvalidDataException">The document violates the application-model contract.</exception>
    /// <exception cref="InvalidOperationException">A resource plan violates its manifest contract.</exception>
    public static ApplicationModelDocument Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ApplicationModelDocument? document = JsonSerializer.Deserialize(
            stream,
            ApplicationModelDocumentJsonContext.Default.ApplicationModelDocument);

        return ValidateDeserialized(document);
    }

    /// <summary>
    /// Loads and validates an application-model document from a file.
    /// </summary>
    /// <param name="path">The document path.</param>
    /// <returns>The loaded application-model document.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The file does not contain a valid JSON document.</exception>
    /// <exception cref="InvalidDataException">The document violates the application-model contract.</exception>
    /// <exception cref="InvalidOperationException">A resource plan violates its manifest contract.</exception>
    public static ApplicationModelDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>
    /// Saves this application-model document to a writable stream.
    /// The method does not close the stream.
    /// </summary>
    /// <param name="stream">The writable JSON stream.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">This document violates the application-model contract.</exception>
    /// <exception cref="InvalidOperationException">A resource plan violates its manifest contract.</exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ToModel();

        JsonSerializer.Serialize(
            stream,
            this,
            ApplicationModelDocumentJsonContext.Default.ApplicationModelDocument);
    }

    /// <summary>
    /// Saves this application-model document to a file, replacing an existing file.
    /// </summary>
    /// <param name="path">The document path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">This document violates the application-model contract.</exception>
    /// <exception cref="InvalidOperationException">A resource plan violates its manifest contract.</exception>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.Create(path);
        Save(stream);
    }

    /// <summary>
    /// Reconstructs the immutable application model represented by this document.
    /// </summary>
    /// <param name="runMode">An optional run-mode override for the importing gateway.</param>
    /// <param name="gatewayIdentity">An optional identity override for the importing gateway.</param>
    /// <returns>The reconstructed application model.</returns>
    /// <exception cref="InvalidDataException">The document violates the application-model contract.</exception>
    /// <exception cref="InvalidOperationException">A resource plan violates its manifest contract.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="runMode"/> is not a supported gateway run mode.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="gatewayIdentity"/> is empty.
    /// </exception>
    public IApplicationModel ToModel(
        GatewayRunMode? runMode = null,
        ResourceName? gatewayIdentity = null)
    {
        Validate();

        var descriptors = new BuiltApplicationResourceDescriptor[Resources.Count];
        var descriptorByName = new Dictionary<string, BuiltApplicationResourceDescriptor>(
            Resources.Count,
            StringComparer.Ordinal);
        var manifests = new ResourceManifest[Resources.Count];
        var plans = new ResourcePlan[Resources.Count];

        for (int index = 0; index < Resources.Count; index++)
        {
            ApplicationModelResourceDocument resourceDocument = Resources[index];
            ResourceManifest manifest = resourceDocument.Manifest.Validate();
            ResourcePlan plan = resourceDocument.Plan;

            if (!string.Equals(resourceDocument.Name, manifest.Name.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Application-model resource '{resourceDocument.Name}' does not match manifest resource '{manifest.Name}'.");
            }

            ResourcePlanValidator.Validate(plan, manifest);

            IApplicationResource resource = CreateResource(resourceDocument, manifest, plan);
            var descriptor = new BuiltApplicationResourceDescriptor(resource, plan);
            if (!descriptorByName.TryAdd(resourceDocument.Name, descriptor))
            {
                throw new InvalidDataException(
                    $"Application-model document contains duplicate resource name '{resourceDocument.Name}'.");
            }

            descriptors[index] = descriptor;
            manifests[index] = manifest;
            plans[index] = plan;
        }

        for (int index = 0; index < Resources.Count; index++)
        {
            ApplicationModelResourceDocument resourceDocument = Resources[index];
            var dependencies = new IApplicationResourceDescriptor[resourceDocument.Dependencies.Count];
            var seenDependencies = new HashSet<string>(StringComparer.Ordinal);

            for (int dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
            {
                string dependencyName = resourceDocument.Dependencies[dependencyIndex];
                if (string.IsNullOrWhiteSpace(dependencyName))
                {
                    throw new InvalidDataException(
                        $"Application-model resource '{resourceDocument.Name}' contains an empty dependency name.");
                }

                if (!seenDependencies.Add(dependencyName))
                {
                    throw new InvalidDataException(
                        $"Application-model resource '{resourceDocument.Name}' contains duplicate dependency '{dependencyName}'.");
                }

                if (!descriptorByName.TryGetValue(dependencyName, out BuiltApplicationResourceDescriptor? dependency))
                {
                    throw new InvalidDataException(
                        $"Application-model resource '{resourceDocument.Name}' depends on unknown resource '{dependencyName}'.");
                }

                dependencies[dependencyIndex] = dependency;
            }

            descriptors[index].SetDependencies(dependencies);
        }

        ValidateDependencyGraph(descriptors);
        var commands = new IResourceCommand[Commands.Count];
        for (int index = 0; index < commands.Length; index++)
        {
            ApplicationModelCommandDocument command = Commands[index];
            if (!descriptorByName.TryGetValue(command.Target, out BuiltApplicationResourceDescriptor? target))
            {
                throw new InvalidDataException($"Command '{command.Id}' targets unknown resource '{command.Target}'.");
            }

            byte[] canonical = DeclarativeResourceCommand.Canonicalize(command.Payload);
            if (!canonical.AsSpan().SequenceEqual(command.Payload.Span)
                || command.Id != DeclarativeResourceCommand.CreateId(command.Kind, target.Resource, canonical))
            {
                throw new InvalidDataException($"Command '{command.Id}' has a noncanonical payload or mismatched deterministic identity.");
            }

            commands[index] = new DeclarativeResourceCommand(command.Id, command.Kind, command.Key,
                target.Resource, ApplicationName.Parse(command.Owner), canonical, command.Optional);
        }
        ResourceCommandValidator.Validate(commands, ApplicationName.Parse(Application), descriptors, manifests);

        GatewayRunMode documentRunMode = Enum.Parse<GatewayRunMode>(Mode, ignoreCase: true);
        GatewayRunMode effectiveRunMode = runMode ?? documentRunMode;
        if (!Enum.IsDefined(effectiveRunMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(runMode),
                effectiveRunMode,
                "The importing gateway run mode is not supported.");
        }

        ResourceName effectiveGatewayIdentity = gatewayIdentity ?? (ResourceName)Gateway;
        if (string.IsNullOrWhiteSpace(effectiveGatewayIdentity.ToString()))
        {
            throw new ArgumentException(
                "The importing gateway identity must not be empty.",
                nameof(gatewayIdentity));
        }

        return new CohesionApplicationModel(
            ApplicationName.Parse(Application),
            ApplicationEnvironment.FromName(Environment),
            descriptors,
            manifests,
            plans,
            effectiveRunMode,
            effectiveGatewayIdentity,
            Adopt,
            RestartOrphans, commands);
    }

    private static ApplicationModelDocument ValidateDeserialized(ApplicationModelDocument? document)
    {
        if (document is null)
        {
            throw new JsonException("The application-model document must not be JSON null.");
        }

        document.ToModel();
        return document;
    }

    private static IApplicationResource CreateResource(
        ApplicationModelResourceDocument resourceDocument,
        ResourceManifest manifest,
        ResourcePlan plan)
    {
        if (resourceDocument.External is ApplicationModelExternalDocument externalDocument)
        {
            bool hasExternalPlan = plan.Hints.TryGetValue(
                    ExternalResource.PlanHint,
                    out string? externalHint)
                && bool.TryParse(externalHint, out bool parsedExternal)
                && parsedExternal;
            if (hasExternalPlan == externalDocument.IsRealized)
            {
                throw new InvalidDataException(
                    $"External resource '{manifest.Name}' has inconsistent realization metadata and plan hints.");
            }

            return ExternalResourceDocumentConverter.Create(manifest, externalDocument);
        }

        if (!plan.Hints.TryGetValue(ExternalResource.PlanHint, out string? external) ||
            !bool.TryParse(external, out bool isExternal) ||
            !isExternal)
        {
            return new GenericPlannedResource<ResourceOptions>(manifest, new ResourceOptions());
        }

        ApplicationName application = plan.Hints.TryGetValue(
                ExternalResource.ApplicationHint,
                out string? applicationValue)
            ? ApplicationName.Parse(applicationValue)
            : manifest.Application;
        bool optional = plan.Hints.TryGetValue(ExternalResource.OptionalHint, out string? optionalValue) &&
            bool.TryParse(optionalValue, out bool parsedOptional) &&
            parsedOptional;
        IReadOnlyList<string> endpoints = plan.Hints.TryGetValue(
                ExternalResource.EndpointsHint,
                out string? endpointValue) &&
            !string.IsNullOrEmpty(endpointValue)
                ? endpointValue.Split('\u001f', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
        var declaration = new ExternalResourceDeclaration(
            manifest.Name,
            application,
            endpoints,
            optional,
            manifest,
            [manifest]);
        return new ExternalResource(
            declaration,
            ExternalBindingOverrides.FromEnvironment(declaration));
    }

    private ApplicationModelDocument Validate()
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application-model schema '{Schema}' is not supported. Expected '{CurrentSchema}'.");
        }

        if (string.IsNullOrWhiteSpace(Application))
        {
            throw new InvalidDataException("Application-model application must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Environment))
        {
            throw new InvalidDataException("Application-model environment must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Gateway))
        {
            throw new InvalidDataException("Application-model gateway must not be empty.");
        }

        string expectedOwner = $"{Application}@{Gateway}";
        if (!string.Equals(Owner, expectedOwner, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application-model owner '{Owner}' does not match '{expectedOwner}'.");
        }

        if (!Enum.TryParse(Mode, ignoreCase: true, out GatewayRunMode runMode) || !Enum.IsDefined(runMode))
        {
            throw new InvalidDataException($"Application-model mode '{Mode}' is not supported.");
        }

        for (int index = 0; index < Resources.Count; index++)
        {
            if (Resources[index] is null)
            {
                throw new InvalidDataException("Application-model resources must not contain null entries.");
            }
        }

        return this;
    }

    private static void ValidateDependencyGraph(IReadOnlyList<BuiltApplicationResourceDescriptor> descriptors)
    {
        var states = new Dictionary<IApplicationResourceDescriptor, int>(
            descriptors.Count,
            ReferenceEqualityComparer.Instance);

        for (int index = 0; index < descriptors.Count; index++)
        {
            Visit(descriptors[index], states);
        }
    }

    private static void Visit(
        IApplicationResourceDescriptor descriptor,
        IDictionary<IApplicationResourceDescriptor, int> states)
    {
        if (states.TryGetValue(descriptor, out int state))
        {
            if (state == 1)
            {
                throw new InvalidDataException(
                    $"Application-model dependency graph contains a cycle at resource '{descriptor.Resource.Name}'.");
            }

            return;
        }

        states.Add(descriptor, 1);
        for (int index = 0; index < descriptor.Dependencies.Count; index++)
        {
            Visit(descriptor.Dependencies[index], states);
        }

        states[descriptor] = 2;
    }
}
