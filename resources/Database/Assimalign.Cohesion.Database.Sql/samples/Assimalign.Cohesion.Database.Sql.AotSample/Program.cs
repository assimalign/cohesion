using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.AotSample;

/// <summary>
/// The NativeAOT guard for the SQL engine's function ABI (phase E2 of
/// <c>docs/programs/DATABASE_ENGINE_EXTENSIBILITY_DESIGN.md</c>). It registers a typed scalar of each
/// <c>Create</c> arity, a typed aggregate over a reference-type state, a hand-written scalar and
/// aggregate leaf, and a strict and a non-strict function; declares a schema whose <c>table.Check</c>
/// calls a registered function; and runs the same statements in process and over the wire through
/// <c>Sql.Client</c>. It prints one line per check and exits 1 when any fails, so a trimmed or
/// AOT-compiled build that loses a code path fails the run, not only the build.
/// </summary>
internal static class Program
{
    private const string EngineName = "aot-sql";
    private const string DatabaseName = "aot";

    private const string Projection =
        "SELECT Id, slugify(Name) AS slug, scaled(Score, 1.5) AS scaled, in_range(Score, 50, 150) AS ranged, " +
        "describe(Name, Score, Active, Token) AS described, clamp(Score, 0, 150) AS clamped, is_missing(Note) AS missing, " +
        "slugify(Note) AS note_slug, or_none(Note) AS note_or FROM customers ORDER BY Id";

    private const string Aggregates = "SELECT product(Score) AS p, median(Score) AS m, join_names(Name) AS names, COUNT(*) AS c FROM customers";

    private const string Registered = "SELECT FUNCTION_NAME FROM COHESION_SCHEMA.FUNCTIONS WHERE IS_BUILT_IN = 'NO' ORDER BY FUNCTION_NAME";

    private static readonly object?[][] _projection =
    [
        [1L, "ada-lovelace", 150.0m, true, "Ada Lovelace:100:on:00000001000000000000000000000000", 100L, true, null, "none"],
        [2L, "grace-hopper", 300.0m, false, "Grace Hopper:200:off:00000002000000000000000000000000", 150L, false, "admiral", "admiral"],
    ];

    private static readonly object?[][] _aggregates = [[20000L, 150.0, "Ada Lovelace,Grace Hopper", 2L]];

    private static readonly object?[][] _registered =
        [["clamp"], ["describe"], ["in_range"], ["is_missing"], ["join_names"], ["median"], ["or_none"], ["product"], ["scaled"], ["slugify"]];

    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.WriteLine($"aot={!RuntimeFeature.IsDynamicCodeSupported} os={Environment.OSVersion.VersionString}");
        try
        {
            var listener = new InMemoryConnectionListener();
            await using (var engine = await BuildAsync(listener))
            {
                await RunInProcessAsync(engine);
                await RunOverTheWireAsync(listener);
            }

            await listener.DisposeAsync();
        }
        catch (Exception exception)
        {
            Fail("unexpected failure", exception.ToString());
        }

        Console.WriteLine($"smoke: {(_failed == 0 ? "passed" : "FAILED")} {_passed} of {_passed + _failed} checks (in process and over the wire)");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>Builds the engine: the functions, the declared database with its CHECK, and a server on the in-memory driver.</summary>
    private static async Task<SqlDatabaseEngine> BuildAsync(InMemoryConnectionListener listener)
    {
        SqlDatabaseEngineBuilder sql = SqlDatabaseEngine.CreateBuilder(EngineName);
        sql.Functions
            .Add(SqlScalarFunction.Create("slugify", static (string text) => Slugify(text), SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("scaled", static (long value, decimal factor) => value * factor, SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("in_range", static (long value, long low, long high) => value >= low && value <= high,
                SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("describe", static (string name, long score, bool active, Guid token) =>
                string.Create(CultureInfo.InvariantCulture, $"{name}:{score}:{(active ? "on" : "off")}:{token:N}"), SqlFunctionVolatility.Stable))
            .Add(SqlAggregateFunction.Create<long, long, long>("product",
                static () => 1L, static (long state, long value) => checked(state * value), static (long state) => state))
            .Add(SqlAggregateFunction.Create<List<string>, string, string>("join_names",
                static () => [], static (List<string> names, string name) => { names.Add(name); return names; },
                static (List<string> names) => string.Join(',', names.Order(StringComparer.Ordinal))))
            // Typed and called on NULL input: the delegate receives null for a NULL argument.
            .Add(SqlScalarFunction.Create("or_none", static (string? note) => note ?? "none", SqlFunctionVolatility.Immutable,
                SqlNullBehavior.CalledOnNullInput))
            .Add(new ClampFunction())
            .Add(new IsMissingFunction())
            .Add(new MedianFunction());
        sql.AddDatabase(DatabaseName, database => database.Schema(schema => schema.Table<Customer>("customers", table =>
        {
            table.Key(customer => customer.Id);
            table.Column(customer => customer.Name);
            table.Column(customer => customer.Score);
            table.Column(customer => customer.Active);
            table.Column(customer => customer.Token);
            table.Column(customer => customer.Note);
            table.Check("ck_name", "slugify(Name) <> ''");
        })));
        sql.AddServer(options => options.Listener = listener);

        SqlDatabaseEngine engine = await sql.BuildAsync();
        Check("build declared the database", engine.TryGetDatabase(DatabaseName, out _), true);
        await engine.Servers[0].StartAsync();
        return engine;
    }

    private static async Task RunInProcessAsync(SqlDatabaseEngine engine)
    {
        engine.TryGetDatabase(DatabaseName, out SqlDatabase? database);
        await using SqlDatabaseSession session = await database!.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(
            "INSERT INTO customers (Id, Name, Score, Active, Token, Note) VALUES " +
            "(1, 'Ada Lovelace', 100, TRUE, @first, NULL), (2, 'Grace Hopper', 200, FALSE, @second, 'admiral')",
            new Dictionary<string, object?> { ["first"] = Token(1), ["second"] = Token(2) },
            CancellationToken.None);

        Check("in process: scalars of every arity, strict and non-strict", await RowsAsync(session, Projection), _projection);
        Check("in process: typed and hand-written aggregates", await RowsAsync(session, Aggregates), _aggregates);
        Check("in process: COHESION_SCHEMA.FUNCTIONS", await RowsAsync(session, Registered), _registered);
        CheckFailure("in process: the declared CHECK refuses a row",
            await FailureAsync(() => session.ExecuteAsync(RefusedInsert, RefusedParameters, CancellationToken.None).AsTask()),
            "CHECK constraint 'ck_name' on 'dbo.customers' was violated");
        CheckFailure("in process: a throwing function is COHSQLE007",
            await FailureAsync(() => RowsAsync(session, "SELECT clamp(1, 5, 0) FROM customers")),
            "COHSQLE007: Function 'clamp' failed: '5' cannot be greater than 0.");
    }

    // A row whose name slugifies to nothing, which the declared CHECK refuses.
    private const string RefusedInsert = "INSERT INTO customers (Id, Name, Score, Active, Token) VALUES (3, '!!!', 1, TRUE, @token)";

    private static Dictionary<string, object?> RefusedParameters => new() { ["token"] = Token(3) };

    private static Guid Token(int value) => new(value, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static async Task RunOverTheWireAsync(InMemoryConnectionListener listener)
    {
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = DatabaseName, Principal = "aot", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using SqlConnection connection = await client.ConnectAsync(timeout.Token);

        Check("over the wire: scalars of every arity, strict and non-strict", await RowsAsync(connection, Projection, timeout.Token), _projection);
        Check("over the wire: typed and hand-written aggregates", await RowsAsync(connection, Aggregates, timeout.Token), _aggregates);
        Check("over the wire: COHESION_SCHEMA.FUNCTIONS", await RowsAsync(connection, Registered, timeout.Token), _registered);
        CheckFailure("over the wire: the declared CHECK refuses a row",
            await FailureAsync(() => connection.ExecuteAsync(RefusedInsert, RefusedParameters, timeout.Token).AsTask()),
            "CHECK constraint 'ck_name' on 'dbo.customers' was violated");
        CheckFailure("over the wire: a throwing function is COHSQLE007",
            await FailureAsync(() => RowsAsync(connection, "SELECT clamp(1, 5, 0) FROM customers", timeout.Token)),
            "COHSQLE007: Function 'clamp' failed: '5' cannot be greater than 0.");
        Check("over the wire: the parameterized call folds",
            await RowsAsync(connection, "SELECT slugify(@text) AS s FROM customers WHERE Id = 1", timeout.Token,
                new Dictionary<string, object?> { ["text"] = " Hello, World " }),
            [["hello-world"]]);
    }

    /// <summary>Lower case, letters and digits kept, every run of spaces one hyphen, everything else dropped.</summary>
    private static string Slugify(string text)
    {
        var slug = new StringBuilder(text.Length);
        foreach (char character in text.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
            }
            else if (character == ' ' && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int field = 0; field < values.Length; field++)
            {
                values[field] = row.GetValue(field);
            }

            rows.Add(values);
        }

        return [.. rows];
    }

    private static async Task<object?[][]> RowsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        SqlResultSet result = await connection.QueryAsync(sql, parameters, cancellationToken);
        return [.. result.Select(static row => Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray())];
    }

    private static async Task<string> FailureAsync(Func<Task> statement)
    {
        try
        {
            await statement();
            return "(no failure)";
        }
        catch (Exception exception) when (exception is DatabaseException or SqlClientException)
        {
            return exception.Message;
        }
    }

    private static void Check(string name, object?[][] actual, object?[][] expected)
        => Check(name, Format(actual), Format(expected));

    private static void Check<T>(string name, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected))
        {
            _passed++;
            Console.WriteLine($"check {name}: ok");
        }
        else
        {
            Fail(name, $"expected {expected}, got {actual}");
        }
    }

    private static void CheckFailure(string name, string message, string expected)
    {
        if (message.Contains(expected, StringComparison.Ordinal))
        {
            _passed++;
            Console.WriteLine($"check {name}: ok");
        }
        else
        {
            Fail(name, $"expected a failure containing '{expected}', got '{message}'");
        }
    }

    private static void Fail(string name, string detail)
    {
        _failed++;
        Console.WriteLine($"check {name}: FAILED {detail}");
    }

    // Rows as text, each value with its CLR type, so a value the wire decodes as another type fails. A
    // decimal is written without trailing zeros: its scale is not what the check is about.
    private static string Format(object?[][] rows)
        => string.Join(" | ", rows.Select(static row => string.Join(", ", row.Select(static value => value switch
        {
            null => "NULL",
            decimal number => number.ToString("0.############", CultureInfo.InvariantCulture) + ":Decimal",
            _ => string.Create(CultureInfo.InvariantCulture, $"{value}:{value.GetType().Name}"),
        }))));

    private sealed record Customer(long Id, string Name, long Score, bool Active, Guid Token, string? Note);
}
