using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Query helpers over an <see cref="IApplicationModel"/>.
/// </summary>
public static class ApplicationModelExtensions
{
    extension(IApplicationModel model)
    {
        /// <summary>
        /// Returns the resource with the given name as <typeparamref name="T"/>, or
        /// <see langword="null"/> if no such resource exists or it is not assignable to
        /// <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The expected resource type.</typeparam>
        /// <param name="name">The resource name to look up.</param>
        /// <returns>The matching resource, or <see langword="null"/>.</returns>
        public T? GetResource<T>(ResourceName name)
            where T : class, IApplicationResource
        {
            foreach (IApplicationResource resource in model.Resources)
            {
                if (resource.Name == name && resource is T typed)
                {
                    return typed;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the model's resource descriptors in dependency order — every descriptor
        /// appears after all of its dependencies.
        /// </summary>
        /// <returns>The descriptors in topological order.</returns>
        /// <exception cref="InvalidOperationException">The dependency graph contains a cycle.</exception>
        public IReadOnlyList<IApplicationResourceDescriptor> TopologicalOrder()
        {
            var ordered = new List<IApplicationResourceDescriptor>(model.Descriptors.Count);
            var state = new Dictionary<IApplicationResourceDescriptor, int>(model.Descriptors.Count);

            foreach (IApplicationResourceDescriptor descriptor in model.Descriptors)
            {
                Visit(descriptor, state, ordered);
            }

            return ordered;
        }
    }

    // Depth-first post-order with cycle detection. 1 == on the current stack, 2 == emitted.
    private static void Visit(
        IApplicationResourceDescriptor node,
        Dictionary<IApplicationResourceDescriptor, int> state,
        List<IApplicationResourceDescriptor> ordered)
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
            Visit(dependency, state, ordered);
        }

        state[node] = 2;
        ordered.Add(node);
    }
}
