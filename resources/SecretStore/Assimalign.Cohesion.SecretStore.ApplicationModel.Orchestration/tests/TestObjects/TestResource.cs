using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// A resource with a name and nothing else — not manifest-backed, so its kind is unknown until
/// the model is built.
/// </summary>
internal sealed class TestResource : IApplicationResource
{
    internal TestResource(string name)
    {
        Name = name;
    }

    public ResourceName Name { get; }
}
