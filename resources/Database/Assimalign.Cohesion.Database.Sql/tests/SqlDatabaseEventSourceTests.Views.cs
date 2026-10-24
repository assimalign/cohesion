using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDatabaseEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Views: a cold view chain on a small stack advises open and reports CS05004 on read")]
    public async Task BindViews_SmallStackChain_ShouldPreserveOpenAndReadFailures()
    {
        // Arrange: put the root first in catalog order, so open must bind the cold chain.
        await using var engine = SqlDatabaseEngine.Create("view-stack", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var setup = new SqlDepthTestThread(8 * 1024);
        setup.Run(() =>
        {
            session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            session.ExecuteAsync("CREATE VIEW root AS SELECT id FROM t", cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            string source = "t";
            // Optimized frames are much smaller; exceed the explicit stack on every OS.
            for (int index = 512; index > 0; index--)
            {
                session.ExecuteAsync($"CREATE VIEW v{index} AS SELECT id FROM {source}", cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                source = "v" + index;
            }
            session.ExecuteAsync("CREATE OR REPLACE VIEW root AS SELECT id FROM v1", cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }).Failure.ShouldBeNull();
        var planner = new SqlPlanner(database.Catalog, null, database.Definitions.Functions);
        using var thread = new SqlDepthTestThread(256);
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);

        // Act
        SqlViewBindingFault.Clear(planner);
        var open = thread.Run(() => { database.Definitions.BindCatalog(CancellationToken.None); return true; });
        SqlViewBindingFault.Clear(planner);
        var read = thread.Run(() => session.ExecuteAsync("SELECT * FROM root", cancellationToken: CancellationToken.None).GetAwaiter().GetResult());

        // Assert
        var openFailure = open.Failure.ShouldBeOfType<DatabaseException>();
        openFailure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
        openFailure.Message.ShouldEndWith("Open the database on a thread with a larger stack.", Case.Sensitive);
        openFailure.Message.ShouldNotContain("cannot be bound", Case.Sensitive);
        var readFailure = read.Failure.ShouldBeOfType<SqlEvaluationException>();
        readFailure.Code.ShouldBe("CS05004");
        readFailure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
        NewEvent(recorder, 11, "StatementTooComplex", "database", "depth").Payload.ShouldBe(["db", -1]);
    }
}
