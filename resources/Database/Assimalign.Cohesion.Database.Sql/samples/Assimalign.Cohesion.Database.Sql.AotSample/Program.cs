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
/// The NativeAOT guard for the SQL engine's function ABI and its types (phases E2 and E3 of
/// <c>docs/programs/DATABASE_ENGINE_EXTENSIBILITY_DESIGN.md</c>). It registers a typed scalar of each
/// <c>Create</c> arity, a typed aggregate over a reference-type state, a hand-written scalar and
/// aggregate leaf, and a strict and a non-strict function; a domain with a check and a domain with
/// application casts to and from it; declares a schema whose <c>table.Check</c> calls a registered
/// function and whose column is of a domain; and runs the same statements in process and over the
/// wire through <c>Sql.Client</c>. It prints one line per check and exits 1 when any fails, so a
/// trimmed or AOT-compiled build that loses a code path fails the run, not only the build.
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
        [["clamp"], ["describe"], ["in_range"], ["is_missing"], ["join_names"], ["median"], ["or_none"], ["product"], ["scaled"], ["slugify"], ["sql_increment"]];

    // E3: a domain column, an application's casts to and from the cents domain (the inner CAST tells
    // the outer one its source is cents), built-in casts from text to DATE and from UUID to canonical TEXT.
    private const string Domains =
        "SELECT Email, CAST(CAST(Score AS cents) AS TEXT) AS shown, CAST('$4.56' AS cents) AS parsed, " +
        "CAST('2026-02-01' AS DATE) AS day, CAST(Token AS TEXT) AS token FROM customers ORDER BY Id";

    private static readonly object?[][] _domains =
    [
        ["ada@example.com", "$1.00", 456L, new DateOnly(2026, 2, 1), "00000001-0000-0000-0000-000000000000"],
        ["grace@example.com", "$2.00", 456L, new DateOnly(2026, 2, 1), "00000002-0000-0000-0000-000000000000"],
    ];

    private const string allCasts = "SELECT CAST(CAST('0.8455124082255701' AS DOUBLE PRECISION) AS NUMERIC), " +
        "CAST(DATE '2024-01-01' AS TIMESTAMP), CAST(TIMESTAMPTZ '2024-01-01 01:00:00+02:00' AS TIME), " +
        "CAST(TIME '01:02:03' AS INTERVAL), CAST(CAST('\\x00ff' AS BYTEA) AS TEXT), " +
        "CAST(CAST('{\"雪\":1}' AS JSONB) AS JSON), TRUE || '', " +
        "CAST(CAST('1.0000000596046448' AS REAL) AS TEXT) FROM customers WHERE Id = 1";
    private static readonly object?[][] _allCasts =
        [[0.8455124082255701m, new DateTime(2024, 1, 1), new TimeOnly(23, 0), new TimeSpan(1, 2, 3), "\\x00ff", "{\"雪\":1}", "TRUE", "1.0000001"]];

    private const string Characters = "SELECT c, CAST(c AS TEXT), LENGTH(c), c || 'b', c LIKE 'a' FROM fixed_codes WHERE c = 'a'";
    private static readonly object?[][] _characters = [["a  ", "a", 1L, "ab", false]];

    private const string typeAliases = "SELECT n, CAST(s AS CHARACTER VARYING(5)), CAST(f AS DOUBLE PRECISION), " +
        "CAST(NULL AS DATETIME2), CAST(NULL AS DATETIMEOFFSET), CAST(NULL AS UNIQUEIDENTIFIER) FROM type_aliases";
    private static readonly object?[][] _typeAliases = [["ab ", "text", 1.5d, null, null, null]];

    private const string typedLiterals = "SELECT DATE '2024-01-01', TIME '10:20:30.1234567', " +
        "TIMESTAMP '2024-01-01 10:00:00', TIMESTAMPTZ '2024-01-01 12:00:00+02:00', " +
        "INTERVAL 'P1DT2H', X'00ff', N'héllo' FROM customers WHERE Id = 1";
    private static readonly object?[][] _typedLiterals =
    [[new DateOnly(2024, 1, 1), new TimeOnly(10, 20, 30).Add(TimeSpan.FromTicks(1234567)),
        new DateTime(2024, 1, 1, 10, 0, 0), new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)),
        TimeSpan.FromHours(26), new byte[] { 0, 255 }, "héllo"]];

    // E3: a CAST no registered cast performs is refused while planning, rows or not.
    private const string expandedJoin = "SELECT a.Id, c.Id FROM customers a JOIN customers b USING (Id) " +
        "LEFT JOIN customers c ON b.Id = c.Id AND c.Id = 1 ORDER BY a.Id";
    private const string crossJoin = "SELECT COUNT(*) FROM customers a, customers b CROSS JOIN customers c";

    private const string CannotCast = "SELECT CAST(Active AS DATE) FROM customers WHERE Id < 0";

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
        SqlType cents = SqlType.CreateDomain("cents", SqlType.BigInt);
        sql.Types
            .Add(SqlType.CreateDomain("email", SqlType.VarChar(320),
                SqlScalarFunction.Create("is_email", static (string value) => value.Contains('@'), SqlFunctionVolatility.Immutable)))
            .Add(cents)
            .AddCast(SqlCast.Create<string, long>(SqlType.Text, cents,
                static text => (long)(decimal.Parse(text.TrimStart('$'), CultureInfo.InvariantCulture) * 100), SqlCastContext.Explicit))
            .AddCast(SqlCast.Create<long, string>(cents, SqlType.Text,
                static value => "$" + (value / 100m).ToString("0.00", CultureInfo.InvariantCulture), SqlCastContext.Assignment));
        sql.AddDatabase(DatabaseName, database => database.Schema(schema => schema.Table<Customer>("customers", table =>
        {
            table.Key(customer => customer.Id);
            table.Column(customer => customer.Name);
            table.Column(customer => customer.Score);
            table.Column(customer => customer.Active);
            table.Column(customer => customer.Token);
            table.Column(customer => customer.Note);
            table.Column(customer => customer.Email, "email");
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
            "INSERT INTO customers (Id, Name, Score, Active, Token, Note, Email) VALUES " +
            "(1, 'Ada Lovelace', 100, TRUE, @first, NULL, 'ada@example.com'), (2, 'Grace Hopper', 200, FALSE, @second, 'admiral', 'grace@example.com')",
            new Dictionary<string, object?> { ["first"] = Token(1), ["second"] = Token(2) },
            CancellationToken.None);

        await session.ExecuteAsync("CREATE FUNCTION sql_increment(p INT) RETURNS INT IMMUTABLE RETURN p + 1");
        await session.ExecuteAsync("CREATE DOMAIN sql_positive AS INT CHECK (VALUE > 0)");
        await session.ExecuteAsync("CREATE TABLE sql_defined (n sql_positive DEFAULT sql_increment(2))");
        await session.ExecuteAsync("INSERT INTO sql_defined VALUES (sql_increment(3))");
        await session.ExecuteAsync("CREATE TABLE fixed_codes (c CHAR(3) PRIMARY KEY)");
        await session.ExecuteAsync("INSERT INTO fixed_codes VALUES ('a')");
        await session.ExecuteAsync("CREATE TABLE type_aliases (n NCHAR(3), s NVARCHAR(MAX), f DOUBLE PRECISION, d TIMESTAMP WITH TIME ZONE)");
        await session.ExecuteAsync("INSERT INTO type_aliases (n, s, f) VALUES ('ab', 'text', 1.5)");
        Check("in process: all CAST families", await RowsAsync(session, allCasts), _allCasts);
        Check("in process: three-way USING and LEFT", await RowsAsync(session, expandedJoin), [[1L, 1L], [2L, null]]);
        Check("in process: comma and CROSS", await RowsAsync(session, crossJoin), [[8L]]);
        Check("in process: strict typed literals", await RowsAsync(session, typedLiterals), _typedLiterals);
        Check("in process: ISO type names and SQL Server lexical aliases", await RowsAsync(session, typeAliases), _typeAliases);
        Check("in process: CHAR storage, cast, length, concat, LIKE and seek", await RowsAsync(session, Characters), _characters);
        await session.ExecuteAsync("CREATE TABLE default_guard (id INT DEFAULT 7 NOT NULL, label TEXT DEFAULT 'seed', note INT)");
        await session.ExecuteAsync("INSERT INTO default_guard (label, id) VALUES (DEFAULT, 1), (NULL, 2)");
        await session.ExecuteAsync("INSERT INTO default_guard DEFAULT VALUES");
        await session.ExecuteAsync("UPDATE default_guard SET label = DEFAULT, note = DEFAULT WHERE id = 2");
        Check("in process: DEFAULT items, DEFAULT VALUES and UPDATE DEFAULT",
            await RowsAsync(session, "SELECT id, label, note FROM default_guard ORDER BY id"),
            [[1, "seed", null], [2, "seed", null], [7, "seed", null]]);
        Check("in process: SQL function and domain", await RowsAsync(session, "SELECT sql_increment(n) FROM sql_defined"), [[5]]);
        var explained = await RowsAsync(session, "EXPLAIN SELECT c FROM fixed_codes WHERE c = 'a'");
        Check("in process: EXPLAIN retains index and shared columns", explained.Any(row => Equals(row[2], "IndexSeek") && row[4] is string && row.Length == 8), true);
        await RowsAsync(session, "EXPLAIN DELETE FROM default_guard");
        Check("in process: EXPLAIN DELETE has no effects", await RowsAsync(session, "SELECT COUNT(*) FROM default_guard"), [[3L]]);
        await session.ExecuteAsync("CREATE TABLE binary_keys (key_bytes VARBINARY(4))");
        await session.ExecuteAsync("CREATE INDEX by_bytes ON binary_keys (key_bytes)");
        await using (var description = (DatabaseResultSet)await session.ExecuteAsync(
            "EXPLAIN SELECT key_bytes FROM binary_keys WHERE key_bytes = @key",
            new Dictionary<string, object?> { ["key"] = new byte[] { 0, 0xAB, 0xFF } }, CancellationToken.None))
        {
            bool describedBytes = false;
            await foreach (var row in description.GetRowsAsync(CancellationToken.None))
            {
                describedBytes |= row.GetString(2) == "IndexSeek" && row.GetString(5) == "key_bytes = X'00ABFF'";
            }
            Check("in process: EXPLAIN binary bounds", describedBytes, true);
        }
        Check("in process: scalars of every arity, strict and non-strict", await RowsAsync(session, Projection), _projection);
        Check("in process: typed and hand-written aggregates", await RowsAsync(session, Aggregates), _aggregates);
        Check("in process: COHESION_SCHEMA.FUNCTIONS", await RowsAsync(session, Registered), _registered);
        Check("in process: a domain column, application casts and a cast to DATE", await RowsAsync(session, Domains), _domains);
        CheckFailure("in process: the domain's check refuses a row",
            await FailureAsync(() => session.ExecuteAsync(DomainRefusedInsert, RefusedParameters, CancellationToken.None).AsTask()),
            "DOMAIN constraint 'email' on 'dbo.customers' was violated");
        CheckFailure("in process: the declared CHECK refuses a row",
            await FailureAsync(() => session.ExecuteAsync(RefusedInsert, RefusedParameters, CancellationToken.None).AsTask()),
            "CHECK constraint 'ck_name' on 'dbo.customers' was violated");
        CheckFailure("in process: a throwing function is COHSQLE007",
            await FailureAsync(() => RowsAsync(session, "SELECT clamp(1, 5, 0) FROM customers")),
            "COHSQLE007: Function 'clamp' failed: '5' cannot be greater than 0.");
        CheckFailure("in process: a CAST no cast performs is COHSQLE011 while planning",
            await FailureAsync(() => RowsAsync(session, CannotCast)),
            "COHSQLE011: Cannot CAST type BOOLEAN to DATE");
    }

    // A row whose name slugifies to nothing, which the declared CHECK refuses.
    private const string RefusedInsert = "INSERT INTO customers (Id, Name, Score, Active, Token) VALUES (3, '!!!', 1, TRUE, @token)";

    // A row whose email the email domain's check refuses.
    private const string DomainRefusedInsert =
        "INSERT INTO customers (Id, Name, Score, Active, Token, Email) VALUES (4, 'Mary Somerville', 1, TRUE, @token, 'nobody')";

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

        await connection.ExecuteAsync("CREATE TABLE default_wire (id INT DEFAULT 7 NOT NULL, label TEXT DEFAULT 'seed', note INT)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO default_wire (label, id) VALUES (DEFAULT, 1), (NULL, 2)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO default_wire (label) DEFAULT VALUES", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("UPDATE default_wire SET label = DEFAULT, note = DEFAULT WHERE id = 2", cancellationToken: timeout.Token);
        Check("over the wire: DEFAULT items, DEFAULT VALUES and UPDATE DEFAULT",
            await RowsAsync(connection, "SELECT id, label, note FROM default_wire ORDER BY id", timeout.Token),
            [[1, "seed", null], [2, "seed", null], [7, "seed", null]]);

        Check("over the wire: CHAR storage, cast, length, concat, LIKE and seek", await RowsAsync(connection, Characters, timeout.Token), _characters);
        var explained = await RowsAsync(connection, "EXPLAIN SELECT c FROM fixed_codes WHERE c = 'a'", timeout.Token);
        Check("over the wire: EXPLAIN uses existing result rows", explained.Any(row => Equals(row[2], "IndexSeek") && row[4] is string && row.Length == 8), true);
        Check("over the wire: all CAST families", await RowsAsync(connection, allCasts, timeout.Token), _allCasts);
        Check("over the wire: three-way USING and LEFT", await RowsAsync(connection, expandedJoin, timeout.Token), [[1L, 1L], [2L, null]]);
        Check("over the wire: comma and CROSS", await RowsAsync(connection, crossJoin, timeout.Token), [[8L]]);
        Check("over the wire: strict typed literals", await RowsAsync(connection, typedLiterals, timeout.Token), _typedLiterals);
        Check("over the wire: ISO type names and SQL Server lexical aliases", await RowsAsync(connection, typeAliases, timeout.Token), _typeAliases);
        var fixedCodes = await connection.QueryAsync(Characters, cancellationToken: timeout.Token);
        Check("over the wire: CHAR type", fixedCodes.Columns[0].Type == Types.DatabaseType.Character, true);
        Check("over the wire: CHAR length", fixedCodes.Columns[0].MaxLength == 3, true);
        Check("over the wire: scalars of every arity, strict and non-strict", await RowsAsync(connection, Projection, timeout.Token), _projection);
        Check("over the wire: typed and hand-written aggregates", await RowsAsync(connection, Aggregates, timeout.Token), _aggregates);
        Check("over the wire: COHESION_SCHEMA.FUNCTIONS", await RowsAsync(connection, Registered, timeout.Token), _registered);
        Check("over the wire: a domain column, application casts and a cast to DATE", await RowsAsync(connection, Domains, timeout.Token), _domains);
        CheckFailure("over the wire: the domain's check refuses a row",
            await FailureAsync(() => connection.ExecuteAsync(DomainRefusedInsert, RefusedParameters, timeout.Token).AsTask()),
            "DOMAIN constraint 'email' on 'dbo.customers' was violated");
        CheckFailure("over the wire: the declared CHECK refuses a row",
            await FailureAsync(() => connection.ExecuteAsync(RefusedInsert, RefusedParameters, timeout.Token).AsTask()),
            "CHECK constraint 'ck_name' on 'dbo.customers' was violated");
        CheckFailure("over the wire: a throwing function is COHSQLE007",
            await FailureAsync(() => RowsAsync(connection, "SELECT clamp(1, 5, 0) FROM customers", timeout.Token)),
            "COHSQLE007: Function 'clamp' failed: '5' cannot be greater than 0.");
        CheckFailure("over the wire: a CAST no cast performs is COHSQLE011 while planning",
            await FailureAsync(() => RowsAsync(connection, CannotCast, timeout.Token)),
            "COHSQLE011: Cannot CAST type BOOLEAN to DATE");
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
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
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
            // Exercise the plain-data diagnostics carrier under NativeAOT too. The wire client
            // already received the server's composed text; in process the code is structured.
            return exception is DatabaseException { Diagnostics.Count: > 0 } failure
                ? $"{failure.Diagnostics[0].Code}: {failure.Message}" : exception.Message;
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
            byte[] bytes => Convert.ToHexString(bytes) + ":Binary",
            decimal number => number.ToString("0.############", CultureInfo.InvariantCulture) + ":Decimal",
            _ => string.Create(CultureInfo.InvariantCulture, $"{value}:{value.GetType().Name}"),
        }))));

    private sealed record Customer(long Id, string Name, long Score, bool Active, Guid Token, string? Note, string? Email);
}
