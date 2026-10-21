using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;


/// <summary>
/// Runs the record-decode tests alone: the scan race keeps a writer reclaiming slots and pages
/// beside the scans, and beside other tests on a small CI runner that writer can be starved until
/// the race never happens.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlRecordDecodeRaceCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "SQL record-decode races";
}
