using System;
using System.Collections.Generic;
using System.Reflection;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationBuilder"/>. Maintains the working resource collection
/// and descriptor graph, the selected gateway, and validates the graph at <see cref="Build"/>.
/// </summary>
internal sealed class ApplicationBuilder : IApplicationBuilder
{
    private readonly string[] _args;
    private readonly ApplicationResourceCollection _resources = new();
    private readonly List<ApplicationResourceDescriptor> _descriptors = new();
    private IApplicationGateway? _gateway;

    public ApplicationBuilder()
        : this(Array.Empty<string>())
    {
    }

    public ApplicationBuilder(string[] args)
    {
        _args = args ?? Array.Empty<string>();
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
        if (_gateway is null)
        {
            throw new InvalidOperationException(
                "No IApplicationGateway selected. Call UseGateway(...) or UseLocalGateway() before Build().");
        }

        IApplicationModel model = BuildModel(validate: true);
        return new CohesionApplication(model, _gateway);
    }

    private CohesionApplicationModel BuildModel(bool validate)
    {
        ApplicationResourceDescriptor[] descriptors = _descriptors.ToArray();

        if (validate)
        {
            ValidateGraph(descriptors);
        }

        return new CohesionApplicationModel(ResolveName(), ApplicationEnvironment.FromHost(), descriptors);
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

    private static ApplicationName ResolveName()
    {
        string? name = Assembly.GetEntryAssembly()?.GetName().Name;
        return string.IsNullOrWhiteSpace(name) ? "application" : name;
    }
}
