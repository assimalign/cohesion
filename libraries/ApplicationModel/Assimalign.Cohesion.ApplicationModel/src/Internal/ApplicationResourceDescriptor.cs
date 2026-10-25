using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// The default <see cref="IApplicationResourceDescriptor"/>: a resource plus its
/// dependency edges. Reference identity is used for dependency comparisons, so the
/// descriptor returned by <see cref="IApplicationBuilder.AddResource(IApplicationResource)"/>
/// is the same instance referenced by <c>DependsOn</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ApplicationResourceDescriptor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationResourceDescriptor : IResourceCommandDescriptor
{
    private readonly List<IApplicationResourceDescriptor> _dependencies = new();

    private readonly Func<ApplicationName>? _owner;
    private readonly Action<IResourceCommand>? _register;
    private readonly List<IResourceCommand> _commands = new();

    public ApplicationResourceDescriptor(IApplicationResource resource,
        Func<ApplicationName>? owner = null, Action<IResourceCommand>? register = null,
        ResourceGroupName? resourceGroup = null)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _owner = owner;
        _register = register;
        ResourceGroup = resourceGroup;
    }

    public IApplicationResource Resource { get; }

    public ResourcePlan? Plan => null;

    public ResourceGroupName? ResourceGroup { get; }

    public IReadOnlyList<IResourceCommand> Commands => _commands.AsReadOnly();

    public IResourceCommand AddCommand<TPayload>(string kind, string key, TPayload payload,
        JsonTypeInfo<TPayload> typeInfo, bool optional = false)
    {
        if (_owner is null || _register is null)
        {
            throw new InvalidOperationException("Declare commands on an authoring descriptor before Build().");
        }

        IResourceCommand command = ResourceCommands.Create(kind, key, Resource, _owner(), payload, typeInfo, optional);
        _register(command);
        return command;
    }

    internal void RecordCommand(IResourceCommand command) => _commands.Add(command);

    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _dependencies;

    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (ReferenceEquals(resource.Resource, Resource))
        {
            throw new InvalidOperationException($"Resource '{Resource.Name}' cannot depend on itself.");
        }

        if (!_dependencies.Contains(resource))
        {
            _dependencies.Add(resource);
        }

        return this;
    }

    public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var resource in resources)
        {
            DependsOn(resource);
        }

        return this;
    }

    private sealed class DebugView
    {
        private readonly ApplicationResourceDescriptor _target;

        public DebugView(ApplicationResourceDescriptor value)
        {
            _target = value;
        }

        public IApplicationResource Resource => _target.Resource;
        public ResourcePlan? Plan => _target.Plan;
        public ResourceGroupName? ResourceGroup => _target.ResourceGroup;
        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _target.Dependencies;
    }
}
