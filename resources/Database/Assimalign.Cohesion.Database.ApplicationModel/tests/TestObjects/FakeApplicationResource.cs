using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

/// <summary>
/// A minimal dependent resource (for example an app that consumes the database) used to
/// assert dependency ordering, teardown ordering, and Failed-dependency blocking.
/// </summary>
internal sealed class FakeApplicationResource : IApplicationResource
{
    public FakeApplicationResource(string name) => Name = name;

    public ResourceName Name { get; }
}
