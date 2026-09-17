using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class BuiltApplicationResourceDescriptor : IResourceCommandDescriptor
{
    private IReadOnlyList<IApplicationResourceDescriptor>? _dependencies;

    public BuiltApplicationResourceDescriptor(IApplicationResource resource, ResourcePlan? plan, IReadOnlyList<IResourceCommand>? commands = null)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        Plan = plan;
        var targetCommands = new List<IResourceCommand>();
        foreach (IResourceCommand command in commands ?? Array.Empty<IResourceCommand>())
        {
            if (ReferenceEquals(command.Target, resource))
            {
                targetCommands.Add(command);
            }
        }
        Commands = targetCommands.AsReadOnly();
    }

    public IApplicationResource Resource { get; }

    public ResourcePlan? Plan { get; }

    public IReadOnlyList<IResourceCommand> Commands { get; }

    public IResourceCommand AddCommand<TPayload>(string kind, string key, TPayload payload,
        JsonTypeInfo<TPayload> typeInfo, bool optional = false) =>
        throw new InvalidOperationException("A built application model is immutable; declare commands before Build().");

    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies =>
        _dependencies ?? Array.Empty<IApplicationResourceDescriptor>();

    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource) =>
        throw new InvalidOperationException(
            "A built application model is immutable; declare dependency edges on the builder descriptor before Build().");

    public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources) =>
        throw new InvalidOperationException(
            "A built application model is immutable; declare dependency edges on the builder descriptor before Build().");

    public void SetDependencies(IReadOnlyList<IApplicationResourceDescriptor> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        if (_dependencies is not null)
        {
            throw new InvalidOperationException("Built descriptor dependencies have already been initialized.");
        }

        var copy = new IApplicationResourceDescriptor[dependencies.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = dependencies[index];
        }

        _dependencies = new ReadOnlyCollection<IApplicationResourceDescriptor>(copy);
    }
}
