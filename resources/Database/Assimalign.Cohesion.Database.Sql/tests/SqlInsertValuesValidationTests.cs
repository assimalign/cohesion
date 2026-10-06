using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// A row of <c>INSERT ... VALUES</c> has no columns in scope (ISO SQL's table value constructor of
/// an INSERT), and neither has a <c>LIMIT</c> or <c>OFFSET</c> count (#1165). A VALUES reference to
/// a target column used to resolve against the table and then index the empty row it is evaluated
/// over: the <c>IndexOutOfRangeException</c> was not a <see cref="DatabaseException"/>, so over the
/// wire it ended the session with <c>Internal</c>. Planning now rejects every column reference in
/// those clauses with <c>COHSQLE005</c>, before anything executes.
/// </summary>
public sealed class SqlInsertValuesValidationTests
{
    private const string ColumnReferenceNotAllowed = "COHSQLE005";

    /// <summary>
    /// A column reference anywhere in a VALUES row fails planning with the coded error on both session
    /// seams: nothing is inserted, not even an earlier row, and the session keeps serving.
    /// </summary>
    /// <param name="sql">The INSERT statement.</param>
    /// <param name="reference">The reference as the diagnostic names it.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - INSERT VALUES: a column reference is a coded error before anything executes")]
    [InlineData("INSERT INTO v (id, a) VALUES (id, 1)", "id")]
    [InlineData("INSERT INTO v VALUES (2, a, 'x', TRUE)", "a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, v.a)", "v.a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, dbo.v.a)", "dbo.v.a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, \"a\")", "a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, nosuch)", "nosuch")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, 1 + (2 * a))", "a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, -a)", "a")]
    [InlineData("INSERT INTO v (id, name) VALUES (2, +name)", "name")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, CAST(a AS BIGINT))", "a")]
    [InlineData("INSERT INTO v (id, name) VALUES (2, name COLLATE case_insensitive)", "name")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, CASE WHEN a = 1 THEN 1 ELSE 0 END)", "a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, COALESCE(a, 1))", "a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, ABS(id))", "id")]
    [InlineData("INSERT INTO v (id, flag) VALUES (2, a IN (1, 2))", "a")]
    [InlineData("INSERT INTO v (id, flag) VALUES (2, name LIKE 'a%')", "name")]
    [InlineData("INSERT INTO v (id, flag) VALUES (2, a IS NULL)", "a")]
    [InlineData("INSERT INTO v (id, flag) VALUES (2, a BETWEEN 1 AND 2)", "a")]
    [InlineData("INSERT INTO v (id, flag) VALUES (2, TRUE AND TRUE AND flag)", "flag")]
    [InlineData("INSERT INTO v (id, flag) VALUES (2, FALSE OR FALSE OR a = 1)", "a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, SUM(a))", "a")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, 2), (3, id)", "id")]
    // DEFAULT in VALUES is outside the dialect and parses as a column named DEFAULT.
    [InlineData("INSERT INTO v (id, a) VALUES (2, DEFAULT)", "DEFAULT")]
    public async Task ExecuteAsync_ColumnReferenceInValues_ShouldFailBeforeExecution(string sql, string reference)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        string expected = $"{ColumnReferenceNotAllowed}: Column reference '{reference}' is not allowed in INSERT ... VALUES, " +
            "which has no columns in scope. Use literals, parameters and expressions over them, or INSERT ... SELECT to read values from a table.";

        // Act
        var text = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql).AsTask());
        var typed = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(Request(sql)).AsTask());

        // Assert
        foreach (var failure in new[] { text, typed })
        {
            failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(ColumnReferenceNotAllowed);
            failure.Message.ShouldBe(expected);
        }
        session.State.ShouldBe(SessionState.Open);
        (await RowsAsync(session, "SELECT id, a, name, flag FROM v")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 10, "ada", true });
        (await RowsAsync(session, "SELECT 1 FROM v")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
    }

    /// <summary>The planner rejects the statement before execution, so the table's contents do not matter.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT VALUES: a column reference fails the same way over an empty table")]
    public async Task ExecuteAsync_ColumnReferenceIntoEmptyTable_ShouldFail()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("values");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u (id INT, a INT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("INSERT INTO u (id, a) VALUES (id, 1)").AsTask());

        // Assert
        failure.Message.ShouldStartWith($"{ColumnReferenceNotAllowed}: Column reference 'id' is not allowed in INSERT ... VALUES", Case.Sensitive);
        (await RowsAsync(session, "SELECT COUNT(*) FROM u")).ShouldHaveSingleItem().ShouldBe(new object?[] { 0L });
    }

    /// <summary>
    /// Inside BEGIN the failure follows the rule for any failed statement: the transaction stays
    /// active with its earlier work, and COMMIT keeps exactly that work.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT VALUES: a column reference inside a transaction keeps it open")]
    public async Task ExecuteAsync_ColumnReferenceInTransaction_ShouldKeepTransactionOpen()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("INSERT INTO v (id, a) VALUES (2, 20)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("INSERT INTO v (id, a) VALUES (3, a)").AsTask());

        // Assert
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(ColumnReferenceNotAllowed);
        session.CurrentTransaction.ShouldNotBeNull();
        await session.ExecuteAsync("COMMIT");
        (await RowsAsync(session, "SELECT id, a FROM v ORDER BY id")).ShouldBe([[1, 10], [2, 20]]);
    }

    /// <summary>
    /// Expressions with no column reference keep working: literals, parameters, operators, signs,
    /// CAST, COLLATE, CASE, predicates and scalar functions over them, in every row.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT VALUES: literals, parameters and expressions over them still insert")]
    public async Task ExecuteAsync_ValuesWithoutColumnReferences_ShouldInsert()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var parameters = new Dictionary<string, object?> { ["id"] = 2, ["a"] = 5, ["name"] = "grace", ["flag"] = false, ["p"] = -7 };

        // Act
        var first = await session.ExecuteAsync(
            "INSERT INTO v (id, a, name, flag) VALUES (@id, @a * 2 + 1, UPPER(@name), NOT @flag), " +
            "(3, -(3 * 4), 'x' || 'y', 1 IN (1, 2)), " +
            "(4, CAST('7' AS INT), 'Lin' COLLATE case_insensitive, 'b' LIKE 'b%'), " +
            "(5, CASE WHEN @flag THEN 1 ELSE 2 END, COALESCE(NULL, 'z'), NULL IS NULL), " +
            "(6, ABS(@p), LOWER('Q'), +1 BETWEEN 0 AND 2)",
            parameters, CancellationToken.None);

        // Assert
        first.AffectedCount.ShouldBe(5);
        (await RowsAsync(session, "SELECT id, a, name, flag FROM v ORDER BY id")).ShouldBe(
        [
            [1, 10, "ada", true],
            [2, 11, "GRACE", true],
            [3, -12, "xy", true],
            [4, 7, "Lin", true],
            [5, 2, "z", true],
            [6, 7, "q", true],
        ]);
    }

    /// <summary>Aggregates and <c>*</c> have no value in a VALUES row, and say so before anything executes.</summary>
    /// <param name="sql">The INSERT statement.</param>
    /// <param name="message">The planning error.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - INSERT VALUES: aggregates and * are rejected while planning")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, COUNT(*))", "Aggregate functions are not allowed in INSERT ... VALUES.")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, SUM(1))", "Aggregate functions are not allowed in INSERT ... VALUES.")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, 2), (3, MAX(4))", "Aggregate functions are not allowed in INSERT ... VALUES.")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, *)", "'*' is not allowed in INSERT ... VALUES.")]
    public async Task ExecuteAsync_AggregateOrStarInValues_ShouldFailBeforeExecution(string sql, string message)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql).AsTask());

        // Assert
        failure.Message.ShouldBe(message);
        (await RowsAsync(session, "SELECT COUNT(*) FROM v")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
    }

    /// <summary>
    /// A sign over an operand the plan types as non-numeric is checked in VALUES too, while
    /// planning, so a later row's fault leaves the earlier rows unwritten.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT VALUES: a non-numeric sign operand is COHSQLE003 while planning")]
    public async Task ExecuteAsync_NonNumericSignInValues_ShouldFailWithInvalidOperandType()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() =>
            session.ExecuteAsync("INSERT INTO v (id, a) VALUES (2, 2), (3, +'abc')").AsTask());

        // Assert
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("COHSQLE003");
        (await RowsAsync(session, "SELECT COUNT(*) FROM v")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
    }

    /// <summary>
    /// <c>LIMIT</c> and <c>OFFSET</c> counts are evaluated while planning against no row, so a column
    /// reference there is the same coded error, over an empty result as over a full one. A subquery's
    /// count is no different: a reference to its own columns or to the outer query's fails with the
    /// same code, thrown like any other planning error, where it used to come back as an error result
    /// carrying <c>COHDBL001</c>'s correlated-subquery diagnosis.
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <param name="reference">The reference as the diagnostic names it.</param>
    /// <param name="clause">The clause the diagnostic names.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - LIMIT/OFFSET: a column reference is a coded error while planning")]
    [InlineData("SELECT a FROM v LIMIT a", "a", "LIMIT")]
    [InlineData("SELECT a FROM v WHERE id < 0 LIMIT v.a", "v.a", "LIMIT")]
    [InlineData("SELECT a FROM v OFFSET 1 + id", "id", "OFFSET")]
    [InlineData("SELECT a, COUNT(*) FROM v GROUP BY a LIMIT a", "a", "LIMIT")]
    [InlineData("SELECT v.a FROM v JOIN w ON v.id = w.id LIMIT w.id", "w.id", "LIMIT")]
    [InlineData("SELECT a FROM v WHERE id IN (SELECT id FROM w LIMIT a)", "a", "LIMIT")]
    [InlineData("SELECT a FROM v WHERE id IN (SELECT id FROM w LIMIT id)", "id", "LIMIT")]
    [InlineData("SELECT a FROM v WHERE id IN (SELECT id FROM w LIMIT w.id)", "w.id", "LIMIT")]
    [InlineData("SELECT a FROM v WHERE EXISTS (SELECT id FROM w OFFSET id)", "id", "OFFSET")]
    public async Task ExecuteAsync_ColumnReferenceInCount_ShouldFailWhilePlanning(string sql, string reference, string clause)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        await session.ExecuteAsync("CREATE TABLE w (id INT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql).AsTask());

        // Assert
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(ColumnReferenceNotAllowed);
        failure.Message.ShouldBe($"{ColumnReferenceNotAllowed}: Column reference '{reference}' is not allowed in {clause}, " +
            "which has no columns in scope. Use literals, parameters and expressions over them.");
        (await RowsAsync(session, "SELECT a FROM v LIMIT @n OFFSET 1 - 1", new Dictionary<string, object?> { ["n"] = 1 }))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 10 });
    }

    /// <summary>An aggregate or a bare <c>*</c> in a count is rejected while planning with the clause named.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="message">The planning error.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - LIMIT/OFFSET: an aggregate or * is rejected while planning")]
    [InlineData("SELECT a FROM v LIMIT COUNT(*)", "Aggregate functions are not allowed in LIMIT.")]
    [InlineData("SELECT a FROM v OFFSET SUM(1)", "Aggregate functions are not allowed in OFFSET.")]
    [InlineData("SELECT a FROM v LIMIT *", "'*' is not allowed in LIMIT.")]
    public async Task ExecuteAsync_AggregateOrStarInCount_ShouldFailWhilePlanning(string sql, string message)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql).AsTask());

        // Assert
        failure.Message.ShouldBe(message);
        session.State.ShouldBe(SessionState.Open);
    }

    /// <summary>
    /// <c>CURRENT_DATE</c>, <c>CURRENT_TIME</c> and <c>CURRENT_TIMESTAMP</c> take no parentheses, so
    /// they reach the planner as names. They are the dialect's recognized functions, not columns, and
    /// report the unsupported function, as <c>NOW()</c> does, rather than a column reference; nothing
    /// is inserted.
    /// </summary>
    /// <param name="sql">The statement.</param>
    /// <param name="function">The function as written.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - INSERT VALUES/LIMIT: a niladic datetime function reports the function, not a column")]
    [InlineData("INSERT INTO v (id, a) VALUES (2, CURRENT_TIMESTAMP)", "CURRENT_TIMESTAMP")]
    [InlineData("INSERT INTO v (id, name) VALUES (2, 'x'), (3, CAST(current_date AS VARCHAR(20)))", "current_date")]
    [InlineData("INSERT INTO v (id, name) VALUES (2, CAST(CURRENT_TIME AS VARCHAR(20)))", "CURRENT_TIME")]
    [InlineData("SELECT a FROM v LIMIT CURRENT_DATE", "CURRENT_DATE")]
    public async Task ExecuteAsync_NiladicDateTimeFunctionWithoutScope_ShouldReportUnsupportedFunction(string sql, string function)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql).AsTask());

        // Assert
        failure.ShouldNotBeOfType<SqlEvaluationException>();
        failure.Message.ShouldBe($"Function '{function}' is not supported by the executor yet.");
        session.State.ShouldBe(SessionState.Open);
        (await RowsAsync(session, "SELECT COUNT(*) FROM v")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
    }

    /// <summary>
    /// Over the server, the statement is an <c>ExecutionFailure</c> carrying the code, not an
    /// <c>Internal</c> error that closes the session: nothing is inserted, and the same connection
    /// runs <c>SELECT 1</c> (with the FROM the dialect requires) and parameterized and literal
    /// INSERTs afterwards.
    /// </summary>
    /// <param name="sql">The failing INSERT.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - INSERT VALUES: a column reference is a coded ExecutionFailure that keeps the session")]
    [InlineData("INSERT INTO users (id, name) VALUES (id, 'lin')")]
    [InlineData("INSERT INTO users (id, name) VALUES (3, 'lin'), (4, users.name)")]
    public async Task Wire_ColumnReferenceInValues_ShouldKeepTheSession(string sql)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith($"{ColumnReferenceNotAllowed}: Column reference '", Case.Sensitive);
        error.Message.ShouldEndWith(" is not allowed in INSERT ... VALUES, which has no columns in scope. " +
            "Use literals, parameters and expressions over them, or INSERT ... SELECT to read values from a table.", Case.Sensitive);
        harness.Server.Sessions.Count.ShouldBe(1);
        (await QueryAsync(client, "SELECT 1 FROM users")).ShouldBe([[1L], [1L]]);
        (await QueryAsync(client, "SELECT COUNT(*) FROM users")).ShouldBe([[2L]]);

        await client.SendAsync(ProtocolMessageType.Execute, new ProtocolExecuteMessage(
            "INSERT INTO users (id, name) VALUES (@id + 1, UPPER(@name))",
            new Dictionary<string, byte[]>
            {
                ["id"] = DatabaseValueCodec.EncodeComponent(2),
                ["name"] = DatabaseValueCodec.EncodeComponent("lin"),
            }).Encode());
        ProtocolResultCompleteMessage.Decode((await client.ExpectAsync(ProtocolMessageType.ResultComplete)).Payload.Span)
            .AffectedCount.ShouldBe(1);
        await client.SendAsync(ProtocolMessageType.Execute,
            ProtocolExecuteMessage.Create("INSERT INTO users (id, name) VALUES (4, 'kay')").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        (await QueryAsync(client, "SELECT id, name FROM users ORDER BY id")).ShouldBe([[1, "ada"], [2, "grace"], [3, "LIN"], [4, "kay"]]);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "insert-values" });

    private static async Task<SqlDatabaseSession> SeedAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("values");
        var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE v (id INT, a INT, name VARCHAR(20), flag BOOLEAN)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO v VALUES (1, 10, 'ada', TRUE)", cancellationToken: CancellationToken.None);
        return session;
    }

    private static SqlQueryRequest Request(string sql)
        => new((SqlQueryStatement)new SqlQueryParser().Parse(sql));

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(sql, parameters, CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static async Task<List<object?[]>> QueryAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var rows = new List<object?[]>();
        while (true)
        {
            var frame = (await client.ReadAsync()).ShouldNotBeNull();
            if (frame.Type == ProtocolMessageType.ResultComplete)
            {
                return rows;
            }
            frame.Type.ShouldBe(ProtocolMessageType.ResultRow);
            var values = new List<object?>();
            var reader = new DatabaseKeyReader(frame.Payload.ToArray());
            while (!reader.IsAtEnd)
            {
                values.Add(DatabaseValueCodec.Read(ref reader));
            }
            rows.Add([.. values]);
        }
    }
}
