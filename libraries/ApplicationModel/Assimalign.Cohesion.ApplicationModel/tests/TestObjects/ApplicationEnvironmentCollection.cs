using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApplicationEnvironmentCollection
{
    public const string Name = "Application environment process variables";
}
