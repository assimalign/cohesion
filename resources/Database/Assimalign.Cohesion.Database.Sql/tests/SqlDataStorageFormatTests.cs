using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>
/// The data-storage format gate (#1099, owner decisions of 2026-10-01 and
/// 2026-10-02): a new database is created on format 6, and an existing database on
/// any other format is refused at open — an older one must be dropped and recreated
/// (there is no upgrade path; upgrades are #1152), and a newer one belongs to the
/// engine that wrote it. The gate reads only the catalog, before the data file set is
/// opened: a cleanly closed database is left byte-identical, and a crashed one keeps
/// its data files and its journals, so the engine that wrote it can still recover and
/// open it. Format 4 differs from 5 only in its index trees (#1194: B-tree page
/// format 1, entries ordered by key alone), and the index manager checks every tree's
/// own page format behind the gate. Format 5 differs from 6 only in its catalog: format
/// 6 table records carry the dropped columns' physical ordinals (#1241), and rows are
/// decoded through them.
/// </summary>
public sealed class SqlDataStorageFormatTests : IDisposable
{
    private const string TestDatabase = "format-db";

    private static readonly DateTimeOffset Instant = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-format", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a new database is created on format 6 and reopens (#1099, #1194, #1241)")]
    public async Task Create_NewDatabase_ShouldBeOnFormatSixAndReopen()
    {
        // Arrange + Act: create, write temporal keys, close.
        await CreateDatabaseAsync(formatVersion: null);

        // Assert: the reopened database is on format 6 and its identity keys seek.
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.OpenDatabaseAsync(TestDatabase);
        database.Catalog.RecordSpaceFormatVersion.ShouldBe(6);
        SqlRowCodec.RecordSpaceFormatVersion.ShouldBe(6);

        await using var session = await database.CreateSessionAsync();
        (await IdsAsync(session, "SELECT id FROM events WHERE at = @p ORDER BY id", Instant.ToOffset(TimeSpan.FromHours(9)))).ShouldBe([1, 2]);
        ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_at");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: an older database is refused at open and its files are left untouched (#1099, #1194, #1241)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Open_OlderFormat_ShouldBeRefusedWithoutTouchingFiles(int version)
    {
        // Arrange: a closed database whose catalog marker says an older format —
        // 3 is what 10.0.0-preview.1 wrote (temporal keys with kind and offset),
        // 4 what engines before #1194 wrote (index entries ordered by key alone), 5
        // what engines before #1241 wrote (no dropped-column layout in the catalog).
        await CreateDatabaseAsync(formatVersion: version);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: the message names the database, both formats and the remedy —
        // drop and create again, since create refuses a name whose storage
        // exists; version 1 (no marker) also covers an interrupted creation...
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format {version}");
        refusal.Message.ShouldContain("this engine supports only format 6");
        refusal.Message.ShouldContain("drop the database (DropDatabaseAsync) and create it again");
        refusal.Message.ShouldContain(version == 1 ? "its creation was interrupted" : "export its data with the engine that wrote it");
        refusal.Message.ShouldContain("#1152");

        // ...no file was written, so nothing was upgraded or stamped: the next
        // open is refused the same way.
        AssertUnchanged(before);
        (await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase)))
            .Message.ShouldBe(refusal.Message);
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a newer database is refused at open and its files are left untouched (#1099)")]
    public async Task Open_NewerFormat_ShouldBeRefusedWithoutTouchingFiles()
    {
        // Arrange
        await CreateDatabaseAsync(formatVersion: SqlRowCodec.RecordSpaceFormatVersion + 1);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 7");
        refusal.Message.ShouldContain("this engine supports only format 6");
        refusal.Message.ShouldContain("written by a newer engine");
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a format-4 database with key-ordered index pages is refused by the gate, its files untouched (#1194)")]
    public async Task Open_FormatFourWithFormatOneIndexPages_ShouldBeRefusedByTheGate()
    {
        // Arrange: what an engine before #1194 left behind — the format-4 marker and
        // index trees in B-tree page format 1.
        await CreateDatabaseAsync(formatVersion: 4);
        LegacyBTreePages.DowngradeDataFiles(_rootPath).ShouldBeGreaterThan(0);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: refused on the catalog alone, before any index page is read.
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 4, but this engine supports only format 6", Case.Sensitive);
        refusal.Message.ShouldContain("export its data with the engine that wrote it");
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: index pages in another B-tree page format are refused at open with COHDBI001 (#1194)")]
    public async Task Open_IndexPagesInFormatOne_ShouldBeRefusedByTheIndexManager()
    {
        // Arrange: the current format-6 marker over index trees in B-tree page format 1 — a
        // marker that does not describe its trees (damage, or a forged marker). The
        // gate passes; the index manager's own check must not.
        await CreateDatabaseAsync(formatVersion: null);
        LegacyBTreePages.DowngradeDataFiles(_rootPath).ShouldBeGreaterThan(0);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: the coded refusal, carried through the engine's own format error
        // with the index manager's as its cause.
        refusal.ShouldBeOfType<SqlDataStorageFormatException>();
        refusal.Message.ShouldStartWith($"Database '{TestDatabase}' uses data-storage format 6, but one of its index trees does not: ", Case.Sensitive);
        refusal.Message.ShouldContain(IndexFormatException.ErrorCode + ": Index '", Case.Sensitive);
        refusal.Message.ShouldContain("uses B-tree page format 1, but this engine supports only format 2", Case.Sensitive);
        var cause = refusal.InnerException.ShouldBeOfType<IndexFormatException>();
        cause.FoundVersion.ShouldBe(1);

        // Nothing was written: the refusal came before recovery's scrub and checkpoint,
        // and a second open is refused the same way.
        AssertUnchanged(before);
        (await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase)))
            .Message.ShouldBe(refusal.Message);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a database without a catalog storage is refused without creating one (#1099)")]
    public async Task Open_MissingCatalogStorage_ShouldBeRefusedWithoutCreatingIt()
    {
        // Arrange: a database whose creation stopped before its catalog existed.
        await CreateDatabaseAsync(formatVersion: null);
        Directory.Delete(Path.Combine(_rootPath, TestDatabase + ".catalog"), recursive: true);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: refused before any file was opened, so no catalog was created.
        refusal.Message.ShouldContain($"Database '{TestDatabase}' has no catalog storage");
        refusal.Message.ShouldContain("#1152");
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: an interrupted creation is refused as unmarked, and drop plus create recovers the name (#1099)")]
    public async Task Open_InterruptedCreation_ShouldBeRefusedUntilDroppedAndCreatedAgain()
    {
        // Arrange: both file sets exist, but the creation stopped before the
        // format marker was committed.
        var strategy = new FileSystemSqlStorageStrategy(_rootPath);
        strategy.CreateStorage(TestDatabase).Dispose();
        strategy.CreateStorage(TestDatabase + SqlDatabaseEngine.CatalogSuffix).Dispose();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: the message reads the missing marker as an interrupted
        // creation, and the remedy it names works.
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 1");
        refusal.Message.ShouldContain("It has no format marker: its creation was interrupted");
        await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync(TestDatabase));

        await engine.DropDatabaseAsync(TestDatabase);
        var created = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(TestDatabase);
        created.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a crashed older database is refused without opening its data files and keeps its journals (#1099)")]
    public async Task Open_CrashedOlderFormat_ShouldBeRefusedAndStayRecoverable()
    {
        // Arrange: a format-3 database (the marker forged before the workload)
        // with committed rows, an uncommitted writer whose records a later commit
        // made durable, then a crash.
        var strategy = new CrashCaptureSqlStorageStrategy();
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "format-crash", StorageStrategy = strategy });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(TestDatabase);
        var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE events (id INT PRIMARY KEY, at TIMESTAMPTZ)");
        await session.ExecuteAsync("CREATE INDEX ix_at ON events (at)");
        await database.Catalog.SetRecordSpaceFormatVersionAsync(3);
        await session.ExecuteAsync("INSERT INTO events VALUES (1, @a), (2, @b)",
            new Dictionary<string, object?> { ["a"] = Instant, ["b"] = Instant.ToOffset(TimeSpan.FromHours(3)) });
        (await session.BeginTransactionAsync()).ShouldNotBeNull();
        await session.ExecuteAsync("INSERT INTO events VALUES (3, @p)", new Dictionary<string, object?> { ["p"] = Instant });
        var flusher = await database.CreateSessionAsync();
        await flusher.ExecuteAsync("INSERT INTO events VALUES (4, @p)", new Dictionary<string, object?> { ["p"] = Instant.ToOffset(TimeSpan.FromHours(-5)) });

        var crashed = strategy.CaptureDurableImages();
        var dataImage = crashed.GetDurableImage(TestDatabase);
        var catalogImage = crashed.GetDurableImage(TestDatabase + SqlDatabaseEngine.CatalogSuffix);

        // Act: open twice with an engine that refuses format 3.
        string message;
        await using (var refusingEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "format-crash-refuse", StorageStrategy = crashed }))
        {
            message = (await Should.ThrowAsync<DatabaseException>(async () => await refusingEngine.OpenDatabaseAsync(TestDatabase))).Message;
            (await Should.ThrowAsync<DatabaseException>(async () => await refusingEngine.OpenDatabaseAsync(TestDatabase)))
                .Message.ShouldBe(message);
        }

        // Assert: refused on the catalog alone. The data file set was never
        // opened, so recovery did not replay into it, and the catalog's journal
        // survived its own open and close.
        message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 3");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase).Data, dataImage.Data, "data file");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase).Journal, dataImage.Journal, "data journal");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase).Backup, dataImage.Backup, "data backup");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase + SqlDatabaseEngine.CatalogSuffix).Journal, catalogImage.Journal, "catalog journal");

        // The engine that wrote the database can still recover it. Stand in for
        // it by re-forging the marker to this engine's format over the post-refusal
        // images (its index pages are this engine's): the open then classifies the
        // journal and scrubs the uncommitted writer.
        var refused = crashed.CaptureDurableImages();
        var catalogStorage = refused.OpenStorage(TestDatabase + SqlDatabaseEngine.CatalogSuffix);
        try
        {
            await SqlCatalog.Open(catalogStorage).SetRecordSpaceFormatVersionAsync(SqlRowCodec.RecordSpaceFormatVersion);
        }
        finally
        {
            catalogStorage.Dispose();
        }

        await using (var recoveringEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "format-crash-recover", StorageStrategy = refused.CaptureDurableImages() }))
        {
            var recovered = await recoveringEngine.OpenDatabaseAsync(TestDatabase);
            await using var verify = await recovered.CreateSessionAsync();
            (await IdsAsync(verify, "SELECT id FROM events WHERE at = @p ORDER BY id", Instant)).ShouldBe([1, 2, 4]);
            ((SqlDatabaseSession)verify).LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_at");
            (await IdsAsync(verify, "SELECT id FROM events WHERE id > @p ORDER BY id", 0)).ShouldBe([1, 2, 4]);
        }

        // The crashed engine's disposal is best-effort (its streams are gated).
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: creating over existing storage is refused instead of re-stamping its catalog (#1099)")]
    public async Task Create_OverExistingCatalog_ShouldBeRefusedWithoutStamping()
    {
        // Arrange: a format-3 database, and a strategy that breaks the
        // CreateStorage contract by reopening storage that already exists.
        await CreateDatabaseAsync(formatVersion: 3);
        var before = Snapshot();

        // Act
        await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "format-reopening",
            StorageStrategy = new ReopeningStorageStrategy(new FileSystemSqlStorageStrategy(_rootPath)),
        }))
        {
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync(TestDatabase));

            // Assert: the format-3 catalog was not declared current...
            refusal.Message.ShouldContain($"Database '{TestDatabase}' cannot be created");
            refusal.Message.ShouldContain("already holds data-storage format 3 and 1 table(s)");
        }

        // ...so its files are as they were, and a real open still refuses it.
        AssertUnchanged(before);
        await using var reopening = CreateEngine();
        (await Should.ThrowAsync<DatabaseException>(async () => await reopening.OpenDatabaseAsync(TestDatabase)))
            .Message.ShouldContain("uses data-storage format 3");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a client whose startup names a refused database gets the refusal over the wire (#1099)")]
    public async Task Handshake_OlderFormatDatabase_ShouldSendRefusalToClient()
    {
        // Arrange
        await CreateDatabaseAsync(formatVersion: 3);
        await using var engine = CreateEngine();
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync();
        Connection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, TestTimeout.Token());
        await using var client = new ProtocolTestClient(connection);

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, TestDatabase, "tester").Encode());

        // Assert: the engine's actionable refusal, not an opaque internal error,
        // and the session closes.
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        error.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 3, but this engine supports only format 6");
        error.Message.ShouldContain("#1152");
        (await client.ReadAsync()).ShouldBeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "format", RootPath = _rootPath });

    /// <summary>
    /// Creates and closes a file-backed database holding a temporal index; when
    /// <paramref name="formatVersion"/> is set, its catalog marker is then forged
    /// to that version before the close.
    /// </summary>
    private async Task CreateDatabaseAsync(int? formatVersion)
    {
        var engine = CreateEngine();

        try
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(TestDatabase);
            await using (var session = await database.CreateSessionAsync())
            {
                await session.ExecuteAsync("CREATE TABLE events (id INT PRIMARY KEY, at TIMESTAMPTZ)");
                await session.ExecuteAsync("CREATE INDEX ix_at ON events (at)");
                await session.ExecuteAsync("INSERT INTO events VALUES (1, @a), (2, @b)",
                    new Dictionary<string, object?> { ["a"] = Instant, ["b"] = Instant.ToOffset(TimeSpan.FromHours(3)) });
            }

            if (formatVersion is { } version)
            {
                await database.Catalog.SetRecordSpaceFormatVersionAsync(version);
            }
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    /// <summary>Reads every file under the engine root, keyed by its relative path.</summary>
    private Dictionary<string, byte[]> Snapshot()
        => Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_rootPath, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private void AssertUnchanged(Dictionary<string, byte[]> before)
    {
        var after = Snapshot();
        after.Keys.Order(StringComparer.OrdinalIgnoreCase).ShouldBe(before.Keys.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var (path, bytes) in before)
        {
            AssertSameBytes(after[path], bytes, $"'{path}'");
        }
    }

    private static void AssertSameBytes(byte[] after, byte[] before, string what)
        => after.AsSpan().SequenceEqual(before).ShouldBeTrue($"The {what} was modified by the refused open.");

    /// <summary>
    /// A storage strategy that breaks the <see cref="ISqlStorageStrategy.CreateStorage"/>
    /// contract by reopening storage that already exists instead of refusing it.
    /// </summary>
    private sealed class ReopeningStorageStrategy(ISqlStorageStrategy inner) : ISqlStorageStrategy
    {
        public SqlStorage CreateStorage(string databaseName)
            => inner.StorageExists(databaseName) ? inner.OpenStorage(databaseName) : inner.CreateStorage(databaseName);

        public SqlStorage OpenStorage(string databaseName) => inner.OpenStorage(databaseName);

        public void DropStorage(string databaseName) => inner.DropStorage(databaseName);

        public bool StorageExists(string databaseName) => inner.StorageExists(databaseName);
    }

    private static async Task<int[]> IdsAsync(IDatabaseSession session, string sql, object parameter)
    {
        await using var result = (await session.ExecuteAsync(sql, new Dictionary<string, object?> { ["p"] = parameter }))
            .ShouldBeAssignableTo<QueryResultSet>();
        var ids = new List<int>();
        await foreach (var row in result!.GetRowsAsync())
        {
            ids.Add((int)row.GetValue(0)!);
        }
        return [.. ids];
    }
}
