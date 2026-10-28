using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceFifthPassTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Correlated join documentation: all candidate predicates run before limited projection")]
    public async Task Documentation_CorrelatedJoinFiltering_ShouldDescribeCandidateEvaluation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("fifth-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "INSERT INTO t VALUES (1),(2)");

        // Act: the scalar correlation selects the correlated join materializer.
        var rows = await Rows(session, "SELECT nextval('s'), (SELECT x.k FROM t x WHERE x.k=a.k) FROM t a CROSS JOIN t b WHERE nextval('s')>0 LIMIT 1");
        string design = File.ReadAllText(Path.Combine(DatabaseDirectory(), "Assimalign.Cohesion.Database.Sql", "docs", "DESIGN.md"));

        // Assert: four candidate predicates followed by one reached projection, then the next call.
        rows.ShouldHaveSingleItem()[0].ShouldBe(5L);
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(6L);
        design.ShouldContain("Correlated joins filter every candidate pair before projection", Case.Sensitive);
        design.ShouldNotContain("Predicate and projection effects therefore interleave per row", Case.Sensitive);
        _output.WriteLine("candidate pairs=4; WHERE calls=4; LIMIT=1; projection=5; next=6");
    }

    private static string DatabaseDirectory([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

}
