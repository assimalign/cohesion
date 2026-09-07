using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class ApplicationModelDocument
{
    private const string CurrentSchema = "cohesion/model/v1";

    private ApplicationModelDocument(
        string application,
        string environment,
        string gateway,
        string owner,
        string mode,
        bool adopt,
        IReadOnlyList<ApplicationModelResourceDocument> resources)
    {
        Schema = CurrentSchema;
        Application = application;
        Environment = environment;
        Gateway = gateway;
        Owner = owner;
        Mode = mode;
        Adopt = adopt;
        Resources = resources;
    }

    public string Schema { get; }

    public string Application { get; }

    public string Environment { get; }

    public string Gateway { get; }

    public string Owner { get; }

    public string Mode { get; }

    public bool Adopt { get; }

    public IReadOnlyList<ApplicationModelResourceDocument> Resources { get; }

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
                model.Plans[index]);
        }

        return new ApplicationModelDocument(
            model.Name.ToString(),
            model.Environment.Name.ToString(),
            model.GatewayIdentity.ToString(),
            model.Owner,
            model.RunMode.ToString().ToLowerInvariant(),
            model.Adopt,
            new ReadOnlyCollection<ApplicationModelResourceDocument>(resources));
    }
}

internal sealed class ApplicationModelResourceDocument
{
    public ApplicationModelResourceDocument(
        string name,
        IReadOnlyList<string> dependencies,
        ResourceManifest manifest,
        ResourcePlan plan)
    {
        Name = name;
        Dependencies = dependencies;
        Manifest = manifest;
        Plan = plan;
    }

    public string Name { get; }

    public IReadOnlyList<string> Dependencies { get; }

    public ResourceManifest Manifest { get; }

    public ResourcePlan Plan { get; }
}

internal static class ApplicationModelDocumentWriter
{
    public static Task WriteAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationModelDocument document = ApplicationModelDocument.Create(model);
        string json = JsonSerializer.Serialize(
            document,
            ApplicationModelDocumentJsonContext.Default.ApplicationModelDocument);

        return Console.Out.WriteLineAsync(json);
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ApplicationModelDocument))]
[JsonSerializable(typeof(ResourceManifest))]
[JsonSerializable(typeof(ResourcePlan))]
internal sealed partial class ApplicationModelDocumentJsonContext : JsonSerializerContext;
