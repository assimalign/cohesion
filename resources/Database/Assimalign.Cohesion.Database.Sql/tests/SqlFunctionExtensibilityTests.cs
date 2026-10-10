using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// An application's functions run on the engine's function abstraction exactly as the built-ins do
/// (phase E2 of the engine extensibility program, owner decisions 60 to 67 and 71): registered
/// through the engine builder's <see cref="SqlDatabaseEngineBuilder.Functions"/>, resolved by name,
/// argument count and type once per statement, folded when immutable over constants, short-circuited
/// when strict, admitted in a CHECK only when immutable, and coded <c>COHSQLE007</c> when they throw.
/// </summary>
public sealed partial class SqlFunctionExtensibilityTests
{
    private const string FunctionSignatureMismatch = "COHSQLE006";
    private const string FunctionFailed = "COHSQLE007";
    private const string AmbiguousFunctionCall = "COHSQLE008";

    /// <summary>The builder starts with the standard library, and the built engine executes it and the application's functions.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: the builder holds the standard library and the engine freezes the registrations")]
    public async Task Build_RegisteredFunctions_ShouldJoinTheStandardLibraryInTheEngineCatalog()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-functions-catalog");
        var slugify = SqlScalarFunction.Create("slugify", static (string text) => text.ToLowerInvariant().Replace(' ', '-'),
            SqlFunctionVolatility.Immutable);

        // Act
        builder.Functions.Add(slugify).Add(new ClampFunction());
        await using var engine = await builder.BuildAsync();
        await using var standard = SqlDatabaseEngine.Create("sql-functions-standard", new SqlDatabaseEngineOptions());

        // Assert
        builder.Functions.Select(function => function.Name).Distinct().Take(9)
            .ShouldBe(["UPPER", "LOWER", "LENGTH", "ABS", "COUNT", "SUM", "AVG", "MIN", "MAX"]);
        builder.Functions.Contains("SLUGIFY").ShouldBeTrue();
        builder.Types.Contains("bigint").ShouldBeTrue();
        builder.Types.ShouldNotContain(SqlType.AnyElement);
        engine.Functions.Count.ShouldBe(standard.Functions.Count + 2);
        engine.Functions.GetOverloads("Slugify").ShouldHaveSingleItem().ShouldBeSameAs(slugify);
        engine.Functions.GetOverloads("count").Count.ShouldBe(2);
        engine.Functions.GetOverloads("missing").ShouldBeEmpty();
        standard.Functions.GetOverloads("slugify").ShouldBeEmpty();
        slugify.Parameters.ShouldBe([SqlType.Text]);
        slugify.ReturnType.ShouldBe(SqlType.Text);
        slugify.Kind.ShouldBe(SqlFunctionKind.Scalar);
        slugify.NullBehavior.ShouldBe(SqlNullBehavior.ReturnsNullOnNullInput);
        SqlScalarFunction.Create("f", static (long value) => value).Volatility.ShouldBe(SqlFunctionVolatility.Volatile);
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(SqlScalarFunction.Create("late", static (long value) => value)));
    }

    /// <summary>A registered scalar runs in every expression position, a typed shorthand and a hand-written leaf alike.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a registered scalar runs in projections, predicates, assignments, grouping and VALUES")]
    public async Task ExecuteAsync_RegisteredScalar_ShouldRunLikeABuiltIn()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("slugify", static (string text) => text.ToLowerInvariant().Replace(' ', '-'),
                SqlFunctionVolatility.Immutable))
            .Add(new ClampFunction()));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE items (id INT, name TEXT, qty BIGINT)");
        await ExecuteAsync(session, "INSERT INTO items VALUES (1, 'Red Apple', 5), (2, 'Green Pear', 40), (3, 'Blue Plum', 15)");

        // Act
        var projected = await RowsAsync(session, "SELECT id, slugify(name), clamp(qty, 10, 20) FROM items ORDER BY id");
        var filtered = await RowsAsync(session, "SELECT id FROM items WHERE slugify(name) = 'green-pear'");
        var grouped = await RowsAsync(session, "SELECT clamp(qty, 10, 20), COUNT(*) FROM items GROUP BY clamp(qty, 10, 20) ORDER BY 1");
        await ExecuteAsync(session, "UPDATE items SET name = slugify(name) WHERE clamp(qty, 0, 10) = qty");
        await ExecuteAsync(session, "INSERT INTO items VALUES (4, slugify('Ripe Fig'), clamp(99, 0, 50))");
        var after = await RowsAsync(session, "SELECT id, name, qty FROM items WHERE id IN (1, 4) ORDER BY id");

        // Assert
        projected.ShouldBe([[1, "red-apple", 10L], [2, "green-pear", 20L], [3, "blue-plum", 15L]]);
        filtered.ShouldBe([[2]]);
        grouped.ShouldBe([[10L, 1L], [15L, 1L], [20L, 1L]]);
        after.ShouldBe([[1, "red-apple", 5L], [4, "ripe-fig", 50L]]);
    }

    /// <summary>
    /// A name carries overloads distinguished by parameter types, and each call resolves to the one
    /// its arguments' types match best: an exact type first, then the cheapest widening, then a
    /// polymorphic overload. An integer literal is typed by its magnitude, as PostgreSQL types one.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: overloads resolve by argument type, exact before widening before polymorphic")]
    public async Task ExecuteAsync_Overloads_ShouldResolveByArgumentType()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("describe", static (long value) => $"bigint {value}"))
            .Add(SqlScalarFunction.Create("describe", static (string value) => $"text {value}"))
            .Add(SqlScalarFunction.Create("describe", static (double value) => $"double {value}"))
            .Add(SqlScalarFunction.Create("widen", static (decimal value) => $"numeric {value}"))
            .Add(new KindOfFunction())
            .Add(SqlScalarFunction.Create("kind_of", static (string value) => $"text {value}")));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, small SMALLINT, name TEXT, flag BOOLEAN)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (7, 3, 'x', TRUE)");

        // Act
        var rows = await RowsAsync(session,
            "SELECT describe(id), describe(name), describe(small), describe(5), describe(2.5), widen(id), kind_of(flag), kind_of(name) FROM t");

        // Assert: INT widens to BIGINT (cheaper than to DOUBLE); 2.5 is DECIMAL, which widens only to DOUBLE;
        // the concrete TEXT overload of kind_of beats its polymorphic one.
        rows.ShouldBe([["bigint 7", "text x", "bigint 3", "bigint 5", "double 2.5", "numeric 7", "any Boolean", "text x"]]);
    }

    /// <summary>
    /// A call that several overloads accept equally well is ambiguous (<c>COHSQLE008</c>, SQLSTATE
    /// 42725), and one no overload's types accept is <c>COHSQLE006</c>; both fail while planning,
    /// over an empty table as over a populated one, and leave the session usable.
    /// </summary>
    /// <param name="sql">The statement.</param>
    /// <param name="code">The coded failure.</param>
    /// <param name="message">The start of its message.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: an ambiguous call is COHSQLE008 and a call no overload accepts is COHSQLE006, at plan time")]
    [InlineData("SELECT describe(NULL) FROM t", AmbiguousFunctionCall, "COHSQLE008: Function call 'describe(unknown)' is ambiguous: describe(BIGINT) and describe(TEXT) accept it equally well.")]
    [InlineData("SELECT describe(@missing) FROM t", AmbiguousFunctionCall, "COHSQLE008: Function call 'describe(unknown)' is ambiguous")]
    [InlineData("SELECT describe(flag) FROM t", FunctionSignatureMismatch, "COHSQLE006: Function 'describe' has no overload that accepts argument types (BOOLEAN). Accepted: describe(BIGINT) or describe(TEXT).")]
    [InlineData("SELECT clamp(name, 1, 2) FROM t", FunctionSignatureMismatch, "COHSQLE006: Function 'clamp' has no overload that accepts argument types (TEXT, INTEGER, INTEGER). Accepted: clamp(BIGINT, BIGINT, BIGINT).")]
    [InlineData("SELECT clamp(1, 2) FROM t", FunctionSignatureMismatch, "COHSQLE006: Function 'clamp' takes exactly 3 arguments but was called with 2. Accepted: clamp(BIGINT, BIGINT, BIGINT).")]
    [InlineData("SELECT id FROM t WHERE describe(NULL) = 'x'", AmbiguousFunctionCall, "COHSQLE008:")]
    public async Task ExecuteAsync_UnresolvableCall_ShouldFailAtPlanTime(string sql, string code, string message)
    {
        foreach (bool withRows in new[] { false, true })
        {
            // Arrange
            await using var engine = await BuildAsync(functions => functions
                .Add(SqlScalarFunction.Create("describe", static (long value) => "bigint"))
                .Add(SqlScalarFunction.Create("describe", static (string value) => "text"))
                .Add(new ClampFunction()));
            await using var session = await SessionAsync(engine);
            await ExecuteAsync(session, "CREATE TABLE t (id INT, name TEXT, flag BOOLEAN)");
            if (withRows)
            {
                await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'a', TRUE)");
            }

            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql, new Dictionary<string, object?> { ["missing"] = null }));

            // Assert
            failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(code, $"withRows: {withRows}");
            failure.Message.ShouldStartWith(message, Case.Sensitive, $"withRows: {withRows}");
            session.State.ShouldBe(SessionState.Open);
        }
    }

    /// <summary>
    /// An immutable call over constants or parameters is computed once, when the statement is planned,
    /// so no row calls it; a volatile one is called for every row.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: an immutable call over constants is folded once and a volatile one runs per row")]
    public async Task ExecuteAsync_ImmutableCallOverConstants_ShouldBeFoldedOnce()
    {
        // Arrange
        var immutable = new CountingFunction("twice", SqlFunctionVolatility.Immutable);
        var stable = new CountingFunction("twice_stable", SqlFunctionVolatility.Stable);
        var volatileFunction = new CountingFunction("twice_volatile", SqlFunctionVolatility.Volatile);
        await using var engine = await BuildAsync(functions => functions.Add(immutable).Add(stable).Add(volatileFunction));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id BIGINT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2), (3), (4), (5)");

        // Act
        var folded = await RowsAsync(session, "SELECT twice(21), twice(@p) FROM t", new Dictionary<string, object?> { ["p"] = 4L });
        long foldedCalls = immutable.Calls;
        var perRow = await RowsAsync(session, "SELECT twice(id), twice_stable(21), twice_volatile(21) FROM t");

        // Assert
        folded.Count.ShouldBe(5);
        folded.ShouldAllBe(row => (long)row[0]! == 42L && (long)row[1]! == 8L);
        foldedCalls.ShouldBe(2);
        perRow.Count.ShouldBe(5);
        immutable.Calls.ShouldBe(7);
        stable.Calls.ShouldBe(5);
        volatileFunction.Calls.ShouldBe(5);
    }

    /// <summary>
    /// A strict function (the default) is never called with a NULL argument: the call is NULL. A
    /// function declared <see cref="SqlNullBehavior.CalledOnNullInput"/> receives the NULL.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a strict function is not called over NULL and a non-strict one is")]
    public async Task ExecuteAsync_NullArgument_ShouldShortCircuitOnlyStrictFunctions()
    {
        // Arrange
        var strict = new CountingFunction("strict_twice", SqlFunctionVolatility.Volatile);
        await using var engine = await BuildAsync(functions => functions.Add(strict).Add(new NullCheckFunction()));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, v BIGINT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 5), (2, NULL)");

        // Act
        var rows = await RowsAsync(session, "SELECT strict_twice(v), is_missing(v) FROM t ORDER BY id");

        // Assert
        rows.ShouldBe([[10L, false], [null, true]]);
        strict.Calls.ShouldBe(1);
    }

    /// <summary>
    /// A CHECK admits only an immutable function (owner decision 64), so a function must be marked
    /// before a CHECK uses it; one marked immutable constrains every write, and one returning BOOLEAN
    /// is a predicate on its own.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a CHECK admits only immutable functions and enforces them")]
    public async Task CreateTable_CheckOverRegisteredFunction_ShouldRequireImmutable()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("is_email", static (string value) => value.Contains('@'), SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("is_email_volatile", static (string value) => value.Contains('@')))
            .Add(SqlScalarFunction.Create("is_email_stable", static (string value) => value.Contains('@'), SqlFunctionVolatility.Stable)));
        await using var session = await SessionAsync(engine);

        // Act
        var volatileRefusal = await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, "CREATE TABLE a (email TEXT CHECK (is_email_volatile(email)))"));
        var stableRefusal = await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, "CREATE TABLE a (email TEXT, CONSTRAINT ck CHECK (is_email_stable(email)))"));
        await ExecuteAsync(session, "CREATE TABLE a (email TEXT, CONSTRAINT ck CHECK (is_email(email)))");
        await ExecuteAsync(session, "INSERT INTO a VALUES ('ann@example.com'), (NULL)");
        var violation = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO a VALUES ('nobody')"));

        // Assert
        volatileRefusal.Message.ShouldBe("Function 'is_email_volatile' is VOLATILE, and a CHECK admits only IMMUTABLE functions. " +
            "Register the function as SqlFunctionVolatility.Immutable if its result depends on nothing but its arguments.");
        stableRefusal.Message.ShouldStartWith("Function 'is_email_stable' is STABLE", Case.Sensitive);
        violation.ShouldNotBeNull();
        (await RowsAsync(session, "SELECT COUNT(*) FROM a")).ShouldBe([[2L]]);
    }

    /// <summary>
    /// A CHECK naming a registered function persists as canonical text and binds again when the
    /// database reopens under an engine that registers the function, which then enforces it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a persisted CHECK naming a registered function binds again when the database reopens")]
    public async Task Open_PersistedCheckOverRegisteredFunction_ShouldBindAgain()
    {
        // Arrange
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cohesion-function-check", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = await BuildAsync(Register, root))
            {
                var database = await engine.CreateDatabaseAsync("shop");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "CREATE TABLE customers (email TEXT, CONSTRAINT ck_email CHECK (is_email(email)))");
                await ExecuteAsync(session, "INSERT INTO customers VALUES ('ann@example.com')");
            }

            // Act
            await using var reopened = await BuildAsync(Register, root);
            var shop = await reopened.OpenDatabaseAsync("shop");
            await using var again = await shop.CreateSessionAsync(CancellationToken.None);
            var violation = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(again, "INSERT INTO customers VALUES ('nobody')"));
            await ExecuteAsync(again, "INSERT INTO customers VALUES ('bob@example.com')");

            // Assert
            shop.Catalog.TryGetTable("dbo", "customers", out var table).ShouldBeTrue();
            table.Constraints.ShouldHaveSingleItem().CheckExpression.ShouldBe("is_email(email)");
            violation.ShouldNotBeNull();
            (await RowsAsync(again, "SELECT COUNT(*) FROM customers")).ShouldBe([[2L]]);
        }
        finally
        {
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }

        static void Register(SqlFunctionCollection functions)
            => functions.Add(SqlScalarFunction.Create("is_email", static (string value) => value.Contains('@'), SqlFunctionVolatility.Immutable));
    }

    /// <summary>
    /// What a function throws fails the statement as <c>COHSQLE007</c>, naming the function, with the
    /// original as the inner exception; a <see cref="DatabaseException"/> it throws reaches the
    /// statement unchanged. Either way the session stays usable.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a throwing function fails the statement as COHSQLE007 naming it")]
    public async Task ExecuteAsync_ThrowingFunction_ShouldFailAsCohsqle007()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(new ClampFunction())
            .Add(SqlScalarFunction.Create("refuse", static (long value) => value < 0
                ? throw new DatabaseException("refuse: negative input")
                : value)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, low BIGINT, high BIGINT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 1, 5), (2, 9, 3)");

        // Act
        var failed = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT clamp(0, low, high) FROM t"));
        var passedThrough = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT refuse(id - 3) FROM t"));
        var after = await RowsAsync(session, "SELECT clamp(0, low, high) FROM t WHERE id = 1");

        // Assert
        var coded = failed.ShouldBeOfType<SqlEvaluationException>();
        coded.Code.ShouldBe(FunctionFailed);
        coded.Message.ShouldStartWith("COHSQLE007: Function 'clamp' failed: ", Case.Sensitive);
        coded.InnerException.ShouldBeOfType<ArgumentException>();
        passedThrough.ShouldNotBeOfType<SqlEvaluationException>();
        passedThrough.Message.ShouldBe("refuse: negative input");
        after.ShouldBe([[1L]]);
        session.State.ShouldBe(SessionState.Open);
    }

    /// <summary>
    /// Inside an explicit transaction a function's failure fails its statement like any coded error:
    /// the statement writes nothing (its first row included), the transaction stays active, and its
    /// other statements commit (the owner's statement-atomic rule, which a division by zero follows too).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a function failure inside a transaction aborts its statement like any coded error")]
    public async Task ExecuteAsync_ThrowingFunctionInExplicitTransaction_ShouldAbortTheStatementOnly()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions.Add(SqlScalarFunction.Create("checked_positive",
            static (long value) => value > 0 ? true : throw new InvalidOperationException("not positive"), SqlFunctionVolatility.Immutable)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id BIGINT, CONSTRAINT ck CHECK (checked_positive(id)))");

        // Act
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        var failed = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t VALUES (2), (-3)"));
        var divided = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t VALUES (5), (1 / 0)"));
        await ExecuteAsync(session, "INSERT INTO t VALUES (4)");
        var inside = await RowsAsync(session, "SELECT id FROM t ORDER BY id");
        await ExecuteAsync(session, "COMMIT");
        var committed = await RowsAsync(session, "SELECT id FROM t ORDER BY id");

        // Assert
        failed.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(FunctionFailed);
        failed.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("not positive");
        divided.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("COHSQLE001");
        inside.ShouldBe([[1L], [4L]]);
        committed.ShouldBe([[1L], [4L]]);
    }

    /// <summary>
    /// A registered aggregate runs like the built-ins: one accumulator per group per call, NULL rows
    /// skipped when strict, finished once per group (the implicit empty group too), and NULL for a
    /// group without a non-NULL value from the typed shorthand, whose finish is then not called.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a registered aggregate accumulates per group like the built-ins")]
    public async Task ExecuteAsync_RegisteredAggregate_ShouldAccumulatePerGroup()
    {
        // Arrange
        int finishes = 0;
        var median = new MedianFunction();
        await using var engine = await BuildAsync(functions => functions
            .Add(median)
            .Add(SqlAggregateFunction.Create<long, long, long>("product",
                seed: static () => 1L,
                step: static (long state, long value) => checked(state * value),
                finish: state => { Interlocked.Increment(ref finishes); return state; })));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE products (category TEXT, price DOUBLE, quantity BIGINT)");
        await ExecuteAsync(session, "INSERT INTO products VALUES ('a', 1.0, 2), ('a', 3.0, 3), ('a', 10.0, NULL), " +
            "('b', 4.0, 5), ('b', NULL, 7), ('c', NULL, NULL)");

        // Act
        var grouped = await RowsAsync(session,
            "SELECT category, median(price), product(quantity), COUNT(*) FROM products GROUP BY category ORDER BY category");
        int groupedFinishes = finishes;
        int groupedAccumulators = median.Accumulators;
        var empty = await RowsAsync(session, "SELECT median(price), product(quantity) FROM products WHERE category = 'none'");
        var having = await RowsAsync(session,
            "SELECT category FROM products GROUP BY category HAVING product(quantity) > 5 ORDER BY category");

        // Assert
        grouped.ShouldBe([["a", 3.0d, 6L, 3L], ["b", 4.0d, 35L, 2L], ["c", null, null, 1L]]);
        groupedFinishes.ShouldBe(2);
        groupedAccumulators.ShouldBe(3);
        empty.ShouldBe([[null, null]]);
        median.Accumulators.ShouldBe(4);
        having.ShouldBe([["a"], ["b"]]);
    }

    /// <summary>An aggregate that throws while it accumulates fails the statement as <c>COHSQLE007</c>.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a throwing aggregate fails the statement as COHSQLE007")]
    public async Task ExecuteAsync_ThrowingAggregate_ShouldFailAsCohsqle007()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions.Add(SqlAggregateFunction.Create<long, long, long>("product",
            seed: static () => 1L,
            step: static (long state, long value) => checked(state * value),
            finish: static state => state)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (v BIGINT)");
        await ExecuteAsync(session, $"INSERT INTO t VALUES ({long.MaxValue}), (2)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT product(v) FROM t"));

        // Assert
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(FunctionFailed);
        failure.Message.ShouldStartWith("COHSQLE007: Function 'product' failed: ", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<OverflowException>();
    }

    /// <summary>
    /// The function's context carries the database the statement runs in and the collation its input
    /// compares under; a registered name is never an unknown function, and an unregistered one still is.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a call's context names its database and collation")]
    public async Task ExecuteAsync_FunctionContext_ShouldCarryTheDatabaseAndCollation()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions.Add(new ContextFunction()));
        var database = await engine.CreateDatabaseAsync("contextual");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (name TEXT COLLATE case_insensitive, other TEXT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES ('a', 'b')");

        // Act
        var rows = await RowsAsync(session, "SELECT context_of(name), context_of(other) FROM t");
        var unknown = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT context_off(name) FROM t"));

        // Assert
        rows.ShouldBe([[$"contextual/{Collation.CaseInsensitive.Name}", $"contextual/{Collation.Binary.Name}"]]);
        unknown.Message.ShouldBe("Unknown function 'context_off'.");
    }

    /// <summary>
    /// A comparand that calls a volatile function can change between the plan and a row, so it never
    /// bounds an index seek; an immutable one still does.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a volatile comparand never bounds an index seek")]
    public async Task ExecuteAsync_VolatileComparand_ShouldScan()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("same", static (long value) => value, SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("same_volatile", static (long value) => value)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id BIGINT, name TEXT)");
        await ExecuteAsync(session, "CREATE INDEX ix_id ON t (id)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        // Act
        var seek = await RowsAsync(session, "SELECT name FROM t WHERE id = same(2)");
        string seekPath = session.LastStatementMetrics!.AccessPath;
        var scan = await RowsAsync(session, "SELECT name FROM t WHERE id = same_volatile(2)");
        string scanPath = session.LastStatementMetrics!.AccessPath;

        // Assert
        seek.ShouldBe([["b"]]);
        seekPath.ShouldBe("seek:ix_id");
        scan.ShouldBe([["b"]]);
        scanPath.ShouldBe("scan");
    }

    /// <summary>
    /// Registration refuses a name that is not an identifier, a special form or a keyword, an exact
    /// duplicate (a built-in's included: the standard library cannot be replaced), and a scalar and
    /// an aggregate under one name; a new overload of a built-in's name is accepted.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: registration refuses bad names, duplicates and mixed kinds")]
    public void Add_InvalidRegistration_ShouldBeRefused()
    {
        // Arrange
        var functions = SqlDatabaseEngine.CreateBuilder("sql-functions-registration").Functions;

        // Act / Assert
        Should.Throw<ArgumentException>(() => functions.Add(SqlScalarFunction.Create("1st", static (long value) => value)));
        Should.Throw<ArgumentException>(() => functions.Add(SqlScalarFunction.Create("my-fn", static (long value) => value)));
        Should.Throw<ArgumentException>(() => functions.Add(SqlScalarFunction.Create("coalesce", static (long value) => value)))
            .Message.ShouldContain("special form");
        Should.Throw<ArgumentException>(() => functions.Add(SqlScalarFunction.Create("NULLIF", static (long value) => value)));
        Should.Throw<ArgumentException>(() => functions.Add(SqlScalarFunction.Create("select", static (long value) => value)))
            .Message.ShouldContain("keyword");
        Should.Throw<InvalidOperationException>(() => functions.Add(new ReplacementUpperFunction()))
            .Message.ShouldContain("standard library");
        Should.Throw<InvalidOperationException>(() => functions.Add(SqlScalarFunction.Create("count", static (long value) => value)))
            .Message.ShouldContain("aggregate");
        var twice = SqlScalarFunction.Create("twice", static (long value) => value * 2);
        functions.Add(twice);
        Should.Throw<InvalidOperationException>(() => functions.Add(twice));
        Should.Throw<InvalidOperationException>(() => functions.Add(SqlScalarFunction.Create("TWICE", static (long value) => value + value)))
            .Message.ShouldContain("cannot be replaced");
        functions.Add(SqlScalarFunction.Create("TWICE", static (int value) => value * 2L));
        functions.Add(SqlScalarFunction.Create("upper", static (string value, long times) => string.Concat(Enumerable.Repeat(value, (int)times))));
        functions.Count(function => function.Name.Equals("twice", StringComparison.OrdinalIgnoreCase)).ShouldBe(2);
        Should.Throw<NotSupportedException>(() => SqlScalarFunction.Create("unsupported", static (Uri value) => value.Host));
        Should.Throw<ArgumentException>(() => new PolymorphicResultFunction());
    }

    /// <summary>
    /// The value ABI round-trips every storage type without boxing a value it creates, an accessor of
    /// another type throws, and a function can be called directly with the default context.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: values round-trip and a function can be called directly")]
    public void Invoke_Directly_ShouldUseTheValueAbi()
    {
        // Arrange
        var clamp = new ClampFunction();
        Span<SqlValue> values = [SqlValue.FromInt64(42), SqlValue.FromInt64(0), SqlValue.FromInt64(10)];

        // Act
        var result = clamp.Invoke(new SqlArguments(values));
        var strictNull = clamp.Invoke(new SqlArguments([SqlValue.Null, SqlValue.FromInt64(0), SqlValue.FromInt64(10)]));

        // Assert
        result.AsInt64().ShouldBe(10);
        result.Type.ShouldBe(DatabaseType.Int64);
        strictNull.IsNull.ShouldBeTrue();
        default(SqlValue).ShouldBe(SqlValue.Null);
        SqlValue.FromDecimal(1.5m).AsDecimal().ShouldBe(1.5m);
        SqlValue.FromGuid(Guid.Empty).AsGuid().ShouldBe(Guid.Empty);
        var moment = new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.FromHours(2));
        SqlValue.FromDateTimeOffset(moment).AsDateTimeOffset().ShouldBe(moment);
        SqlValue.FromString("x").ShouldBe(SqlValue.FromString("x"));
        SqlValue.FromString(null).IsNull.ShouldBeTrue();
        Should.Throw<InvalidCastException>(() => SqlValue.FromInt32(1).AsInt64());
        Should.Throw<InvalidCastException>(() => SqlValue.Null.AsString());
        Should.Throw<ArgumentException>(() => clamp.Invoke(new SqlArguments([SqlValue.FromInt64(1), SqlValue.FromInt64(2)])));
    }

    private static async Task<SqlDatabaseEngine> BuildAsync(Action<SqlFunctionCollection> register, string? rootPath = null, string? declare = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("sql-functions");
        if (rootPath is not null)
        {
            builder.Options.RootPath = rootPath;
        }

        register(builder.Functions);
        if (declare is not null)
        {
            builder.AddDatabase(declare);
        }

        return await builder.BuildAsync();
    }

    private static async Task<SqlDatabaseSession> SessionAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("functions");
        return await database.CreateSessionAsync(CancellationToken.None);
    }

    private static Task<QueryResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, sql, parameters)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    /// <summary>The design's hand-written leaf: <c>clamp(BIGINT, BIGINT, BIGINT)</c>, throwing when low exceeds high.</summary>
    private sealed class ClampFunction : SqlScalarFunction
    {
        public ClampFunction()
            : base("clamp", [SqlType.BigInt, SqlType.BigInt, SqlType.BigInt], SqlType.BigInt, SqlFunctionVolatility.Immutable)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => SqlValue.FromInt64(Math.Clamp(arguments.GetInt64(0), arguments.GetInt64(1), arguments.GetInt64(2)));
    }

    /// <summary>A polymorphic leaf with a concrete overload beside it: <c>kind_of(ANY)</c>.</summary>
    private sealed class KindOfFunction : SqlScalarFunction
    {
        public KindOfFunction()
            : base("kind_of", [SqlType.Any], SqlType.Text, SqlFunctionVolatility.Immutable)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromString($"any {arguments[0].Type}");
    }

    /// <summary>Counts its calls: <c>name(BIGINT)</c> doubling its argument.</summary>
    private sealed class CountingFunction : SqlScalarFunction
    {
        private long _calls;

        public CountingFunction(string name, SqlFunctionVolatility volatility)
            : base(name, [SqlType.BigInt], SqlType.BigInt, volatility)
        {
        }

        public long Calls => Interlocked.Read(ref _calls);

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        {
            Interlocked.Increment(ref _calls);
            return SqlValue.FromInt64(arguments.GetInt64(0) * 2);
        }
    }

    /// <summary>Called on NULL input: whether its argument is NULL.</summary>
    private sealed class NullCheckFunction : SqlScalarFunction
    {
        public NullCheckFunction()
            : base("is_missing", [SqlType.Any], SqlType.Boolean, SqlFunctionVolatility.Immutable, SqlNullBehavior.CalledOnNullInput)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromBoolean(arguments.IsNull(0));
    }

    /// <summary>Reports its context: the database and the collation of its input.</summary>
    private sealed class ContextFunction : SqlScalarFunction
    {
        public ContextFunction()
            : base("context_of", [SqlType.Text], SqlType.Text)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => SqlValue.FromString($"{arguments.Context.Database}/{arguments.Context.Collation.Name}");
    }

    /// <summary>An exact duplicate of the standard library's <c>UPPER(ANYELEMENT)</c>.</summary>
    private sealed class ReplacementUpperFunction : SqlScalarFunction
    {
        public ReplacementUpperFunction()
            : base("Upper", [SqlType.AnyElement], SqlType.AnyElement, SqlFunctionVolatility.Immutable)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => arguments[0];
    }

    /// <summary>An ANYELEMENT result without an ANYELEMENT parameter, which construction refuses.</summary>
    private sealed class PolymorphicResultFunction : SqlScalarFunction
    {
        public PolymorphicResultFunction()
            : base("broken", [SqlType.Text], SqlType.AnyElement)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => arguments[0];
    }

    /// <summary>The design's hand-written aggregate: the median of the non-NULL doubles of a group.</summary>
    private sealed class MedianFunction : SqlAggregateFunction
    {
        private int _accumulators;

        public MedianFunction()
            : base("median", [SqlType.Double], SqlType.Double)
        {
        }

        public int Accumulators => Volatile.Read(ref _accumulators);

        protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
        {
            Interlocked.Increment(ref _accumulators);
            return new Accumulator();
        }

        private sealed class Accumulator : SqlAggregateAccumulator
        {
            private readonly List<double> _values = [];

            protected override void AddCore(scoped in SqlArguments arguments) => _values.Add(arguments.GetDouble(0));

            protected override SqlValue FinishCore()
            {
                if (_values.Count == 0)
                {
                    return SqlValue.Null;
                }

                _values.Sort();
                int middle = _values.Count / 2;
                return SqlValue.FromDouble(_values.Count % 2 == 1 ? _values[middle] : (_values[middle - 1] + _values[middle]) / 2);
            }
        }
    }
}
