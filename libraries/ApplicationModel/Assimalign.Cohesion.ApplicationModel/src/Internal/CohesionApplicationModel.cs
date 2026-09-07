using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationModel"/>. <see cref="Descriptors"/> is authoritative;
/// <see cref="Resources"/> is a one-to-one projection of it.
/// </summary>
internal sealed class CohesionApplicationModel : IApplicationModel
{
    public CohesionApplicationModel(
        ApplicationName name,
        IApplicationEnvironment environment,
        IReadOnlyList<IApplicationResourceDescriptor> descriptors,
        IReadOnlyList<ResourceManifest> manifests,
        IReadOnlyList<ResourcePlan> plans,
        GatewayRunMode runMode,
        ResourceName gatewayIdentity,
        bool adopt,
        bool restartOrphans)
    {
        Name = name;
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(plans);

        if (manifests.Count != descriptors.Count)
        {
            throw new ArgumentException(
                "The manifest count must match the descriptor count.",
                nameof(manifests));
        }

        if (plans.Count != 0 && plans.Count != descriptors.Count)
        {
            throw new ArgumentException(
                "The plan count must be empty for an authoring snapshot or match the descriptor count.",
                nameof(plans));
        }

        Manifests = Copy(manifests);
        Plans = Copy(plans);
        Descriptors = CopyDescriptors(descriptors, Plans);
        RunMode = runMode;
        GatewayIdentity = gatewayIdentity;
        Adopt = adopt;
        RestartOrphans = restartOrphans;
        Owner = $"{name}@{gatewayIdentity}";

        var resources = new IApplicationResource[Descriptors.Count];
        for (int i = 0; i < Descriptors.Count; i++)
        {
            resources[i] = Descriptors[i].Resource;
        }

        Resources = new ReadOnlyCollection<IApplicationResource>(resources);
    }

    public ApplicationName Name { get; }

    public IApplicationEnvironment Environment { get; }

    public GatewayRunMode RunMode { get; }

    public ResourceName GatewayIdentity { get; }

    public string Owner { get; }

    public bool Adopt { get; }

    public bool RestartOrphans { get; }

    public IReadOnlyList<IApplicationResourceDescriptor> Descriptors { get; }

    public IReadOnlyList<IApplicationResource> Resources { get; }

    public IReadOnlyList<ResourceManifest> Manifests { get; }

    public IReadOnlyList<ResourcePlan> Plans { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<T>(copy);
    }

    private static IReadOnlyList<IApplicationResourceDescriptor> CopyDescriptors(
        IReadOnlyList<IApplicationResourceDescriptor> source,
        IReadOnlyList<ResourcePlan> plans)
    {
        var copies = new Dictionary<IApplicationResourceDescriptor, BuiltApplicationResourceDescriptor>(
            ReferenceEqualityComparer.Instance);
        var descriptorPlans = new Dictionary<IApplicationResourceDescriptor, ResourcePlan?>(
            ReferenceEqualityComparer.Instance);
        var topLevel = new IApplicationResourceDescriptor[source.Count];

        for (int index = 0; index < topLevel.Length; index++)
        {
            descriptorPlans.Add(source[index], plans.Count == 0 ? null : plans[index]);
        }

        for (int index = 0; index < topLevel.Length; index++)
        {
            topLevel[index] = CopyDescriptor(source[index], copies, descriptorPlans);
        }

        return new ReadOnlyCollection<IApplicationResourceDescriptor>(topLevel);
    }

    private static BuiltApplicationResourceDescriptor CopyDescriptor(
        IApplicationResourceDescriptor source,
        IDictionary<IApplicationResourceDescriptor, BuiltApplicationResourceDescriptor> copies,
        IReadOnlyDictionary<IApplicationResourceDescriptor, ResourcePlan?> descriptorPlans)
    {
        if (copies.TryGetValue(source, out BuiltApplicationResourceDescriptor? existing))
        {
            return existing;
        }

        descriptorPlans.TryGetValue(source, out ResourcePlan? plan);
        var copy = new BuiltApplicationResourceDescriptor(source.Resource, plan);
        copies.Add(source, copy);

        var dependencies = new IApplicationResourceDescriptor[source.Dependencies.Count];
        for (int index = 0; index < dependencies.Length; index++)
        {
            dependencies[index] = CopyDescriptor(
                source.Dependencies[index],
                copies,
                descriptorPlans);
        }

        copy.SetDependencies(dependencies);
        return copy;
    }
}
