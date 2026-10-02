using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The expression nesting limit against the live engine (#1151, owner decision of 2026-10-01: a
/// happy medium between SQL Server and PostgreSQL). A 200,000-term expression used to overflow
/// the stack, which .NET cannot catch: one statement ended the process, and over the wire every
/// session of the server with it. Only genuine nesting counts now: an AND/OR chain of any length
/// is one level and executes, text nested deeper than the engine's configured limit (256 by
/// default) is a parse failure, a statement within the limit that runs out of stack fails with
/// <c>COHSQLE004</c>, and in every case the session, and the server, keep serving.
/// </summary>
/// <remarks>
/// <para>
/// A level of parentheses, calls, CASE or CAST costs the parser about 6 KB of stack in the debug
/// build these tests run in, so the deepest text the default limit accepts needs more than the
/// 1.5 MB a default thread has; statements at the limit therefore run on a thread with ample
/// stack (<see cref="OnLargeStack{T}"/>). A release build spends about 1 KB per level.
/// </para>
/// <para>
/// A tree deeper than the parser builds needs the language package's internal constructors,
/// which this assembly does not reach. The stack checks are proven instead by running a tree of
/// 128 levels with almost no stack left (<see cref="NearStackLimit"/>): a walker that checks as it
/// descends throws long before the tree ends, and one that does not check would complete inside
/// the runtime's reserve, failing the test instead of the process.
/// </para>
/// </remarks>
public sealed class SqlExpressionDepthExecutionTests : IDisposable
{
    private const int Limit = SqlQueryParserOptions.DefaultExpressionNestingLimit;
    private const int Hostile = 200_000;
    private const string StatementTooComplex = "COHSQLE004";

    /// <summary>The depth of the trees the near-stack-limit walks use: well inside the runtime's reserve.</summary>
    private const int WalkDepth = 128;

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-expression-depth", Guid.NewGuid().ToString("N"));

    /// <summary>Statements whose expressions nest exactly 256 levels, or that chain 10,000 terms, and the value each returns.</summary>
    public static TheoryData<string, object> AtLimit => new()
    {
        // 256 terms; each + is a level over the chain before it.
        { $"SELECT {Chain("1", " + ", Limit)} FROM t", (long)Limit },
        // 255 signs over a column: negation computes in BIGINT, an odd count of signs negates.
        { $"SELECT {Repeat("- ", Limit - 1)}id FROM t", -1L },
        { $"SELECT {Repeat("ABS(", Limit - 1)}id{Close(Limit - 1)} FROM t", 1L },
        { $"SELECT {Repeat("CASE WHEN TRUE THEN ", Limit - 1)}id{Repeat(" END", Limit - 1)} FROM t", 1 },
        // 256 grouping parentheses add no level to the tree.
        { $"SELECT {Open(Limit)}id{Close(Limit)} FROM t", 1 },
        // 255 comparisons in right-nested AND groups: 254 nested chains over a comparison.
        { $"SELECT id FROM t WHERE {RightNested("id <> 0", "AND", Limit - 1)}", 1 },
        // A flat chain is one level however long.
        { $"SELECT id FROM t WHERE {Chain("id <> 0", " AND ", 10_000)}", 1 },
        { $"SELECT id FROM t WHERE {Chain("id = 0", " OR ", 9_999)} OR id = 1", 1 },
    };

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a statement at the 256-level limit, or with a 10,000-term chain, executes")]
    [MemberData(nameof(AtLimit))]
    public void ExecuteAsync_AtLimit_ShouldExecute(string sql, object expected)
    {
        // Act
        var value = OnLargeStack(async () =>
        {
            await using var engine = CreateEngine();
            await using var session = await SeedAsync(engine);
            return await ScalarAsync(session, sql);
        });

        // Assert
        value.ShouldBe(expected);
    }

    /// <summary>One level past the limit and the adversarial 200,000 terms are parse failures on both session seams.</summary>
    /// <param name="terms">The number of terms in a <c>1 + 1 + ...</c> chain.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: an expression past the limit is SQL0006 before anything executes")]
    [InlineData(Limit)]
    [InlineData(Hostile)]
    public async Task ExecuteAsync_PastLimit_ShouldFailToParseAndKeepTheSession(int terms)
    {
        // Arrange: the comparison adds a level over the chain.
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        string sql = $"DELETE FROM t WHERE id = {Chain("1", " + ", terms)}";

        // Act
        var text = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync(sql).AsTask());
        var typed = await session.ExecuteAsync(new SqlQueryRequest((SqlQueryStatement)new SqlQueryParser().Parse(sql)));

        // Assert
        text.Message.ShouldStartWith("SQL parse error SQL0006: Expression nesting exceeds the supported limit of 256 levels.", Case.Sensitive);
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
    [InlineData("nested-or")]
    public void ExecuteAsync_HostileShape_ShouldFailToParse(string shape)
    {
        // Arrange
        string sql = shape switch
        {
            "parentheses" => $"SELECT {Open(Hostile)}id{Close(Hostile)} FROM t",
            "signs" => $"SELECT {Repeat("- ", Hostile)}id FROM t",
            "not" => $"SELECT id FROM t WHERE {Repeat("NOT ", Hostile)}TRUE",
            "functions" => $"SELECT {Repeat("ABS(", Hostile)}id{Close(Hostile)} FROM t",
            _ => $"UPDATE t SET id = 2 WHERE {RightNested("id = 0", "OR", Hostile)}",
        };

        // Act
        var (failure, after) = OnLargeStack(async () =>
        {
            await using var engine = CreateEngine();
            await using var session = await SeedAsync(engine);
            var failure = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync(sql).AsTask());
            return (failure, await ScalarAsync(session, "SELECT id FROM t"));
        });

        // Assert
        failure.Message.ShouldStartWith("SQL parse error SQL0006:", Case.Sensitive);
        after.ShouldBe(1);
    }

    /// <summary>
    /// A 10,000-term AND or OR predicate is one n-ary node: it parses, plans and executes in every
    /// statement kind, on the text seam and as a typed request, on an ordinary thread.
    /// </summary>
    /// <param name="keyword">The chain's operator.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a 10,000-term AND or OR predicate plans and executes in every statement kind")]
    [InlineData("AND")]
    [InlineData("OR")]
    public async Task ExecuteAsync_TenThousandTermChain_ShouldPlanAndExecute(string keyword)
    {
        // Arrange: rows 1..20; the chain selects rows 1..10 either way.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("chains");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE n (id INT NOT NULL, v INT)");
        await session.ExecuteAsync("INSERT INTO n (id, v) VALUES " + string.Join(", ", Enumerable.Range(1, 20).Select(id => $"({Number(id)}, {Number(id)})")));
        string predicate = keyword == "AND"
            ? string.Join(" AND ", Enumerable.Range(1, 9_999).Select(term => $"v <> {Number(term + 100)}")) + " AND id <= 10"
            : string.Join(" OR ", Enumerable.Range(1, 9_999).Select(term => $"v = {Number(term + 100)}")) + " OR id <= 10";

        // Act
        var counted = await ScalarAsync(session, $"SELECT COUNT(*) FROM n WHERE {predicate}");
        var typed = await ReadScalarAsync(await session.ExecuteAsync(Request($"SELECT COUNT(*) FROM n WHERE {predicate}")));
        var updated = await session.ExecuteAsync($"UPDATE n SET v = 0 WHERE {predicate}");
        var grouped = await ScalarAsync(session, $"SELECT COUNT(*) FROM n GROUP BY v HAVING ({predicate.Replace("id <= 10", "COUNT(*) > 1", StringComparison.Ordinal)}) AND v = 0");
        var deleted = await session.ExecuteAsync($"DELETE FROM n WHERE {predicate}");

        // Assert
        counted.ShouldBe(10L);
        typed.ShouldBe(10L);
        updated.AffectedCount.ShouldBe(10);
        grouped.ShouldBe(10L);
        deleted.AffectedCount.ShouldBe(10);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM n")).ShouldBe(10L);
    }

    /// <summary>
    /// The n-ary chain computes what the nested binary operators computed: SQL three-valued logic
    /// for every combination of TRUE, FALSE and NULL, the same as a chain nested in a later
    /// position (still two nodes) and one grouped in first position (merged into one).
    /// </summary>
    /// <param name="keyword">The chains' operator.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: AND/OR chains keep three-valued logic for every operand combination")]
    [InlineData("AND")]
    [InlineData("OR")]
    public async Task LogicalChain_ShouldKeepThreeValuedLogic(string keyword)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        string[] values = ["TRUE", "FALSE", "NULL"];

        foreach (string a in values)
        {
            foreach (string b in values)
            {
                foreach (string c in values)
                {
                    bool? expected = Fold(keyword, [Value(a), Value(b), Value(c)]);

                    // Act
                    var row = await RowAsync(session,
                        $"SELECT {a} {keyword} {b} {keyword} {c}, {a} {keyword} ({b} {keyword} {c}), ({a} {keyword} {b}) {keyword} {c}, " +
                        $"{Chain(a, $" {keyword} ", 50)} {keyword} {Chain(b, $" {keyword} ", 50)} {keyword} {c} FROM t");

                    // Assert
                    row.ShouldBe(new object?[] { expected, expected, expected, expected }, $"{a} {keyword} {b} {keyword} {c}");
                }
            }
        }

        static bool? Value(string literal) => literal switch { "TRUE" => true, "FALSE" => false, _ => null };
    }

    /// <summary>
    /// The terms of a chain run first to last and the first deciding term ends it (the #1069
    /// contract): a guard anywhere before a division protects it, however long the chain, and a
    /// guard after it or an unknown term does not.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: AND/OR chains short-circuit left to right")]
    public async Task LogicalChain_ShouldShortCircuitLeftToRight()
    {
        // Arrange: d is 0 on the first row.
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        await session.ExecuteAsync("CREATE TABLE g (id INT, d INT)");
        await session.ExecuteAsync("INSERT INTO g VALUES (1, 0), (2, 2), (3, 20)");
        string terms = Chain("id > 0", " AND ", 5_000);
        string falseTerms = Chain("id < 0", " OR ", 5_000);

        // Act
        var guardedAnd = await ScalarAsync(session, $"SELECT COUNT(*) FROM g WHERE {terms} AND d <> 0 AND 10 / d >= 1 AND {terms}");
        var guardedOr = await ScalarAsync(session, $"SELECT COUNT(*) FROM g WHERE {falseTerms} OR d = 0 OR 10 / d >= 1 OR {falseTerms}");
        var guardAfter = await Should.ThrowAsync<DatabaseException>(() =>
            session.ExecuteAsync($"SELECT COUNT(*) FROM g WHERE {terms} AND 10 / d >= 1 AND d <> 0").AsTask());
        var unknownAnd = await Should.ThrowAsync<DatabaseException>(() =>
            session.ExecuteAsync("SELECT COUNT(*) FROM g WHERE NULL AND 10 / d >= 1").AsTask());
        var unknownOr = await Should.ThrowAsync<DatabaseException>(() =>
            session.ExecuteAsync("SELECT COUNT(*) FROM g WHERE NULL OR id < 0 OR 10 / d >= 1").AsTask());
        var faultFirst = await Should.ThrowAsync<DatabaseException>(() =>
            session.ExecuteAsync("SELECT COUNT(*) FROM g WHERE 10 / d >= 1 OR TRUE").AsTask());

        // Assert
        guardedAnd.ShouldBe(1L);
        guardedOr.ShouldBe(2L);
        foreach (var fault in new[] { guardAfter, unknownAnd, unknownOr, faultFirst })
        {
            fault.Message.ShouldStartWith("COHSQLE001:", Case.Sensitive);
        }
    }

    /// <summary>
    /// The planner reads every term of a conjunction at one level: an indexed equality anywhere
    /// in a long chain, or inside a nested conjunction, still seeks, and a join still seeks on an
    /// equality inside a long ON chain.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: an indexed term inside a long chain still seeks")]
    public async Task LongConjunction_ShouldStillSeekAndJoinSeek()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("seeks");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT NOT NULL, v INT)");
        await session.ExecuteAsync("CREATE INDEX ix_id ON s (id)");
        await session.ExecuteAsync("INSERT INTO s (id, v) VALUES " + string.Join(", ", Enumerable.Range(0, 100).Select(id => $"({Number(id)}, {Number(id)})")));
        await session.ExecuteAsync("CREATE TABLE r (k INT)");
        await session.ExecuteAsync("INSERT INTO r VALUES (42), (7)");
        string terms = Chain("v >= 0", " AND ", 2_000);

        // Act
        var flat = await ScalarAsync(session, $"SELECT v FROM s WHERE {terms} AND id = 42");
        var flatMetrics = Metrics(session);
        var nested = await ScalarAsync(session, $"SELECT v FROM s WHERE {terms} AND (v < 1000 AND id = 42)");
        var nestedMetrics = Metrics(session);
        var joined = await RowsAsync(session, $"SELECT r.k, s.v FROM r JOIN s ON {Chain("s.v >= 0", " AND ", 1_000)} AND s.id = r.k AND s.v < 1000 ORDER BY r.k");
        var joinMetrics = Metrics(session);

        // Assert
        flat.ShouldBe(42);
        flatMetrics.AccessPath.ShouldBe("seek:ix_id");
        flatMetrics.RecordsExamined.ShouldBe(1);
        nested.ShouldBe(42);
        nestedMetrics.AccessPath.ShouldBe("seek:ix_id");
        joined.Select(row => (row[0], row[1])).ShouldBe([(7, 7), (42, 42)]);
        joinMetrics.AccessPath.ShouldBe("join-seek:ix_id");
    }

    /// <summary>
    /// The limit is the engine's: its text seam parses with it, a typed request another parser
    /// accepted is refused when it nests deeper, and a flat chain of any length still runs.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a configured limit applies to the text seam and to typed requests")]
    public async Task Engine_ConfiguredLimit_ShouldApplyToEverySeam()
    {
        // Arrange
        await using var engine = CreateEngine(limit: 32);
        await using var session = await SeedAsync(engine);

        // Act
        var atLimit = await ScalarAsync(session, $"SELECT {Chain("1", " + ", 32)} FROM t");
        var pastLimit = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync($"SELECT {Chain("1", " + ", 33)} FROM t").AsTask());
        var typedAtLimit = await ReadScalarAsync(await session.ExecuteAsync(Request($"SELECT {Chain("1", " + ", 32)} FROM t")));
        var typedPastLimit = await session.ExecuteAsync(Request($"SELECT {Chain("1", " + ", 40)} FROM t"));
        var typedParentheses = await session.ExecuteAsync(Request($"SELECT {Open(33)}id{Close(33)} FROM t"));
        var flat = await ScalarAsync(session, $"SELECT COUNT(*) FROM t WHERE {Chain("id = 1", " OR ", 1_000)}");

        // Assert
        atLimit.ShouldBe(32L);
        pastLimit.Message.ShouldBe("SQL parse error SQL0006: Expression nesting exceeds the supported limit of 32 levels.");
        typedAtLimit.ShouldBe(32L);
        foreach (var (result, depth) in new[] { (typedPastLimit, 40), (typedParentheses, 33) })
        {
            result.Status.ShouldBe(QueryResultStatus.Error);
            var diagnostic = result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("SQL0006");
            diagnostic.Message.ShouldBe($"Expression nesting of {Number(depth)} levels exceeds this engine's limit of 32 levels.");
        }
        flat.ShouldBe(1L);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM t")).ShouldBe(1L);
    }

    /// <summary>
    /// A subquery taken out of a parsed statement and sent as a typed request of its own carries
    /// no parser's measure. It is held to the depth of its own tree, which the engine's walks
    /// recurse over, so the engine's limit holds for it as for the statement it came from.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a subquery taken out of a statement meets the engine's limit")]
    public async Task Engine_ExtractedSubquery_ShouldMeetTheLimit()
    {
        // Arrange: 40 levels of NOT are 41 with their leaf, past the engine's 32; 20 are within it.
        await using var engine = CreateEngine(limit: 32);
        await using var session = await SeedAsync(engine);
        var deep = Request($"SELECT id FROM t WHERE id IN (SELECT id FROM t WHERE {Repeat("NOT ", 40)}TRUE)");
        var shallow = Request($"SELECT id FROM t WHERE id IN (SELECT id FROM t WHERE {Repeat("NOT ", 20)}TRUE)");
        static SqlQueryRequest Extract(SqlQueryRequest request) => new(new SqlQueryStatement(request.Statement.SqlExpression
            .ShouldBeOfType<SqlSelectExpression>().Where.ShouldBeOfType<SqlInExpression>().Subquery.ShouldNotBeNull()));

        // Act
        var whole = await session.ExecuteAsync(deep);
        var extracted = await session.ExecuteAsync(Extract(deep));
        var withinLimit = await ReadScalarAsync(await session.ExecuteAsync(Extract(shallow)));

        // Assert: the IN node is a level over its subquery.
        foreach (var (result, depth) in new[] { (whole, 42), (extracted, 41) })
        {
            result.Status.ShouldBe(QueryResultStatus.Error);
            var diagnostic = result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("SQL0006");
            diagnostic.Message.ShouldBe($"Expression nesting of {Number(depth)} levels exceeds this engine's limit of 32 levels.");
        }
        withinLimit.ShouldBe(1);
    }

    /// <summary>
    /// A typed caller parses with the engine's limit through the public <c>FromSql</c> overload
    /// that takes parser options, so the typed path accepts exactly what the engine accepts as
    /// text. The overload without options stays at the default 256, and a limit outside 32..4096
    /// is refused as it is everywhere else.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: FromSql with the engine's limit accepts what the engine's text seam accepts")]
    public async Task FromSql_WithEngineLimit_ShouldMatchTheTextSeam()
    {
        // Arrange
        const int EngineLimit = 1000;
        await using var engine = CreateEngine(limit: EngineLimit);
        await using var session = await SeedAsync(engine);
        string sql = $"SELECT {Chain("1", " + ", 400)} FROM t";
        var options = new SqlQueryParserOptions { ExpressionNestingLimit = EngineLimit };

        // Act
        var text = OnLargeStack(() => ScalarAsync(session, sql));
        var typed = OnLargeStack(async () => await ReadScalarAsync(await session.ExecuteAsync(SqlQueryRequest.FromSql(sql, null, options))));
        var defaultLimit = Should.Throw<DatabaseParseException>(() => SqlQueryRequest.FromSql(sql));
        var outOfRange = Should.Throw<ArgumentOutOfRangeException>(() => SqlQueryRequest.FromSql(sql, null,
            new SqlQueryParserOptions { ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit + 1 }));

        // Assert
        text.ShouldBe(400L);
        typed.ShouldBe(400L);
        defaultLimit.Message.ShouldBe("SQL parse error SQL0006: Expression nesting exceeds the supported limit of 256 levels.");
        outOfRange.ParamName.ShouldBe("options");
    }

    /// <summary>
    /// A high limit admits deeper statements, not more stack. Where the thread has room the
    /// statement runs; where it does not, the statement fails with COHSQLE004, whether it ran out
    /// while parsing or while planning and evaluating, and the session keeps serving.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: under a high limit a statement runs or fails with COHSQLE004, never a crash")]
    public async Task Engine_HighLimit_ShouldRunOrFailWithStatementTooComplex()
    {
        // Arrange
        await using var engine = CreateEngine(limit: SqlQueryParserOptions.MaximumExpressionNestingLimit);
        await using var session = await SeedAsync(engine);
        string deepChain = $"SELECT {Chain("1", " + ", 3_000)} FROM t";
        string deepParentheses = $"SELECT {Open(3_000)}id{Close(3_000)} FROM t";

        // Act: the chain parses in a loop, so a small thread runs out in the walks after the parse;
        // the parentheses recurse in the parser itself.
        var ample = RunOnThread(64 * 1024, () => ScalarAsync(session, deepChain).GetAwaiter().GetResult());
        var walkOut = RunOnThread(256, () => ScalarAsync(session, deepChain).GetAwaiter().GetResult());
        var parseOut = RunOnThread(1024, () => ScalarAsync(session, deepParentheses).GetAwaiter().GetResult());

        // Assert
        ample.Failure.ShouldBeNull();
        ample.Result.ShouldBe(3_000L);
        AssertStatementTooComplex(walkOut.Failure.ShouldBeAssignableTo<DatabaseException>().ShouldNotBeNull());
        var parseFailure = parseOut.Failure.ShouldBeAssignableTo<DatabaseException>().ShouldNotBeNull();
        AssertStatementTooComplex(parseFailure);
        parseFailure.InnerException!.Message.ShouldStartWith("SQL parse error SQL0007:", Case.Sensitive);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM t")).ShouldBe(1L);
    }

    /// <summary>
    /// A limit outside 32..4096 is refused by the engine and fails every builder in
    /// <see cref="IDatabaseEngineBuilder.Build"/>, not in the setter: the engine's own builder and
    /// one written outside the repository, which reports the engine's range check because it
    /// builds through <see cref="SqlDatabaseEngine.Create"/>.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: an engine, and every builder's Build, refuses a limit outside 32..4096")]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(SqlQueryParserOptions.MinimumExpressionNestingLimit - 1)]
    [InlineData(SqlQueryParserOptions.MaximumExpressionNestingLimit + 1)]
    [InlineData(int.MaxValue)]
    public void Create_LimitOutOfRange_ShouldThrow(int limit)
    {
        // Arrange
        ISqlDatabaseEngineBuilder own = SqlDatabaseEngine.CreateBuilder();
        ISqlDatabaseEngineBuilder external = new ExternalEngineBuilder();
        own.ExpressionNestingLimit = limit;
        external.ExpressionNestingLimit = limit;

        // Act
        var direct = Should.Throw<ArgumentOutOfRangeException>(() =>
            SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { ExpressionNestingLimit = limit }));
        var built = Should.Throw<ArgumentOutOfRangeException>(() => own.Build());
        var externallyBuilt = Should.Throw<ArgumentOutOfRangeException>(() => external.Build());

        // Assert
        own.ExpressionNestingLimit.ShouldBe(limit);
        external.ExpressionNestingLimit.ShouldBe(limit);
        foreach (var failure in new[] { direct, built, externallyBuilt })
        {
            failure.ParamName.ShouldBe("options");
            failure.ActualValue.ShouldBe(limit);
            failure.Message.ShouldStartWith("ExpressionNestingLimit must be between 32 and 4096 levels.", Case.Sensitive);
        }
    }

    /// <summary>
    /// The builder carries the limit to the engine, which captures it when it is created: changing
    /// the options object afterwards changes nothing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: the builder's limit reaches the engine, which keeps the value it was created with")]
    public async Task Builder_Limit_ShouldReachTheEngineAndStayFixed()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.ExpressionNestingLimit = 40;
        await using var built = (SqlDatabaseEngine)builder.Build();
        var options = new SqlDatabaseEngineOptions { ExpressionNestingLimit = 40 };
        await using var created = SqlDatabaseEngine.Create(options);
        options.ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit;

        foreach (var engine in new[] { built, created })
        {
            await using var session = await SeedAsync(engine);

            // Act
            var atLimit = await ScalarAsync(session, $"SELECT {Chain("1", " + ", 40)} FROM t");
            var pastLimit = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync($"SELECT {Chain("1", " + ", 41)} FROM t").AsTask());

            // Assert
            atLimit.ShouldBe(40L);
            pastLimit.Message.ShouldContain("supported limit of 40 levels", Case.Sensitive);
        }

        builder.ExpressionNestingLimit.ShouldBe(40);
        new SqlDatabaseEngineOptions().ExpressionNestingLimit.ShouldBe(Limit);
    }

    /// <summary>
    /// The builder's limit is an ordinary member of an interface meant to be implemented outside
    /// the repository (owner decision of 2026-10-02). The interface gives it no body to fall back
    /// on, so a builder that leaves it out does not compile and none can drop the value it is given.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: the builder interface requires every implementer to supply the limit")]
    public void BuilderInterface_Limit_ShouldBeRequiredOfEveryImplementer()
    {
        // Act
        var property = typeof(ISqlDatabaseEngineBuilder).GetProperty(nameof(ISqlDatabaseEngineBuilder.ExpressionNestingLimit)).ShouldNotBeNull();
        var withBodies = typeof(ISqlDatabaseEngineBuilder)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsAbstract)
            .Select(method => method.Name)
            .ToArray();

        // Assert
        property.PropertyType.ShouldBe(typeof(int));
        property.GetMethod.ShouldNotBeNull().IsAbstract.ShouldBeTrue();
        property.SetMethod.ShouldNotBeNull().IsAbstract.ShouldBeTrue();
        withBodies.ShouldBeEmpty();
    }

    /// <summary>
    /// A builder written outside the repository supplies the limit itself, reports the engine's
    /// default until it is set, and carries the value it is given through
    /// <see cref="IDatabaseEngineBuilder.Build"/> to the engine, which parses with it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a custom builder's limit reaches the engine through Build")]
    public async Task ExternalBuilder_Limit_ShouldReachTheEngineThroughBuild()
    {
        // Arrange
        ISqlDatabaseEngineBuilder builder = new ExternalEngineBuilder();
        int unset = builder.ExpressionNestingLimit;
        builder.ExpressionNestingLimit = 40;

        // Act
        await using var engine = builder.Build().ShouldBeOfType<SqlDatabaseEngine>();
        await using var session = await SeedAsync(engine);
        var atLimit = await ScalarAsync(session, $"SELECT {Chain("1", " + ", 40)} FROM t");
        var pastLimit = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync($"SELECT {Chain("1", " + ", 41)} FROM t").AsTask());
        var typedPastLimit = await session.ExecuteAsync(Request($"SELECT {Chain("1", " + ", 41)} FROM t"));

        // Assert
        unset.ShouldBe(Limit);
        builder.ExpressionNestingLimit.ShouldBe(40);
        atLimit.ShouldBe(40L);
        pastLimit.Message.ShouldBe("SQL parse error SQL0006: Expression nesting exceeds the supported limit of 40 levels.");
        typedPastLimit.Status.ShouldBe(QueryResultStatus.Error);
        var diagnostic = typedPastLimit.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("SQL0006");
        diagnostic.Message.ShouldBe($"Expression nesting of {Number(41)} levels exceeds this engine's limit of 40 levels.");
    }

    /// <summary>
    /// A statement whose walk runs out of stack fails as that statement, with COHSQLE004 and the
    /// exhausted-stack signal inside, and none of it takes effect; the same statement then runs
    /// with ample stack. Every stack check leads here: a deeply backtracking LIKE, a statement
    /// within a high limit, or a statement run on a thread created with a small stack.
    /// </summary>
    /// <param name="sql">A statement whose predicate or value nests exactly 128 levels once <c>{0}</c> is filled.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a statement that runs out of stack fails with COHSQLE004 and keeps the session")]
    [InlineData("SELECT COUNT(*) FROM t WHERE id < {0}")]
    [InlineData("UPDATE t SET name = 'z' WHERE id < {0}")]
    [InlineData("DELETE FROM t WHERE id < {0}")]
    [InlineData("INSERT INTO t VALUES ({0} - 126, 'z')")]
    [InlineData("SELECT COUNT(*) FROM t WHERE id > 0 AND id <> 7 AND id < {0}")]
    public async Task ExecuteAsync_StackExhausted_ShouldFailWithStatementTooComplex(string sql)
    {
        // Arrange: 127 terms under a comparison, 128 levels.
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var request = Request(sql.Replace("{0}", Chain("1", " + ", WalkDepth - 1), StringComparison.Ordinal));

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
        var delete = Request($"DELETE FROM t WHERE id < {Chain("1", " + ", WalkDepth - 1)}");

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
        var create = Request($"CREATE TABLE c (qty INT, CONSTRAINT ck CHECK ({Repeat("- ", WalkDepth - 2)}qty > 0))");

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
    /// of a 128-level chain, and of a long AND chain over it, completes; with a few KB left each
    /// one throws.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: every expression walker checks the stack as it recurses")]
    public void Walkers_NearStackLimit_ShouldThrowInsufficientStack()
    {
        // Arrange: two separately parsed trees of each shape, so equivalence walks both.
        string chain = Chain("1", " + ", WalkDepth);
        string conjunction = $"{Chain("TRUE", " AND ", 1_000)} AND {Chain("1", " + ", WalkDepth - 2)} = 126";
        var columns = new[] { new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32)) };
        var evaluator = new SqlExpressionEvaluator(columns, null);

        foreach (string expression in new[] { chain, conjunction })
        {
            var deep = Projection(expression);
            var twin = Projection(expression);
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

            evaluator.Evaluate(deep, [1]).ShouldBe(expression == chain ? (object)(long)WalkDepth : true);
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
    /// <param name="step">One step of a pattern that recurses once per step.</param>
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
    /// The DDL parses with the engine's limit, and the canonical text stored for a CHECK at the
    /// limit parses back, so a definition the engine stores always reopens. A sign applied to a
    /// sign is stored parenthesized, which is the case that adds the most parentheses.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a CHECK at the limit is stored, reopens and is enforced")]
    public void Check_AtLimit_ShouldPersistReopenAndEnforce()
    {
        // Arrange: 254 signs over a column, compared: 256 levels.
        string predicate = Repeat("- ", Limit - 2) + "qty > 0";
        string tooDeep = Repeat("- ", Limit - 1) + "qty > 0";

        // Act
        var (refused, canonical, reopened) = OnLargeStack(async () =>
        {
            DatabaseParseException refused;
            string canonical;
            await using (var engine = CreateEngine(_rootPath))
            {
                var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await session.ExecuteAsync($"CREATE TABLE c (qty INT, CONSTRAINT ck CHECK ({predicate}))");
                refused = await Should.ThrowAsync<DatabaseParseException>(() =>
                    session.ExecuteAsync($"CREATE TABLE d (qty INT, CONSTRAINT ck CHECK ({tooDeep}))").AsTask());
                canonical = Table(database, "c").Constraints.Single(constraint => constraint.Name == "ck").CheckExpression!;
                Table(database, "d", exists: false);
            }

            await using var reopenedEngine = CreateEngine(_rootPath);
            var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("db");
            await using var reopenedSession = await reopened.CreateSessionAsync(CancellationToken.None);
            (await reopenedSession.ExecuteAsync("INSERT INTO c VALUES (1)")).AffectedCount.ShouldBe(1);
            await Should.ThrowAsync<SqlConstraintViolationException>(() => reopenedSession.ExecuteAsync("INSERT INTO c VALUES (-1)").AsTask());
            return (refused, canonical, Table(reopened, "c").Constraints.Single(constraint => constraint.Name == "ck").CheckExpression);
        });

        // Assert
        refused.Message.ShouldContain("SQL0006", Case.Sensitive);
        canonical.ShouldStartWith("-(-(-(", Case.Sensitive);
        canonical.Count(character => character == '(').ShouldBe(Limit - 3);
        reopened.ShouldBe(canonical);
    }

    /// <summary>
    /// The limit decides which statements an engine accepts, not which databases it opens: a CHECK
    /// an engine with a high limit stored opens, and is enforced, under the lowest limit, which
    /// would refuse the same text as a statement.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a definition stored under a high limit opens under a low one")]
    public async Task Check_StoredUnderHighLimit_ShouldOpenUnderLowLimit()
    {
        // Arrange: 100 levels, beyond the lowest limit of 32.
        string predicate = $"qty + {Chain("1", " + ", 98)} > 100";
        await using (var engine = CreateEngine(_rootPath, limit: 1_000))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync($"CREATE TABLE c (qty INT, CONSTRAINT ck CHECK ({predicate}))");
        }

        // Act
        await using var lowEngine = CreateEngine(_rootPath, limit: SqlQueryParserOptions.MinimumExpressionNestingLimit);
        var reopened = await lowEngine.OpenDatabaseAsync("db");
        await using var reopenedSession = await reopened.CreateSessionAsync(CancellationToken.None);
        var inserted = await reopenedSession.ExecuteAsync("INSERT INTO c VALUES (3)");
        var violation = await Should.ThrowAsync<SqlConstraintViolationException>(() => reopenedSession.ExecuteAsync("INSERT INTO c VALUES (2)").AsTask());
        var restated = await Should.ThrowAsync<DatabaseParseException>(() =>
            reopenedSession.ExecuteAsync($"CREATE TABLE d (qty INT, CONSTRAINT ck CHECK ({predicate}))").AsTask());

        // Assert
        inserted.AffectedCount.ShouldBe(1);
        violation.Message.ShouldContain("ck", Case.Sensitive);
        restated.Message.ShouldContain("supported limit of 32 levels", Case.Sensitive);
    }

    /// <summary>
    /// Canonical text within the limits that a thread is too small to read back is not catalog
    /// damage: the signal names the definition and says so instead of sending the operator to a
    /// backup, and the same text loads on a thread with more stack.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Nesting: a definition read on a thread too small for it is not reported as catalog damage")]
    public void Load_OnSmallStack_ShouldNotReportDamage()
    {
        // Arrange: the stored text of 254 signs over a column, compared, 256 levels deep.
        string canonical = Repeat("-(", Limit - 3) + "-qty" + Close(Limit - 3) + " > 0";
        const string subject = "CHECK constraint 'ck' on table 'dbo.c'";

        // Act: reading the deepest text back costs the parser about 1.4 MB of stack in a debug
        // build, more than a default thread spares, so the ample run gets 8 MB.
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
        // Arrange: a stored CHECK of 128 levels.
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using (var session = await database.CreateSessionAsync(CancellationToken.None))
        {
            await session.ExecuteAsync($"CREATE TABLE c (qty INT, CONSTRAINT ck CHECK ({Repeat("- ", WalkDepth - 2)}qty > 0))");
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
    /// Over the wire the hostile statement is refused: a ParseFailure (SQL0006) where the
    /// server's thread reaches the limit, which a release build always does, or COHSQLE004 where
    /// the thread runs out of stack first, as 256 parentheses can in a debug build. Either way the
    /// server stays up, the session that sent it stays ready on the same connection, and other
    /// connections are unaffected.
    /// </summary>
    /// <param name="statement">Which hostile statement to send.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Nesting: a 200,000-level statement is refused and keeps the server and the session")]
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
            : $"SELECT {Open(Hostile)}1{Close(Hostile)} FROM users";

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Assert: a chain is parsed in a loop and always reaches the limit.
        if (statement == "chain" || error.Code == ProtocolErrorCode.ParseFailure)
        {
            error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
            error.Message.ShouldContain("SQL0006", Case.Sensitive);
        }
        else
        {
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Message.ShouldStartWith(StatementTooComplex + ":", Case.Sensitive);
        }
        harness.Server.Context.Sessions.Count.ShouldBe(1);
        (await ScalarAsync(client, "SELECT 1 FROM users WHERE id = 1")).ShouldBe(1L);

        await using var other = await harness.DialAsync();
        await other.HandshakeAsync();
        (await ScalarAsync(other, "SELECT COUNT(*) FROM users")).ShouldBe(2L);
        harness.Server.Context.Sessions.Count.ShouldBe(2);
    }

    /// <summary>A 10,000-term predicate executes over the wire, where the server parses it with the engine's limit.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Nesting: a 10,000-term OR predicate executes over the wire")]
    public async Task Wire_TenThousandTermChain_ShouldExecute()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        string predicate = string.Join(" OR ", Enumerable.Range(3, 9_999).Select(id => $"id = {Number(id)}")) + " OR id = 2";

        // Act
        var count = await ScalarAsync(client, $"SELECT COUNT(*) FROM users WHERE {predicate}");

        // Assert
        count.ShouldBe(1L);
    }

    /// <summary>
    /// Over the wire, a statement within a high configured limit runs where the server's thread
    /// has the stack for it, and where it does not, it is COHSQLE004, an ExecutionFailure, not a
    /// ParseFailure: the text is valid, the thread ran out. 4,000 nested parentheses need a few MB
    /// to parse: more than the 1.5 MB a .NET thread has by default on Windows and macOS, less than
    /// the 8 MB it has on Linux. Either way the session stays ready.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Nesting: a statement within a high limit runs, or is COHSQLE004 over the wire")]
    public async Task Wire_ParseOutOfStack_ShouldRunOrReturnStatementTooComplex()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(configureEngine: options =>
            options.ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute,
            ProtocolExecuteMessage.Create($"SELECT {Open(4_000)}1{Close(4_000)} FROM users WHERE id = 1").Encode());
        var reply = (await client.ReadAsync(timeoutSeconds: 30)).ShouldNotBeNull();

        // Assert
        if (reply.Type == ProtocolMessageType.Error)
        {
            var error = ProtocolErrorMessage.Decode(reply.Payload.Span);
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Message.ShouldStartWith(StatementTooComplex + ": Statement too complex", Case.Sensitive);
        }
        else
        {
            reply.Type.ShouldBe(ProtocolMessageType.ResultHeader);
            DatabaseValueCodec.DecodeComponent((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.Span).ShouldBe(1L);
            await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        }
        (await ScalarAsync(client, "SELECT COUNT(*) FROM users")).ShouldBe(2L);
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

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static string Open(int count) => new('(', count);

    private static string Close(int count) => new(')', count);

    /// <summary><c>t AND (t AND (... t))</c>: <paramref name="count"/> terms, each chain nested in the one before.</summary>
    private static string RightNested(string term, string keyword, int count)
        => Repeat($"{term} {keyword} (", count - 1) + term + Close(count - 1);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>SQL three-valued AND or OR over the operands, folded left to right.</summary>
    private static bool? Fold(string keyword, bool?[] operands)
    {
        bool? result = operands[0];
        for (int index = 1; index < operands.Length; index++)
        {
            bool? right = operands[index];
            result = keyword == "AND"
                ? result == false || right == false ? false : result == true && right == true ? true : null
                : result == true || right == true ? true : result == false && right == false ? false : null;
        }

        return result;
    }

    /// <summary>
    /// Runs work on a thread with 8 MB of stack and returns its result, or rethrows its failure.
    /// Statements at the default limit need more than a default thread in a debug build.
    /// </summary>
    private static T OnLargeStack<T>(Func<Task<T>> work)
    {
        var (result, failure) = RunOnThread(8 * 1024, () => work().GetAwaiter().GetResult());
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

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

    private static SqlStatementMetrics Metrics(IDatabaseSession session)
        => ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull();

    private static SqlCatalogTable Table(SqlDatabaseInstance database, string name, bool exists = true)
    {
        bool found = database.Catalog.TryGetTable(SqlPlanner.DefaultSchema, name, out var table);
        found.ShouldBe(exists);
        return table!;
    }

    private static SqlDatabaseEngine CreateEngine(int limit = Limit) => CreateEngine(null, limit);

    // The background workers stay quiet for the test's lifetime, so a statement never waits on
    // one of them and resumes on a pool thread: the stack tests run each statement on a thread
    // of a known size, and its walks must stay there.
    private static SqlDatabaseEngine CreateEngine(string? rootPath, int limit = Limit)
    {
        var options = new SqlDatabaseEngineOptions
        {
            EngineName = "expression-depth",
            ExpressionNestingLimit = limit,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        };
        if (rootPath is not null)
        {
            options.RootPath = rootPath;
        }

        return SqlDatabaseEngine.Create(options);
    }

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

    private static async Task<object?[]> RowAsync(IDatabaseSession session, string sql)
        => (await RowsAsync(session, sql)).ShouldHaveSingleItem();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var rows = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>()!;
        var result = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                values[ordinal] = row.GetValue(ordinal);
            }
            result.Add(values);
        }
        return result;
    }

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

    // Parsed here, with ample stack and at the highest limit, so only the statement's execution
    // meets the engine's limit and runs short of stack.
    private static SqlQueryRequest Request(string sql)
    {
        var statement = RunOnThread(8 * 1024, () => (SqlQueryStatement)new SqlQueryParser(
            new SqlQueryParserOptions { ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit }).Parse(sql)).Result!;
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

    /// <summary>
    /// An <see cref="ISqlDatabaseEngineBuilder"/> written outside the repository, as the interface
    /// intends: it supplies every member itself, the expression nesting limit included, keeps them
    /// on a <see cref="SqlDatabaseEngineOptions"/> (so each reports the engine's default until it is
    /// set), and builds through the public <see cref="SqlDatabaseEngine.Create"/>, whose range check
    /// is the one its <see cref="Build"/> reports. Only the engine's own builder can hand worker and
    /// server products to a <see cref="SqlDatabaseEngine"/>, so this one refuses to compose them.
    /// </summary>
    private sealed class ExternalEngineBuilder : ISqlDatabaseEngineBuilder
    {
        // Quiet background workers, as CreateEngine keeps them.
        private readonly SqlDatabaseEngineOptions _options = new()
        {
            EngineName = "external-builder",
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        };
        private bool _buildAttempted;

        public string? EngineName { get => _options.EngineName; set => _options.EngineName = value; }

        public FileSystemPath? RootPath { get => _options.RootPath; set => _options.RootPath = value; }

        public StorageCommitDurability? Durability { get => _options.Durability; set => _options.Durability = value; }

        public ISqlStorageStrategy? StorageStrategy { get => _options.StorageStrategy; set => _options.StorageStrategy = value; }

        public TimeSpan GroupCommitWindow { get => _options.GroupCommitWindow; set => _options.GroupCommitWindow = value; }

        public TimeSpan CheckpointInterval { get => _options.CheckpointInterval; set => _options.CheckpointInterval = value; }

        public TimeSpan PageWriteBackInterval { get => _options.PageWriteBackInterval; set => _options.PageWriteBackInterval = value; }

        public int PageWriteBackBatchSize { get => _options.PageWriteBackBatchSize; set => _options.PageWriteBackBatchSize = value; }

        public TimeSpan MaintenanceInterval { get => _options.MaintenanceInterval; set => _options.MaintenanceInterval = value; }

        public int ExpressionNestingLimit { get => _options.ExpressionNestingLimit; set => _options.ExpressionNestingLimit = value; }

        public IDatabaseEngineBuilder AddWorker(Func<IDatabaseEngine, IDatabaseEngineWorker> configure)
            => throw new NotSupportedException("Only the engine's own builder can attach a worker to a SqlDatabaseEngine.");

        public IDatabaseEngineBuilder AddServer(Func<IDatabaseEngine, IDatabaseServer> configure)
            => throw new NotSupportedException("Only the engine's own builder can attach a server to a SqlDatabaseEngine.");

        public IDatabaseEngine Build()
        {
            if (_buildAttempted)
            {
                throw new InvalidOperationException("A build was already attempted.");
            }

            _buildAttempted = true;
            return SqlDatabaseEngine.Create(_options);
        }
    }
}
