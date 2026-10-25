using System;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlExecutionDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Diagnostics: coded resources never carry a second code prefix (#1653)")]
    public void Resources_CodedCarrierMessages_ShouldHaveNoCodePrefix()
    {
        // Arrange / Act
        var messages = ErrorMessages.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, true)!;

        // Literal message-only script refusals joined these resources in #1470. Preserve
        // that contract, validate each prefix exactly once, and keep carrier templates bare.
        foreach (DictionaryEntry entry in messages)
        {
            if (entry.Key is not string name || entry.Value is not string message) { continue; }
            string? messageOnlyCode = name switch
            {
                nameof(ErrorMessages.ScriptNamespaceTypedObjects) => "CS03205",
                nameof(ErrorMessages.ValidateScriptOwnershipFailure) => "CS03205",
                nameof(ErrorMessages.DatabaseValidateScriptsAsyncFailure) => "CS03201",
                nameof(ErrorMessages.DatabaseValidateScriptsAsyncFailure2) => "CS03201",
                nameof(ErrorMessages.DatabaseValidateScriptsAsyncFailure3) => "CS03201",
                nameof(ErrorMessages.DatabaseValidateScriptsAsyncFailure4) => "CS03201",
                nameof(ErrorMessages.DatabaseValidateScriptsAsyncFailure5) => "CS03202",
                nameof(ErrorMessages.DatabaseApplyScriptsAsyncFailure) => "CS03204",
                nameof(ErrorMessages.DatabaseApplyScriptsAsyncFailure2) => "CS03204",
                nameof(ErrorMessages.DatabaseApplyScriptsAsyncFailure3) => "CS03204",
                nameof(ErrorMessages.DatabaseApplyScriptsAsyncFailure4) => "CS03201",
                nameof(ErrorMessages.DeclaredDatabaseScriptTargetFailure) => "CS03201",
                nameof(ErrorMessages.SqlDeclaredScriptDatabase) => "CS03203",
                _ => null,
            };
            if (messageOnlyCode is not null)
            {
                message.ShouldStartWith(messageOnlyCode + ": ", Case.Sensitive, name);
                Regex.Matches(message, @"(?:COH[A-Z]+\d+|C[KSDBGVC]\d{5}):\s").Count.ShouldBe(1, name);
            }
            else
            {
                Regex.IsMatch(message, @"^(?:COH[A-Z]+\d+|C[KSDBGVC]\d{5}):\s").ShouldBeFalse(name);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Provisioning: synthetic CHECK locations never escape schema validation (#1653)")]
    public void BindDeclaredChecks_InvalidPredicate_ShouldDiscardCarrierLocations()
    {
        // Arrange
        var schema = SqlSchema.Compile("app", builder => builder.Table<ProvisionedRow>("t",
            table => { table.Column(row => row.Value); table.Check("ck", "1 IS TRUE"); }));

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlSchemaProvisioner.BindDeclaredChecks(schema,
            Collation.Binary, SqlFunctionEnvironment.Standard, new SqlQueryParserOptions()));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS02003");
        diagnostic.Start.ShouldBeNull();
        diagnostic.End.ShouldBeNull();
        diagnostic.Location.ShouldBeNull();
        diagnostic.Line.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - View reload: new rendering is an invariant failure; loaded text is corruption (#1653)")]
    [InlineData(false, "CS06058")]
    [InlineData(true, "CS05060")]
    public void LoadQuery_InvalidText_ShouldClassifyItsOrigin(bool declaring, string code)
    {
        // Arrange / Act
        var failure = Should.Throw<DatabaseException>(() => SqlPersistedExpression.LoadQuery("SELECT 1 +", "view v", declaring));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Persisted CHECK: synthetic offsets are discarded even inside the statement bounds (#1653)")]
    [InlineData(false, "t")]
    [InlineData(true, "t")]
    [InlineData(false, "long_statement_diagnostic_target")]
    [InlineData(true, "long_statement_diagnostic_target")]
    public async Task ExecuteAsync_PersistedCheckFailure_ShouldUseStatementSpan(bool wire, string table)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var session = await database!.CreateSessionAsync(TestTimeout.Token());
        await session.ExecuteAsync($"CREATE TABLE {table} (v INT CHECK(1 / v > 0))", cancellationToken: TestTimeout.Token());
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "diagnostics", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        string sql = $"INSERT INTO {table} VALUES (0)";

        // Act
        var diagnostics = wire
            ? (await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()))).Diagnostics
            : (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()))).Diagnostics;

        // Assert
        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS02001");
        diagnostic.Start.ShouldBe(0);
        diagnostic.End.ShouldBe(sql.Length);
        diagnostic.Line.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Persisted failures: damaged definitions and default wrappers retain codes and statement spans (#1653)")]
    [InlineData(false, "qty >", "CS06058")]
    [InlineData(true, "qty >", "CS06058")]
    [InlineData(false, "'bad'", "CS02022")]
    [InlineData(true, "'bad'", "CS02022")]
    [InlineData(false, "1 IS TRUE", "CS02003")]
    [InlineData(true, "1 IS TRUE", "CS02003")]
    [InlineData(false, "'a' COLLATE \"nope\"", "CS06058")]
    [InlineData(true, "'a' COLLATE \"nope\"", "CS06058")]
    public async Task ExecuteAsync_PersistedFailure_ShouldKeepCarrier(bool wire, string literal, string code)
    {
        // Arrange: use the engine's existing test grant to publish a definition SQL DDL refuses.
        await using var harness = await ServerTestHarness.StartAsync();
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await database!.Catalog.CreateTableAsync("dbo", "broken",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32)),
             new SqlCatalogColumn("v", new DatabaseTypeInfo(DatabaseType.Guid), defaultLiteral: literal)],
            [], TestTimeout.Token());
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "diagnostics", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        const string sql = "INSERT INTO broken(id) VALUES (1)";

        // Act
        var diagnostics = wire
            ? (await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()))).Diagnostics
            : (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()))).Diagnostics;

        // Assert
        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(0);
        diagnostic.End.ShouldBe(sql.Length);
        (await connection.QueryAsync("SELECT id FROM users", cancellationToken: TestTimeout.Token())).Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Internal invariants: an unavailable constraint index refuses writes with a located diagnostic (#1653)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UnavailableIndex_ShouldCarryInternalError(bool wire)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var session = await database!.CreateSessionAsync(TestTimeout.Token());
        await session.ExecuteAsync("CREATE UNIQUE INDEX uq_users ON users(id)", cancellationToken: TestTimeout.Token());
        database.Catalog.TryGetTable("dbo", "users", out var table).ShouldBeTrue();
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        try { await database.IndexManager.DropIndexAsync(transaction, table!.ObjectId, "uq_users", TestTimeout.Token()); }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
        const string sql = "INSERT INTO users VALUES (3, 'x')";
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "diagnostics", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());

        // Act
        var diagnostics = wire
            ? (await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()))).Diagnostics
            : (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()))).Diagnostics;

        // Assert
        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS05060");
        diagnostic.Start.ShouldBe(0);
        diagnostic.End.ShouldBe(sql.Length);
        diagnostic.Message.ShouldBe("Index 'uq_users' on 'dbo.users' is unavailable; writes cannot bypass its constraints.");
        (await connection.QueryAsync("SELECT id FROM users", cancellationToken: TestTimeout.Token())).Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: native ownership codes live in the carrier and the wire prefixes them once (#1653)")]
    [InlineData("DROP FUNCTION native_echo(INT)")]
    [InlineData("DROP DOMAIN native_domain")]
    public async Task ExecuteAsync_NativeDrop_ShouldCarryOwnershipCode(string sql)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("native-diagnostics");
        builder.Functions.Add(SqlScalarFunction.Create("native_echo", static (int value) => value));
        builder.Types.Add(SqlType.CreateDomain("native_domain", SqlType.Integer));
        await using var engine = await builder.BuildAsync(TestTimeout.Token());
        var database = await engine.CreateDatabaseAsync("app", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(TestTimeout.Token());
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app", Principal = "diagnostics", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());

        // Act
        var local = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));
        var remote = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

        // Assert
        var diagnostic = local.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS03303");
        diagnostic.Start.ShouldBe(0);
        diagnostic.End.ShouldBe(sql.Length);
        local.Message.ShouldNotContain("CS03303:", Case.Sensitive);
        remote.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe(local.Message);
        remote.Message.ShouldBe("CS03303: " + local.Message);
        remote.ConnectionUsable.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Persisted defaults: wrappers retain the inner code and original message (#1653)")]
    [InlineData(DatabaseType.Guid, "'bad'", "bad", "CS02022")]
    [InlineData(DatabaseType.DateTime, "'bad'", "bad", "CS02047")]
    [InlineData(DatabaseType.Int32, "DATE '2026-01-01'", "2026-01-01", "CS02055")]
    public void ResolveDefault_InvalidStoredValue_ShouldRetainCarrier(DatabaseType type, string literal, string value, string code)
    {
        // Arrange
        var column = new SqlCatalogColumn("v", new DatabaseTypeInfo(type), defaultLiteral: literal);

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlPlanExecutor.ResolveDefault(column, value));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        failure.InnerException.ShouldNotBeNull();
        failure.Message.ShouldStartWith($"Column 'v': DEFAULT value cannot be stored as {type}.", Case.Sensitive);
        failure.Diagnostics[0].Start.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CHECK result mismatch: the function call retains its precise span and datatype code (#1653)")]
    public async Task ExecuteAsync_CheckResultMismatch_ShouldLocateCall()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("check-result-diagnostics");
        builder.Functions.Add(SqlScalarFunction.Create("text_fn", static (int value) => value.ToString(), SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(TestTimeout.Token());
        var database = await engine.CreateDatabaseAsync("app", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        const string sql = "CREATE TABLE t (v INT, CONSTRAINT ck CHECK(text_fn(v) = 1))";

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS02055");
        diagnostic.Start.ShouldBe(sql.IndexOf("text_fn", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + "text_fn(v)".Length);
        failure.Message.ShouldStartWith("Function 'text_fn' returns TEXT, where the CHECK needs a value that compares with ", Case.Sensitive);

        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(TestTimeout.Token());
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app", Principal = "diagnostics", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        var remote = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));
        remote.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(diagnostic.Code);
        remote.Diagnostics[0].Start.ShouldBe(diagnostic.Start);
        remote.Diagnostics[0].End.ShouldBe(diagnostic.End);
        remote.Message.ShouldBe("CS02055: " + failure.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Compiled ownership: a located refusal retains its concrete type and metadata (#1653)")]
    public async Task ExecuteAsync_SchemaOwnedTable_ShouldRetainOwnershipMetadata()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ownership-diagnostics", new());
        var database = await engine.CreateDatabaseAsync("app", TestTimeout.Token());
        await database.Catalog.CreateTableAsync("dbo", "owned", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))],
            [], DatabaseObjectOwner.Schema, "model", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        const string sql = "DROP TABLE owned";

        // Act
        var failure = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

        // Assert
        failure.ObjectName.ShouldBe("owned");
        failure.OwningSchema.ShouldBe("model");
        failure.Operation.ShouldBe("DROP TABLE");
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS03066");
        diagnostic.Start.ShouldBe(0);
        diagnostic.End.ShouldBe(sql.Length);
        diagnostic.Message.ShouldBe(failure.Message);

        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(TestTimeout.Token());
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app", Principal = "diagnostics", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        var remote = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));
        remote.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03066");
        remote.Diagnostics[0].Start.ShouldBe(0);
        remote.Diagnostics[0].End.ShouldBe(sql.Length);
        remote.Message.ShouldBe("CS03066: " + failure.Message);
        remote.ConnectionUsable.ShouldBeTrue();
    }

    private sealed record ProvisionedRow(int Value);
}
