using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseEngineInventoryCollection
{
    public const string Name = "Database open inventory";
}
