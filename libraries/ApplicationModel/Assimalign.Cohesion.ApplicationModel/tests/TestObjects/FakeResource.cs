using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// A minimal <see cref="IApplicationResource"/> used by the abstraction tests.
/// </summary>
internal sealed class FakeResource : IApplicationResource
{
    public FakeResource(string name)
    {
        Name = name;
    }

    public ResourceName Name { get; }
}
