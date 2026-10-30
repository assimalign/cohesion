using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNextvalReopenFixTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - NEXTVAL defaults: every spelling survives restart (#1770)")]
    [InlineData("'s'")]
    [InlineData("'s'::REGCLASS")]
    [InlineData("'s' : : REGCLASS")]
    [InlineData("CAST('s' AS REGCLASS)")]
    [InlineData(null)]
    public async Task Open_NextvalDefault_ShouldDrawNextValuesAsync(string? operand)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("nextval", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE SEQUENCE s");
            await ExecuteAsync(session, $"CREATE TABLE t(seq_id BIGINT DEFAULT {DefaultExpression(operand)})");
            await ExecuteAsync(session, "INSERT INTO t DEFAULT VALUES");
            (await RowsAsync(session, "SELECT seq_id FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("nextval", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(reader, "INSERT INTO t DEFAULT VALUES");

        // Assert: restart skips the unused tail of the established 32-value reservation.
        (await RowsAsync(reader, "SELECT seq_id FROM t ORDER BY seq_id")).ShouldBe(new object?[][] { [1L], [33L] });
        (await RowsAsync(reader, "SELECT nextval('s')")).ShouldHaveSingleItem()[0].ShouldBe(34L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NEXTVAL defaults: create, insert and restart survive InMemory and TCP (#1770)")]
    [InlineData("'s'", false)]
    [InlineData("'s'::REGCLASS", false)]
    [InlineData("'s' : : REGCLASS", false)]
    [InlineData("CAST('s' AS REGCLASS)", false)]
    [InlineData("'s'", true)]
    [InlineData("'s'::REGCLASS", true)]
    [InlineData("'s' : : REGCLASS", true)]
    [InlineData("CAST('s' AS REGCLASS)", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task Connect_NextvalDefaultRestart_ShouldDrawNextValuesAsync(string? operand, bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("nextval-wire", new() { StorageStrategy = storage }))
        {
            await engine.CreateDatabaseAsync("db", timeout.Token);
            await RoundAsync(engine, tcp, async connection =>
            {
                await connection.ExecuteAsync("CREATE SEQUENCE s", cancellationToken: timeout.Token);
                await connection.ExecuteAsync($"CREATE TABLE t(seq_id BIGINT DEFAULT {DefaultExpression(operand)})", cancellationToken: timeout.Token);
                await connection.ExecuteAsync("INSERT INTO t DEFAULT VALUES", cancellationToken: timeout.Token);
                (await connection.QueryAsync("SELECT seq_id FROM t", cancellationToken: timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
            }, timeout.Token);
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("nextval-wire", new() { StorageStrategy = storage.CaptureDurableImages() });
        await reopened.OpenDatabaseAsync("db", timeout.Token);
        await RoundAsync(reopened, tcp, async connection =>
        {
            await connection.ExecuteAsync("INSERT INTO t DEFAULT VALUES", cancellationToken: timeout.Token);

            // Assert
            (await connection.QueryAsync("SELECT seq_id FROM t ORDER BY seq_id", cancellationToken: timeout.Token))
                .Select(row => row[0]).ShouldBe(new object?[] { 1L, 33L });
            (await connection.QueryAsync("SELECT nextval('s')", cancellationToken: timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(34L);
        }, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence default reopen: CASCADE clears removed identities without retargeting replacements (#1770)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_RemovedSequence_ShouldKeepClearedDefaultAndLiveDependencyAsync(bool replace)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("nextval-drop", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE SEQUENCE s");
            await ExecuteAsync(session, "CREATE SEQUENCE live");
            await ExecuteAsync(session, "CREATE TABLE t(removed BIGINT DEFAULT nextval('s'), retained BIGINT DEFAULT nextval('live'))");
            await ExecuteAsync(session, "INSERT INTO t DEFAULT VALUES");
            var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP SEQUENCE s RESTRICT"));
            failure.Diagnostics[0].Code.ShouldBe("CS03080");
            await ExecuteAsync(session, "DROP SEQUENCE s CASCADE");
            if (replace) { await ExecuteAsync(session, "CREATE SEQUENCE s START 101"); }
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("nextval-drop", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(reader, "INSERT INTO t DEFAULT VALUES");

        // Assert: a separate live default still binds; the deleted default stays cleared.
        (await RowsAsync(reader, "SELECT removed, retained FROM t ORDER BY retained")).ShouldBe(new object?[][] { [1L, 1L], [null, 33L] });
        restored.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns[0].DefaultLiteral.ShouldBeNull();
        table.Columns[0].DefaultFunctionCalls.ShouldBeEmpty();
        if (replace) { (await RowsAsync(reader, "SELECT nextval('s')")).ShouldHaveSingleItem()[0].ShouldBe(101L); }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(reader, "SELECT nextval('s')"));
            failure.Diagnostics[0].Code.ShouldBe("CS03073");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Domain defaults: every NEXTVAL spelling remains refused at declaration (#1770)")]
    [InlineData("'s'")]
    [InlineData("'s'::REGCLASS")]
    [InlineData("'s' : : REGCLASS")]
    [InlineData("CAST('s' AS REGCLASS)")]
    public async Task Execute_NextvalDomainDefault_ShouldRefuseUnsupportedDependencyAsync(string operand)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("nextval-domain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SEQUENCE s");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"CREATE DOMAIN d AS BIGINT DEFAULT NEXTVAL({operand})"));

        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS03074");
        database.Catalog.Domains.ShouldBeEmpty();
        (await RowsAsync(session, "SELECT nextval('s')")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence default damage: invalid vectors and dangling or replaced identities still fail open (#1770)")]
    [InlineData("missing-vector")]
    [InlineData("extra-vector")]
    [InlineData("function-vector")]
    [InlineData("mismatched-id")]
    [InlineData("missing-sequence")]
    [InlineData("replaced-sequence")]
    public async Task Open_DamagedSequenceDefault_ShouldKeepCorruptionDiagnosticAsync(string damage)
    {
        // Arrange: the catalog API deliberately writes metadata that SQL DDL cannot publish.
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("nextval-damage", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            var sequence = await database.Catalog.CreateSequenceAsync("dbo", "s", new());
            ulong id = sequence.ObjectId;
            string[] calls = damage switch
            {
                "missing-vector" => [],
                "extra-vector" => [$"S:{id}", $"S:{id}"],
                "function-vector" => ["F:\"NEXTVAL\"(8)"],
                _ => [$"S:{id}"],
            };
            await database.Catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("seq_id", new DatabaseTypeInfo(DatabaseType.Int64),
                defaultLiteral: $"nextval({(damage == "mismatched-id" ? id + 100 : id)})", defaultFunctionCalls: calls)]);
            if (damage is "missing-sequence" or "replaced-sequence")
            { DamageSequence(database, id, damage == "replaced-sequence"); }
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("nextval-damage", new() { StorageStrategy = storage.CaptureDurableImages() });
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("db", CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS06061");
    }

    private static string DefaultExpression(string? operand)
        => operand is null ? "NEXT VALUE FOR s" : $"NEXTVAL({operand})";

    private static void DamageSequence(SqlDatabase database, ulong id, bool replace)
    {
        var storage = database.CatalogStorage;
        using var transaction = storage.BeginTransaction();
        using var iterator = storage.GetUnitIterator();
        int changed = 0;
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var reader = new DatabaseKeyReader(unit.Data.Span);
            int kind = reader.ReadInt32();
            if (kind is not (18 or 19) || (ulong)reader.ReadInt64() != id) { continue; }
            if (replace)
            {
                byte[] record = unit.Data.ToArray();
                byte[] prefix = new DatabaseKeyWriter().AppendInt32(kind).AppendInt64((long)id + 100).ToArray();
                prefix.CopyTo(record, 0);
                storage.UpdateRow(transaction, unit.PageId, unit.SlotIndex, record);
            }
            else { storage.DeleteRow(transaction, unit.PageId, unit.SlotIndex); }
            changed++;
        }
        changed.ShouldBeGreaterThan(0);
        transaction.Commit();
    }

    private static Task ExecuteAsync(SqlDatabaseSession session, string sql)
        => SqlPredicateExecutionTests.ExecuteAsync(session, sql);

    private static Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
        => SqlPredicateExecutionTests.RowsAsync(session, sql);

    private static async Task RoundAsync(SqlDatabaseEngine engine, bool tcp, Func<SqlConnection, Task> action, CancellationToken cancellationToken)
    {
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(cancellationToken);
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "nextval-reopen", EndPoint = listener.EndPoint },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(cancellationToken);
        await action(connection);
    }
}
