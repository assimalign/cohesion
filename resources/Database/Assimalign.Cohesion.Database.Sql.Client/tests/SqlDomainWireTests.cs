using System;
using System.Globalization;
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
/// Domains and the cast registry over the wire (phase E3 of the engine extensibility program, owner
/// decision 68): through the real SQL server and the typed client, DDL names a domain registered on the
/// engine, its column returns its base type, a write that assigns it is checked, an application's cast
/// runs under <c>CAST</c>, and once the domain is gone from the next engine's build, the database still
/// opens and serves reads while each write to the column fails with <c>COHSQLE010</c> on a connection
/// that stays usable.
/// </summary>
public sealed class SqlDomainWireTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - SQL functions and domains execute and enforce constraints over the wire")]
    public async Task QueryAsync_SqlDefinitions_ShouldUseTheFunctionAndDomainPaths()
    {
        // Arrange
        await using var harness = await DomainHarness.StartAsync(_ => { });
        await using var connection = await harness.Client.ConnectAsync(Timeout());
        await connection.ExecuteAsync("CREATE FUNCTION inc(p INT) RETURNS INT IMMUTABLE STRICT RETURN p + 1", cancellationToken: Timeout());
        await connection.ExecuteAsync("CREATE DOMAIN positive AS INT CHECK (VALUE > 0)", cancellationToken: Timeout());
        await connection.ExecuteAsync("CREATE FUNCTION fallback(p INT) RETURNS INT RETURN COALESCE(p, 9)", cancellationToken: Timeout());
        await connection.ExecuteAsync("CREATE FUNCTION twice(p INT) RETURNS INT IMMUTABLE RETURN inc(p) * 2", cancellationToken: Timeout());
        await connection.ExecuteAsync("CREATE TABLE values_table (id INT, n positive DEFAULT inc(1), CHECK (inc(n) > 0))", cancellationToken: Timeout());
        await connection.ExecuteAsync("INSERT INTO values_table (id) VALUES (1)", cancellationToken: Timeout());
        // Act
        var result = await connection.QueryAsync("SELECT inc(n) AS answer, twice(n) AS doubled, inc(NULL) AS missing, fallback(NULL) AS filled FROM values_table WHERE inc(n) = 3", cancellationToken: Timeout());
        // Assert
        var row = result.ShouldHaveSingleItem();
        row["answer"].ShouldBe(3);
        row["doubled"].ShouldBe(6);
        row["missing"].ShouldBeNull();
        row["filled"].ShouldBe(9);
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("INSERT INTO values_table VALUES (2, -1)", cancellationToken: Timeout()));
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE038");
        await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("CREATE FUNCTION weak(p INT) RETURNS INT IMMUTABLE RETURN fallback(p)", cancellationToken: Timeout()));
        await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("CREATE FUNCTION recursive(p INT) RETURNS INT RETURN recursive(p)", cancellationToken: Timeout()));
        await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("DROP DOMAIN positive", cancellationToken: Timeout()));
        await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("DROP FUNCTION inc(INT)", cancellationToken: Timeout()));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Domains: DDL and DML over a domain, and an application's cast, over the wire")]
    public async Task QueryAsync_DomainColumnAndApplicationCast_ShouldRunOverTheWire()
    {
        // Arrange
        await using var harness = await DomainHarness.StartAsync(Register);
        await using var connection = await harness.Client.ConnectAsync(Timeout());
        await connection.ExecuteAsync("CREATE TABLE contacts (id INT NOT NULL, email email, balance cents)", cancellationToken: Timeout());
        await connection.ExecuteAsync("INSERT INTO contacts VALUES (1, 'ann@example.com', 123)", cancellationToken: Timeout());

        // Act
        SqlClientException violation = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("INSERT INTO contacts VALUES (2, 'nobody', 1)", cancellationToken: Timeout()));
        SqlResultSet rows = await connection.QueryAsync(
            "SELECT email, balance, CAST(balance AS TEXT) AS shown, CAST('$4.56' AS cents) AS parsed FROM contacts", cancellationToken: Timeout());

        // Assert
        violation.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        violation.ConnectionUsable.ShouldBeTrue();
        violation.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE038");
        violation.Message.ShouldBe("COHSQLE038: DOMAIN constraint 'email' on 'dbo.contacts' was violated.");
        SqlRow row = rows.ShouldHaveSingleItem();
        (row["email"], row["balance"], row["shown"], row["parsed"]).ShouldBe(("ann@example.com", 123L, "$1.23", 456L));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Domains: a domain column reopens bound, then refuses writes with COHSQLE010 once the domain is removed")]
    public async Task Reopen_DomainColumn_ShouldBindAgainThenRefuseWritesOnceTheDomainIsRemoved()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-domain-wire", Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange
            await using (var first = await DomainHarness.StartAsync(Register, root))
            {
                await using var connection = await first.Client.ConnectAsync(Timeout());
                await connection.ExecuteAsync("CREATE TABLE contacts (id INT NOT NULL, email email, note TEXT)", cancellationToken: Timeout());
                await connection.ExecuteAsync("INSERT INTO contacts VALUES (1, 'ann@example.com', 'a')", cancellationToken: Timeout());
            }

            // Act 1: an engine that registers the domain binds it again.
            await using (var second = await DomainHarness.StartAsync(Register, root, create: false))
            {
                await using var connection = await second.Client.ConnectAsync(Timeout());
                (await Should.ThrowAsync<SqlClientException>(async () =>
                    await connection.ExecuteAsync("INSERT INTO contacts VALUES (2, 'nobody', 'b')", cancellationToken: Timeout())))
                    .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE038");
            }

            // Act 2: an engine whose build no longer registers it.
            await using var third = await DomainHarness.StartAsync(static _ => { }, root, create: false);
            await using var reader = await third.Client.ConnectAsync(Timeout());
            SqlResultSet rows = await reader.QueryAsync("SELECT id, email FROM contacts", cancellationToken: Timeout());
            SqlClientException refused = await Should.ThrowAsync<SqlClientException>(async () =>
                await reader.ExecuteAsync("INSERT INTO contacts VALUES (2, 'bob@example.com', 'b')", cancellationToken: Timeout()));
            await reader.ExecuteAsync("UPDATE contacts SET note = 'changed' WHERE id = 1", cancellationToken: Timeout());

            // Assert
            rows.Select(row => (row["id"], row["email"])).ShouldBe([(1, "ann@example.com")]);
            refused.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            refused.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            refused.ConnectionUsable.ShouldBeTrue();
            refused.Message.ShouldBe(
                "COHSQLE010: Column 'email' of table 'dbo.contacts' is of domain 'email', which this engine does not register, so the column " +
                "cannot be written and the write is refused. Register the domain on the engine's builder (SqlDatabaseEngineBuilder.Types), " +
                "or drop the column.");
            (await reader.QueryAsync("SELECT note FROM contacts", cancellationToken: Timeout())).ShouldHaveSingleItem()["note"].ShouldBe("changed");
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

    /// <summary>
    /// An application cast that throws is a coded statement failure over the wire: invalid text is
    /// COHSQLE022, and implementation faults or unrelated cancellation are COHSQLE007. The connection
    /// stays usable, where a raw cancellation would have closed it.
    /// </summary>
    /// <param name="input">The text the cast is given.</param>
    /// <param name="message">The failure the client receives.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Domains: application cast failures keep their wire code and the connection stays usable")]
    [InlineData("npe", "COHSQLE007: Cast TEXT AS points failed: npe!")]
    [InlineData("oce", "COHSQLE007: Cast TEXT AS points failed: not this statement")]
    [InlineData("bad", "COHSQLE022: CAST from TEXT to points failed: not a number of points")]
    public async Task QueryAsync_ThrowingApplicationCast_ShouldBeCodedAndKeepTheConnection(string input, string message)
    {
        // Arrange
        await using var harness = await DomainHarness.StartAsync(static types =>
        {
            var points = SqlType.CreateDomain("points", SqlType.BigInt);
            types.Add(points).AddCast(SqlCast.Create<string, long>(SqlType.Text, points, static text => text switch
            {
                "npe" => throw new NullReferenceException("npe!"),
                "oce" => throw new TaskCanceledException("not this statement"),
                "bad" => throw new FormatException("not a number of points"),
                _ => long.Parse(text, CultureInfo.InvariantCulture),
            }, SqlCastContext.Explicit));
        });
        await using var connection = await harness.Client.ConnectAsync(Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)", cancellationToken: Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: Timeout());

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync($"SELECT CAST('{input}' AS points) AS p FROM t", cancellationToken: Timeout()));
        SqlResultSet after = await connection.QueryAsync("SELECT CAST('7' AS points) AS p FROM t", cancellationToken: Timeout());

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldBe(message);
        after.ShouldHaveSingleItem()["p"].ShouldBe(7L);
    }

    private static CancellationToken Timeout(int seconds = 10) => SqlClientTestHarness.Timeout(seconds);

    private static void Register(SqlTypeCollection types)
    {
        var cents = SqlType.CreateDomain("cents", SqlType.BigInt);
        types.Add(SqlType.CreateDomain("email", SqlType.VarChar(320),
                SqlScalarFunction.Create("is_email", static (string value) => value.Contains('@'), SqlFunctionVolatility.Immutable)))
            .Add(cents)
            .AddCast(SqlCast.Create<string, long>(SqlType.Text, cents,
                static text => (long)(decimal.Parse(text.TrimStart('$'), CultureInfo.InvariantCulture) * 100), SqlCastContext.Explicit))
            .AddCast(SqlCast.Create<long, string>(cents, SqlType.Text,
                static value => "$" + (value / 100m).ToString("0.00", CultureInfo.InvariantCulture), SqlCastContext.Assignment));
    }

    private sealed class DomainHarness : IAsyncDisposable
    {
        private const string DatabaseName = "app";

        private readonly SqlDatabaseEngine _engine;
        private readonly InMemoryConnectionListener _listener;
        private readonly SqlDatabaseServer _server;

        private DomainHarness(SqlDatabaseEngine engine, InMemoryConnectionListener listener, SqlDatabaseServer server, SqlClient client)
        {
            _engine = engine;
            _listener = listener;
            _server = server;
            Client = client;
        }

        public SqlClient Client { get; }

        public static async Task<DomainHarness> StartAsync(Action<SqlTypeCollection> register, string? root = null, bool create = true)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("sql-domains-wire");
            if (root is not null)
            {
                builder.Options.RootPath = root;
            }

            register(builder.Types);
            SqlDatabaseEngine engine = await builder.BuildAsync();
            if (create)
            {
                await engine.CreateDatabaseAsync(DatabaseName);
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

            return new DomainHarness(engine, listener, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _server.DisposeAsync();
            await _listener.DisposeAsync();
            await _engine.DisposeAsync();
        }
    }
}
