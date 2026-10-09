using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// An application's functions over the wire (phase E2 of the engine extensibility program, owner
/// decisions 60 to 66 and 71): through the real SQL server and the typed client, a registered scalar
/// and aggregate resolve, fold, short-circuit and fail like the built-ins; a CHECK admits only an
/// immutable function; a stored CHECK binds again when its database reopens; and once the function
/// is gone from the next engine's build, the database still opens and serves reads while each write
/// that would evaluate the CHECK fails with <c>COHSQLE009</c> on a connection that stays usable.
/// </summary>
public sealed class SqlFunctionExtensibilityWireTests
{
    /// <summary>A registered scalar and aggregate return the types the client decodes, and resolve, fold and short-circuit as in process.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Functions: registered functions resolve, fold and short-circuit over the wire")]
    public async Task QueryAsync_RegisteredFunctions_ShouldRunLikeBuiltIns()
    {
        // Arrange
        var doubled = new CountingFunction();
        await using var harness = await FunctionHarness.StartAsync(functions =>
        {
            Register(functions);
            functions.Add(doubled);
        });
        await using var connection = await harness.Client.ConnectAsync(Timeout());

        // Act
        SqlResultSet scalars = await connection.QueryAsync(
            "SELECT id, slugify(name) AS slug, clamp(score, 0, 150) AS clamped, pick(score) AS by_number, pick(name) AS by_text " +
            "FROM users ORDER BY id", cancellationToken: Timeout());
        SqlResultSet folded = await connection.QueryAsync("SELECT doubled(21) AS d, doubled(NULL) AS n FROM users", cancellationToken: Timeout());
        SqlResultSet aggregates = await connection.QueryAsync(
            "SELECT product(score) AS p, median(score) AS m, COUNT(*) AS c FROM users", cancellationToken: Timeout());
        SqlResultSet parameterized = await connection.QueryAsync("SELECT slugify(@text) AS s FROM users WHERE id = 1",
            new Dictionary<string, object?> { ["text"] = " Hello World " }, Timeout());

        // Assert
        scalars.Select(row => (row["id"], row["slug"], row["clamped"], row["by_number"], row["by_text"])).ShouldBe(
            [(1, "ada", 100L, "number", "text"), (2, "grace", 150L, "number", "text")]);
        folded.Select(row => (row["d"], row["n"])).ShouldBe([(42L, null), (42L, null)]);
        doubled.Calls.ShouldBe(1, "an immutable call over a constant is folded once, and a strict one over NULL is never called");
        SqlRow aggregate = aggregates.ShouldHaveSingleItem();
        (aggregate["p"], aggregate["m"], aggregate["c"]).ShouldBe((20000L, 150.0, 2L));
        parameterized.ShouldHaveSingleItem()["s"].ShouldBe("hello-world");
    }

    /// <summary>A function's failure, an ambiguous call and a call no overload accepts are coded execution failures on a usable connection.</summary>
    /// <param name="statement">The failing statement.</param>
    /// <param name="message">The complete failure message.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Functions: COHSQLE006, COHSQLE007 and COHSQLE008 are coded failures on a usable connection")]
    [InlineData("SELECT clamp(1, 5, 0) FROM users",
        "COHSQLE007: Function 'clamp' failed: '5' cannot be greater than 0.")]
    [InlineData("SELECT pick(NULL) FROM users",
        "COHSQLE008: Function call 'pick(unknown)' is ambiguous: pick(BIGINT) and pick(TEXT) accept it equally well. Cast an argument to choose one.")]
    [InlineData("SELECT pick(TRUE) FROM users",
        "COHSQLE006: Function 'pick' has no overload that accepts argument types (BOOLEAN). Accepted: pick(BIGINT) or pick(TEXT).")]
    public async Task QueryAsync_FunctionFailure_ShouldBeCodedOnAUsableConnection(string statement, string message)
    {
        // Arrange
        await using var harness = await FunctionHarness.StartAsync(Register);
        await using var connection = await harness.Client.ConnectAsync(Timeout());

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldBe(message);
        (await connection.QueryAsync("SELECT slugify(name) AS s FROM users WHERE id = 2", cancellationToken: Timeout()))
            .ShouldHaveSingleItem()["s"].ShouldBe("grace");
    }

    /// <summary>
    /// A CHECK admits only an immutable function (owner decision 64); one built on a registered
    /// function is enforced over the wire, and <c>COHESION_SCHEMA.FUNCTIONS</c> lists what the engine
    /// registered.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Functions: a CHECK over a registered function needs it immutable and is enforced over the wire")]
    public async Task ExecuteAsync_CheckOverRegisteredFunction_ShouldRequireImmutableAndEnforce()
    {
        // Arrange
        await using var harness = await FunctionHarness.StartAsync(Register);
        await using var connection = await harness.Client.ConnectAsync(Timeout());

        // Act
        SqlClientException refused = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "CREATE TABLE contacts (id INT, email TEXT, CONSTRAINT ck CHECK (is_email_volatile(email)))", cancellationToken: Timeout()));
        await connection.ExecuteAsync("CREATE TABLE contacts (id INT, email TEXT, CONSTRAINT ck_email CHECK (is_email(email)))",
            cancellationToken: Timeout());
        await connection.ExecuteAsync("INSERT INTO contacts VALUES (1, 'ann@example.com')", cancellationToken: Timeout());
        SqlClientException violation = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("INSERT INTO contacts VALUES (2, 'nobody')", cancellationToken: Timeout()));
        SqlResultSet registered = await connection.QueryAsync(
            "SELECT FUNCTION_NAME, FUNCTION_KIND, PARAMETER_TYPES, RETURN_TYPE, VOLATILITY FROM COHESION_SCHEMA.FUNCTIONS " +
            "WHERE IS_BUILT_IN = 'NO' AND FUNCTION_NAME LIKE 'is_%' ORDER BY FUNCTION_NAME", cancellationToken: Timeout());

        // Assert
        refused.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        refused.ConnectionUsable.ShouldBeTrue();
        refused.Message.ShouldStartWith("Function 'is_email_volatile' is VOLATILE, and a CHECK admits only IMMUTABLE functions.", Case.Sensitive);
        violation.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        violation.ConnectionUsable.ShouldBeTrue();
        violation.Message.ShouldContain("ck_email", Case.Sensitive);
        registered.Select(row => (row[0], row[1], row[2], row[3], row[4])).ShouldBe([
            ("is_email", "SCALAR", "TEXT", "BOOLEAN", "IMMUTABLE"),
            ("is_email_volatile", "SCALAR", "TEXT", "BOOLEAN", "VOLATILE"),
        ]);
        (await connection.QueryAsync("SELECT COUNT(*) AS c FROM contacts", cancellationToken: Timeout())).ShouldHaveSingleItem()["c"].ShouldBe(1L);
    }

    /// <summary>
    /// A stored CHECK naming a registered function binds again when its database reopens under an
    /// engine that registers the function; once the next engine's build no longer registers it, the
    /// database still opens and serves reads, each write that would evaluate the CHECK fails with
    /// <c>COHSQLE009</c> on a usable connection, and dropping the CHECK lets writes through again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Functions: a stored CHECK reopens, then fails writes with COHSQLE009 once its function is removed")]
    public async Task Reopen_StoredCheck_ShouldBindAgainThenFailWritesOnceTheFunctionIsRemoved()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-function-wire", Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange: the CHECK is declared over the wire and the engine is closed.
            await using (var first = await FunctionHarness.StartAsync(Register, root))
            {
                await using var connection = await first.Client.ConnectAsync(Timeout());
                await connection.ExecuteAsync("CREATE TABLE contacts (id INT, email TEXT, CONSTRAINT ck_email CHECK (is_email(email)))",
                    cancellationToken: Timeout());
                await connection.ExecuteAsync("INSERT INTO contacts VALUES (1, 'ann@example.com')", cancellationToken: Timeout());
            }

            // Act 1: reopened by an engine that registers the function.
            await using (var second = await FunctionHarness.StartAsync(Register, root, create: false))
            {
                await using var connection = await second.Client.ConnectAsync(Timeout());
                SqlClientException violation = await Should.ThrowAsync<SqlClientException>(async () =>
                    await connection.ExecuteAsync("INSERT INTO contacts VALUES (2, 'nobody')", cancellationToken: Timeout()));
                await connection.ExecuteAsync("INSERT INTO contacts VALUES (2, 'bob@example.com')", cancellationToken: Timeout());

                // Assert 1
                violation.Message.ShouldContain("ck_email", Case.Sensitive);
                violation.ConnectionUsable.ShouldBeTrue();
            }

            // Act 2: reopened by an engine whose build no longer registers it.
            await using var third = await FunctionHarness.StartAsync(static _ => { }, root, create: false);
            await using var reader = await third.Client.ConnectAsync(Timeout());
            SqlResultSet rows = await reader.QueryAsync("SELECT id, email FROM contacts ORDER BY id", cancellationToken: Timeout());
            SqlClientException refused = await Should.ThrowAsync<SqlClientException>(async () =>
                await reader.ExecuteAsync("INSERT INTO contacts VALUES (3, 'carol@example.com')", cancellationToken: Timeout()));
            SqlResultSet stillReadable = await reader.QueryAsync("SELECT COUNT(*) AS c FROM contacts", cancellationToken: Timeout());
            await reader.ExecuteAsync("ALTER TABLE contacts DROP CONSTRAINT ck_email", cancellationToken: Timeout());
            await reader.ExecuteAsync("INSERT INTO contacts VALUES (3, 'nobody')", cancellationToken: Timeout());

            // Assert 2
            rows.Select(row => (row["id"], row["email"])).ShouldBe([(1, "ann@example.com"), (2, "bob@example.com")]);
            refused.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            refused.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            refused.ConnectionUsable.ShouldBeTrue();
            refused.Message.ShouldBe(
                "COHSQLE009: CHECK constraint 'ck_email' on table 'dbo.contacts' calls function 'is_email(TEXT)', which this engine does not " +
                "register, so the constraint cannot be evaluated and the write is refused. Register the function on the engine's builder " +
                "(SqlDatabaseEngineBuilder.Functions), or drop the constraint.");
            stillReadable.ShouldHaveSingleItem()["c"].ShouldBe(2L);
            (await reader.QueryAsync("SELECT COUNT(*) AS c FROM contacts", cancellationToken: Timeout())).ShouldHaveSingleItem()["c"].ShouldBe(3L);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private static CancellationToken Timeout(int seconds = 10) => SqlClientTestHarness.Timeout(seconds);

    private static void Register(SqlFunctionCollection functions)
        => functions
            .Add(SqlScalarFunction.Create("slugify",
                static (string text) => text.Trim().ToLowerInvariant().Replace(' ', '-'), SqlFunctionVolatility.Immutable))
            .Add(new ClampFunction())
            .Add(SqlScalarFunction.Create("pick", static (long _) => "number", SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("pick", static (string _) => "text", SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("is_email", static (string value) => value.Contains('@'), SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("is_email_volatile", static (string value) => value.Contains('@')))
            .Add(SqlAggregateFunction.Create<long, long, long>("product",
                static () => 1L, static (long state, long value) => checked(state * value), static (long state) => state))
            .Add(new MedianFunction());

    /// <summary>
    /// A live SQL engine built with registered functions, a running server on the in-memory driver,
    /// and a typed client, with a seeded <c>users</c> table when the database is created.
    /// </summary>
    private sealed class FunctionHarness : IAsyncDisposable
    {
        private const string DatabaseName = "app";

        private readonly SqlDatabaseEngine _engine;
        private readonly InMemoryConnectionListener _listener;
        private readonly SqlDatabaseServer _server;

        private FunctionHarness(SqlDatabaseEngine engine, InMemoryConnectionListener listener, SqlDatabaseServer server, SqlClient client)
        {
            _engine = engine;
            _listener = listener;
            _server = server;
            Client = client;
        }

        public SqlClient Client { get; }

        public static async Task<FunctionHarness> StartAsync(Action<SqlFunctionCollection> register, string? root = null, bool create = true)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("sql-functions-wire");
            if (root is not null)
            {
                builder.Options.RootPath = root;
            }

            register(builder.Functions);
            SqlDatabaseEngine engine = await builder.BuildAsync();
            if (create)
            {
                var database = await engine.CreateDatabaseAsync(DatabaseName);
                await using var session = await database.CreateSessionAsync();
                await session.ExecuteAsync("CREATE TABLE users (id INT NOT NULL, name VARCHAR(100), score BIGINT)");
                await session.ExecuteAsync("INSERT INTO users (id, name, score) VALUES (1, 'ada', 100), (2, 'grace', 200)");
            }
            else
            {
                await engine.OpenDatabaseAsync(DatabaseName);
            }

            var listener = new InMemoryConnectionListener();
            var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
            await server.StartAsync();
            var client = SqlClient.Create(new SqlClientOptions
            {
                Settings = new DatabaseConnectionSettings { Database = DatabaseName, Principal = "tester", EndPoint = listener.EndPoint },
                ConnectionFactory = listener.CreateFactory(),
            });

            return new FunctionHarness(engine, listener, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _server.DisposeAsync();
            await _listener.DisposeAsync();
            await _engine.DisposeAsync();
        }
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

    /// <summary>An immutable <c>doubled(BIGINT)</c> that counts its calls.</summary>
    private sealed class CountingFunction : SqlScalarFunction
    {
        private int _calls;

        public CountingFunction()
            : base("doubled", [SqlType.BigInt], SqlType.BigInt, SqlFunctionVolatility.Immutable)
        {
        }

        public int Calls => Volatile.Read(ref _calls);

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        {
            Interlocked.Increment(ref _calls);
            return SqlValue.FromInt64(arguments.GetInt64(0) * 2);
        }
    }

    /// <summary>The design's hand-written aggregate: the median of a group's non-NULL doubles.</summary>
    private sealed class MedianFunction : SqlAggregateFunction
    {
        public MedianFunction()
            : base("median", [SqlType.Double], SqlType.Double)
        {
        }

        protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

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
