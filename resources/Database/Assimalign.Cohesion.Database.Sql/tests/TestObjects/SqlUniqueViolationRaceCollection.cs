using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;


/// <summary>
/// Runs the unique-violation rollback races alone: two writers, a committer and four readers
/// would starve, or be starved by, other tests running beside them on a small CI runner.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlUniqueViolationRaceCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "SQL unique-violation rollback races";
}
