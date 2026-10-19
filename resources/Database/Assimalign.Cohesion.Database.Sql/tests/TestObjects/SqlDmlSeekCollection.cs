using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Serializes DML seek timing and lock-race probes.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlDmlSeekCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "SQL DML seeks";
}
