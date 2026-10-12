using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    private const string ScriptSql = "CREATE FUNCTION inc(p INT) RETURNS INT IMMUTABLE RETURN p + 1; CREATE DOMAIN positive AS INT DEFAULT 4 NOT NULL CHECK (VALUE > 0); CREATE TABLE items (id positive, n INT DEFAULT inc(2)); INSERT INTO items (id) VALUES (1); CREATE INDEX ix_n ON items(n);";

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Declared scripts reopen on disk, preserve ownership, verify and refuse changed checksums")]
    public async Task BuildAsync_PersistedScript_ShouldSkipVerifyAndRefuseChangedChecksum()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-e4", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = await ScriptBuilder(root: root).BuildAsync()) { }
            // Act
            await using (var reopened = await ScriptBuilder(root: root, mode: SqlProvisioningMode.Verify).BuildAsync())
            {
                var database = await reopened.OpenDatabaseAsync("functions");
                await using var session = await database.CreateSessionAsync();
                // Assert
                (await RowsAsync(session, "SELECT inc(n), id FROM items")).ShouldBe([[4, 1]]);
                database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(5);
                await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecuteAsync(session, "DROP FUNCTION inc(INT)"));
                await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecuteAsync(session, "DROP DOMAIN positive"));
                (await RowsAsync(session, "SELECT IS_SQL_DEFINED FROM COHESION_SCHEMA.FUNCTIONS WHERE FUNCTION_NAME = 'inc'")).ShouldBe([["YES"]]);
                (await RowsAsync(session, "SELECT IS_SQL_DEFINED FROM COHESION_SCHEMA.DOMAINS WHERE DOMAIN_NAME = 'positive'")).ShouldBe([["YES"]]);
            }
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await ScriptBuilder(root: root, sql: ScriptSql + " ").BuildAsync());
            failure.Message.ShouldContain("COHSQLS001");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Declared scripts converge at every durable crash point without replaying data")]
    public async Task BuildAsync_CrashAtEveryWrite_ShouldConvergeWithoutDuplicateInsert()
    {
        // Arrange: start with an existing, empty database so every recorded point can reopen.
        var storage = new CrashCaptureSqlStorageStrategy();
        var seed = SqlDatabaseEngine.CreateBuilder("sql-script");
        seed.Options.StorageStrategy = storage;
        await using (var engine = await seed.BuildAsync()) { await engine.CreateDatabaseAsync("functions"); }
        storage = storage.CaptureDurableImages();
        using var recording = storage.RecordCrashPoints();
        await using (var first = await ScriptBuilder(storage).BuildAsync()) { }
        recording.Dispose();
        recording.Count.ShouldBeGreaterThan(10);
        // Act + Assert: replay each physical durability boundary, including catalog reservations,
        // logical data commit before catalog receipt, index publication, and final marker writes.
        for (int point = 0; point < recording.Count; point++)
        {
            await using var reopened = await ScriptBuilder(recording.Open(point)).BuildAsync();
            var database = await reopened.OpenDatabaseAsync("functions");
            await using var session = await database.CreateSessionAsync();
            (await RowsAsync(session, "SELECT inc(n), id FROM items")).ShouldBe([[4, 1]], $"crash point {point}");
            database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(5);
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Already applied scripts reconcile below one millisecond without catalog walks")]
    public async Task ProvisionAsync_AppliedScripts_ShouldUseConstantTimeMarker()
    {
        // Arrange: the 50-table file-backed Q8 fixture, including immutable CHECK calls.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-e4-q8", Guid.NewGuid().ToString("N"));
        try
        {
            var builder = SqlDatabaseEngine.CreateBuilder("sql-script-q8");
            builder.Options.RootPath = root;
            builder.Functions.Add(SqlScalarFunction.Create("has_text", static (string text) => text.Length > 0, SqlFunctionVolatility.Immutable));
            builder.AddDatabase("q8", database =>
            {
                database.Schema(Benchmarks.Q8Schema.CreateSchema(checks: true));
                database.Script("001", ScriptSql);
            });
            await using var engine = await builder.BuildAsync();
            var declaration = engine.DeclaredDatabases.ShouldHaveSingleItem();
            var database = await engine.OpenDatabaseAsync("q8");
            await declaration.ProvisionAsync(engine, CancellationToken.None);
            int walks = database.Definitions.ValidationWalkCount;
            // Act: opening and compilation are outside this reconciliation measurement.
            var watch = Stopwatch.StartNew();
            for (int index = 0; index < 1000; index++)
            {
                await declaration.ProvisionAsync(engine, CancellationToken.None);
            }
            watch.Stop();
            // Assert
            double milliseconds = watch.Elapsed.TotalMilliseconds / 1000;
            _output.WriteLine($"Q8 with scripts: {milliseconds:F6} ms/reconciliation (1000 iterations, 50 typed tables).");
            milliseconds.ShouldBeLessThan(1);
            database.Definitions.ValidationWalkCount.ShouldBe(walks);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - A schema cannot adopt existing ad-hoc functions or domains")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_AdhocDefinition_ShouldRefuseAdoption(bool domain)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("sql-script");
        builder.Options.StorageStrategy = storage;
        await using (var engine = await builder.BuildAsync())
        {
            await using var session = await SessionAsync(engine);
            await ExecuteAsync(session, domain ? "CREATE DOMAIN positive AS INT" : "CREATE FUNCTION inc(p INT) RETURNS INT RETURN p + 1");
        }
        // Act + Assert
        await Should.ThrowAsync<DatabaseException>(async () => await ScriptBuilder(storage.CaptureDurableImages()).BuildAsync());
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Durable script crash images reopen through real files")]
    public async Task Open_FileBackedCrashImages_ShouldRecoverDefinitionsAndData()
    {
        // Arrange: copy durable images before orderly disposal can checkpoint anything.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-e4-crash", Guid.NewGuid().ToString("N"));
        var storage = new CrashCaptureSqlStorageStrategy();
        try
        {
            await using (var engine = await ScriptBuilder(storage).BuildAsync())
            {
                string directory = Path.Combine(root, "functions");
                Directory.CreateDirectory(directory);
                foreach (bool catalog in new[] { false, true })
                {
                    var image = storage.GetDurableImage("functions", catalog);
                    string prefix = Path.Combine(directory, catalog ? "catalog" : "sql");
                    File.WriteAllBytes(prefix + ".dat", image.Data);
                    File.WriteAllBytes(prefix + ".log", image.Journal);
                    File.WriteAllBytes(prefix + ".bak", image.Backup);
                }
            }
            // Act
            await using var reopened = await ScriptBuilder(root: root).BuildAsync();
            var database = await reopened.OpenDatabaseAsync("functions");
            await using var session = await database.CreateSessionAsync();
            // Assert
            (await RowsAsync(session, "SELECT inc(n), id FROM items")).ShouldBe([[4, 1]]);
            database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(5);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Missing native calls inside persisted SQL bodies open unresolved and fail declared Build")]
    public async Task Open_MissingNativeCall_ShouldRemainReadableAndRefuseBuild()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("native-missing");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("native_next", static (long value) => value + 1, SqlFunctionVolatility.Immutable));
        await using (var first = await builder.BuildAsync())
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, "CREATE FUNCTION calculated(p BIGINT) RETURNS BIGINT IMMUTABLE RETURN native_next(p)");
            await ExecuteAsync(session, "CREATE TABLE t (id INT, n BIGINT DEFAULT calculated(1))");
            await ExecuteAsync(session, "INSERT INTO t (id) VALUES (1)");
        }
        var images = storage.CaptureDurableImages();
        var absent = SqlDatabaseEngine.CreateBuilder("native-missing");
        absent.Options.StorageStrategy = images;
        // Act
        await using (var reopened = await absent.BuildAsync())
        {
            var database = await reopened.OpenDatabaseAsync("functions");
            await using var session = await database.CreateSessionAsync();
            // Assert
            (await RowsAsync(session, "SELECT n FROM t")).ShouldBe([[2L]]);
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t (id) VALUES (2)"))).Message.ShouldContain("COHSQLE009");
        }
        var declared = SqlDatabaseEngine.CreateBuilder("native-missing");
        declared.Options.StorageStrategy = images.CaptureDurableImages();
        declared.AddDatabase("functions");
        (await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await declared.BuildAsync())).Message.ShouldContain("COHSQLE009");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted SQL function domain signatures enforce argument and result constraints")]
    public async Task Open_DomainSignatures_ShouldPreserveArgumentAndResultChecks()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("domain-signature");
        builder.Options.StorageStrategy = storage;
        await using (var first = await builder.BuildAsync())
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, "CREATE TABLE t (id INT)");
            await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
            await ExecuteAsync(session, "CREATE DOMAIN positive AS INT NOT NULL CHECK (VALUE > 0)");
            await ExecuteAsync(session, "CREATE FUNCTION read_positive(p positive) RETURNS INT RETURN p");
            await ExecuteAsync(session, "CREATE FUNCTION make_positive(p INT) RETURNS positive RETURN p");
        }
        // Act
        var reopen = SqlDatabaseEngine.CreateBuilder("domain-signature");
        reopen.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var engine = await reopen.BuildAsync();
        var database = await engine.OpenDatabaseAsync("functions");
        await using var reader = await database.CreateSessionAsync();
        // Assert
        (await RowsAsync(reader, "SELECT read_positive(2), make_positive(3) FROM t")).ShouldBe([[2, 3]]);
        await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(reader, "SELECT read_positive(CAST(-1 AS INT)) FROM t"));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(reader, "SELECT make_positive(CAST(-1 AS INT)) FROM t"));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(reader, "SELECT make_positive(NULL) FROM t"));
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "DROP DOMAIN positive"))).Message.ShouldContain("COHSQLD002");
    }

    private sealed class ScriptRow
    {
        public int Id { get; set; }
    }

    private static SqlDatabaseEngineBuilder ScriptBuilder(CrashCaptureSqlStorageStrategy? storage = null, string? root = null,
        string sql = ScriptSql, SqlProvisioningMode mode = SqlProvisioningMode.Apply)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("sql-script");
        if (storage is not null) { builder.Options.StorageStrategy = storage; }
        if (root is not null) { builder.Options.RootPath = root; }
        builder.AddDatabase("functions", database => { database.Provisioning = mode; database.Schema(schema => schema.Table<ScriptRow>("typed_rows", table => table.Key(row => row.Id))); database.Script("001", sql); });
        return builder;
    }
}
