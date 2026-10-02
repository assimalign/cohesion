using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Function calls are matched against their signatures (#1189). A call whose argument count no
/// signature of its function accepts used to plan, evaluate no argument and return NULL, so
/// <c>SELECT ABS(1, 2), UPPER(), COALESCE()</c> returned three NULLs and a CHECK built on one
/// never fired. It now fails with <c>COHSQLE006</c> while planning, in every expression position
/// and over an empty table as over a populated one; the evaluator refuses one that reaches it
/// without planning, and a stored definition holding one fails the database's open.
/// </summary>
public sealed class SqlFunctionArityTests : IDisposable
{
    private const string FunctionSignatureMismatch = "COHSQLE006";

    private static readonly string[] _schema =
    [
        "CREATE TABLE t (id INT PRIMARY KEY, name TEXT, age INT, flag BOOLEAN);",
        "CREATE TABLE u (id INT PRIMARY KEY, name TEXT);",
    ];

    private static readonly string[] _rows =
    [
        "INSERT INTO t VALUES (1, 'ann', 36, TRUE), (2, 'bob', -45, NULL), (3, NULL, 41, FALSE);",
        "INSERT INTO u VALUES (1, 'x'), (4, 'y');",
    ];

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-function-arity", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A wrong-arity call fails before any row is read, in every position an expression can take,
    /// so the statement fails the same way over an empty table as over a populated one, and
    /// changes nothing: no row, no table, no column, no constraint.
    /// </summary>
    /// <param name="sql">A statement that calls a function with the wrong number of arguments somewhere.</param>
    /// <param name="function">The function the error names, as written.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: a wrong-arity call fails at plan time in every position, whatever the rows")]
    // Projections, and the issue's own statement: the first call in source order is reported.
    [InlineData("SELECT ABS(1, 2), UPPER(), COALESCE() FROM t;", "ABS")]
    [InlineData("SELECT UPPER() FROM t;", "UPPER")]
    [InlineData("SELECT COALESCE() FROM t;", "COALESCE")]
    [InlineData("SELECT LOWER(name, name) FROM t;", "LOWER")]
    [InlineData("SELECT LENGTH() FROM t;", "LENGTH")]
    [InlineData("SELECT UPPER(*) FROM t;", "UPPER")]
    [InlineData("select abs(age, 1) from t;", "abs")]
    // Nested: an argument's error is reported before its caller's, as PostgreSQL resolves calls.
    [InlineData("SELECT COALESCE(name, UPPER()) FROM t;", "UPPER")]
    [InlineData("SELECT FOO(ABS()) FROM t;", "ABS")]
    // Predicates, grouping, ordering, joins, counts and subqueries.
    [InlineData("SELECT id FROM t WHERE ABS(id, 1) > 0;", "ABS")]
    [InlineData("SELECT UPPER(name, 1), COUNT(*) FROM t GROUP BY UPPER(name, 1);", "UPPER")]
    [InlineData("SELECT age FROM t GROUP BY age HAVING ABS(COUNT(*), 1) > 0;", "ABS")]
    [InlineData("SELECT id FROM t ORDER BY LOWER();", "LOWER")]
    [InlineData("SELECT t.id FROM t JOIN u ON ABS(t.id, 1) = u.id;", "ABS")]
    [InlineData("SELECT id FROM t LIMIT ABS(1, 2);", "ABS")]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT ABS(id, 1) FROM u);", "ABS")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT id FROM u WHERE LENGTH(name, 1) = 1);", "LENGTH")]
    [InlineData("SELECT (SELECT COALESCE() FROM u LIMIT 1) FROM t;", "COALESCE")]
    // DML: VALUES, INSERT ... SELECT, UPDATE SET and WHERE, DELETE WHERE.
    [InlineData("INSERT INTO t (id, name) VALUES (9, UPPER('a', 'b'));", "UPPER")]
    [InlineData("INSERT INTO t (id, name) VALUES (9, 'ok'), (10, COALESCE());", "COALESCE")]
    [InlineData("INSERT INTO t (id, name) SELECT id, UPPER() FROM u;", "UPPER")]
    [InlineData("UPDATE t SET age = ABS(age, 1);", "ABS")]
    [InlineData("UPDATE t SET age = 0 WHERE LENGTH() = 1;", "LENGTH")]
    [InlineData("DELETE FROM t WHERE ABS() = 1;", "ABS")]
    // CHECK in CREATE TABLE (column and table level) and ALTER TABLE, and DEFAULT.
    [InlineData("CREATE TABLE c (a INT CHECK (ABS(a, 1) > 0));", "ABS")]
    [InlineData("CREATE TABLE c (a INT, CONSTRAINT ck CHECK (COALESCE() > 0));", "COALESCE")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck CHECK (ABS(age, 1) > 0);", "ABS")]
    [InlineData("ALTER TABLE t ADD COLUMN extra INT CHECK (UPPER() IS NULL);", "UPPER")]
    [InlineData("CREATE TABLE c (a INT DEFAULT ABS(1, 2));", "ABS")]
    [InlineData("ALTER TABLE t ADD COLUMN extra INT DEFAULT LENGTH();", "LENGTH")]
    // Aggregates: one argument, and '*' only for COUNT, wherever they appear.
    [InlineData("SELECT COUNT(id, age) FROM t;", "COUNT")]
    [InlineData("SELECT COUNT() FROM t;", "COUNT")]
    [InlineData("SELECT SUM() FROM t;", "SUM")]
    [InlineData("SELECT SUM(*) FROM t;", "SUM")]
    [InlineData("SELECT AVG(age, age) FROM t;", "AVG")]
    [InlineData("SELECT MIN(*) FROM t;", "MIN")]
    [InlineData("SELECT age FROM t GROUP BY age HAVING MAX(id, age) > 1;", "MAX")]
    [InlineData("SELECT age FROM t GROUP BY age ORDER BY COUNT(id, age);", "COUNT")]
    [InlineData("UPDATE t SET age = SUM();", "SUM")]
    [InlineData("SELECT id FROM t WHERE COUNT(id, age) > 1;", "COUNT")]
    public async Task ExecuteAsync_WrongArity_ShouldFailAtPlanTimeWhateverTheRows(string sql, string function)
    {
        foreach (bool withRows in new[] { false, true })
        {
            // Arrange
            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-function-arity" });
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("arity");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            await SeedAsync(session, withRows);
            var before = await SnapshotAsync(session);
            new SqlQueryParser().Parse(sql).Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

            // Act
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

            // Assert
            error.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(FunctionSignatureMismatch, $"withRows: {withRows}");
            error.Message.ShouldStartWith($"{FunctionSignatureMismatch}: Function '{function}' takes ", Case.Sensitive, $"withRows: {withRows}");
            (await SnapshotAsync(session)).ShouldBe(before);
            database.Catalog.TryGetTable("dbo", "c", out _).ShouldBeFalse();
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            table.Columns.Select(column => column.Name).ShouldBe(["id", "name", "age", "flag"]);
            table.Constraints.ShouldBeEmpty();
        }
    }

    /// <summary>The message names the function as written, the counts it accepts, what the call passed, and its call forms.</summary>
    /// <param name="sql">A statement with one wrong-arity call.</param>
    /// <param name="message">The complete message.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: the wrong-arity error names the function, its accepted counts and its call forms")]
    [InlineData("SELECT ABS(1, 2) FROM t;", "COHSQLE006: Function 'ABS' takes exactly 1 argument but was called with 2. Accepted: ABS(numeric).")]
    [InlineData("SELECT upper() FROM t;", "COHSQLE006: Function 'upper' takes exactly 1 argument but was called with none. Accepted: UPPER(value).")]
    [InlineData("SELECT COALESCE() FROM t;", "COHSQLE006: Function 'COALESCE' takes 1 or more arguments but was called with none. Accepted: COALESCE(value [, value ...]).")]
    [InlineData("SELECT COUNT(id, age) FROM t;", "COHSQLE006: Function 'COUNT' takes exactly 1 argument or '*' but was called with 2. Accepted: COUNT(*) or COUNT(value).")]
    [InlineData("SELECT SUM(*) FROM t;", "COHSQLE006: Function 'SUM' takes exactly 1 argument but was called with '*'. Accepted: SUM(numeric).")]
    [InlineData("SELECT LENGTH(*) FROM t;", "COHSQLE006: Function 'LENGTH' takes exactly 1 argument but was called with '*'. Accepted: LENGTH(value).")]
    // '*' among other arguments is counted with them, not reported as if it were the only one.
    [InlineData("SELECT COUNT(id, *) FROM t;", "COHSQLE006: Function 'COUNT' takes exactly 1 argument or '*' but was called with 2 arguments including '*'. Accepted: COUNT(*) or COUNT(value).")]
    [InlineData("SELECT COALESCE(age, *) FROM t;", "COHSQLE006: Function 'COALESCE' takes 1 or more arguments but was called with 2 arguments including '*'. Accepted: COALESCE(value [, value ...]).")]
    public async Task ExecuteAsync_WrongArity_ShouldNameTheFunctionAndItsSignature(string sql, string message)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-function-arity-message" });
        var database = await engine.CreateDatabaseAsync("arity");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session, withRows: true);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        error.Message.ShouldBe(message);
    }

    /// <summary>
    /// The issue's CHECK can no longer be stored, so it cannot silently admit every row; the same
    /// constraint with a valid call is stored and enforced.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a wrong-arity CHECK is refused and a valid one is enforced")]
    public async Task CreateTable_WrongArityCheck_ShouldBeRefusedAndAValidOneEnforced()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-function-arity-check" });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("arity");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        // Act
        var refused = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE TABLE ck1 (c INT CHECK (ABS(c, 1) > 0));"));
        await ExecuteAsync(session, "CREATE TABLE ck1 (c INT CHECK (ABS(c) < 5));");
        var violation = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO ck1 VALUES (-5);"));
        var accepted = await ExecuteAsync(session, "INSERT INTO ck1 VALUES (-4);");

        // Assert
        refused.Message.ShouldStartWith(FunctionSignatureMismatch, Case.Sensitive);
        violation.ShouldNotBeNull();
        accepted.AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "SELECT c FROM ck1;")).ShouldHaveSingleItem().ShouldBe(new object?[] { -4 });
    }

    /// <summary>
    /// Calls within their signatures keep working. <c>COALESCE</c> takes one or more operands, as
    /// PostgreSQL's grammar does (ISO requires two), and one operand is the operand itself.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: calls within their signatures execute, COALESCE with one operand included")]
    public async Task ExecuteAsync_CallsWithinTheirSignatures_ShouldExecute()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-function-arity-valid" });
        var database = await engine.CreateDatabaseAsync("arity");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session, withRows: true);

        // Act
        var scalars = await RowsAsync(session,
            "SELECT COALESCE(name), COALESCE(NULL, name, 'z'), UPPER(name), LOWER(name), LENGTH(name), ABS(age) FROM t ORDER BY id;");
        var aggregates = await RowsAsync(session, "SELECT COUNT(*), COUNT(name), SUM(age), AVG(age), MIN(name), MAX(age) FROM t;");

        // Assert
        scalars.ShouldBe(
        [
            new object?[] { "ann", "ann", "ANN", "ann", 3L, 36L },
            new object?[] { "bob", "bob", "BOB", "bob", 3L, 45L },
            new object?[] { null, "z", null, null, null, 41L },
        ]);
        aggregates.ShouldHaveSingleItem().ShouldBe(new object?[] { 3L, 2L, 32m, 32m / 3m, "ann", 41 });
    }

    /// <summary>
    /// A tree that reaches the evaluator without planning is matched against the same signatures:
    /// the evaluator throws the planner's coded error instead of computing NULL.
    /// </summary>
    /// <param name="expression">A call, parsed but never planned.</param>
    /// <param name="function">The function the error names.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: the evaluator refuses a wrong-arity call that was never planned")]
    [InlineData("ABS(1, 2)", "ABS")]
    [InlineData("UPPER()", "UPPER")]
    [InlineData("COALESCE()", "COALESCE")]
    [InlineData("LENGTH('a', 'b')", "LENGTH")]
    [InlineData("LOWER(*)", "LOWER")]
    [InlineData("COALESCE(NULL, ABS())", "ABS")]
    [InlineData("SUM(1, 2)", "SUM")]
    public void Evaluate_UnplannedWrongArity_ShouldThrowTheCodedError(string expression, string function)
    {
        // Arrange
        var call = ParseProjection(expression);
        var evaluator = new SqlExpressionEvaluator(Array.Empty<SqlCatalogColumn>(), null);

        // Act
        var error = Should.Throw<SqlEvaluationException>(() => evaluator.Evaluate(call, []));

        // Assert
        error.Code.ShouldBe(FunctionSignatureMismatch);
        error.Message.ShouldStartWith($"{FunctionSignatureMismatch}: Function '{function}' takes ", Case.Sensitive);
    }

    /// <summary>
    /// The signature table is the one list of executable functions. Every entry is a name the SQL
    /// profile declares, and every scalar entry has an evaluator case that computes it at the
    /// fewest and at the most arguments its signature admits (one more than the fewest when there
    /// is no limit), so a scalar added to the table without a case, or whose case reads arguments
    /// another count implies, fails here rather than at a user's first call.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: every signature is a declared name and every scalar evaluates at each bound")]
    public void Signatures_EveryEntry_ShouldBeDeclaredAndEveryScalarShouldEvaluate()
    {
        // Arrange
        var declared = new HashSet<string>(SqlLanguageProfile.Instance.Functions.ToArray(), StringComparer.OrdinalIgnoreCase);
        var evaluator = new SqlExpressionEvaluator(Array.Empty<SqlCatalogColumn>(), null);

        // Act
        var signatures = SqlFunctionSignatures.All.ToArray();

        // Assert
        signatures.Select(signature => signature.Name).ShouldBe(
            ["COALESCE", "UPPER", "LOWER", "LENGTH", "ABS", "COUNT", "SUM", "AVG", "MIN", "MAX"], ignoreOrder: true);
        signatures.Select(signature => signature.Function).Distinct().Count().ShouldBe(signatures.Length);
        foreach (var signature in signatures)
        {
            declared.ShouldContain(signature.Name);
            signature.Name.ShouldBe(signature.Name.ToUpperInvariant());
            signature.Usage.ShouldStartWith($"{signature.Name}(", Case.Sensitive);
            if (signature.Kind == SqlFunctionKind.Scalar)
            {
                int most = signature.MaximumArguments ?? signature.MinimumArguments + 1;
                foreach (int count in new[] { signature.MinimumArguments, most }.Distinct())
                {
                    string arguments = string.Join(", ", Enumerable.Repeat("-1", count));
                    Should.NotThrow(() => evaluator.Evaluate(ParseProjection($"{signature.Name}({arguments})"), []), $"{signature.Name}/{count}");
                }
            }
        }
    }

    /// <summary>
    /// Every aggregate entry of the signature table has an accumulator in the grouping executor,
    /// so an aggregate added to the table without one fails here rather than at a user's first call.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: every aggregate signature accumulates in a grouping plan")]
    public async Task Signatures_EveryAggregate_ShouldAccumulate()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-function-arity-aggregates" });
        var database = await engine.CreateDatabaseAsync("arity");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session, withRows: true);
        var aggregates = SqlFunctionSignatures.All.Where(signature => signature.Kind == SqlFunctionKind.Aggregate)
            .OrderBy(signature => signature.Name, StringComparer.Ordinal).ToArray();

        // Act
        var row = (await RowsAsync(session,
            $"SELECT {string.Join(", ", aggregates.Select(signature => $"{signature.Name}(age)"))} FROM t;")).ShouldHaveSingleItem();

        // Assert: AVG, COUNT, MAX, MIN, SUM over 36, -45 and 41.
        aggregates.Select(signature => signature.Name).ShouldBe(["AVG", "COUNT", "MAX", "MIN", "SUM"]);
        row.ShouldBe(new object?[] { 32m / 3m, 3L, 41, -45, 32m });
    }

    /// <summary>
    /// A format-4 catalog written before #1189 can hold a CHECK whose call has the wrong number of
    /// arguments, which never had a value. Format 4 is unreleased and has no upgrade path (#1152),
    /// so such a definition is not carried: the open fails with the coded error, naming the
    /// constraint, and points at the build that stored it rather than at a backup holding the
    /// same definition.
    /// </summary>
    /// <param name="predicate">The persisted predicate.</param>
    /// <param name="call">The start of the coded error the open reports.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: a stored wrong-arity CHECK fails the open with the coded error and the constraint's name")]
    [InlineData("ABS(qty, 1) > 0", "COHSQLE006: Function 'ABS' takes exactly 1 argument but was called with 2.")]
    [InlineData("COALESCE() IS NULL", "COHSQLE006: Function 'COALESCE' takes 1 or more arguments but was called with none.")]
    [InlineData("qty > 0 AND UPPER(note, note) IS NOT NULL", "COHSQLE006: Function 'UPPER' takes exactly 1 argument but was called with 2.")]
    public async Task Open_StoredWrongArityCheck_ShouldFailWithTheCodedErrorNamingTheConstraint(string predicate, string call)
    {
        // Arrange: a healthy format-4 database, then the predicate written straight to its catalog,
        // as an engine that did not check arity stored it.
        await using (var engine = CreateEngine())
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("shop");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE orders (id INT, qty INT, note TEXT, CONSTRAINT ck_arity CHECK (qty > 0))");
            database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
            var stored = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, table.Columns.ToArray(), table.PrimaryKeyColumns,
                table.Owner, table.OwningSchema,
                [new SqlCatalogConstraint("ck_arity", SqlCatalogConstraintKind.Check, [], checkExpression: predicate)]);
            await SqlCatalog.PublishTableAsync(database.Catalog, stored, [], database.Catalog.GetIndexRegistrations(), replaceExisting: true);
        }

        // Act
        await using var reopenedEngine = CreateEngine();
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopenedEngine.OpenDatabaseAsync("shop"));

        // Assert
        failure.Message.ShouldStartWith("Database 'shop' cannot be opened. CHECK constraint 'ck_arity' on table 'dbo.orders' cannot be loaded", Case.Sensitive);
        failure.Message.ShouldContain(call, Case.Sensitive);
        failure.Message.ShouldContain("open the database with that build and drop the constraint", Case.Sensitive);
        failure.Message.ShouldNotContain("restore the database from a backup", Case.Sensitive);
        var coded = failure.InnerException.ShouldNotBeNull().InnerException.ShouldBeOfType<SqlEvaluationException>();
        coded.Code.ShouldBe(FunctionSignatureMismatch);
    }

    private SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-function-arity-open", RootPath = _rootPath });

    private static SqlExpression ParseProjection(string expression)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {expression} FROM t");
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return ((SqlSelectExpression)statement.SqlExpression!).Columns[0].Expression;
    }

    private static async Task SeedAsync(IDatabaseSession session, bool withRows)
    {
        foreach (string statement in withRows ? [.. _schema, .. _rows] : _schema)
        {
            (await ExecuteAsync(session, statement)).Status.ShouldBe(QueryResultStatus.Success, statement);
        }
    }

    private static async Task<string[]> SnapshotAsync(IDatabaseSession session)
    {
        var t = await RowsAsync(session, "SELECT id, name, age, flag FROM t ORDER BY id;");
        var u = await RowsAsync(session, "SELECT id, name FROM u ORDER BY id;");
        return [.. t.Select(row => "t:" + string.Join(",", row)), .. u.Select(row => "u:" + string.Join(",", row))];
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement)
        => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string statement)
    {
        await using var result = (await ExecuteAsync(session, statement)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
