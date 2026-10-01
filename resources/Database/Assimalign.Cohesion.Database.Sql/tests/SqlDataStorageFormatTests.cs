using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The data-storage format gate (#1099, owner decision of 2026-10-01): a new
/// database is created on format 4, and an existing database on any other format
/// is refused at open — an older one must be recreated (there is no upgrade path;
/// upgrades are #1152), and a newer one belongs to the engine that wrote it. The
/// refusal comes before the engine reads or writes the database, so its files are
/// left byte-identical and the engine that wrote them can still open them.
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: a new database is created on format 4 and reopens (#1099)")]
    public async Task Create_NewDatabase_ShouldBeOnFormatFourAndReopen()
    {
        // Arrange + Act: create, write temporal keys, close.
        await CreateDatabaseAsync(formatVersion: null);

        // Assert: the reopened database is on format 4 and its identity keys seek.
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.OpenDatabaseAsync(TestDatabase);
        database.Catalog.RecordSpaceFormatVersion.ShouldBe(4);
        SqlRowCodec.RecordSpaceFormatVersion.ShouldBe(4);

        await using var session = await database.CreateSessionAsync();
        (await IdsAsync(session, "SELECT id FROM events WHERE at = @p ORDER BY id", Instant.ToOffset(TimeSpan.FromHours(9)))).ShouldBe([1, 2]);
        ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_at");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Data-storage format: an older database is refused at open and its files are left untouched (#1099)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Open_OlderFormat_ShouldBeRefusedWithoutTouchingFiles(int version)
    {
        // Arrange: a closed database whose catalog marker says an older format —
        // 3 is what 10.0.0-preview.1 wrote (temporal keys with kind and offset).
        await CreateDatabaseAsync(formatVersion: version);
        var before = Snapshot();

        // Act
        await using var engine = CreateEngine();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(TestDatabase));

        // Assert: the message names the database, both formats and the remedy...
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format {version}");
        refusal.Message.ShouldContain("this engine supports only format 4");
        refusal.Message.ShouldContain("recreate the database");
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
        refusal.Message.ShouldContain($"Database '{TestDatabase}' uses data-storage format 5");
        refusal.Message.ShouldContain("this engine supports only format 4");
        refusal.Message.ShouldContain("written by a newer engine");
        AssertUnchanged(before);
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
            after[path].AsSpan().SequenceEqual(bytes).ShouldBeTrue($"'{path}' was modified by the refused open.");
        }
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
