using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlUpsertConcurrencyCollection
{
    public const string Name = "SQL upsert concurrency";
}
