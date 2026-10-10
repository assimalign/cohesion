using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The function ABI's contract with the engine, as the E2 review pinned it: the standard library
/// cannot be shadowed and reserved names cannot be taken; a call over a grouping key or an
/// aggregate result resolves by its type; a nest of calls that need their arguments' types plans in
/// time linear in its depth; a result is checked against its declaration; a typed function can be
/// called on NULL input; binary arguments are read-only; and REAL widens to DOUBLE.
/// </summary>
public sealed partial class SqlFunctionExtensibilityTests
{
    /// <summary>
    /// An application overload of a standard-library name that takes calls the built-in takes would
    /// outrank the built-in's pseudo-typed parameter or tie with it, replacing it or making its calls
    /// ambiguous, in stored CHECKs too; it is refused (owner decision 62). An overload of another
    /// number of arguments is not.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: an overload that would shadow a built-in is refused")]
    public void Add_OverloadShadowingABuiltIn_ShouldBeRefused()
    {
        // Arrange
        var functions = SqlDatabaseEngine.CreateBuilder("sql-functions-shadowing").Functions;

        // Act
        var upper = Should.Throw<InvalidOperationException>(() => functions.Add(SqlScalarFunction.Create("upper", static (string text) => "x" + text)));
        var abs = Should.Throw<InvalidOperationException>(() => functions.Add(SqlScalarFunction.Create("abs", static (long value) => -value)));
        var length = Should.Throw<InvalidOperationException>(() => functions.Add(new PolymorphicLengthFunction()));
        var count = Should.Throw<InvalidOperationException>(() => functions.Add(
            SqlAggregateFunction.Create<long, long, long>("count", static () => 0L, static (long state, long _) => state + 1, static (long state) => state)));
        var variadic = Should.Throw<InvalidOperationException>(() => functions.Add(new VariadicUpperFunction()));
        functions.Add(SqlScalarFunction.Create("upper", static (string text, long times) => string.Concat(Enumerable.Repeat(text, (int)times))));
        functions.Add(SqlAggregateFunction.Create<long, long, long>("sum_twice", static () => 0L, static (long state, long value) => state + 2 * value,
            static (long state) => state));

        // Assert
        upper.Message.ShouldBe(
            "Function upper(TEXT) takes calls the standard library's UPPER(value) takes, so it would replace the built-in for them or make " +
            "them ambiguous, in stored CHECK constraints too; a standard-library function cannot be replaced. Register the function under " +
            "another name, or with a number of parameters UPPER does not take.");
        abs.Message.ShouldContain("ABS(numeric)", Case.Sensitive);
        length.Message.ShouldContain("LENGTH(value)", Case.Sensitive);
        count.Message.ShouldContain("COUNT(value)", Case.Sensitive);
        variadic.Message.ShouldContain("UPPER(value)", Case.Sensitive);
        functions.Count(function => function.Name.Equals("upper", StringComparison.OrdinalIgnoreCase)).ShouldBe(2);
    }

    /// <summary>
    /// A name the dialect reserves for a built-in that does not execute yet, a type name, a quantifier
    /// and a date-time value function cannot name an application's function: the engine shipping the
    /// built-in, or the grammar parsing the word, would break the application.
    /// </summary>
    /// <param name="name">The refused name.</param>
    /// <param name="reason">What the refusal says.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: reserved, type and quantifier names cannot name a function")]
    [InlineData("TRIM", "reserved for a built-in function of the SQL dialect")]
    [InlineData("now", "reserved for a built-in function of the SQL dialect")]
    [InlineData("Round", "reserved for a built-in function of the SQL dialect")]
    [InlineData("CURRENT_TIMESTAMP", "reserved for a built-in function of the SQL dialect")]
    [InlineData("INT", "is a SQL type name")]
    [InlineData("date", "is a SQL type name")]
    [InlineData("INTERVAL", "is a SQL type name")]
    [InlineData("ANY", "is reserved by the SQL dialect")]
    [InlineData("some", "is reserved by the SQL dialect")]
    [InlineData("LOCALTIMESTAMP", "is reserved by the SQL dialect")]
    public void Add_ReservedName_ShouldBeRefused(string name, string reason)
    {
        // Arrange
        var functions = SqlDatabaseEngine.CreateBuilder("sql-functions-reserved").Functions;

        // Act
        var failure = Should.Throw<ArgumentException>(() => functions.Add(SqlScalarFunction.Create(name, static (long value) => value)));

        // Assert
        failure.Message.ShouldContain(reason, Case.Sensitive);
        functions.Contains(name).ShouldBeFalse();
    }

    /// <summary>
    /// Inside a grouping, a call over a grouping key or an aggregate result resolves by the type the
    /// key or the aggregate has over the input row, as the same call does outside the grouping, so an
    /// overloaded function is not ambiguous there (PostgreSQL resolves <c>describe(count(*))</c> to
    /// <c>describe(bigint)</c>).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a call over a grouping key or an aggregate result resolves by its type")]
    public async Task ExecuteAsync_OverloadsOverGroupingSlots_ShouldResolveByTheSlotType()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("describe", static (long value) => $"bigint {value}", SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("describe", static (string value) => $"text {value}", SqlFunctionVolatility.Immutable)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (n BIGINT, s TEXT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'a'), (1, 'b'), (2, 'c')");

        // Act
        var byKey = await RowsAsync(session, "SELECT describe(n), COUNT(*) FROM t GROUP BY n ORDER BY n");
        var keyFirst = await RowsAsync(session, "SELECT n, describe(n) FROM t GROUP BY n ORDER BY n");
        var overCount = await RowsAsync(session, "SELECT describe(COUNT(*)) FROM t");
        var overMax = await RowsAsync(session, "SELECT describe(MAX(s)) FROM t");
        var having = await RowsAsync(session, "SELECT n FROM t GROUP BY n HAVING describe(n) = 'bigint 1'");
        var ordered = await RowsAsync(session, "SELECT s, COUNT(*) FROM t GROUP BY s ORDER BY describe(s) DESC");

        // Assert
        byKey.ShouldBe([["bigint 1", 2L], ["bigint 2", 1L]]);
        keyFirst.ShouldBe([[1L, "bigint 1"], [2L, "bigint 2"]]);
        overCount.ShouldBe([["bigint 3"]]);
        overMax.ShouldBe([["text c"]]);
        having.ShouldBe([[1L]]);
        ordered.Select(row => row[0]).ShouldBe(["c", "b", "a"]);
    }

    /// <summary>
    /// A nest of calls that need their arguments' types to choose an overload or type a polymorphic
    /// result types each argument once per level and remembers each level, so planning time grows
    /// linearly with depth. (Typing the arguments twice per level doubled the work at every level:
    /// depth 20 took seconds, and a deeper statement from any client held a thread indefinitely.)
    /// </summary>
    /// <param name="shape">The nested call: <c>{0}</c> is the inner expression.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: a deep nest of typed or polymorphic calls plans in linear time")]
    [InlineData("pick({0}, 1)")]
    [InlineData("first_of({0}, n)")]
    [InlineData("plus_one({0})")]
    public async Task Plan_DeepNestOfTypedCalls_ShouldPlanInLinearTime(string shape)
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(new PickFunction())
            .Add(new FirstOfFunction())
            .Add(SqlScalarFunction.Create("plus_one", static (long value) => value + 1, SqlFunctionVolatility.Immutable)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (n BIGINT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        string nest = "n";
        for (int level = 0; level < 100; level++)
        {
            nest = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, nest);
        }

        // Act: bounded, so a regression fails here instead of hanging the run.
        var clock = Stopwatch.StartNew();
        var rows = await Task.Run(() => RowsAsync(session, $"SELECT {nest} FROM t")).WaitAsync(TimeSpan.FromSeconds(60));
        clock.Stop();

        // Assert
        rows.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBe(shape.StartsWith("plus_one", StringComparison.Ordinal) ? 101L : 1L);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The engine checks what a function returns against its declaration, which plans, result
    /// metadata and outer calls rely on: a value of a type that widens to the declared one is
    /// converted; another type, a NULL from a function that declares it never returns one, and an
    /// ANYELEMENT result of another type than its arguments fail the statement as <c>COHSQLE007</c>,
    /// naming the function, whether it is called by a statement or directly.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a result is checked against the declaration")]
    public async Task ExecuteAsync_ResultOfAnotherType_ShouldBeConvertedOrFailAsCohsqle007()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(new ReturningFunction("narrow", SqlType.BigInt, static _ => SqlValue.FromInt32(7)))
            .Add(new ReturningFunction("lying", SqlType.BigInt, static _ => SqlValue.FromString("not a number")))
            .Add(new ReturningFunction("fractional", SqlType.BigInt, static _ => SqlValue.FromDouble(2.5)))
            .Add(new ReturningFunction("never_null", SqlType.BigInt, static _ => SqlValue.Null, neverNull: true))
            .Add(new ElementFunction())
            .Add(new LyingAggregateFunction()));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (n BIGINT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");

        // Act
        var widened = await RowsAsync(session, "SELECT narrow(n), narrow(n) + 1 FROM t");
        var lying = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT lying(n) FROM t"));
        var fractional = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT fractional(n) FROM t"));
        var nullResult = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT never_null(n) FROM t"));
        var element = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT element_of(n) FROM t"));
        var aggregate = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT lying_sum(n) FROM t"));
        var direct = Should.Throw<DatabaseException>(() =>
            new ReturningFunction("lying", SqlType.BigInt, static _ => SqlValue.FromString("x")).Invoke(new SqlArguments([SqlValue.FromInt64(1)])));

        // Assert
        widened.ShouldBe([[7L, 8L]]);
        lying.Message.ShouldBe($"{FunctionFailed}: Function 'lying' failed: The function returned TEXT, but it declares BIGINT.");
        lying.InnerException.ShouldBeOfType<InvalidCastException>();
        fractional.Message.ShouldBe($"{FunctionFailed}: Function 'fractional' failed: The function returned DOUBLE, but it declares BIGINT.");
        nullResult.Message.ShouldBe($"{FunctionFailed}: Function 'never_null' failed: The function returned NULL, but it declares that it never returns NULL.");
        element.Message.ShouldBe($"{FunctionFailed}: Function 'element_of' failed: The function returned TEXT, but its ANYELEMENT arguments are BIGINT.");
        aggregate.Message.ShouldBe($"{FunctionFailed}: Function 'lying_sum' failed: The function returned TEXT, but it declares BIGINT.");
        direct.Message.ShouldStartWith($"{FunctionFailed}: Function 'lying' failed:", Case.Sensitive);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldBe([[1L]]);
    }

    /// <summary>
    /// A typed function can be called on NULL input (owner decision 63 made strictness the default,
    /// not the only choice): its nullable parameters then receive <see langword="null"/>, a
    /// non-nullable parameter is refused when the function is created, and a typed aggregate called
    /// on NULL input folds its NULL rows too.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a typed function can be called on NULL input")]
    public async Task ExecuteAsync_TypedFunctionCalledOnNullInput_ShouldReceiveNull()
    {
        // Arrange
        var nz = SqlScalarFunction.Create("nz", static (long? value) => value ?? 0L, SqlFunctionVolatility.Immutable,
            SqlNullBehavior.CalledOnNullInput);
        await using var engine = await BuildAsync(functions => functions
            .Add(nz)
            .Add(SqlScalarFunction.Create("strict_nz", static (long? value) => value ?? 0L, SqlFunctionVolatility.Immutable))
            .Add(SqlAggregateFunction.Create<long, string?, long>("count_all",
                static () => 0L, static (long count, string? _) => count + 1, static (long count) => count, SqlNullBehavior.CalledOnNullInput)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (n BIGINT, s TEXT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'a'), (NULL, NULL)");

        // Act
        var rows = await RowsAsync(session, "SELECT nz(n), strict_nz(n) FROM t ORDER BY n");
        var counted = await RowsAsync(session, "SELECT count_all(s), COUNT(s) FROM t");
        var refused = Should.Throw<ArgumentException>(() => SqlScalarFunction.Create("bad", static (long value) => value,
            SqlFunctionVolatility.Immutable, SqlNullBehavior.CalledOnNullInput));

        // Assert
        nz.NullBehavior.ShouldBe(SqlNullBehavior.CalledOnNullInput);
        rows.ShouldBe([[0L, null], [1L, 1L]]);
        counted.ShouldBe([[2L, 1L]]);
        refused.Message.ShouldBe(
            "Function 'bad' is called on NULL input, but argument 1 is 'Int64', which cannot receive NULL; declare it 'Int64?'. (Parameter 'nullBehavior')");
    }

    /// <summary>
    /// A binary argument is read-only: a typed function receives a copy, and a leaf reads the bytes
    /// through a read-only view, so no function can change a stored row's value or a caller's
    /// parameter.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a function cannot change a binary argument")]
    public async Task ExecuteAsync_BinaryArgument_ShouldBeReadOnly()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("mutate", static (byte[] bytes) => { Array.Fill(bytes, (byte)0xEE); return (long)bytes.Length; })));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE m (id INT, bin BYTEA)");
        byte[] parameter = [1, 2, 3];
        await ExecuteAsync(session, "INSERT INTO m VALUES (1, @bin)", new System.Collections.Generic.Dictionary<string, object?> { ["bin"] = new byte[] { 9, 9 } });

        // Act
        await ExecuteAsync(session, "UPDATE m SET bin = bin WHERE mutate(bin) IS NOT NULL");
        var stored = await RowsAsync(session, "SELECT bin FROM m");
        var length = await RowsAsync(session, "SELECT mutate(@b) FROM m", new System.Collections.Generic.Dictionary<string, object?> { ["b"] = parameter });

        // Assert
        stored.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBe(new byte[] { 9, 9 });
        length.ShouldBe([[3L]]);
        parameter.ShouldBe(new byte[] { 1, 2, 3 });
        SqlValue.FromBinary([4, 5]).AsBinary().ToArray().ShouldBe(new byte[] { 4, 5 });
    }

    /// <summary>
    /// REAL widens to DOUBLE implicitly, losslessly, as PostgreSQL's float4 does to float8, so a REAL
    /// column reaches a function over DOUBLE; nothing widens to REAL.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: REAL widens to DOUBLE")]
    public async Task ExecuteAsync_RealArgument_ShouldWidenToDouble()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("twice", static (double value) => value * 2, SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("half", static (float value) => value / 2, SqlFunctionVolatility.Immutable)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE r (v REAL)");
        await ExecuteAsync(session, "INSERT INTO r VALUES (1.5)");

        // Act
        var twice = await RowsAsync(session, "SELECT twice(v), half(v) FROM r");
        var literal = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT half(1.5) FROM r"));

        // Assert
        twice.ShouldBe([[3.0, 0.75f]]);
        literal.Message.ShouldStartWith($"{FunctionSignatureMismatch}: Function 'half' has no overload that accepts argument types (NUMERIC).", Case.Sensitive);
    }

    /// <summary>
    /// The pieces an application leaf needs to do what the built-ins do are public: a never-NULL
    /// result reported in a projection's metadata, the numeric pseudo-type, the engine's value order,
    /// and a context a test can construct; and the safe volatility is the default value.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: an application leaf can do what the built-ins do")]
    public async Task Abi_PublicSurface_ShouldMatchWhatTheBuiltInsUse()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions.Add(new CountPositiveFunction()));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (n BIGINT)");

        // Act
        await using var result = (await ExecuteAsync(session, "SELECT count_positive(n) AS c FROM t")).ShouldBeAssignableTo<QueryResultSet>();
        var column = result.Columns.ShouldHaveSingleItem();
        var described = InvokeWithContext(new ContextFunction(), Collation.CaseInsensitive);

        // Assert
        column.IsNullable.ShouldBeFalse();
        new CountPositiveFunction().IsNeverNull.ShouldBeTrue();
        SqlType.AnyNumeric.IsPseudo.ShouldBeTrue();
        default(SqlFunctionVolatility).ShouldBe(SqlFunctionVolatility.Volatile);
        SqlValue.Compare(SqlValue.FromInt32(2), SqlValue.FromDecimal(10m)).ShouldBeLessThan(0);
        SqlValue.Compare(SqlValue.FromString("a"), SqlValue.FromString("A"), Collation.CaseInsensitive).ShouldBe(0);
        Should.Throw<ArgumentException>(() => SqlValue.Compare(SqlValue.Null, SqlValue.FromInt32(1)));
        described.AsString().ShouldBe($"/{Collation.CaseInsensitive.Name}");
        Should.Throw<InvalidCastException>(() => SqlValue.FromInt32(1).AsInt64()).Message
            .ShouldBe("The value is INTEGER, not BIGINT; read it with the accessor of its type.");
    }

    /// <summary>
    /// A one-argument call, scalar or aggregate, converts its argument in the frame that makes the
    /// coded call: a column of a narrower type widens to the parameter's, a column of the parameter's
    /// type passes as it is, an integer literal the planner typed by its magnitude narrows to it, and
    /// NULL skips a strict function, for an application's function as for a built-in.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a one-argument call converts its argument to the parameter's type")]
    public async Task ExecuteAsync_OneArgumentConversion_ShouldGiveTheFunctionItsParameterType()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(SqlScalarFunction.Create("plus_one", static (int value) => value + 1))
            .Add(SqlScalarFunction.Create("as_wide", static (long value) => value))
            .Add(SqlAggregateFunction.Create<long, int, long>("sum_int", static () => 0L,
                static (long state, int value) => state + value, static (long state) => state))
            .Add(SqlAggregateFunction.Create<long, long, long>("sum_wide", static () => 0L,
                static (long state, long value) => state + value, static (long state) => state)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (small INT, big BIGINT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 10), (3, 30), (NULL, NULL)");

        // Act
        var scalars = await RowsAsync(session,
            "SELECT as_wide(small), as_wide(big), plus_one(small), plus_one(5), UPPER(small), ABS(small) FROM t WHERE big IS NOT NULL ORDER BY big");
        var nulls = await RowsAsync(session, "SELECT as_wide(small), plus_one(small), UPPER(small), ABS(small) FROM t WHERE big IS NULL");
        var aggregates = await RowsAsync(session, "SELECT sum_wide(small), sum_wide(big), sum_int(small), sum_int(5), MAX(small) FROM t");

        // Assert
        scalars.ShouldBe([[1L, 10L, 2, 6, 1, 1L], [3L, 30L, 4, 6, 3, 3L]]);
        nulls.ShouldBe([[null, null, null, null]]);
        aggregates.ShouldBe([[4L, 40L, 4L, 15L, 3]]);
    }

    /// <summary>
    /// The engine's one-argument entry points convert the argument before the coded call, so an
    /// argument that does not fit or does not convert is the engine's fault and never a
    /// <c>COHSQLE007</c> that blames the function: the scalar's overflow reaches the evaluator,
    /// which codes it <c>COHSQLE002</c>; the accumulator's is <c>COHSQLE002</c> already; a type that
    /// does not convert is a plain database exception; and only what the function itself throws is
    /// <c>COHSQLE007</c>. SQL cannot reach the faults (the planner types literals and parameters, and
    /// refuses a mismatch as <c>COHSQLE006</c>), so without this an edit that moved the conversion
    /// inside the coded call would pass every statement test.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a one-argument conversion fault is the engine's, not the function's")]
    public void InvokeResolved_ArgumentConversionFault_ShouldNotBlameTheFunction()
    {
        // Arrange
        var scalar = SqlScalarFunction.Create("small_echo", static (short value) => value);
        var failingScalar = SqlScalarFunction.Create("small_failing", static short (short value) => throw new InvalidOperationException("no echo"));
        var aggregate = SqlAggregateFunction.Create<long, short, long>("small_sum", static () => 0L,
            static (long state, short value) => state + value, static (long state) => state);
        var failingAggregate = SqlAggregateFunction.Create<long, short, long>("small_failing_sum", static () => 0L,
            static long (long state, short value) => throw new InvalidOperationException("no sum"), static (long state) => state);
        var context = new SqlFunctionContext(default, Collation.Binary, CancellationToken.None);
        var accumulator = aggregate.CreateAccumulator(in context);
        var failingAccumulator = failingAggregate.CreateAccumulator(in context);

        // Act
        object? echoed = scalar.InvokeResolved(7L, DatabaseType.Int16, default, Collation.Binary, CancellationToken.None);
        var scalarOverflow = Should.Throw<OverflowException>(() =>
            scalar.InvokeResolved(40000L, DatabaseType.Int16, default, Collation.Binary, CancellationToken.None));
        var scalarMismatch = Should.Throw<DatabaseException>(() =>
            scalar.InvokeResolved("x", DatabaseType.Int16, default, Collation.Binary, CancellationToken.None));
        var scalarFailure = Should.Throw<DatabaseException>(() =>
            failingScalar.InvokeResolved(7L, DatabaseType.Int16, default, Collation.Binary, CancellationToken.None));
        accumulator.AddResolved(7L, DatabaseType.Int16, default, Collation.Binary, CancellationToken.None);
        var aggregateOverflow = Should.Throw<DatabaseException>(() =>
            accumulator.AddResolved(40000L, DatabaseType.Int16, default, Collation.Binary, CancellationToken.None));
        var aggregateMismatch = Should.Throw<DatabaseException>(() =>
            accumulator.AddResolved("x", DatabaseType.Int16, default, Collation.Binary, CancellationToken.None));
        var aggregateFailure = Should.Throw<DatabaseException>(() =>
            failingAccumulator.AddResolved(7L, DatabaseType.Int16, default, Collation.Binary, CancellationToken.None));

        // Assert
        echoed.ShouldBe((short)7);
        scalarOverflow.ShouldBeOfType<OverflowException>();
        scalarOverflow.Message.ShouldBe("BIGINT 40000 does not fit argument 1 of function 'small_echo', which is SMALLINT.");
        scalarMismatch.ShouldBeOfType<DatabaseException>();
        scalarMismatch.Message.ShouldBe("Function 'small_echo' takes SMALLINT for argument 1, but the value is TEXT.");
        scalarFailure.Message.ShouldBe($"{FunctionFailed}: Function 'small_failing' failed: no echo");
        aggregateOverflow.Message.ShouldBe(
            "COHSQLE002: Numeric value out of range: BIGINT 40000 does not fit argument 1 of function 'small_sum', which is SMALLINT.");
        aggregateOverflow.InnerException.ShouldBeOfType<OverflowException>();
        aggregateMismatch.ShouldBeOfType<DatabaseException>();
        aggregateMismatch.Message.ShouldBe("Function 'small_sum' takes SMALLINT for argument 1, but the value is TEXT.");
        aggregateFailure.Message.ShouldBe($"{FunctionFailed}: Function 'small_failing_sum' failed: no sum");
        accumulator.Finish().ShouldBe(SqlValue.FromInt64(7));
    }

    /// <summary>
    /// A call of no arguments, a shape only an application's function has, takes the path every call
    /// takes, from a frame of its own as <c>COUNT(*)</c> adds through one: with no argument to be
    /// NULL a strict function is called on every row, an immutable one folds to one call, a result of
    /// another type fails the check, and what the function throws is <c>COHSQLE007</c>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a call of no arguments is made, checked and coded as every call is")]
    public async Task ExecuteAsync_NoArgumentCall_ShouldTakeThePathEveryCallTakes()
    {
        // Arrange
        long numbered = 0;
        long answered = 0;
        await using var engine = await BuildAsync(functions => functions
            .Add(new NoArgumentFunction("next_number", SqlFunctionVolatility.Volatile, () => SqlValue.FromInt64(Interlocked.Increment(ref numbered))))
            .Add(new NoArgumentFunction("answer", SqlFunctionVolatility.Immutable, () =>
            {
                Interlocked.Increment(ref answered);
                return SqlValue.FromInt64(42);
            }))
            .Add(new NoArgumentFunction("wrong_answer", SqlFunctionVolatility.Volatile, () => SqlValue.FromString("42")))
            .Add(new NoArgumentFunction("no_answer", SqlFunctionVolatility.Volatile, () => throw new InvalidOperationException("nothing to say"))));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (n BIGINT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2), (3)");

        // Act
        var numbers = await RowsAsync(session, "SELECT next_number() FROM t");
        var answers = await RowsAsync(session, "SELECT answer() FROM t");
        var wrong = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT wrong_answer() FROM t"));
        var failed = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT no_answer() FROM t"));

        // Assert
        numbers.Select(row => (long)row[0]!).Order().ToArray().ShouldBe([1L, 2L, 3L]);
        numbered.ShouldBe(3);
        answers.ShouldBe([[42L], [42L], [42L]]);
        answered.ShouldBe(1);
        wrong.Message.ShouldBe($"{FunctionFailed}: Function 'wrong_answer' failed: The function returned TEXT, but it declares BIGINT.");
        failed.Message.ShouldBe($"{FunctionFailed}: Function 'no_answer' failed: nothing to say");
    }

    // A test of a function that reads its context builds the context itself.
    private static SqlValue InvokeWithContext(SqlScalarFunction function, Collation collation)
    {
        var context = new SqlFunctionContext(default, collation, CancellationToken.None);
        return function.Invoke(new SqlArguments([SqlValue.FromString("x")], context));
    }

    /// <summary><c>length(ANYELEMENT)</c>: the same calls as the built-in <c>LENGTH(ANY)</c>.</summary>
    private sealed class PolymorphicLengthFunction : SqlScalarFunction
    {
        public PolymorphicLengthFunction()
            : base("length", [SqlType.AnyElement], SqlType.BigInt, SqlFunctionVolatility.Immutable)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromInt64(-1);
    }

    /// <summary><c>upper(TEXT, TEXT ...)</c>: variadic from one argument, so it takes <c>UPPER</c>'s one-argument calls.</summary>
    private sealed class VariadicUpperFunction : SqlScalarFunction
    {
        public VariadicUpperFunction()
            : base("upper", [SqlType.Text], SqlType.Text, SqlFunctionVolatility.Immutable, variadicParameter: SqlType.Text)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => arguments[0];
    }

    /// <summary><c>pick(ANYELEMENT, BIGINT)</c> returning its first argument: a typed parameter beside a polymorphic one.</summary>
    private sealed class PickFunction : SqlScalarFunction
    {
        public PickFunction()
            : base("pick", [SqlType.AnyElement, SqlType.BigInt], SqlType.AnyElement, SqlFunctionVolatility.Immutable)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => arguments[0];
    }

    /// <summary><c>first_of(ANYELEMENT, ANYELEMENT)</c>: two polymorphic arguments that must agree.</summary>
    private sealed class FirstOfFunction : SqlScalarFunction
    {
        public FirstOfFunction()
            : base("first_of", [SqlType.AnyElement, SqlType.AnyElement], SqlType.AnyElement, SqlFunctionVolatility.Immutable)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => arguments[0];
    }

    /// <summary>A <c>name(BIGINT)</c> leaf that returns whatever its delegate makes, to test the result check.</summary>
    private sealed class ReturningFunction : SqlScalarFunction
    {
        private readonly Func<long, SqlValue> _result;

        public ReturningFunction(string name, SqlType returnType, Func<long, SqlValue> result, bool neverNull = false)
            : base(name, [SqlType.BigInt], returnType, SqlFunctionVolatility.Volatile)
        {
            _result = result;
            IsNeverNull = neverNull;
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => _result(arguments.GetInt64(0));
    }

    /// <summary><c>name()</c> declared to return BIGINT, returning whatever its delegate makes: a function of no arguments.</summary>
    private sealed class NoArgumentFunction : SqlScalarFunction
    {
        private readonly Func<SqlValue> _result;

        public NoArgumentFunction(string name, SqlFunctionVolatility volatility, Func<SqlValue> result)
            : base(name, [], SqlType.BigInt, volatility)
        {
            _result = result;
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => arguments.Count == 0 ? _result() : throw new InvalidOperationException($"Called with {arguments.Count} arguments.");
    }

    /// <summary><c>element_of(ANYELEMENT)</c> declared to return its argument's type, returning text.</summary>
    private sealed class ElementFunction : SqlScalarFunction
    {
        public ElementFunction()
            : base("element_of", [SqlType.AnyElement], SqlType.AnyElement)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromString("text");
    }

    /// <summary><c>lying_sum(BIGINT)</c> declared to return BIGINT, finishing with text.</summary>
    private sealed class LyingAggregateFunction : SqlAggregateFunction
    {
        public LyingAggregateFunction()
            : base("lying_sum", [SqlType.BigInt], SqlType.BigInt)
        {
        }

        protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

        private sealed class Accumulator : SqlAggregateAccumulator
        {
            protected override void AddCore(scoped in SqlArguments arguments)
            {
            }

            protected override SqlValue FinishCore() => SqlValue.FromString("total");
        }
    }

    /// <summary><c>count_positive(BIGINT)</c>: 0 for an empty group, so it declares that it never returns NULL, as <c>COUNT</c> does.</summary>
    private sealed class CountPositiveFunction : SqlAggregateFunction
    {
        public CountPositiveFunction()
            : base("count_positive", [SqlType.BigInt], SqlType.BigInt)
        {
            IsNeverNull = true;
        }

        protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

        private sealed class Accumulator : SqlAggregateAccumulator
        {
            private long _count;

            protected override void AddCore(scoped in SqlArguments arguments) => _count += arguments.GetInt64(0) > 0 ? 1 : 0;

            protected override SqlValue FinishCore() => SqlValue.FromInt64(_count);
        }
    }
}
