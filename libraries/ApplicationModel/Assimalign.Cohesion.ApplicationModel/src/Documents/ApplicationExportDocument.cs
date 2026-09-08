using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The public discovery document exported by a Cohesion application gateway.
/// </summary>
public sealed class ApplicationExportDocument
{
    /// <summary>The monotonic application-export schema version supported by this contract.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Initializes an application export.
    /// </summary>
    /// <param name="schemaVersion">The application-export schema version.</param>
    /// <param name="application">The application name.</param>
    /// <param name="environment">The target environment.</param>
    /// <param name="version">The exported application version.</param>
    /// <param name="trustKey">The application's public JSON Web Key, when available.</param>
    /// <param name="resources">The exported resources.</param>
    /// <param name="model">The portable application model.</param>
    /// <exception cref="ArgumentNullException">
    /// A required string, <paramref name="resources"/>, or <paramref name="model"/> is
    /// <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ApplicationExportDocument(
        int schemaVersion,
        string application,
        string environment,
        string version,
        JsonElement? trustKey,
        IReadOnlyList<ApplicationExportResource> resources,
        ApplicationModelDocument model)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(model);

        var resourceCopy = new ApplicationExportResource[resources.Count];
        for (int index = 0; index < resourceCopy.Length; index++)
        {
            resourceCopy[index] = resources[index];
        }

        SchemaVersion = schemaVersion;
        Application = application;
        Environment = environment;
        Version = version;
        TrustKey = trustKey?.Clone();
        Resources = new ReadOnlyCollection<ApplicationExportResource>(resourceCopy);
        Model = model;
    }

    /// <summary>Gets the monotonic application-export schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the application name.</summary>
    public string Application { get; }

    /// <summary>Gets the target environment.</summary>
    public string Environment { get; }

    /// <summary>Gets the exported application version.</summary>
    public string Version { get; }

    /// <summary>Gets the application's public JSON Web Key, when available.</summary>
    public JsonElement? TrustKey { get; }

    /// <summary>Gets the exported resources.</summary>
    public IReadOnlyList<ApplicationExportResource> Resources { get; }

    /// <summary>Gets the portable application model.</summary>
    public ApplicationModelDocument Model { get; }

    /// <summary>
    /// Creates an export from a built model and its observed endpoint addresses.
    /// </summary>
    /// <param name="model">The built application model.</param>
    /// <param name="version">The application version to publish.</param>
    /// <param name="endpoints">
    /// Observed endpoint addresses keyed by resource name. Omitted resources are exported with no endpoints.
    /// </param>
    /// <param name="trustKey">The application's public JSON Web Key, when available.</param>
    /// <returns>The corresponding application export.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="model"/> or <paramref name="version"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="version"/> is empty, or <paramref name="endpoints"/> contains a
    /// <see langword="null"/> value or an unknown resource.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The model's descriptor, manifest, and plan collections do not align.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A manifest or the generated export violates its contract.
    /// </exception>
    public static ApplicationExportDocument Create(
        IApplicationModel model,
        string version,
        IReadOnlyDictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>? endpoints = null,
        JsonElement? trustKey = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (model.Manifests.Count != model.Descriptors.Count ||
            model.Manifests.Count != model.Plans.Count)
        {
            throw new InvalidOperationException(
                "The application export cannot be created because its descriptor, manifest, and plan counts differ.");
        }

        var resources = new List<ApplicationExportResource>(model.Manifests.Count);
        var exportedNames = new HashSet<ResourceName>();
        for (int index = 0; index < model.Manifests.Count; index++)
        {
            ResourceManifest manifest = model.Manifests[index].Validate();
            ResourcePlan plan = model.Plans[index];
            if (plan.Hints.TryGetValue(ExternalResource.PlanHint, out string? external) &&
                bool.TryParse(external, out bool isExternal) &&
                isExternal)
            {
                continue;
            }

            IReadOnlyList<ApplicationExportEndpoint> observedEndpoints = Array.Empty<ApplicationExportEndpoint>();
            if (endpoints is not null &&
                endpoints.TryGetValue(manifest.Name, out IReadOnlyList<ApplicationExportEndpoint>? suppliedEndpoints))
            {
                observedEndpoints = suppliedEndpoints
                    ?? throw new ArgumentException(
                        $"Observed endpoints for resource '{manifest.Name}' must not be null.",
                        nameof(endpoints));
            }

            resources.Add(new ApplicationExportResource(
                manifest.Name.ToString(),
                manifest.Kind,
                ResourceManifestCanonicalizer.ComputeHash(manifest),
                observedEndpoints));
            exportedNames.Add(manifest.Name);
        }

        if (endpoints is not null)
        {
            foreach (ResourceName resourceName in endpoints.Keys)
            {
                if (!exportedNames.Contains(resourceName))
                {
                    throw new ArgumentException(
                        $"Observed endpoints were supplied for unknown resource '{resourceName}'.",
                        nameof(endpoints));
                }
            }
        }

        return new ApplicationExportDocument(
            CurrentSchemaVersion,
            model.Name.ToString(),
            model.Environment.Name.ToString(),
            version,
            trustKey,
            new ReadOnlyCollection<ApplicationExportResource>(resources),
            ApplicationModelDocument.Create(model)).Validate();
    }

    /// <summary>
    /// Parses and validates an application export from JSON text.
    /// </summary>
    /// <param name="json">The JSON document.</param>
    /// <returns>The parsed application export.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is malformed or JSON <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    public static ApplicationExportDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ApplicationExportDocument? document = JsonSerializer.Deserialize(
            json,
            ApplicationModelDocumentJsonContext.Default.ApplicationExportDocument);

        return ValidateDeserialized(document);
    }

    /// <summary>
    /// Loads and validates an application export from a readable stream.
    /// The method does not close the stream.
    /// </summary>
    /// <param name="stream">The readable JSON stream.</param>
    /// <returns>The loaded application export.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The stream does not contain a valid JSON document.</exception>
    /// <exception cref="InvalidDataException">The export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    public static ApplicationExportDocument Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ApplicationExportDocument? document = JsonSerializer.Deserialize(
            stream,
            ApplicationModelDocumentJsonContext.Default.ApplicationExportDocument);

        return ValidateDeserialized(document);
    }

    /// <summary>
    /// Loads and validates an application export from a file.
    /// </summary>
    /// <param name="path">The export path.</param>
    /// <returns>The loaded application export.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The file does not contain a valid JSON document.</exception>
    /// <exception cref="InvalidDataException">The export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    public static ApplicationExportDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>
    /// Asynchronously loads and validates an application export from a readable stream.
    /// The method does not close the stream.
    /// </summary>
    /// <param name="stream">The readable JSON stream.</param>
    /// <param name="cancellationToken">A token that can cancel the read.</param>
    /// <returns>The loaded application export.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The stream does not contain a valid JSON document.</exception>
    /// <exception cref="InvalidDataException">The export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    public static async Task<ApplicationExportDocument> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ApplicationExportDocument? document = await JsonSerializer.DeserializeAsync(
            stream,
            ApplicationModelDocumentJsonContext.Default.ApplicationExportDocument,
            cancellationToken).ConfigureAwait(false);

        return ValidateDeserialized(document);
    }

    /// <summary>
    /// Asynchronously loads and validates an application export from a file.
    /// </summary>
    /// <param name="path">The export path.</param>
    /// <param name="cancellationToken">A token that can cancel the read.</param>
    /// <returns>The loaded application export.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The file does not contain a valid JSON document.</exception>
    /// <exception cref="InvalidDataException">The export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    public static async Task<ApplicationExportDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using FileStream stream = File.OpenRead(path);
        return await LoadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves this application export to a writable stream. The method does not close the stream.
    /// </summary>
    /// <param name="stream">The writable JSON stream.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">This export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Validate();

        JsonSerializer.Serialize(
            stream,
            this,
            ApplicationModelDocumentJsonContext.Default.ApplicationExportDocument);
    }

    /// <summary>
    /// Saves this application export to a file, replacing an existing file.
    /// </summary>
    /// <param name="path">The export path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">This export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.Create(path);
        Save(stream);
    }

    /// <summary>
    /// Asynchronously saves this application export to a writable stream. The method does
    /// not close the stream.
    /// </summary>
    /// <param name="stream">The writable JSON stream.</param>
    /// <param name="cancellationToken">A token that can cancel the write.</param>
    /// <returns>A task that completes after the export is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">This export violates the application-export contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    public async Task SaveAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Validate();

        await JsonSerializer.SerializeAsync(
            stream,
            this,
            ApplicationModelDocumentJsonContext.Default.ApplicationExportDocument,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconstructs the immutable application model carried by this export.</summary>
    /// <param name="runMode">An optional run-mode override for the importing gateway.</param>
    /// <param name="gatewayIdentity">An optional identity override for the importing gateway.</param>
    /// <returns>The reconstructed application model.</returns>
    /// <exception cref="InvalidDataException">The export or embedded model violates its contract.</exception>
    /// <exception cref="InvalidOperationException">The embedded model contains an invalid resource plan.</exception>
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
        return Model.ToModel(runMode, gatewayIdentity);
    }

    private static ApplicationExportDocument ValidateDeserialized(ApplicationExportDocument? document)
    {
        if (document is null)
        {
            throw new JsonException("The application export must not be JSON null.");
        }

        return document.Validate();
    }

    private ApplicationExportDocument Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Application-export schemaVersion '{SchemaVersion}' is not supported. Expected '{CurrentSchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(Application))
        {
            throw new InvalidDataException("Application-export application must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Environment))
        {
            throw new InvalidDataException("Application-export environment must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidDataException("Application-export version must not be empty.");
        }

        if (TrustKey is JsonElement trustKey && trustKey.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Application-export trustKey must be a JSON object when present.");
        }

        if (TrustKey is JsonElement publicTrustKey &&
            publicTrustKey.TryGetProperty("d", out _))
        {
            throw new InvalidDataException(
                "Application-export trustKey must contain public key material only.");
        }

        if (!string.Equals(Application, Model.Application, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application-export application '{Application}' does not match model application '{Model.Application}'.");
        }

        if (!string.Equals(Environment, Model.Environment, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application-export environment '{Environment}' does not match model environment '{Model.Environment}'.");
        }

        var modelResources = new Dictionary<string, ApplicationModelResourceDocument>(
            Model.Resources.Count,
            StringComparer.Ordinal);
        var locallyRealizedNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Model.Resources.Count; index++)
        {
            ApplicationModelResourceDocument modelResource = Model.Resources[index]
                ?? throw new InvalidDataException(
                    "Application-export model resources must not contain null entries.");
            if (!modelResources.TryAdd(modelResource.Name, modelResource))
            {
                throw new InvalidDataException(
                    $"Application-export model contains duplicate resource '{modelResource.Name}'.");
            }

            if (!IsExternal(modelResource.Plan))
            {
                locallyRealizedNames.Add(modelResource.Name);
            }
        }

        var exportNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Resources.Count; index++)
        {
            ApplicationExportResource resource = Resources[index]
                ?? throw new InvalidDataException("Application-export resources must not contain null entries.");
            resource.Validate();

            if (!exportNames.Add(resource.Name))
            {
                throw new InvalidDataException(
                    $"Application export contains duplicate resource '{resource.Name}'.");
            }

            if (!modelResources.TryGetValue(resource.Name, out ApplicationModelResourceDocument? modelResource))
            {
                throw new InvalidDataException(
                    $"Application-export resource '{resource.Name}' does not exist in the model payload.");
            }

            if (IsExternal(modelResource.Plan))
            {
                throw new InvalidDataException(
                    $"Application-export resource '{resource.Name}' is external and must not be published as locally realized.");
            }

            if (!string.Equals(resource.Kind, modelResource.Manifest.Kind, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Application-export resource '{resource.Name}' kind '{resource.Kind}' does not match model kind '{modelResource.Manifest.Kind}'.");
            }

            var declaredEndpoints = new HashSet<string>(StringComparer.Ordinal);
            for (int endpointIndex = 0;
                 endpointIndex < modelResource.Manifest.Endpoints.Count;
                 endpointIndex++)
            {
                declaredEndpoints.Add(modelResource.Manifest.Endpoints[endpointIndex].Name);
            }

            for (int endpointIndex = 0; endpointIndex < resource.Endpoints.Count; endpointIndex++)
            {
                string endpointName = resource.Endpoints[endpointIndex].Name;
                if (!declaredEndpoints.Contains(endpointName))
                {
                    throw new InvalidDataException(
                        $"Application-export resource '{resource.Name}' endpoint '{endpointName}' is not declared by its model manifest.");
                }
            }

            string expectedHash = ResourceManifestCanonicalizer.ComputeHash(modelResource.Manifest);
            if (!string.Equals(resource.ManifestHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Application-export resource '{resource.Name}' manifestHash does not match its model manifest.");
            }
        }

        if (!exportNames.SetEquals(locallyRealizedNames))
        {
            throw new InvalidDataException(
                "Application-export resources must contain every locally realized resource in the model payload exactly once.");
        }

        Model.ToModel();
        return this;
    }

    private static bool IsExternal(ResourcePlan plan) =>
        plan.Hints.TryGetValue(ExternalResource.PlanHint, out string? value) &&
        bool.TryParse(value, out bool isExternal) &&
        isExternal;
}
