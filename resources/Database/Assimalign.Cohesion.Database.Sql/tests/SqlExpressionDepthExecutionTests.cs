using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The expression nesting limit against the live engine (#1151). A 200,000-term expression used
/// to overflow the stack, which .NET cannot catch: one statement ended the process, and over the
/// wire every session of the server with it. Text deeper than 128 levels is now a parse failure,
/// a tree deeper than any the parser builds (only a hand-built one can be) fails its statement
/// with <c>COHSQLE004</c>, and in every case the session, and the server, keep serving.
/// </summary>
public sealed class SqlExpressionDepthExecutionTests : IDisposable
{
    private const int Limit = 128;
    private const int Hostile = 200_000;
    private const string StatementTooComplex = "COHSQLE004";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-expression-depth", Guid.NewGuid().ToString("N"));

    /// <summary>Statements whose expressions nest exactly 128 levels, and the value each returns.</summary>
    public static TheoryData<string, object> AtLimit => new()
    {
        // 128 terms; each + is a level over the chain before it.
        { $"SELECT {Chain("1", " + ", Limit)} FROM t", 128L },
        // 127 signs over a column: negation computes in BIGINT, an odd count of signs negates.
        { $"SELECT {string.Concat(Enumerable.Repeat("- ", Limit - 1))}id FROM t", -1L },
        { $"SELECT {string.Concat(Enumerable.Repeat("ABS(", Limit - 1))}id{new string(')', Limit - 1)} FROM t", 1L },
        { $"SELECT {string.Concat(Enumerable.Repeat("CASE WHEN TRUE THEN ", Limit - 1))}id{string.Concat(Enumerable.Repeat(" END", Limit - 1))} FROM t", 1 },
        // 128 grouping parentheses add no level to the tree.
        { $"SELECT {new string('(', Limit)}id{new string(')', Limit)} FROM t", 1 },
        // 127 comparisons under 126 ANDs.
        { $"SELECT id FROM t WHERE {Chain("id <> 0", " AND ", Limit - 1)}", 1 },
    };

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a statement at the 128-level limit executes")]
    [MemberData(nameof(AtLimit))]
    public async Task ExecuteAsync_AtLimit_ShouldExecute(string sql, object expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        var value = await ScalarAsync(session, sql);

        // Assert
        value.ShouldBe(expected);
    }

    /// <summary>One level past the limit and the adversarial 200,000 terms are parse failures on both session seams.</summary>
    /// <param name="terms">The number of terms in a <c>1 + 1 + ...</c> chain.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: an expression past the limit is SQL0006 before anything executes")]
    [InlineData(Limit + 1)]
    [InlineData(Hostile)]
    public async Task ExecuteAsync_PastLimit_ShouldFailToParseAndKeepTheSession(int terms)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        string sql = $"DELETE FROM t WHERE id = {Chain("1", " + ", terms)}";

        // Act
        var text = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync(sql).AsTask());
        var typed = await session.ExecuteAsync(new SqlQueryRequest((SqlQueryStatement)new SqlQueryParser().Parse(sql)));

        // Assert
        text.Message.ShouldStartWith("SQL parse error SQL0006: Expression nesting exceeds the supported limit of 128 levels.", Case.Sensitive);
        typed.Status.ShouldBe(QueryResultStatus.Error);
        typed.Diagnostics.ShouldNotBeNull().ShouldContain(diagnostic => diagnostic.Code == "SQL0006");
        (await ScalarAsync(session, "SELECT COUNT(*) FROM t")).ShouldBe(1L);
    }

    /// <summary>Every shape of hostile nesting is refused the same way; none reaches the planner.</summary>
    /// <param name="shape">The nesting construct.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: 200,000 levels of any construct fail to parse and keep the session")]
    [InlineData("parentheses")]
    [InlineData("signs")]
    [InlineData("not")]
    [InlineData("functions")]
    [InlineData("or")]
    public async Task ExecuteAsync_HostileShape_ShouldFailToParse(string shape)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        string sql = shape switch
        {
            "parentheses" => $"SELECT {new string('(', Hostile)}id{new string(')', Hostile)} FROM t",
            "signs" => $"SELECT {string.Concat(Enumerable.Repeat("- ", Hostile))}id FROM t",
            "not" => $"SELECT id FROM t WHERE {string.Concat(Enumerable.Repeat("NOT ", Hostile))}TRUE",
            "functions" => $"SELECT {string.Concat(Enumerable.Repeat("ABS(", Hostile))}id{new string(')', Hostile)} FROM t",
            _ => $"UPDATE t SET id = 2 WHERE {Chain("id = 0", " OR ", Hostile)}",
        };

        // Act
        var failure = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync(sql).AsTask());

        // Assert
        failure.Message.ShouldStartWith("SQL parse error SQL0006:", Case.Sensitive);
        (await ScalarAsync(session, "SELECT id FROM t")).ShouldBe(1);
    }

    /// <summary>
    /// A tree deeper than the parser builds can only be built by hand. Every recursive walker
    /// checks the stack, so the statement fails with COHSQLE004 instead of the process: a
    /// SELECT in the session's system-relation scan, a DELETE in the planner.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a hand-built 200,000-level tree fails its statement with COHSQLE004")]
    public async Task ExecuteAsync_HandBuiltTree_ShouldFailWithStatementTooComplex()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var deep = HandBuilt.Chain(Hostile);
        var select = HandBuilt.Select(deep, where: null, limit: null);
        var limit = HandBuilt.Select(HandBuilt.Column("id"), where: null, limit: deep);
        var delete = HandBuilt.Delete(HandBuilt.Equal(HandBuilt.Column("id"), deep));

        // Act
        var failures = new List<DatabaseException>();
        foreach (var statement in new SqlQueryExpression[] { select, limit, delete })
        {
            failures.Add(await Should.ThrowAsync<DatabaseException>(() =>
                session.ExecuteAsync(new SqlQueryRequest(new SqlQueryStatement(statement))).AsTask()));
        }

        // Assert
        foreach (var failure in failures)
        {
            AssertStatementTooComplex(failure);
        }
        (await ScalarAsync(session, "SELECT COUNT(*) FROM t")).ShouldBe(1L);
    }

    /// <summary>Inside BEGIN the failure follows the rule for any failed statement: the transaction stays open.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: COHSQLE004 inside a transaction keeps it open for COMMIT")]
    public async Task ExecuteAsync_HandBuiltTreeInTransaction_ShouldKeepTransactionOpen()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("INSERT INTO t VALUES (2, 'b')");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(new SqlQueryRequest(
            new SqlQueryStatement(HandBuilt.Delete(HandBuilt.Equal(HandBuilt.Column("id"), HandBuilt.Chain(Hostile)))))).AsTask());

        // Assert
        AssertStatementTooComplex(failure);
        session.CurrentTransaction.ShouldNotBeNull();
        await session.ExecuteAsync("COMMIT");
        (await ScalarAsync(session, "SELECT COUNT(*) FROM t")).ShouldBe(2L);
    }

    /// <summary>
    /// The walkers a statement reaches only after another one has walked the same tree check the
    /// stack themselves, so none of them depends on running second.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: every expression walker checks the stack before it recurses")]
    public void Walkers_HandBuiltTree_ShouldThrowInsufficientStackInsteadOfOverflowing()
    {
        // Arrange
        var deep = HandBuilt.Chain(Hostile);
        var columns = new[] { new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32)) };
        var evaluator = new SqlExpressionEvaluator(columns, null);

        // Act / Assert
        Should.Throw<InsufficientExecutionStackException>(() => evaluator.Evaluate(deep, [1]));
        Should.Throw<InsufficientExecutionStackException>(() => evaluator.ResolveCollation(deep));
        Should.Throw<InsufficientExecutionStackException>(() => SqlPlanner.ValidateExpression(deep, evaluator));
        Should.Throw<InsufficientExecutionStackException>(() => SqlPersistedExpression.Bind(deep, evaluator));
        Should.Throw<InsufficientExecutionStackException>(() => SqlPersistedExpression.AreEquivalent(deep, deep));

        // The renderer refuses a tree the parser could not read back before it walks it.
        Should.Throw<DatabaseException>(() => SqlPersistedExpression.Canonicalize(deep, "CHECK constraint 'ck'"))
            .Message.ShouldContain("nests 200000 levels deep", Case.Sensitive);
    }

    /// <summary>
    /// LIKE matching recursed once per wildcard it backtracked through, so a long enough pattern
    /// over a long enough value overflowed the stack from data alone. It now fails its statement.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a LIKE match deeper than the stack fails its statement with COHSQLE004")]
    public async Task ExecuteAsync_DeepLikeMatch_ShouldFailWithStatementTooComplex()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var parameters = new Dictionary<string, object?>
        {
            ["value"] = new string('a', Hostile),
            ["pattern"] = string.Concat(Enumerable.Repeat("%a", Hostile)),
        };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() =>
            session.ExecuteAsync("SELECT id FROM t WHERE @value LIKE @pattern", parameters).AsTask());

        // Assert
        AssertStatementTooComplex(failure);
        (await ScalarAsync(session, "SELECT id FROM t WHERE 'abc' LIKE '%b%'")).ShouldBe(1);
    }

    /// <summary>Both LIKE matchers check the stack, the index-backed one and the compatibility collation's.</summary>
    /// <param name="collation">The effective collation.</param>
    /// <param name="pattern">One step of a pattern that recurses once per step.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: both LIKE matchers check the stack before they recurse")]
    [InlineData("binary", "%a")]
    [InlineData("invariant", "_")]
    public void LikeMatches_DeeperThanStack_ShouldThrowInsufficientStack(string collation, string step)
    {
        // Arrange: a small stack makes the check fail long before the value is exhausted.
        string value = new('a', 10_000);
        string pattern = string.Concat(Enumerable.Repeat(step, 10_000));

        // Act
        var outcome = RunOnThread(256, () => SqlExpressionEvaluator.LikeMatches(value, pattern, Collation.FromName(collation)));

        // Assert
        outcome.Failure.ShouldBeOfType<InsufficientExecutionStackException>();
    }

    /// <summary>
    /// The DDL parses with the same limit, and the canonical text stored for a CHECK at the limit
    /// parses back under it, so a definition the engine stores always reopens. A sign applied to a
    /// sign is stored parenthesized, which is the case that adds the most parentheses.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a CHECK at the limit is stored, reopens and is enforced")]
    public async Task Check_AtLimit_ShouldPersistReopenAndEnforce()
    {
        // Arrange: 126 signs over a column, compared: 128 levels.
        string predicate = string.Concat(Enumerable.Repeat("- ", Limit - 2)) + "qty > 0";
        string tooDeep = string.Concat(Enumerable.Repeat("- ", Limit - 1)) + "qty > 0";
        string canonical;

        await using (var engine = CreateEngine(_rootPath))
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);

            // Act
            await session.ExecuteAsync($"CREATE TABLE c (qty INT, CONSTRAINT ck CHECK ({predicate}))");
            var refused = await Should.ThrowAsync<DatabaseParseException>(() =>
                session.ExecuteAsync($"CREATE TABLE d (qty INT, CONSTRAINT ck CHECK ({tooDeep}))").AsTask());

            // Assert
            refused.Message.ShouldContain("SQL0006", Case.Sensitive);
            canonical = Table(database, "c").Constraints.Single(constraint => constraint.Name == "ck").CheckExpression!;
            canonical.ShouldStartWith("-(-(-(", Case.Sensitive);
            canonical.Count(character => character == '(').ShouldBe(Limit - 3);
            Table(database, "d", exists: false);
        }

        await using var reopenedEngine = CreateEngine(_rootPath);
        var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("db");
        await using var reopenedSession = await reopened.CreateSessionAsync(CancellationToken.None);
        (await reopenedSession.ExecuteAsync("INSERT INTO c VALUES (1)")).AffectedCount.ShouldBe(1);
        await Should.ThrowAsync<SqlConstraintViolationException>(() => reopenedSession.ExecuteAsync("INSERT INTO c VALUES (-1)").AsTask());
        Table(reopened, "c").Constraints.Single(constraint => constraint.Name == "ck").CheckExpression.ShouldBe(canonical);
    }

    /// <summary>
    /// Canonical text within the limits that a thread is too small to read back is not catalog
    /// damage: the error says so instead of sending the operator to a backup, and the same text
    /// loads on a thread with more stack.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a definition read on a thread too small for it is not reported as catalog damage")]
    public void Load_OnSmallStack_ShouldNotReportDamage()
    {
        // Arrange: the stored text of 126 signs over a column, compared, 128 levels deep.
        string canonical = string.Concat(Enumerable.Repeat("-(", Limit - 3)) + "-qty" + new string(')', Limit - 3) + " > 0";
        const string subject = "CHECK constraint 'ck' on table 'dbo.c'";

        // Act
        // The stack check keeps about 128 KB in reserve, so a 160 KB thread cannot recurse far.
        var small = RunOnThread(160, () => SqlPersistedExpression.Load(canonical, subject));
        var large = RunOnThread(8 * 1024, () => SqlPersistedExpression.Load(canonical, subject));

        // Assert
        var failure = small.Failure.ShouldBeOfType<DatabaseException>();
        failure.Message.ShouldStartWith(subject + " cannot be loaded on this thread", Case.Sensitive);
        failure.Message.ShouldContain("The catalog is not damaged", Case.Sensitive);
        failure.Message.ShouldNotContain("backup");
        large.Failure.ShouldBeNull();
        SqlExpressionRenderer.Render(large.Result.ShouldNotBeNull()).ShouldBe(canonical);
    }

    /// <summary>
    /// Over the wire the hostile statement is a ParseFailure: the server stays up, the session that
    /// sent it stays ready on the same connection, and other connections are unaffected.
    /// </summary>
    /// <param name="statement">Which hostile statement to send.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Nesting: a 200,000-level statement is a ParseFailure that keeps the server and the session")]
    [InlineData("chain")]
    [InlineData("parentheses")]
    public async Task Wire_HostileStatement_ShouldReturnParseFailureAndKeepServing(string statement)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        string sql = statement == "chain"
            ? $"SELECT {Chain("1", "+", Hostile)} FROM users"
            : $"SELECT {new string('(', Hostile)}1{new string(')', Hostile)} FROM users";

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldContain("SQL0006", Case.Sensitive);
        harness.Server.Context.Sessions.Count.ShouldBe(1);
        (await ScalarAsync(client, "SELECT 1 FROM users WHERE id = 1")).ShouldBe(1L);

        await using var other = await harness.DialAsync();
        await other.HandshakeAsync();
        (await ScalarAsync(other, "SELECT COUNT(*) FROM users")).ShouldBe(2L);
        harness.Server.Context.Sessions.Count.ShouldBe(2);
    }

    /// <summary>A LIKE match that runs out of stack is an ExecutionFailure on the wire, and the session stays ready.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Nesting: a LIKE match deeper than the stack is an ExecutionFailure that keeps the session")]
    public async Task Wire_DeepLikeMatch_ShouldReturnExecutionFailureAndKeepSession()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        string sql = $"SELECT id FROM users WHERE '{new string('a', Hostile)}' LIKE '{string.Concat(Enumerable.Repeat("%a", Hostile))}'";

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith(StatementTooComplex + ":", Case.Sensitive);
        harness.Server.Context.Sessions.Count.ShouldBe(1);
        (await ScalarAsync(client, "SELECT COUNT(*) FROM users")).ShouldBe(2L);
    }

    private static string Chain(string term, string separator, int count) => string.Join(separator, Enumerable.Repeat(term, count));

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

    private static void AssertStatementTooComplex(DatabaseException failure)
    {
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(StatementTooComplex);
        failure.Message.ShouldStartWith(StatementTooComplex + ": Statement too complex", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
    }

    private static SqlCatalogTable Table(SqlDatabaseInstance database, string name, bool exists = true)
    {
        bool found = database.Catalog.TryGetTable(SqlPlanner.DefaultSchema, name, out var table);
        found.ShouldBe(exists);
        return table!;
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "expression-depth" });

    private static SqlDatabaseEngine CreateEngine(string rootPath)
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "expression-depth", RootPath = rootPath });

    private static async Task<IDatabaseSession> SeedAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("depth");
        var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, name TEXT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 'a')");
        return session;
    }

    private static async Task<object?> ScalarAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        object? value = null;
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            value = row.GetValue(0);
            rows++;
        }
        rows.ShouldBe(1);
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    /// <summary>
    /// Builds trees the parser never would, through the language package's internal constructors:
    /// the only way a tree deeper than the limit can reach the engine.
    /// </summary>
    private static class HandBuilt
    {
        private const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly ConstructorInfo _literal = typeof(SqlLiteralExpression).GetConstructors(NonPublicInstance).Single();
        private static readonly ConstructorInfo _column = typeof(SqlColumnReferenceExpression).GetConstructors(NonPublicInstance).Single();
        private static readonly ConstructorInfo _binary = typeof(SqlBinaryExpression).GetConstructors(NonPublicInstance).Single();
        private static readonly ConstructorInfo _selectColumn = typeof(SqlSelectColumn).GetConstructors(NonPublicInstance).Single();
        private static readonly ConstructorInfo _table = typeof(SqlTableReference).GetConstructors(NonPublicInstance).Single();
        private static readonly ConstructorInfo _select = typeof(SqlSelectExpression).GetConstructors(NonPublicInstance).Single();
        private static readonly ConstructorInfo _delete = typeof(SqlDeleteExpression).GetConstructors(NonPublicInstance).Single();

        /// <summary><c>1 + 1 + ...</c>, <paramref name="depth"/> levels deep.</summary>
        internal static SqlExpression Chain(int depth)
        {
            var tree = One();
            for (int level = 1; level < depth; level++)
            {
                tree = Binary(tree, SqlBinaryOperator.Add, One());
            }
            return tree;
        }

        internal static SqlExpression Column(string name) => (SqlExpression)_column.Invoke([name, null, null, null]);

        internal static SqlExpression Equal(SqlExpression left, SqlExpression right) => Binary(left, SqlBinaryOperator.Equal, right);

        internal static SqlSelectExpression Select(SqlExpression projection, SqlExpression? where, SqlExpression? limit)
            => (SqlSelectExpression)_select.Invoke(
            [
                new[] { (SqlSelectColumn)_selectColumn.Invoke([projection, null]) }, Table(), Array.Empty<SqlJoinClause>(),
                where, Array.Empty<SqlExpression>(), null, Array.Empty<SqlOrderByColumn>(), limit, null, false, null, null,
            ]);

        internal static SqlDeleteExpression Delete(SqlExpression where) => (SqlDeleteExpression)_delete.Invoke([Table(), where, null, null]);

        private static SqlExpression One() => (SqlExpression)_literal.Invoke(["1", SqlLiteralType.Integer, null]);

        private static SqlExpression Binary(SqlExpression left, SqlBinaryOperator op, SqlExpression right)
            => (SqlExpression)_binary.Invoke([left, op, right, null]);

        private static SqlTableReference Table() => (SqlTableReference)_table.Invoke(["t", null, null]);
    }
}
