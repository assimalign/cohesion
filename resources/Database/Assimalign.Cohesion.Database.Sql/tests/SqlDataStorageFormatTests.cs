using System.Threading;
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
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// The data-storage format gate (#1099, owner decisions of 2026-10-01 and
/// 2026-10-02): a new database is created on format 25, and an existing database on
/// any other format is refused at open — an older one must be dropped and recreated
/// (there is no upgrade path; upgrades are #1152), and a newer one belongs to the
/// engine that wrote it. The gate reads only the catalog, before the data file set is
/// opened: a cleanly closed database is left byte-identical, and a crashed one keeps
/// its data files and its journals, so the engine that wrote it can still recover and
/// open it. Format 4 differs from 5 only in its index trees (#1194: B-tree page
/// format 1, entries ordered by key alone), and the index manager checks every tree's
/// own page format behind the gate. Format 5 differs from 6 only in its catalog: format
/// 6 table records carry the dropped columns' physical ordinals (#1241), and rows are
/// decoded through them. Format 6 differs from 7 only in its catalog too: format 7 table
/// records carry each column's domain name (phase E3). Format 8 adds a mandatory
/// DDL generation to counter and applied-schema records (phase B2); a real older
/// counter must still reach the same actionable format refusal.
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a new database is created on format 25 and reopens (#1099, #1194, #1241, E3)")]
    public async Task Create_NewDatabase_ShouldUseCurrentFormatAndReopen()
    {
        // Arrange + Act: create, write temporal keys, close.
        await CreateDatabaseAsync(formatVersion: null);

        // Assert: the reopened database is on format 25 and its identity keys seek.
        await using var engine = CreateEngine();
        var database = await engine.OpenDatabaseAsync(TestDatabase);
        database.Catalog.RecordSpaceFormatVersion.ShouldBe(25);
        SqlRowCodec.RecordSpaceFormatVersion.ShouldBe(25);

        await using var session = await database.CreateSessionAsync();
        (await IdsAsync(session, "SELECT id FROM events WHERE at = @p ORDER BY id", Instant.ToOffset(TimeSpan.FromHours(9)))).ShouldBe([1, 2]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_at");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: an older database is refused at open and its files are left untouched (#1099, #1194, #1241, E3)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public async Task Open_OlderFormat_ShouldBeRefusedWithoutTouchingFiles(int version)
    {
        // Arrange: a closed database whose catalog marker says an older format —
        // 3 is what 10.0.0-preview.1 wrote (temporal keys with kind and offset),
        // 4 what engines before #1194 wrote (index entries ordered by key alone), 5
        // what engines before #1241 wrote (no dropped-column layout in the catalog), 6
        // what engines before E3 wrote (no domain names in the catalog).
        await CreateDatabaseAsync(formatVersion: version);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: the message names the database, both formats and the remedy —
        // drop and create again, since create refuses a name whose storage
        // exists; version 1 (no marker) also covers an interrupted creation...
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format {version}");
        refusal.Message.ShouldContain("this engine supports only format 25");
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Refused catalog: a torn journal tail is preserved by the read-only format probe (#1158)")]
    public async Task Open_OlderCatalogWithTornJournalTail_ShouldRefuseWithoutTruncatingFiles()
    {
        // Arrange
        await CreateDatabaseAsync(formatVersion: 7);
        using (var journal = new FileStream(Path.Combine(_rootPath, TestDatabase, "catalog.log"), FileMode.Append, FileAccess.Write))
        {
            journal.Write([1, 2, 3]);
        }
        var before = Snapshot();
        await using var engine = CreateEngine();

        // Act
        var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(() => engine.OpenDatabaseAsync(TestDatabase, CancellationToken.None).AsTask());

        // Assert
        refusal.Message.ShouldContain("uses data-storage format 7", Case.Sensitive);
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a pre-generation counter reaches the format gate and leaves files untouched (B2)")]
    public async Task Open_FormatSevenCounterWithoutGeneration_ShouldUseExistingFormatGate()
    {
        // Arrange: format 7's actual counter tuple, not only its format marker.
        await CreateDatabaseAsync(formatVersion: 7);
        RemoveCounterGeneration();
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: this is the refusal the server forwards as Unavailable.
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 7, but this engine supports only format 25");
        refusal.Message.ShouldContain("export its data with the engine that wrote it");
        refusal.Message.ShouldContain("#1152");
        engine.TryGetDatabase(TestDatabase, out _).ShouldBeFalse();
        AssertUnchanged(before);
        (await Should.ThrowAsync<SqlDataStorageFormatException>(async () => await engine.OpenDatabaseAsync(TestDatabase)))
            .Message.ShouldBe(refusal.Message);
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a marker-free legacy counter reaches the actionable format refusal")]
    public async Task Open_MarkerFreeLegacyCounter_ShouldRefuseFormatOneWithoutTouchingFiles()
    {
        // Arrange: format 1 genuinely predates the marker and generation suffix.
        await CreateDatabaseAsync(formatVersion: 1);
        RemoveCounterGeneration(removeFormatMarker: true);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert
        refusal.Message.ShouldContain("uses data-storage format 1, but this engine supports only format 25");
        refusal.Message.ShouldContain("drop the database (DropDatabaseAsync) and create it again");
        engine.TryGetDatabase(TestDatabase, out _).ShouldBeFalse();
        AssertUnchanged(before);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a damaged marker-free legacy catalog remains corruption")]
    [InlineData(2, 2)]
    [InlineData(4, -1)]
    [InlineData(9, 1)]
    public async Task Open_DamagedMarkerFreeLegacyCatalog_ShouldPreserveCatalogRefusal(int kind, int value)
    {
        // Arrange: one genuine old counter, plus corruption that must not become a format refusal.
        await CreateDatabaseAsync(formatVersion: 1);
        RemoveCounterGeneration(removeFormatMarker: true);
        using (var storage = new FileSystemSqlStorageStrategy(_rootPath).OpenStorage(TestDatabase, catalog: true))
        {
            var record = new DatabaseKeyWriter();
            record.AppendInt32(kind);
            if (kind == 2)
            {
                record.AppendInt64(value);
            }
            else
            {
                record.AppendInt32(value);
            }
            using var transaction = storage.BeginTransaction();
            storage.InsertRow(transaction, record.ToArray());
            transaction.Commit();
        }
        var before = Snapshot();

        // Act / Assert
        await using var engine = CreateEngine();
        await Should.ThrowAsync<SqlCatalogException>(async () => await engine.OpenDatabaseAsync(TestDatabase));
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a current-format counter missing its generation remains catalog damage (B2)")]
    public async Task Open_CurrentFormatCounterWithoutGeneration_ShouldPreserveCatalogRefusal()
    {
        // Arrange: the current marker promises a generation that the counter lacks.
        await CreateDatabaseAsync(formatVersion: null);
        RemoveCounterGeneration();
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<SqlCatalogException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: catalog corruption is not reported as another engine's format.
        refusal.Message.ShouldContain("Unsupported catalog counter format");
        engine.TryGetDatabase(TestDatabase, out _).ShouldBeFalse();
        AssertUnchanged(before);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a newer database reaches the format gate even with unknown record kinds")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_NewerFormat_ShouldBeRefusedWithoutTouchingFiles(bool unknownRecord)
    {
        // Arrange
        await CreateDatabaseAsync(formatVersion: SqlRowCodec.RecordSpaceFormatVersion + 1);
        if (unknownRecord)
        {
            using var storage = new FileSystemSqlStorageStrategy(_rootPath).OpenStorage(TestDatabase, catalog: true);
            using var transaction = storage.BeginTransaction();
            storage.InsertRow(transaction, new DatabaseKeyWriter().AppendInt32(999).AppendInt32(1).ToArray());
            transaction.Commit();
        }
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format {SqlRowCodec.RecordSpaceFormatVersion + 1}");
        refusal.Message.ShouldContain("this engine supports only format 25");
        refusal.Message.ShouldContain("written by a newer engine");
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CREATE DATABASE: every accepted crash image contains dbo")]
    public async Task CreateDatabaseAsync_EveryCrashPoint_ShouldPublishDboBeforeCurrentFormat()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var creator = SqlDatabaseEngine.Create("creation-crashes", new() { StorageStrategy = storage });
        using var recording = storage.RecordCrashPoints();

        // Act
        await creator.CreateDatabaseAsync(TestDatabase, CancellationToken.None);
        recording.Dispose();

        // Assert: every point is exercised through create, open and application Build.
        int preHeader = 0;
        for (int point = 0; point < recording.Count; point++)
        {
            for (int route = 0; route < 3; route++)
            {
                var image = recording.Open(point);
                bool incompleteHeader = image.ProbeStorage(TestDatabase, catalog: false)?.IsPreHeader == true
                    || image.ProbeStorage(TestDatabase, catalog: true)?.IsPreHeader == true;
                if (incompleteHeader) { preHeader++; }
                if (route == 2)
                {
                    var builder = Assimalign.Cohesion.Database.Hosting.DatabaseApplication.CreateBuilder();
                    builder.AddSql("creation-build", sql =>
                    {
                        sql.Options.StorageStrategy = image;
                        sql.AddDatabase(TestDatabase);
                    });
                    await using var application = builder.Build(CancellationToken.None);
                    var engine = application.Context.GetEngine("creation-build").ShouldBeOfType<SqlDatabaseEngine>();
                    engine.TryGetDatabase(TestDatabase, out SqlDatabase? built).ShouldBeTrue();
                    built!.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
                    built.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo", $"Build crash point {point}");
                    await using var builtSession = await built.CreateSessionAsync(CancellationToken.None);
                    (await builtSession.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
                    (await builtSession.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
                    (await IdsAsync(builtSession, "SELECT id FROM t", 0)).ShouldBe([1]);
                    continue;
                }
                await using var reopened = SqlDatabaseEngine.Create("creation-image", new() { StorageStrategy = image });
                SqlDatabase database;
                if (route == 0)
                {
                    try { database = await reopened.CreateDatabaseAsync(TestDatabase, CancellationToken.None); }
                    catch (DatabaseException failure)
                    {
                        failure.Message.ShouldContain("already exists", Case.Sensitive);
                        database = await reopened.OpenDatabaseAsync(TestDatabase, CancellationToken.None);
                    }
                }
                else
                {
                    try { database = await reopened.OpenDatabaseAsync(TestDatabase, CancellationToken.None); }
                    catch (DatabaseNotFoundException failure)
                    {
                        if (incompleteHeader)
                        {
                            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLC001");
                            failure.Message.ShouldContain("CreateDatabaseAsync", Case.Sensitive);
                        }
                        database = await reopened.CreateDatabaseAsync(TestDatabase, CancellationToken.None);
                    }
                }
                database.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
                database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo", $"Crash point {point}, route {route}");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                (await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
                (await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
                (await IdsAsync(session, "SELECT id FROM t", 0)).ShouldBe([1]);
            }
        }
        recording.Count.ShouldBeGreaterThan(1);
        preHeader.ShouldBeGreaterThanOrEqualTo(6);
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
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 4, but this engine supports only format 25", Case.Sensitive);
        refusal.Message.ShouldContain("export its data with the engine that wrote it");
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: index pages in another B-tree page format are refused at open with COHDBI001 (#1194)")]
    public async Task Open_IndexPagesInFormatOne_ShouldBeRefusedByTheIndexManager()
    {
        // Arrange: the current format-8 marker over index trees in B-tree page format 1 — a
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
        refusal.Message.ShouldStartWith($"Database '{TestDatabase}' uses data-storage format 25, but one of its index trees does not: ", Case.Sensitive);
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

    /// <summary>
    /// Below the data-storage format sits the storage format of each file set (#1251): the
    /// storage refuses a file set in another storage format with <c>COHDBS001</c> before
    /// anything is read through it. A database has two file sets, so the engine names the
    /// database and the one that was refused, and forwards the refusal like its own format
    /// errors.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Storage format: a file set in storage format 2 (the format before #1253) is refused with COHDBS001 naming the database and the file set, its files untouched (#1251, #1253)")]
    [InlineData("catalog")]
    [InlineData("data")]
    public async Task Open_FileSetInStorageFormatTwo_ShouldBeRefusedNamingTheDatabaseAndTheFileSet(string role)
    {
        // Arrange: a closed database whose page 0 in one file set names storage format 2, the format before #1253.
        await CreateDatabaseAsync(formatVersion: null);
        bool catalog = role == "catalog";
        string storageName = catalog ? TestDatabase + "/catalog" : TestDatabase;
        StorageFormatFiles.WriteVersion(Path.Combine(_rootPath, TestDatabase, catalog ? "catalog.dat" : "sql.dat"), version: 2);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert
        refusal.ShouldBeOfType<SqlDataStorageFormatException>();
        refusal.Message.ShouldStartWith(
            $"Database '{TestDatabase}' cannot be opened: its {role} file set '{storageName}' was refused. {StorageFormatException.ErrorCode}: ",
            Case.Sensitive);
        refusal.Message.ShouldContain("uses storage format 2, but this engine supports only storage format 3", Case.Sensitive);
        refusal.InnerException.ShouldBeOfType<StorageFormatException>().FoundVersion.ShouldBe(2);
        engine.TryGetDatabase(TestDatabase, out _).ShouldBeFalse();
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a populated database without catalog storage is refused without creating one (#1099)")]
    public async Task Open_MissingCatalogStorage_ShouldBeRefusedWithoutCreatingIt()
    {
        // Arrange: a database whose creation stopped before its catalog existed.
        await CreateDatabaseAsync(formatVersion: null);
        foreach (string extension in new[] { ".dat", ".log", ".bak" })
        {
            File.Delete(Path.Combine(_rootPath, TestDatabase, "catalog" + extension));
        }

        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: the orphaned data is refused and no replacement catalog is created.
        refusal.Message.ShouldContain($"Database '{TestDatabase}' has missing or incomplete catalog storage");
        refusal.Message.ShouldContain("#1152");
        File.Exists(Path.Combine(_rootPath, TestDatabase, "catalog.dat")).ShouldBeFalse();
        AssertUnchanged(before);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a crashed older database is refused without opening its data files and keeps its journals (#1099)")]
    public async Task Open_CrashedOlderFormat_ShouldBeRefusedAndStayRecoverable()
    {
        // Arrange: a format-3 database (the marker forged before the workload)
        // with committed rows, an uncommitted writer whose records a later commit
        // made durable, then a crash.
        var strategy = new CrashCaptureSqlStorageStrategy();
        var engine = SqlDatabaseEngine.Create("format-crash", new SqlDatabaseEngineOptions { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync(TestDatabase);
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
        var catalogImage = crashed.GetDurableImage(TestDatabase, catalog: true);

        // Act: open twice with an engine that refuses format 3.
        string message;
        await using (var refusingEngine = SqlDatabaseEngine.Create("format-crash-refuse", new SqlDatabaseEngineOptions { StorageStrategy = crashed }))
        {
            message = (await Should.ThrowAsync<DatabaseException>(async () => await refusingEngine.OpenDatabaseAsync(TestDatabase))).Message;
            (await Should.ThrowAsync<DatabaseException>(async () => await refusingEngine.OpenDatabaseAsync(TestDatabase)))
                .ShouldHaveMessage(message);
        }

        // Assert: refused on the catalog alone. The data file set was never
        // opened, so recovery did not replay into it, and the catalog's journal
        // survived its own open and close.
        message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 3");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase).Data, dataImage.Data, "data file");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase).Journal, dataImage.Journal, "data journal");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase).Backup, dataImage.Backup, "data backup");
        AssertSameBytes(crashed.GetDurableImage(TestDatabase, catalog: true).Journal, catalogImage.Journal, "catalog journal");

        // The engine that wrote the database can still recover it. Stand in for
        // it by re-forging the marker to this engine's format over the post-refusal
        // images (its index pages are this engine's): the open then classifies the
        // journal and scrubs the uncommitted writer.
        var refused = crashed.CaptureDurableImages();
        var catalogStorage = refused.OpenStorage(TestDatabase, catalog: true);
        try
        {
            await SqlCatalog.Open(catalogStorage).SetRecordSpaceFormatVersionAsync(SqlRowCodec.RecordSpaceFormatVersion);
        }
        finally
        {
            catalogStorage.Dispose();
        }

        await using (var recoveringEngine = SqlDatabaseEngine.Create("format-crash-recover", new SqlDatabaseEngineOptions { StorageStrategy = refused.CaptureDurableImages() }))
        {
            var recovered = await recoveringEngine.OpenDatabaseAsync(TestDatabase);
            await using var verify = await recovered.CreateSessionAsync();
            (await IdsAsync(verify, "SELECT id FROM events WHERE at = @p ORDER BY id", Instant)).ShouldBe([1, 2, 4]);
            verify.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_at");
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
        await using (var engine = SqlDatabaseEngine.Create("format-reopening", new SqlDatabaseEngineOptions
        {
            StorageStrategy = new ReopeningStorageStrategy(new FileSystemSqlStorageStrategy(_rootPath)),
        }))
        {
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync(TestDatabase));

            // Assert: the format-3 catalog was not declared current...
            refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 3");
            refusal.Message.ShouldContain("this engine supports only format 25");
        }

        // ...so its files are as they were, and a real open still refuses it.
        AssertUnchanged(before);
        await using var reopening = CreateEngine();
        (await Should.ThrowAsync<DatabaseException>(async () => await reopening.OpenDatabaseAsync(TestDatabase)))
            .Message.ShouldContain("uses data-storage format 3");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a client whose startup names a refused database gets the refusal over the wire (#1099, B2)")]
    [InlineData(3, false)]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    [InlineData(18, false)]
    [InlineData(19, false)]
    [InlineData(20, false)]
    [InlineData(21, false)]
    [InlineData(22, false)]
    [InlineData(7, true)]
    [InlineData(1, true)]
    public async Task Handshake_OlderFormatDatabase_ShouldSendRefusalToClient(int version, bool removeCounterGeneration)
    {
        // Arrange
        await CreateDatabaseAsync(formatVersion: version);
        if (removeCounterGeneration)
        {
            RemoveCounterGeneration(removeFormatMarker: version == 1);
        }
        await using var engine = CreateEngine();
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync();
        IDuplexConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, TestTimeout.Token());
        await using var client = new ProtocolTestClient(connection);

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, TestDatabase, "tester").Encode());

        // Assert: the engine's actionable refusal, not an opaque internal error,
        // and the session closes.
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        error.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format {version}, but this engine supports only format 25");
        error.Message.ShouldContain("#1152");
        (await client.ReadAsync()).ShouldBeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create("format", new SqlDatabaseEngineOptions { RootPath = _rootPath });

    /// <summary>
    /// Creates and closes a file-backed database holding a temporal index; when
    /// <paramref name="formatVersion"/> is set, its catalog marker is then forged
    /// to that version before the close.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Large older catalogs: a refusal preserves every file before real recovery (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_MultiPageOlderCatalog_ShouldRefuseWithoutWriting(bool storageFormat)
    {
        // Arrange: retain an uncheckpointed catalog that requires physical redo.
        var strategy = new CrashCaptureSqlStorageStrategy();
        Dictionary<string, byte[]> crash;
        await using (var creator = SqlDatabaseEngine.Create("large-catalog", new() { StorageStrategy = strategy }))
        {
            var database = await creator.CreateDatabaseAsync(TestDatabase, CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            for (int index = 0; index < 50; index++)
            {
                (await session.ExecuteAsync($"CREATE TABLE t{index} (id INT, name TEXT, value1 INT, value2 INT, value3 INT)", cancellationToken: CancellationToken.None))
                    .Status.ShouldBe(DatabaseResultStatus.Success);
            }
            await database.Catalog.SetRecordSpaceFormatVersionAsync(7, CancellationToken.None);
            var catalog = strategy.GetDurableImage(TestDatabase, catalog: true);
            catalog.Data.Length.ShouldBeGreaterThan(2 * Assimalign.Cohesion.Database.Storage.Units.Page.Size);
            catalog.Journal.ShouldNotBeEmpty();
            var data = strategy.GetDurableImage(TestDatabase);
            crash = new Dictionary<string, byte[]>
            {
                ["catalog.dat"] = catalog.Data, ["catalog.log"] = catalog.Journal, ["catalog.bak"] = catalog.Backup,
                ["sql.dat"] = data.Data, ["sql.log"] = data.Journal, ["sql.bak"] = data.Backup,
            };
        }
        string directory = Path.Combine(_rootPath, TestDatabase);
        Directory.CreateDirectory(directory);
        foreach (var file in crash) { File.WriteAllBytes(Path.Combine(directory, file.Key), file.Value); }
        if (storageFormat) { StorageFormatFiles.WriteVersion(Path.Combine(directory, "catalog.dat"), version: 2); }
        var before = Snapshot();
        await using var engine = CreateEngine();

        // Act
        var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(() => engine.OpenDatabaseAsync(TestDatabase, CancellationToken.None).AsTask());

        // Assert
        refusal.Message.ShouldContain(storageFormat ? "uses storage format 2" : "uses data-storage format 7", Case.Sensitive);
        AssertUnchanged(before);
        await Should.ThrowAsync<SqlDataStorageFormatException>(() => engine.OpenDatabaseAsync(TestDatabase, CancellationToken.None).AsTask());
        AssertUnchanged(before);
    }

    private async Task CreateDatabaseAsync(int? formatVersion)
    {
        var engine = CreateEngine();

        try
        {
            var database = await engine.CreateDatabaseAsync(TestDatabase);
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

    private void RemoveCounterGeneration(bool removeFormatMarker = false)
    {
        var strategy = new FileSystemSqlStorageStrategy(_rootPath);
        using var storage = strategy.OpenStorage(TestDatabase, catalog: true);
        (PageId PageId, int SlotIndex)? location = null;
        long nextObjectId = 0;
        (PageId PageId, int SlotIndex)? formatLocation = null;
        using (var iterator = storage.GetUnitIterator())
        {
            while (iterator.MoveNext())
            {
                var unit = iterator.Current;
                var reader = new DatabaseKeyReader(unit.Data.Span);
                int kind = reader.ReadInt32();
                if (kind is 4 or 8)
                {
                    formatLocation = (unit.PageId, unit.SlotIndex);
                }
                if (kind == 2)
                {
                    location.ShouldBeNull();
                    location = (unit.PageId, unit.SlotIndex);
                    nextObjectId = reader.ReadInt64();
                }
            }
        }

        location.HasValue.ShouldBeTrue();
        var counter = new DatabaseKeyWriter();
        counter.AppendInt32(2).AppendInt64(nextObjectId);
        using var transaction = storage.BeginTransaction();
        var persisted = location.GetValueOrDefault();
        storage.UpdateRow(transaction, persisted.PageId, persisted.SlotIndex, counter.ToArray());
        if (removeFormatMarker)
        {
            var format = formatLocation.ShouldNotBeNull();
            storage.DeleteRow(transaction, format.PageId, format.SlotIndex);
        }
        transaction.Commit();
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
    /// A storage strategy that breaks the <see cref="SqlStorageStrategy.CreateStorage"/>
    /// contract by reopening storage that already exists instead of refusing it.
    /// </summary>
    private sealed class ReopeningStorageStrategy : SqlStorageStrategy
    {
        private readonly SqlStorageStrategy _inner;

        public ReopeningStorageStrategy(SqlStorageStrategy inner)
        {
            _inner = inner;
        }

        public override SqlStorage CreateStorage(string databaseName, bool catalog)
            => _inner.StorageExists(databaseName, catalog) ? _inner.OpenStorage(databaseName, catalog) : _inner.CreateStorage(databaseName, catalog);

        public override SqlStorage OpenStorage(string databaseName, bool catalog, CancellationToken cancellationToken = default) => _inner.OpenStorage(databaseName, catalog);

        public override void DropStorage(string databaseName) => _inner.DropStorage(databaseName);

        public override bool StorageExists(string databaseName, bool catalog) => _inner.StorageExists(databaseName, catalog);
    }

    private static async Task<int[]> IdsAsync(SqlDatabaseSession session, string sql, object parameter)
    {
        await using var result = (await session.ExecuteAsync(sql, new Dictionary<string, object?> { ["p"] = parameter }))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var ids = new List<int>();
        await foreach (var row in result!.GetRowsAsync())
        {
            ids.Add((int)row.GetValue(0)!);
        }
        return [.. ids];
    }
}
