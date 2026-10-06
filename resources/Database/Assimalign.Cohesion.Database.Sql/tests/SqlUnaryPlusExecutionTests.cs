using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// ISO unary plus: a numeric operand passes through with its type unchanged, NULL propagates,
/// and a non-numeric operand is the same coded type error as for unary minus — at plan time
/// when the plan knows the type, at evaluation when only the value reveals it.
/// </summary>
public sealed class SqlUnaryPlusExecutionTests
{
    private const string InvalidOperandType = "COHSQLE003";

    private const string CreateValues =
        "CREATE TABLE v (id INT, tiny TINYINT, small SMALLINT, whole INT, big BIGINT, single REAL, approximate DOUBLE, " +
        "exact DECIMAL(10, 2), name VARCHAR(20), flag BOOLEAN, missing INT)";

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Unary plus: numeric operands keep their value and type; NULL propagates")]
    public async Task Select_UnaryPlus_ShouldReturnOperandUnchanged()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine, withRow: true);
        var parameters = new Dictionary<string, object?> { ["p"] = 7L };

        // Act
        await using var result = (await session.ExecuteAsync(
            "SELECT +tiny, +small, +whole, +big, +single, +approximate, +exact, +missing, +(whole + 1), +@p, + +whole, - +whole, +1 FROM v",
            parameters, CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = await ReadRowsAsync(result);

        // Assert
        result.Columns.Take(7).Select(column => column.Type).ShouldBe(
        [
            DatabaseType.Int8, DatabaseType.Int16, DatabaseType.Int32, DatabaseType.Int64,
            DatabaseType.Float32, DatabaseType.Float64, DatabaseType.Decimal,
        ]);
        rows.ShouldHaveSingleItem().ShouldBe(new object?[]
        {
            (sbyte)-8, (short)-300, -70000, -5000000000L, -1.5f, -2.25d, -3.75m, null, -69999L, 7L, -70000, 70000L, 1L,
        });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Unary plus: works in predicates, DML values, ordering and CHECK")]
    public async Task Statements_UnaryPlus_ShouldEvaluateEverywhere()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine, withRow: true);
        await session.ExecuteAsync("CREATE TABLE c (qty INT, CONSTRAINT ck CHECK (+qty > 0))", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM v WHERE +whole = -70000 ORDER BY +id")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1 });
        (await session.ExecuteAsync("UPDATE v SET whole = +(whole + 1) WHERE +id = 1", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "SELECT whole FROM v")).ShouldHaveSingleItem().ShouldBe(new object?[] { -69999 });
        (await session.ExecuteAsync("INSERT INTO c VALUES (+5)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await session.ExecuteAsync("INSERT INTO c VALUES (+(0))", cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// A sign over an operand the plan types as non-numeric fails planning with COHSQLE003, so an
    /// empty table fails exactly like a populated one. Unary minus follows the same rule.
    /// </summary>
    /// <param name="expression">The projected expression.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unary plus: a non-numeric operand is a type error before execution")]
    [InlineData("+'abc'")]
    [InlineData("+TRUE")]
    [InlineData("+name")]
    [InlineData("+flag")]
    [InlineData("+(name || 'x')")]
    [InlineData("+(id = 1)")]
    [InlineData("+UPPER(name)")]
    [InlineData("+CAST(id AS VARCHAR(5))")]
    [InlineData("-'abc'")]
    [InlineData("-name")]
    [InlineData("-(NOT flag)")]
    public async Task Select_NonNumericSignOperand_ShouldFailBeforeExecution(string expression)
    {
        // Arrange: no rows, so only planning can raise the error.
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine, withRow: false);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"SELECT {expression} FROM v", cancellationToken: CancellationToken.None));

        // Assert
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(InvalidOperandType);
        failure.Message.ShouldStartWith(InvalidOperandType + ": Invalid operand type: unary", Case.Sensitive);
        session.State.ShouldBe(SessionState.Open);
    }

    /// <summary>
    /// A parameter's type is known from its supplied value before any row is read, so a sign over
    /// a non-numeric parameter fails planning with the same code — over an empty table exactly as
    /// over a populated one. A NULL parameter propagates.
    /// </summary>
    /// <param name="sign">The sign under test.</param>
    /// <param name="withRow">Whether the table has a row.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unary plus: a non-numeric parameter is a type error before execution")]
    [InlineData("+", false)]
    [InlineData("-", false)]
    [InlineData("+", true)]
    [InlineData("-", true)]
    public async Task Select_NonNumericSignParameter_ShouldFailBeforeExecution(string sign, bool withRow)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine, withRow);
        var parameters = new Dictionary<string, object?> { ["p"] = "abc" };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"SELECT {sign}@p FROM v", parameters, CancellationToken.None));

        // Assert
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(InvalidOperandType);
        failure.Message.ShouldBe($"{InvalidOperandType}: Invalid operand type: unary '{sign}' requires a numeric operand, but the operand is String.");
        (await RowsAsync(session, "SELECT COUNT(*) FROM v")).ShouldHaveSingleItem().ShouldBe(new object?[] { withRow ? 1L : 0L });
        (await RowsAsync(session, $"SELECT {sign}@p FROM v", new Dictionary<string, object?> { ["p"] = null }))
            .Select(row => row[0]).ShouldAllBe(value => value == null);
    }

    /// <summary>A CHECK whose sign operand is not numeric is rejected when it is declared, before anything is stored.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Unary plus: a non-numeric signed CHECK operand fails the DDL")]
    public async Task Check_NonNumericSignOperand_ShouldFailDdl()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("signs");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("CREATE TABLE c (name TEXT, CHECK (+name = 'a'))", cancellationToken: CancellationToken.None));

        // Assert
        failure.Message.ShouldStartWith(InvalidOperandType, Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "c", out _).ShouldBeFalse();
    }

    /// <summary>Over the server, values keep their type and a type error is a coded execution failure on a live session.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Unary plus: values and type errors over the wire")]
    public async Task Wire_UnaryPlus_ShouldReturnValuesAndCodedFailures()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute,
            ProtocolExecuteMessage.Create("SELECT +id, +(id * 2), - +id FROM users WHERE +id = 2").Encode());
        var header = ProtocolResultHeaderMessage.Decode((await client.ExpectAsync(ProtocolMessageType.ResultHeader)).Payload.Span);
        var row = await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT +name FROM users").Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Assert
        header.Columns.Select(column => column.Item2).ShouldBe([(byte)DatabaseType.Int32, (byte)DatabaseType.Int64, (byte)DatabaseType.Int64]);
        DecodeRow(row.Payload.ToArray()).ShouldBe(new object?[] { 2, 4L, -2L });
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith(InvalidOperandType + ":", Case.Sensitive);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "unary-plus" });

    private static async Task<SqlDatabaseSession> SeedAsync(SqlDatabaseEngine engine, bool withRow)
    {
        var database = await engine.CreateDatabaseAsync("signs");
        var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(CreateValues, cancellationToken: CancellationToken.None);
        if (withRow)
        {
            await session.ExecuteAsync(
                "INSERT INTO v VALUES (1, -8, -300, -70000, -5000000000, -1.5, -2.25, -3.75, 'ada', TRUE, NULL)",
                cancellationToken: CancellationToken.None);
        }
        return session;
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(sql, parameters, CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(QueryResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static object?[] DecodeRow(byte[] payload)
    {
        var values = new List<object?>();
        var reader = new DatabaseKeyReader(payload);
        while (!reader.IsAtEnd)
        {
            values.Add(DatabaseValueCodec.Read(ref reader));
        }
        return [.. values];
    }
}
