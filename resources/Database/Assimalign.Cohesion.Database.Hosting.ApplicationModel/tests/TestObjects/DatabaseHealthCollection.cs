using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseHealthCollection
{
    public const string Name = "Database application-model health workers";
}
