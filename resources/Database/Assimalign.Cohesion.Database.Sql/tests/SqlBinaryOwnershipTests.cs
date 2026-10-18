using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlBinaryOwnershipTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Binary CTEs: shared materialization detaches every delivered cell")]
    public async Task Select_CommonTableFanout_ShouldOwnEveryRetainedBinaryValue()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-cte", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, b BINARY);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, X'00FF'), (2, X'00FF');", cancellationToken: CancellationToken.None);

        // Act
        var values = await ValuesAsync(session, "WITH c AS (SELECT b FROM t) SELECT x.b, x.b, y.b FROM c x CROSS JOIN c y;");

        // Assert
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(2);
        values.Count.ShouldBe(12);
        foreach (var value in values)
        {
            value.ShouldBe(new byte[] { 0, 255 });
            value[0] = 0x99;
        }
        (await ValuesAsync(session, "SELECT b FROM t;")).ShouldAllBe(value => value.SequenceEqual(new byte[] { 0, 255 }));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Binary derived tables: fanout owns every emitted cell")]
    public async Task Select_DerivedFanout_ShouldDetachRetainedBinaryValues()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-derived", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, b BINARY);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, X'00FF'), (2, X'00FF');", cancellationToken: CancellationToken.None);

        // Act
        var values = await ValuesAsync(session, "SELECT d.b, d.b FROM t CROSS JOIN (SELECT b FROM t LIMIT 1) d;");

        // Assert
        values.Count.ShouldBe(4);
        values[0][0] = 41;
        foreach (var bytes in values.Skip(1)) { bytes.ShouldBe(new byte[] { 0, 255 }); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binary joins: repeated projections and fanout own each emitted cell")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Select_BinaryJoinFanout_ShouldIsolateReusedSourceRows(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-fanout", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE l (id INT, b BINARY);");
        await session.ExecuteAsync("CREATE TABLE r (id INT, b BINARY);");
        await session.ExecuteAsync("INSERT INTO l VALUES (1, X'00FF'), (2, X'00FF');");
        await session.ExecuteAsync("INSERT INTO r VALUES (1, X'00FF'), (1, X'00FF'), (2, X'00FF');");
        if (indexed) { await session.ExecuteAsync("CREATE INDEX ix_r ON r(id);"); }

        // Act
        var values = await ValuesAsync(session, "SELECT l.b, COALESCE(NULL, l.b), r.b FROM l JOIN r ON l.id = r.id;");

        // Assert
        session.LastStatementMetrics.ShouldNotBeNull().JoinAccessPaths.ShouldBe([indexed ? "join-seek:ix_r" : "join-scan"]);
        values.Count.ShouldBe(9);
        foreach (var value in values)
        {
            value.ShouldBe(new byte[] { 0, 255 });
            value[0] = 0x99;
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binary storage: repeated scans and covered projections return independent arrays")]
    [InlineData("00FF")]
    [InlineData("")]
    public async Task Select_StoredBinary_ShouldOwnRepeatedScanAndIndexValues(string hex)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-storage", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (b BINARY);");
        await session.ExecuteAsync($"INSERT INTO t VALUES (X'{hex}');");
        byte[] expected = Convert.FromHexString(hex);

        // Act: read the same stored row twice, then project only the indexed column through a seek.
        var first = (await ValuesAsync(session, "SELECT b FROM t;")).ShouldHaveSingleItem();
        var second = (await ValuesAsync(session, "SELECT b FROM t;")).ShouldHaveSingleItem();
        await session.ExecuteAsync("CREATE INDEX ix_b ON t(b);");
        var indexed = (await ValuesAsync(session, $"SELECT b FROM t WHERE b = X'{hex}';")).ShouldHaveSingleItem();
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_b");
        var repeated = (await ValuesAsync(session, $"SELECT b FROM t WHERE b = X'{hex}';")).ShouldHaveSingleItem();

        // Assert
        byte[][] values = [first, second, indexed, repeated];
        for (int i = 0; i < values.Length; i++)
        {
            values[i].ShouldBe(expected);
            values[i].ShouldNotBeSameAs(Array.Empty<byte>());
            for (int j = 0; j < i; j++) { values[i].ShouldNotBeSameAs(values[j]); }
        }
        if (first.Length > 0) { first[0] = 0x99; }
        (await ValuesAsync(session, $"SELECT b FROM t WHERE b = X'{hex}';")).ShouldHaveSingleItem().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binary decoding: storage creates fresh payload arrays before result construction")]
    [InlineData(DatabaseType.Binary)]
    [InlineData(DatabaseType.JsonBinary)]
    public async Task Decode_StoredBinary_ShouldAllocateAtTheCodec(DatabaseType type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-codec", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (b {(type == DatabaseType.Binary ? "BINARY" : "JSONB")});");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        byte[] original = [0, 255];
        byte[] record = SqlRowCodec.Encode(table, [original], default);

        // Act: bypass materialization, so a result-boundary copy cannot hide a shared decoder value.
        var first = SqlRowCodec.Decode(record, table, out _, out _, out _)[0].ShouldBeOfType<byte[]>();
        var second = SqlRowCodec.Decode(record, table, out _, out _, out _)[0].ShouldBeOfType<byte[]>();
        first[0] = 0x99;

        // Assert
        first.ShouldNotBeSameAs(second);
        first.ShouldNotBeSameAs(original);
        second.ShouldBe(original);
        SqlRowCodec.Decode(record, table, out _, out _, out _)[0].ShouldBe(original);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Shared binary producers: cells, rows and later calls cannot mutate retained bytes")]
    [InlineData("SELECT shared(1), shared(1) FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT shared(id), shared(id) FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT shared(id), shared(id) FROM t;", SqlFunctionVolatility.Stable)]
    [InlineData("SELECT shared(id), shared(id) FROM t;", SqlFunctionVolatility.Volatile)]
    [InlineData("SELECT cached(id), cached(id) FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT (SELECT b FROM t WHERE id = 1), X'00FF' FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT (SELECT CAST(inner_t.b AS BYTEA) FROM t inner_t WHERE inner_t.id = t.id), (SELECT CAST(inner_t.b AS BYTEA) FROM t inner_t WHERE inner_t.id = t.id) FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT (SELECT shared(inner_t.id) FROM t inner_t WHERE inner_t.id = t.id), (SELECT shared(inner_t.id) FROM t inner_t WHERE inner_t.id = t.id) FROM t;", SqlFunctionVolatility.Stable)]
    [InlineData("SELECT CASE WHEN id > 0 THEN b ELSE NULL END, COALESCE(NULL, b) FROM t WHERE EXISTS (SELECT inner_t.id FROM t inner_t WHERE inner_t.id = t.id);", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT shared_agg(id), shared_agg(id) FROM t GROUP BY id;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT CAST(id AS BINARY), CAST(id AS BINARY) FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT CAST('\\x00ff' AS BYTEA), CAST('\\x00ff' AS BYTEA) FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT CAST(X'00FF' AS BYTEA), CAST(X'00FF' AS BYTEA) FROM t;", SqlFunctionVolatility.Immutable)]
    [InlineData("SELECT shared(1), X'00FF' FROM COHESION_SCHEMA.FUNCTIONS LIMIT 2;", SqlFunctionVolatility.Immutable)]
    public async Task Select_SharedBinaryProducer_ShouldCopyEachDeliveredValue(string sql, SqlFunctionVolatility volatility)
    {
        // Arrange
        byte[] retained = [0, 255];
        var builder = SqlDatabaseEngine.CreateBuilder("binary-producers");
        builder.Functions.Add(SqlScalarFunction.Create<int, byte[]>("shared", _ => retained, volatility));
        builder.Functions.Add(SqlAggregateFunction.Create<int, int, byte[]>("shared_agg", () => 0, (state, _) => state + 1, _ => retained));
        builder.Types.AddCast(SqlCast.Create<int, byte[]>(SqlType.Integer, SqlType.Binary, _ => retained, SqlCastContext.Explicit));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE FUNCTION cached(p INT) RETURNS BINARY STABLE RETURN CASE WHEN p = 1 THEN X'00FF' ELSE COALESCE(NULL, X'00FF') END;");
        await session.ExecuteAsync("CREATE TABLE t (id INT, b BINARY DEFAULT X'00FF');");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2);");

        // Act
        var values = await ValuesAsync(session, sql);
        foreach (var value in values)
        {
            // Assert: mutate through the same backing-array route as GetBytes.
            value.ShouldBe(new byte[] { 0, 255 });
            value.ShouldNotBeSameAs(retained);
            value[0] = 0x99;
        }

        // Assert: cached bodies and application-owned arrays survive later executions.
        values.Count.ShouldBe(4);
        retained.ShouldBe(new byte[] { 0, 255 });
        (await ValuesAsync(session, sql)).ShouldAllBe(value => value.SequenceEqual(new byte[] { 0, 255 }));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binary CAST: binary and JSONB outputs own each cell, row and later execution")]
    [InlineData("BYTEA", "\\x00ff", "00FF")]
    [InlineData("BYTEA", "\\x", "")]
    [InlineData("JSONB", "{\"雪\":1}", "7B22E99BAA223A317D")]
    public async Task Select_BinaryProducingCast_ShouldDetachEveryDeliveredValue(string type, string text, string expectedHex)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-cast", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2);", cancellationToken: CancellationToken.None);
        string sql = $"SELECT CAST('{text}' AS {type}), CAST('{text}' AS {type}) FROM t;";
        byte[] expected = Convert.FromHexString(expectedHex);

        // Act
        var first = await ValuesAsync(session, sql);
        var second = await ValuesAsync(session, sql);

        // Assert
        first.Count.ShouldBe(4);
        second.Count.ShouldBe(4);
        var all = first.Concat(second).ToArray();
        for (int i = 0; i < all.Length; i++)
        {
            all[i].ShouldBe(expected);
            for (int j = 0; j < i; j++) { all[i].ShouldNotBeSameAs(all[j]); }
            if (all[i].Length > 0) { all[i][0] = 0x99; }
        }
        (await ValuesAsync(session, sql)).ShouldAllBe(value => value.SequenceEqual(expected));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binary joins: N-way and LEFT fanout preserve cell ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Select_BinaryNWayAndLeft_ShouldOwnEveryDeliveredCell(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-nway", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE a (id INT, b BINARY);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE b (id INT, b BINARY);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE c (id INT, b BINARY);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO a VALUES (1, X'00FF'), (2, X'00FF');", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO b VALUES (1, X'00FF'), (1, X'00FF'), (2, X'00FF');", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (1, X'00FF'), (1, X'00FF');", cancellationToken: CancellationToken.None);
        if (indexed)
        {
            await session.ExecuteAsync("CREATE INDEX ix_b ON b(id);", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("CREATE INDEX ix_c ON c(id);", cancellationToken: CancellationToken.None);
        }

        // Act / Assert: NULL extension still delivers the retained outer binary cells independently.
        const string sql = "SELECT a.b, b.b, COALESCE(c.b, a.b) FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id;";
        var values = await ValuesAsync(session, sql);
        values.Count.ShouldBe(15);
        foreach (var value in values)
        {
            value.ShouldBe(new byte[] { 0, 255 });
            value[0] = 0x99;
        }
        (await ValuesAsync(session, sql)).ShouldAllBe(value => value.SequenceEqual(new byte[] { 0, 255 }));
        await using var left = (await session.ExecuteAsync("SELECT a.b, c.b FROM a LEFT JOIN c ON a.id = c.id WHERE a.id = 2;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in left.GetRowsAsync(CancellationToken.None))
        {
            row.GetValue(0).ShouldBe(new byte[] { 0, 255 });
            row.GetValue(1).ShouldBeNull();
        }
    }

    private static async Task<List<byte[]>> ValuesAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<byte[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            for (int i = 0; i < row.FieldCount; i++)
            {
                MemoryMarshal.TryGetArray(row.GetBytes(i), out var bytes).ShouldBeTrue();
                bytes.Offset.ShouldBe(0);
                values.Add(bytes.Array!);
            }
        }
        return values;
    }
}
