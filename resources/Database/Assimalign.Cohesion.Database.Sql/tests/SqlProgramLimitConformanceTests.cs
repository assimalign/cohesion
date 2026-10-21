using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Live conformance for the SQLSTATE 54000 size limits (#1266).</summary>
public sealed class SqlProgramLimitConformanceTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string name in new[] { "Insert", "InsertSelect", "Update", "Upsert", "SetDefault",
            "KeyInsertUnique", "KeyInsertNonUnique", "KeyUpdate", "KeyUpsert" })
        { yield return [name]; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: size limits preserve rows and an explicit transaction (#1266)")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_OversizedRecord_ShouldRejectBeforeApplyAndKeepTransaction(string name)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("program-limits", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        foreach (string sql in Setup(name)) { await session.ExecuteAsync(sql, cancellationToken: timeout.Token); }
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO marker VALUES (9)", cancellationToken: timeout.Token);
        var before = await ReadAsync(session, name, timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<SqlEvaluationException>(async () =>
            await session.ExecuteAsync(Statement(name), Parameters(name), timeout.Token));

        // Assert
        CheckFailure(failure.Message, failure.Diagnostics, name);
        transaction.State.ShouldBe(TransactionState.Active);
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        (await ReadAsync(session, name, timeout.Token)).ShouldBe(before);
        await transaction.CommitAsync(timeout.Token);
        await using var marker = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM marker", cancellationToken: timeout.Token);
        (await SqlT1ScalarTests.ReadAsync(marker)).ShouldHaveSingleItem()[0].ShouldBe(9);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: size limits cross the wire and keep the session and transaction usable (#1266)")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_OversizedRecordOverWire_ShouldKeepConnectionAndTransaction(string name)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "program-limits", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        foreach (string sql in Setup(name)) { await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()); }
        await connection.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync("INSERT INTO marker VALUES (9)", cancellationToken: TestTimeout.Token());
        var before = await connection.QueryAsync(Query(name), cancellationToken: TestTimeout.Token());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(Statement(name), Parameters(name), TestTimeout.Token()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldStartWith("COHSQLE042: ");
        CheckFailure(failure.Diagnostics.ShouldHaveSingleItem().Message!, failure.Diagnostics, name);
        var after = await connection.QueryAsync(Query(name), cancellationToken: TestTimeout.Token());
        after.Count.ShouldBe(before.Count);
        for (int i = 0; i < before.Count; i++) { after[i][0].ShouldBe(before[i][0]); after[i][1].ShouldBe(before[i][1]); }
        if (name == "SetDefault")
        { (await connection.QueryAsync("SELECT id FROM p", cancellationToken: TestTimeout.Token())).Count.ShouldBe(2); }
        await connection.ExecuteAsync("COMMIT", cancellationToken: TestTimeout.Token());
        (await connection.QueryAsync("SELECT id FROM marker", cancellationToken: TestTimeout.Token())).ShouldHaveSingleItem()[0].ShouldBe(9);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Row codec: the exact record limit succeeds and one extra byte has a SQL diagnostic (#1266)")]
    public void Encode_AtRecordLimit_ShouldAcceptLimitAndRejectOneMoreByte()
    {
        // Arrange
        var table = new Catalog.SqlCatalogTable(1, "dbo", "t", [new Catalog.SqlCatalogColumn("v", new DatabaseTypeInfo(DatabaseType.String))]);
        var writer = new TransactionSequence(1);
        int overhead = SqlRowCodec.Encode(table, [""], writer).Length;

        // Act
        var record = SqlRowCodec.Encode(table, [new string('x', SlottedPage.MaxRecordSize - overhead)], writer);
        var failure = Should.Throw<SqlEvaluationException>(() =>
            SqlRowCodec.Encode(table, [new string('x', SlottedPage.MaxRecordSize - overhead + 1)], writer));

        // Assert
        record.Length.ShouldBe(SlottedPage.MaxRecordSize);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE042");
        failure.Message.ShouldContain((SlottedPage.MaxRecordSize + 1).ToString(CultureInfo.CurrentCulture), Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Index keys: the public index accepts exactly the SQL key limit (#1266)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_AtIndexKeyLimit_ShouldAcceptLimitAndRejectOneMoreByte(bool unique)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("key-limit", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v TEXT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync($"CREATE {(unique ? "UNIQUE " : "")}INDEX ix_v ON t(v)", cancellationToken: timeout.Token);
        var key = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(key, DatabaseType.String, "", Collation.Binary);
        int length = 1024 - IndexKey.From(key).Length;

        // Act
        await session.ExecuteAsync("INSERT INTO t VALUES (1, @v)", new Dictionary<string, object?> { ["v"] = new string('x', length) }, timeout.Token);
        var failure = await Should.ThrowAsync<SqlEvaluationException>(async () =>
            await session.ExecuteAsync("INSERT INTO t VALUES (2, @v)", new Dictionary<string, object?> { ["v"] = new string('x', length + 1) }, timeout.Token));

        // Assert: inserting the boundary key exercises Indexing's public insertion path too.
        failure.Code.ShouldBe("COHSQLE042");
        failure.Message.ShouldContain("size 1025 bytes, maximum size 1024 bytes", Case.Sensitive);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM t", cancellationToken: timeout.Token);
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Index build: an oversized stored key leaves the catalog and connection usable (#1266)")]
    public async Task ExecuteAsync_OversizedIndexBuild_ShouldRejectWithoutPublishingIndex()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "index-limit", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        foreach (string sql in Setup("KeyDdl")) { await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()); }

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(Statement("KeyDdl"), cancellationToken: TestTimeout.Token()));

        // Assert
        failure.ConnectionUsable.ShouldBeTrue();
        CheckFailure(failure.Diagnostics.ShouldHaveSingleItem().Message!, failure.Diagnostics, "KeyDdl");
        // A failed build did not attach an index that could refuse this next large value.
        await connection.ExecuteAsync("INSERT INTO t VALUES (3, @value)", Parameters("KeyDdl"), TestTimeout.Token());
        (await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout.Token())).Count.ShouldBe(3);
    }

    private static IEnumerable<string> Setup(string name)
    {
        yield return "CREATE TABLE marker (id INT)";
        if (name == "SetDefault")
        {
            string value = new('x', 900);
            yield return "CREATE TABLE p (id TEXT PRIMARY KEY)";
            yield return $"INSERT INTO p VALUES ('a'), ('{value}')";
            yield return $"CREATE TABLE c (id INT PRIMARY KEY, pid TEXT DEFAULT '{value}' REFERENCES p(id) ON DELETE SET DEFAULT, value TEXT)";
            yield return $"INSERT INTO c VALUES (1, 'a', '{new string('v', 7300)}')";
            yield break;
        }
        yield return "CREATE TABLE t (id INT PRIMARY KEY, value TEXT)";
        yield return "INSERT INTO t VALUES (1, 'old')";
        if (name.StartsWith("Key", StringComparison.Ordinal) && name != "KeyDdl")
        { yield return $"CREATE {(name == "KeyInsertNonUnique" ? "" : "UNIQUE ")}INDEX ix_value ON t(value)"; }
        if (name == "KeyDdl") { yield return $"INSERT INTO t VALUES (2, '{new string('x', 2000)}')"; }
    }

    private static string Statement(string name) => name switch
    {
        "InsertSelect" => "INSERT INTO t SELECT 2, @value",
        "Update" or "KeyUpdate" => "UPDATE t SET value = @value WHERE id = 1",
        "Upsert" or "KeyUpsert" => "INSERT INTO t VALUES (2, 'ok'), (1, 'new') ON CONFLICT(id) DO UPDATE SET value = @value",
        "SetDefault" => "DELETE FROM p WHERE id = 'a'",
        "KeyDdl" => "CREATE INDEX ix_value ON t(value)",
        _ => "INSERT INTO t VALUES (2, 'ok'), (3, @value)",
    };

    private static Dictionary<string, object?> Parameters(string name)
        => new() { ["value"] = new string('x', name.StartsWith("Key", StringComparison.Ordinal) ? 2000 : 9000) };

    private static string Query(string name) => name == "SetDefault" ? "SELECT id, pid FROM c" : "SELECT id, value FROM t ORDER BY id";

    private static async Task<string> ReadAsync(SqlDatabaseSession session, string name, CancellationToken cancellationToken)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(Query(name), cancellationToken: cancellationToken);
        var rows = await SqlT1ScalarTests.ReadAsync(result);
        if (name == "SetDefault")
        {
            await using var parents = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM p ORDER BY id", cancellationToken: cancellationToken);
            (await SqlT1ScalarTests.ReadAsync(parents)).Count.ShouldBe(2);
        }
        return string.Join("|", rows.Select(row => string.Join(",", row)));
    }

    private static void CheckFailure(string message, IReadOnlyList<Diagnostic> diagnostics, string name)
    {
        diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE042");
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);
        message.ShouldBe(diagnostics[0].Message);
        message.ShouldContain(name == "SetDefault" ? "dbo.c" : "dbo.t", Case.Sensitive);
        int limit = name.StartsWith("Key", StringComparison.Ordinal) ? 1024 : SlottedPage.MaxRecordSize;
        message.ShouldContain($"maximum size {limit} bytes", Case.Sensitive);
        // Nonzero ASCII adds exactly one encoded byte per character.
        int size;
        if (limit == 1024)
        {
            var key = new DatabaseKeyWriter();
            SqlRowCodec.AppendKeyValue(key, DatabaseType.String, new string('x', 2000), Collation.Binary);
            size = IndexKey.From(key).Length;
        }
        else
        {
            var table = new Catalog.SqlCatalogTable(1, "dbo", "t", name == "SetDefault"
                ? [new("id", new(DatabaseType.Int32)), new("pid", new(DatabaseType.String)), new("value", new(DatabaseType.String))]
                : [new("id", new(DatabaseType.Int32)), new("value", new(DatabaseType.String))]);
            size = SqlRowCodec.Encode(table, name == "SetDefault" ? [1, "", new string('v', 7300)] : [1, ""], new(1)).Length
                + (name == "SetDefault" ? 900 : 9000);
        }
        message.ShouldContain($"size {size} bytes", Case.Sensitive);
    }
}
