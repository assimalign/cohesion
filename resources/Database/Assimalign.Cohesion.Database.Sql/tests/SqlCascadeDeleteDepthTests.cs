using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// <c>ON DELETE CASCADE</c> at depth (#1164). The cascade walk recursed once per level, so deleting
/// the head of a 20,000-row self-referencing chain overflowed the stack, which .NET cannot catch:
/// one statement ended the process, and over the wire every session of the server with it. The
/// walk now keeps its path on the heap, so the depth of a cascade is bounded by memory, not by the
/// thread it runs on, and its results, order and cycle handling are the recursion's.
/// </summary>
/// <remarks>
/// Every chain here has an index on its referencing column, as any cascade at this scale needs:
/// without one each level of the walk scans the child table. The acceptance depth is 100,000; the
/// tests of what a deep cascade does besides finish run at the 20,000 rows the issue reported,
/// already ten times the depth that ended a test host on a 1 MB stack.
/// </remarks>
public sealed class SqlCascadeDeleteDepthTests
{
    // Five times the depth the issue reported.
    private const int acceptanceDepth = 100_000;

    // The depth the issue reported overflowing the stack.
    private const int reportedDepth = 20_000;

    // A multi-row INSERT checks each self-reference against the rest of its rows, so seeding goes
    // in batches small enough to keep that check cheap.
    private const int seedBatch = 200;

    // The deep statements get a generous bound, so a slow machine fails an assertion, not the run.
    private const int timeoutSeconds = 300;

    /// <summary>
    /// Deleting the head deletes the whole chain as one statement whose affected count is its own
    /// target, as for any cascade, and the session goes on to run more statements.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: deleting the head of a 100,000-row self-referencing chain deletes every row and keeps the session")]
    public async Task Delete_DeepSelfReferencingChain_ShouldCascadeCompletely()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await SeedChainAsync(session, acceptanceDepth);

        // Act
        var result = await session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout());

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await CountAsync(session)).ShouldBe(0L);
        (await session.ExecuteAsync("INSERT INTO chain VALUES (1, NULL), (2, 1)")).AffectedCount.ShouldBe(2);
        (await session.ExecuteAsync("DELETE FROM chain WHERE id = 1")).AffectedCount.ShouldBe(1);
        (await CountAsync(session)).ShouldBe(0L);
    }

    /// <summary>
    /// The depth of a cascade does not depend on the stack of the thread that runs it: the delete
    /// runs on a thread with a 512 KB stack, a quarter of the stack that 2,000 recursive levels
    /// overflowed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: a 20,000-row cascade completes on a thread with a 512 KB stack")]
    public async Task Delete_DeepChainOnSmallStack_ShouldCascadeCompletely()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await SeedChainAsync(session, reportedDepth);

        // Act
        var (result, failure) = RunOnThread(512, () =>
            session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout()).AsTask().GetAwaiter().GetResult());

        // Assert
        failure.ShouldBeNull();
        result.ShouldNotBeNull().AffectedCount.ShouldBe(1);
        (await CountAsync(session)).ShouldBe(0L);
    }

    /// <summary>
    /// Inside an explicit transaction the deep cascade is visible to the transaction and rolls
    /// back completely, after which the same statement deletes the chain again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: a 20,000-row cascade inside BEGIN rolls back completely")]
    public async Task Delete_DeepCascadeInTransaction_ShouldRollBackCompletely()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await SeedChainAsync(session, reportedDepth);

        // Act
        await session.ExecuteAsync("BEGIN");
        var deleted = await session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout());
        long countInTransaction = await CountAsync(session);
        await session.ExecuteAsync("ROLLBACK");
        long countAfterRollback = await CountAsync(session);
        var again = await session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout());

        // Assert
        deleted.AffectedCount.ShouldBe(1);
        countInTransaction.ShouldBe(0L);
        countAfterRollback.ShouldBe(reportedDepth);
        again.AffectedCount.ShouldBe(1);
        (await CountAsync(session)).ShouldBe(0L);
    }

    /// <summary>
    /// A ring is a cycle as deep as the chain: the walk comes back to the row it started from,
    /// which is already in the deletion set, and stops there having deleted each row once. Every
    /// target after the first is a row the first one's cascade already reached, and still counts.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: a 20,000-row reference cycle deletes each row once")]
    public async Task Delete_DeepReferenceCycle_ShouldDeleteEachRowOnce()
    {
        // Arrange: the head references the tail, closing the chain into a ring.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await SeedChainAsync(session, reportedDepth);
        await session.ExecuteAsync($"UPDATE chain SET parent_id = {reportedDepth} WHERE id = 1");

        // Act
        var result = await session.ExecuteAsync("DELETE FROM chain WHERE id <= 3", cancellationToken: Timeout());

        // Assert
        result.AffectedCount.ShouldBe(3);
        (await CountAsync(session)).ShouldBe(0L);
    }

    /// <summary>
    /// A RESTRICT reference at the far end of a deep cascade still rejects the whole statement,
    /// with nothing deleted; once the restricting row is gone the same statement succeeds.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: a RESTRICT reference 20,000 levels down rejects the whole delete")]
    public async Task Delete_DeepCascadeEndingInRestrict_ShouldRejectWholeStatement()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await SeedChainAsync(session, reportedDepth);
        await session.ExecuteAsync("CREATE TABLE pin (id INT PRIMARY KEY, node INT, CONSTRAINT fk_pin FOREIGN KEY(node) REFERENCES chain(id) ON DELETE RESTRICT)");
        await session.ExecuteAsync($"INSERT INTO pin VALUES (1, {reportedDepth})");

        // Act
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout()).AsTask());
        long countAfterFailure = await CountAsync(session);
        await session.ExecuteAsync("DELETE FROM pin");
        var result = await session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout());

        // Assert
        failure.ConstraintName.ShouldBe("fk_pin");
        failure.Table.ShouldBe("dbo.pin");
        failure.OffendingValue.ShouldBe(reportedDepth);
        countAfterFailure.ShouldBe(reportedDepth);
        result.AffectedCount.ShouldBe(1);
        (await CountAsync(session)).ShouldBe(0L);
    }

    /// <summary>
    /// A self-referencing RESTRICT chain is unchanged: deleting its head is refused by the row
    /// below it, and deleting every row at once is accepted because the whole chain is in the
    /// deletion set.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: a self-referencing RESTRICT chain keeps its results")]
    public async Task Delete_RestrictChain_ShouldKeepItsResults()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await SeedChainAsync(session, 1_000, action: "RESTRICT");

        // Act
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            session.ExecuteAsync("DELETE FROM chain WHERE id = 1").AsTask());
        long countAfterFailure = await CountAsync(session);
        var result = await session.ExecuteAsync("DELETE FROM chain");

        // Assert
        failure.ConstraintName.ShouldBe("fk_chain");
        failure.OffendingValue.ShouldBe(1);
        countAfterFailure.ShouldBe(1_000L);
        result.AffectedCount.ShouldBe(1_000);
        (await CountAsync(session)).ShouldBe(0L);
    }

    /// <summary>
    /// The walk visits rows depth-first, in reference-declaration order and then in the order the
    /// child lookup returns them, exactly as the recursion did; the observable consequence is which
    /// RESTRICT violation a statement reports when its closure holds more than one. Here the row
    /// reached through <c>a</c> and then <c>aa</c> is discovered before the row reached through
    /// <c>b</c>, so its violation is the one reported; a breadth-first walk would report
    /// <c>fk_rb</c>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: the walk discovers rows depth-first, so the first RESTRICT violation reported is the recursion's")]
    public async Task Delete_TwoRestrictViolations_ShouldReportTheDepthFirstOne()
    {
        // Arrange: p -> a -> aa -> ra (RESTRICT), and p -> b -> rb (RESTRICT).
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE a (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_a FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE b (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_b FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE aa (id INT PRIMARY KEY, aid INT, CONSTRAINT fk_aa FOREIGN KEY(aid) REFERENCES a(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE ra (id INT PRIMARY KEY, aaid INT, CONSTRAINT fk_ra FOREIGN KEY(aaid) REFERENCES aa(id) ON DELETE RESTRICT)");
        await session.ExecuteAsync("CREATE TABLE rb (id INT PRIMARY KEY, bid INT, CONSTRAINT fk_rb FOREIGN KEY(bid) REFERENCES b(id) ON DELETE RESTRICT)");
        await session.ExecuteAsync("INSERT INTO p VALUES (1)");
        await session.ExecuteAsync("INSERT INTO a VALUES (10, 1)");
        await session.ExecuteAsync("INSERT INTO b VALUES (20, 1)");
        await session.ExecuteAsync("INSERT INTO aa VALUES (11, 10)");
        await session.ExecuteAsync("INSERT INTO ra VALUES (12, 11)");
        await session.ExecuteAsync("INSERT INTO rb VALUES (21, 20)");

        // Act
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(() => session.ExecuteAsync("DELETE FROM p").AsTask());

        // Assert
        failure.ConstraintName.ShouldBe("fk_ra");
        failure.OffendingValue.ShouldBe(11);
        foreach (string table in new[] { "p", "a", "b", "aa", "ra", "rb" })
        {
            (await ScalarAsync(session, $"SELECT COUNT(*) FROM {table}")).ShouldBe(1L, table);
        }
    }

    /// <summary>
    /// A row reached along two cascade paths, from one target or from two, is deleted once, and
    /// rows outside the closure are untouched.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: a row reached along two paths deletes once")]
    public async Task Delete_SharedDescendants_ShouldDeleteEachRowOnce()
    {
        // Arrange: p -> a -> d and p -> b -> d, and q -> a.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY, kind TEXT)");
        await session.ExecuteAsync("CREATE TABLE a (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_a FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE b (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_b FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE d (id INT PRIMARY KEY, aid INT, bid INT, " +
            "CONSTRAINT fk_da FOREIGN KEY(aid) REFERENCES a(id) ON DELETE CASCADE, CONSTRAINT fk_db FOREIGN KEY(bid) REFERENCES b(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("INSERT INTO p VALUES (1, 'x'), (2, 'x'), (3, 'y')");
        await session.ExecuteAsync("INSERT INTO a VALUES (10, 1), (11, 1), (12, 3)");
        await session.ExecuteAsync("INSERT INTO b VALUES (20, 1), (21, 2)");
        await session.ExecuteAsync("INSERT INTO d VALUES (30, 10, 20), (31, 11, 21), (32, 12, NULL)");

        // Act
        var result = await session.ExecuteAsync("DELETE FROM p WHERE kind = 'x'");

        // Assert
        result.AffectedCount.ShouldBe(2);
        (await ScalarAsync(session, "SELECT id FROM p")).ShouldBe(3);
        (await ScalarAsync(session, "SELECT id FROM a")).ShouldBe(12);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM b")).ShouldBe(0L);
        (await ScalarAsync(session, "SELECT id FROM d")).ShouldBe(32);
    }

    /// <summary>
    /// A row leaves the walk's path once it has handed out the children of its last cascading
    /// reference, which is not always its last reference: here a cascading <c>tag</c> reference and a
    /// restricting <c>pin</c> reference follow the chain's self-reference, and the <c>tag</c> key is
    /// null in nine rows of ten. Every row still has each of its cascading references walked, the
    /// restricting one still rejects the statement, and rows outside the closure are untouched.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: references after a chain's self-reference are walked or checked for every row")]
    public async Task Delete_ChainWithTrailingReferences_ShouldWalkEveryCascadingReference()
    {
        // Arrange: chain rows 1..2,000 form one chain and rows 2,001..2,010 a separate one. A row's
        // (code_a, code_b) key is (id, 0) for every tenth row of the long chain and every row of the
        // short one, and (id, NULL) otherwise; each non-null key has one tag.
        const int length = 2_000;
        const int separate = 10;
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE chain (id INT PRIMARY KEY, parent_id INT, code_a INT, code_b INT, " +
            "CONSTRAINT fk_chain FOREIGN KEY(parent_id) REFERENCES chain(id) ON DELETE CASCADE, CONSTRAINT uq_code UNIQUE(code_a, code_b))");
        await session.ExecuteAsync("CREATE INDEX chain_parent ON chain(parent_id)");
        await session.ExecuteAsync("CREATE TABLE tag (id INT PRIMARY KEY, code_a INT, code_b INT, " +
            "CONSTRAINT fk_tag FOREIGN KEY(code_a, code_b) REFERENCES chain(code_a, code_b) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE pin (id INT PRIMARY KEY, node INT, CONSTRAINT fk_pin FOREIGN KEY(node) REFERENCES chain(id) ON DELETE RESTRICT)");

        var chainRows = new StringBuilder();
        var tagRows = new StringBuilder();
        for (int first = 1; first <= length + separate; first += seedBatch)
        {
            chainRows.Clear().Append("INSERT INTO chain VALUES ");
            tagRows.Clear();
            int last = Math.Min(length + separate, first + seedBatch - 1);
            for (int id = first; id <= last; id++)
            {
                bool tagged = id > length || id % 10 == 0;
                string parent = id == 1 || id == length + 1 ? "NULL" : (id - 1).ToString(CultureInfo.InvariantCulture);
                chainRows.Append(id == first ? "" : ", ").Append(CultureInfo.InvariantCulture, $"({id}, {parent}, {id}, {(tagged ? "0" : "NULL")})");
                if (tagged)
                {
                    tagRows.Append(tagRows.Length == 0 ? "INSERT INTO tag VALUES " : ", ").Append(CultureInfo.InvariantCulture, $"({id}, {id}, 0)");
                }
            }

            await session.ExecuteAsync(chainRows.ToString(), cancellationToken: Timeout());
            await session.ExecuteAsync(tagRows.ToString(), cancellationToken: Timeout());
        }

        await session.ExecuteAsync($"INSERT INTO pin VALUES (1, {length - 1})");

        // Act
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout()).AsTask());
        long chainAfterFailure = await CountAsync(session);
        object? tagsAfterFailure = await ScalarAsync(session, "SELECT COUNT(*) FROM tag");
        await session.ExecuteAsync("DELETE FROM pin");
        var result = await session.ExecuteAsync("DELETE FROM chain WHERE id = 1", cancellationToken: Timeout());

        // Assert
        failure.ConstraintName.ShouldBe("fk_pin");
        failure.OffendingValue.ShouldBe(length - 1);
        chainAfterFailure.ShouldBe(length + separate);
        tagsAfterFailure.ShouldBe((long)(length / 10 + separate));
        result.AffectedCount.ShouldBe(1);
        (await CountAsync(session)).ShouldBe(separate);
        (await ScalarAsync(session, "SELECT MIN(id) FROM chain")).ShouldBe(length + 1);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM tag")).ShouldBe((long)separate);
        (await ScalarAsync(session, "SELECT MIN(id) FROM tag")).ShouldBe(length + 1);
    }

    /// <summary>
    /// Over the wire the deep cascade completes as an ordinary statement: the server stays up, the
    /// connection that sent it stays ready for the next statement, and other connections are
    /// unaffected.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Cascade: a 100,000-row cascade over the wire completes and keeps the connection")]
    public async Task Wire_DeepCascade_ShouldCompleteAndKeepTheConnection()
    {
        // Arrange: the chain is seeded in process, into the database the server fronts.
        await using var harness = await ServerTestHarness.StartAsync();
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out SqlDatabase? database).ShouldBeTrue();
        await using (var seeding = await database!.CreateSessionAsync())
        {
            await SeedChainAsync(seeding, acceptanceDepth);
        }

        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("DELETE FROM chain WHERE id = 1").Encode());
        var frame = await client.ReadAsync(timeoutSeconds);

        // Assert
        frame.ShouldNotBeNull();
        frame.Value.Type.ShouldBe(ProtocolMessageType.ResultComplete);
        ProtocolResultCompleteMessage.Decode(frame.Value.Payload.Span).AffectedCount.ShouldBe(1);
        harness.Server.Sessions.Count.ShouldBe(1);
        (await ScalarAsync(client, "SELECT COUNT(*) FROM chain")).ShouldBe(0L);
        (await ScalarAsync(client, "SELECT COUNT(*) FROM users")).ShouldBe(2L);

        await using var other = await harness.DialAsync();
        await other.HandshakeAsync();
        (await ScalarAsync(other, "SELECT COUNT(*) FROM chain")).ShouldBe(0L);
        harness.Server.Sessions.Count.ShouldBe(2);
    }

    private static CancellationToken Timeout() => TestTimeout.Token(timeoutSeconds);

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create("cascade-depth", new SqlDatabaseEngineOptions());

    private static (T? Result, Exception? Failure) RunOnThread<T>(int stackKilobytes, Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }, maxStackSize: stackKilobytes * 1024);
        thread.Start();
        thread.Join();
        return (result, failure);
    }

    /// <summary>
    /// Creates <c>chain</c>, in which row <c>n</c> references row <c>n - 1</c> and row 1 references
    /// nothing, with an index on the referencing column.
    /// </summary>
    private static async Task SeedChainAsync(SqlDatabaseSession session, int length, string action = "CASCADE")
    {
        await session.ExecuteAsync("CREATE TABLE chain (id INT PRIMARY KEY, parent_id INT, " +
            $"CONSTRAINT fk_chain FOREIGN KEY(parent_id) REFERENCES chain(id) ON DELETE {action})");
        await session.ExecuteAsync("CREATE INDEX chain_parent ON chain(parent_id)");

        var sql = new StringBuilder();
        for (int first = 1; first <= length; first += seedBatch)
        {
            sql.Clear().Append("INSERT INTO chain VALUES ");
            int last = Math.Min(length, first + seedBatch - 1);
            for (int id = first; id <= last; id++)
            {
                string parent = id == 1 ? "NULL" : (id - 1).ToString(CultureInfo.InvariantCulture);
                sql.Append(id == first ? "" : ", ").Append(CultureInfo.InvariantCulture, $"({id}, {parent})");
            }

            await session.ExecuteAsync(sql.ToString(), cancellationToken: Timeout());
        }

        (await CountAsync(session)).ShouldBe(length);
    }

    private static async Task<long> CountAsync(SqlDatabaseSession session)
        => (long)(await ScalarAsync(session, "SELECT COUNT(*) FROM chain"))!;

    private static async Task<object?> ScalarAsync(SqlDatabaseSession session, string sql)
    {
        await using var rows = (await session.ExecuteAsync(sql, cancellationToken: Timeout())).ShouldBeAssignableTo<QueryResultSet>();
        object? value = null;
        int count = 0;
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        {
            value = row.GetValue(0);
            count++;
        }

        count.ShouldBe(1, sql);
        return value;
    }

    private static async Task<object?> ScalarAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var row = await client.ExpectAsync(ProtocolMessageType.ResultRow);
        object? value = DatabaseValueCodec.DecodeComponent(row.Payload.Span);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        return value;
    }
}
