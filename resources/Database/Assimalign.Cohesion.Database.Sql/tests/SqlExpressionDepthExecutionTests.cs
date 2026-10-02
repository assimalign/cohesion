using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
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
/// a walk that runs out of stack fails its statement with <c>COHSQLE004</c>, and in every case the
/// session, and the server, keep serving.
/// </summary>
/// <remarks>
/// A tree deeper than the parser builds needs the language package's internal constructors,
/// which this assembly does not reach. The stack checks are proven instead by running a tree at
/// the limit with almost no stack left (<see cref="NearStackLimit"/>): a walker that checks as
/// it descends throws long before the tree ends, and one that does not check would complete
/// inside the runtime's reserve, failing the test instead of the process.
/// </remarks>
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
    /// A statement whose walk runs out of stack fails as that statement, with COHSQLE004 and the
    /// exhausted-stack signal inside, and none of it takes effect; the same statement then runs
    /// with ample stack. Every stack check leads here: a deeply backtracking LIKE, a tree deeper
    /// than the parser builds, or a statement run on a thread created with a small stack.
    /// </summary>
    /// <param name="sql">A statement whose predicate or value nests exactly 128 levels once <c>{0}</c> is filled.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a statement that runs out of stack fails with COHSQLE004 and keeps the session")]
    [InlineData("SELECT COUNT(*) FROM t WHERE id < {0}")]
    [InlineData("UPDATE t SET name = 'z' WHERE id < {0}")]
    [InlineData("DELETE FROM t WHERE id < {0}")]
    [InlineData("INSERT INTO t VALUES ({0} - 126, 'z')")]
    public async Task ExecuteAsync_StackExhausted_ShouldFailWithStatementTooComplex(string sql)
    {
        // Arrange: 127 terms under a comparison, 128 levels.
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var request = Request(sql.Replace("{0}", Chain("1", " + ", Limit - 1), StringComparison.Ordinal));

        // Act
        var outcome = NearStackLimit.Run(() => session.ExecuteAsync(request).AsTask());
        var failure = await Should.ThrowAsync<DatabaseException>(() => outcome.Result.ShouldNotBeNull());

        // Assert: nothing of the statement happened, and with ample stack it runs.
        AssertStatementTooComplex(failure);
        (await ScalarAsync(session, "SELECT name FROM t")).ShouldBe("a");
        var result = await session.ExecuteAsync(request);
        if (result is QueryResultSet)
        {
            (await ReadScalarAsync(result)).ShouldBe(1L);
        }
        else
        {
            result.AffectedCount.ShouldBe(1);
        }
    }

    /// <summary>Inside BEGIN the failure follows the rule for any failed statement: the transaction stays open.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: COHSQLE004 inside a transaction keeps it open for COMMIT")]
    public async Task ExecuteAsync_StackExhaustedInTransaction_ShouldKeepTransactionOpen()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("INSERT INTO t VALUES (2, 'b')");
        var delete = Request($"DELETE FROM t WHERE id < {Chain("1", " + ", Limit - 1)}");

        // Act
        var outcome = NearStackLimit.Run(() => session.ExecuteAsync(delete).AsTask());
        var failure = await Should.ThrowAsync<DatabaseException>(() => outcome.Result.ShouldNotBeNull());

        // Assert
        AssertStatementTooComplex(failure);
        session.CurrentTransaction.ShouldNotBeNull();
        await session.ExecuteAsync("COMMIT");
        (await ScalarAsync(session, "SELECT COUNT(*) FROM t")).ShouldBe(2L);
    }

    /// <summary>A CHECK whose DDL runs out of stack creates nothing; with ample stack the same DDL creates it.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a DDL that runs out of stack fails with COHSQLE004 and changes nothing")]
    public async Task ExecuteAsync_StackExhaustedDdl_ShouldChangeNothing()
    {
        // Arrange: 126 signs over a column, compared: 128 levels.
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var create = Request($"CREATE TABLE c (qty INT, CONSTRAINT ck CHECK ({string.Concat(Enumerable.Repeat("- ", Limit - 2))}qty > 0))");

        // Act
        var outcome = NearStackLimit.Run(() => session.ExecuteAsync(create).AsTask());
        var failure = await Should.ThrowAsync<DatabaseException>(() => outcome.Result.ShouldNotBeNull());

        // Assert
        AssertStatementTooComplex(failure);
        Table(database, "c", exists: false);
        await session.ExecuteAsync(create);
        Table(database, "c").Constraints.ShouldContain(constraint => constraint.Name == "ck");
    }

    /// <summary>
    /// Every expression walker checks the stack as it descends, not only on entry, so none of them
    /// depends on another walker having checked the same tree first. With ample stack each walk
    /// of the deepest chain the parser accepts completes; with a few KB left each one throws.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: every expression walker checks the stack as it recurses")]
    public void Walkers_NearStackLimit_ShouldThrowInsufficientStack()
    {
        // Arrange: two separately parsed trees, so equivalence walks both.
        var deep = Projection(Chain("1", " + ", Limit));
        var twin = Projection(Chain("1", " + ", Limit));
        var columns = new[] { new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32)) };
        var evaluator = new SqlExpressionEvaluator(columns, null);
        var walkers = new (string Name, Func<object?> Walk)[]
        {
            ("Evaluate", () => evaluator.Evaluate(deep, [1])),
            ("ResolveCollation", () => evaluator.ResolveCollation(deep)),
            ("ValidateExpression", () => { SqlPlanner.ValidateExpression(deep, evaluator); return null; }),
            ("RejectColumnReferences", () => { SqlPlanner.RejectColumnReferences(deep, "INSERT ... VALUES", null); return null; }),
            ("ContainsStar", () => SqlPlanner.ContainsStar(deep)),
            ("Bind", () => { SqlPersistedExpression.Bind(deep, evaluator); return null; }),
            ("AreEquivalent", () => SqlPersistedExpression.AreEquivalent(deep, twin)),
            ("Canonicalize", () => SqlPersistedExpression.Canonicalize(deep, "CHECK constraint 'ck'")),
        };

        foreach (var (name, walk) in walkers)
        {
            // Act: the ample-stack run also compiles and initializes everything the walk touches.
            Should.NotThrow(walk, name);
            var outcome = NearStackLimit.Run(walk);

            // Assert
            outcome.Failure.ShouldBeOfType<InsufficientExecutionStackException>(name);
        }
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
        // Arrange: with a few KB of stack left the check fails long before the value is exhausted.
        var effective = Collation.FromName(collation);
        string value = new('a', 10_000);
        string pattern = string.Concat(Enumerable.Repeat(step, 10_000));
        SqlExpressionEvaluator.LikeMatches("abc", step + "%", effective).ShouldBeTrue();

        // Act
        var outcome = NearStackLimit.Run(() => SqlExpressionEvaluator.LikeMatches(value, pattern, effective));

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
    /// damage: the signal names the definition and says so instead of sending the operator to a
    /// backup, and the same text loads on a thread with more stack.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a definition read on a thread too small for it is not reported as catalog damage")]
    public void Load_OnSmallStack_ShouldNotReportDamage()
    {
        // Arrange: the stored text of 126 signs over a column, compared, 128 levels deep.
        string canonical = string.Concat(Enumerable.Repeat("-(", Limit - 3)) + "-qty" + new string(')', Limit - 3) + " > 0";
        const string subject = "CHECK constraint 'ck' on table 'dbo.c'";

        // Act: reading the deepest text back costs the parser a few hundred KB of stack, more
        // than a default thread spares in a debug build, so the ample run gets 8 MB.
        var small = NearStackLimit.Run(() => SqlPersistedExpression.Load(canonical, subject));
        var large = RunOnThread(8 * 1024, () => SqlPersistedExpression.Load(canonical, subject));

        // Assert
        var failure = small.Failure.ShouldBeOfType<InsufficientExecutionStackException>();
        failure.Message.ShouldStartWith(subject + " cannot be loaded on this thread", Case.Sensitive);
        failure.Message.ShouldContain("The catalog is not damaged", Case.Sensitive);
        failure.Message.ShouldNotContain("backup");
        large.Failure.ShouldBeNull();
        SqlExpressionRenderer.Render(large.Result.ShouldNotBeNull()).ShouldBe(canonical);
    }

    /// <summary>
    /// Binding a table version on a thread too small for its CHECK fails differently for the two
    /// callers (#1151 review): the open says to open the database on a thread with a larger stack,
    /// while binding on first use, which runs inside a statement, raises the exhausted-stack
    /// signal itself, which the session reports as COHSQLE004 like any walk out of stack (see
    /// the statement tests above), never advice about opening the database.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: binding out of stack fails the open with advice and a statement with COHSQLE004")]
    public async Task Bind_OnSmallStack_ShouldAdviseOnlyTheOpen()
    {
        // Arrange: a stored CHECK at the limit.
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using (var session = await database.CreateSessionAsync(CancellationToken.None))
        {
            await session.ExecuteAsync($"CREATE TABLE c (qty INT, CONSTRAINT ck CHECK ({string.Concat(Enumerable.Repeat("- ", Limit - 2))}qty > 0))");
        }
        var table = Table(database, "c");
        RunOnThread(8 * 1024, () => new SqlBoundTableCache(database.Catalog).Get(table)).Failure.ShouldBeNull();

        // Act
        var open = NearStackLimit.Run(() => { new SqlBoundTableCache(database.Catalog).BindCatalog(); return true; });
        var firstUse = NearStackLimit.Run(() => new SqlBoundTableCache(database.Catalog).Get(table));

        // Assert
        var openFailure = open.Failure.ShouldBeOfType<DatabaseException>();
        openFailure.Message.ShouldStartWith("CHECK constraint 'ck' on table 'dbo.c' cannot be loaded on this thread", Case.Sensitive);
        openFailure.Message.ShouldEndWith("The catalog is not damaged. Open the database on a thread with a larger stack.", Case.Sensitive);
        openFailure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
        var firstUseFailure = firstUse.Failure.ShouldBeOfType<InsufficientExecutionStackException>();
        firstUseFailure.Message.ShouldStartWith("CHECK constraint 'ck' on table 'dbo.c' cannot be loaded on this thread", Case.Sensitive);
        firstUseFailure.Message.ShouldNotContain("Open the database");
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
        => await ReadScalarAsync(await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

    private static async Task<object?> ReadScalarAsync(QueryResult result)
    {
        await using var rows = result.ShouldBeAssignableTo<QueryResultSet>();
        object? value = null;
        int count = 0;
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        {
            value = row.GetValue(0);
            count++;
        }
        count.ShouldBe(1);
        return value;
    }

    // Parsed here, with ample stack, so only the statement's execution runs short of it.
    private static SqlQueryRequest Request(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return new SqlQueryRequest(statement);
    }

    private static SqlExpression Projection(string expression)
        => Request($"SELECT {expression} FROM t").Statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression;

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
    /// Runs work on a dedicated thread after using up its stack to a few KB above the point where
    /// <see cref="RuntimeHelpers.EnsureSufficientExecutionStack"/> starts to throw. The point is
    /// measured on that thread with the runtime's own check, so it holds whatever the thread's
    /// size, the platform, the build configuration or the reserve the runtime keeps below it.
    /// </summary>
    /// <remarks>
    /// The headroom, about three frames of padding, is more than a walker spends before its first
    /// check and far less than a walk of a 128-level tree, so a walker that checks as it descends
    /// throws within the first levels. One that did not check would carry on into the runtime's
    /// reserve, which a 128-level walk fits inside, and complete: the test fails, not the process.
    /// </remarks>
    private static class NearStackLimit
    {
        private const int FrameBytes = 1024;
        private const int HeadroomFrames = 3;

        /// <summary>Runs <paramref name="work"/> with a few KB of stack left before the check fails.</summary>
        /// <param name="work">The work.</param>
        /// <returns>Its result, or the exception it threw.</returns>
        internal static (T? Result, Exception? Failure) Run<T>(Func<T> work)
        {
            T? result = default;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                int frames = Descend(0, null);
                Descend(Math.Max(0, frames - HeadroomFrames), () =>
                {
                    try
                    {
                        result = work();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                });
            });
            thread.Start();
            thread.Join();
            return (result, failure);
        }

        // One method measures (work is null: how many more frames still pass the check) and
        // descends (framesLeft frames, then work), so every frame of both is the same size;
        // AggressiveOptimization keeps tiering from changing that size between the two.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static int Descend(int framesLeft, Action? work)
        {
            Span<byte> frame = stackalloc byte[FrameBytes];
            frame[^1] = 1;
            int depth;
            if (work is null)
            {
                depth = RuntimeHelpers.TryEnsureSufficientExecutionStack() ? Descend(0, null) + 1 : 0;
            }
            else if (framesLeft > 0)
            {
                depth = Descend(framesLeft - 1, work);
            }
            else
            {
                work();
                depth = 0;
            }

            // Read after the call, so the frame stays allocated across it.
            return depth + frame[^1] - 1;
        }
    }
}
