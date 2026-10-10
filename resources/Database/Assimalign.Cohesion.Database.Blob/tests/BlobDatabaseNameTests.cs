using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// The blob engine maps every spelling of a database name to one database (#1392). Names compare
/// ignoring case; on disk a database is the directory named for the lower case of its name, holding
/// <c>blob.*</c>, so a case-variant spelling finds the same files on a case-sensitive file system
/// without enumerating the root, and the engine lists each database by the name it was created with,
/// which its storage header keeps. Each test asserts the exact directory names it leaves on disk,
/// which is what a case-sensitive file system sees.
/// </summary>
public sealed class BlobDatabaseNameTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-blob-names", Guid.NewGuid().ToString("N"));

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
                // Best-effort cleanup.
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Names: a database created as 'sales' and opened again as 'SALES' is the same database, with its data and its created name (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldOpenItWithItsData(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        var blob = await WriteAsync(created, "sales-blob");
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        var opened = await engine.OpenDatabaseAsync("SALES", TestTimeout.Token());
        var again = await engine.OpenDatabaseAsync("Sales", TestTimeout.Token());

        // Assert: one database, with its blob, reporting the spelling it was created with.
        (await ReadAsync(opened, blob)).ShouldBe("sales-blob");
        again.ShouldBeSameAs(opened);
        opened.Name.ToString().ShouldBe("sales");
        AssertDirectories(onDisk, "sales");
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Names: a create of 'SALES' after 'sales' is refused as existing, open or not (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabaseAsync_CaseVariantOfAnExistingDatabase_ShouldBeRefused(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        var blob = await WriteAsync(created, "sales-blob");

        // Act: refused while it is open, then by its files once it is closed.
        var whileOpen = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("SALES", TestTimeout.Token()));
        engine = await CloseAsync(engine, created, onDisk);
        var whileClosed = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("Sales", TestTimeout.Token()));

        // Assert: the refusals left the database as it was.
        whileOpen.ShouldNotBeOfType<DatabaseNotFoundException>();
        whileClosed.ShouldNotBeOfType<DatabaseNotFoundException>();
        AssertDirectories(onDisk, "sales");
        (await ReadAsync(await engine.OpenDatabaseAsync("sales", TestTimeout.Token()), blob)).ShouldBe("sales-blob");
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Names: a drop of 'SALES' drops the database created as 'sales' (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DropDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldDropIt(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(created, "sales-blob");
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        await engine.DropDatabaseAsync("SALES", TestTimeout.Token());

        // Assert: its files are gone, so the name opens nothing, lists nothing and creates afresh.
        AssertDirectories(onDisk);
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("sales", TestTimeout.Token()));
        (await ListAsync(engine)).ShouldBeEmpty();
        var recreated = await engine.CreateDatabaseAsync("Sales", TestTimeout.Token());
        recreated.Name.ToString().ShouldBe("Sales");
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Names: the engine lists a database once, by the name it was created with, however it was opened (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetDatabasesAsync_AfterCaseVariantOpens_ShouldReportOneDatabase(bool onDisk)
    {
        // Arrange: created with mixed case and closed.
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("Sales", TestTimeout.Token());
        engine = await CloseAsync(engine, created, onDisk);

        // Act: listed from its files alone, then again after opens under two other spellings.
        var fromFiles = await ListAsync(engine);
        await engine.OpenDatabaseAsync("SALES", TestTimeout.Token());
        await engine.OpenDatabaseAsync("sales", TestTimeout.Token());
        var afterOpens = await ListAsync(engine);

        // Assert: the lower-case directory lists as the spelling the database was created with.
        fromFiles.ShouldBe(["Sales"]);
        afterOpens.ShouldBe(["Sales"]);
        AssertDirectories(onDisk, "sales");
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Names: a database's directory is the lower case of its name, and a directory no name maps to is not listed (#1392)")]
    public async Task CreateDatabaseAsync_UpperCaseName_ShouldCreateTheLowerCaseDirectory()
    {
        // Arrange: a directory made outside the engine that no database name maps to (an upper-case
        // twin a case-sensitive file system could hold), with a data file in it.
        await using var engine = CreateEngine(onDisk: true);
        string stray = Path.Combine(_rootPath, "Stray");
        Directory.CreateDirectory(stray);
        await File.WriteAllBytesAsync(Path.Combine(stray, "blob.dat"), [], TestTimeout.Token());

        // Act
        await engine.CreateDatabaseAsync("SALES", TestTimeout.Token());
        var listed = await ListAsync(engine);

        // Assert: the exact entries on disk, compared ordinally, and only the database is listed.
        AssertDirectories(onDisk: true, "Stray", "sales");
        Directory.EnumerateFiles(Path.Combine(_rootPath, "sales")).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe(["blob.bak", "blob.dat", "blob.log"]);
        listed.ShouldBe(["SALES"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Names: a directory without a data file is no database: an open does not find it, a create refuses it, a drop clears it (#1392)")]
    public async Task OpenDatabaseAsync_DirectoryWithoutADataFile_ShouldNotFindIt()
    {
        // Arrange: the directory 'sales' maps to, empty, as an interrupted create or drop leaves it.
        await using var engine = CreateEngine(onDisk: true);
        Directory.CreateDirectory(Path.Combine(_rootPath, "sales"));

        // Act
        var open = await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("SALES", TestTimeout.Token()));
        var listed = await ListAsync(engine);
        var create = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("sales", TestTimeout.Token()));
        await engine.DropDatabaseAsync("Sales", TestTimeout.Token());
        var afterDrop = Directory.EnumerateDirectories(_rootPath).ToArray();
        var created = await engine.CreateDatabaseAsync("Sales", TestTimeout.Token());

        // Assert: not found rather than a missing file, refused with the remedy, then cleared.
        open.Message.ShouldBe("Database 'SALES' does not exist.");
        listed.ShouldBeEmpty();
        create.ShouldNotBeOfType<DatabaseNotFoundException>();
        create.Message.ShouldContain("without a database in it", Case.Sensitive);
        afterDrop.ShouldBeEmpty();
        created.Name.ToString().ShouldBe("Sales");
        AssertDirectories(onDisk: true, "sales");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Names: a lookup of a name that breaks the rule finds nothing instead of throwing (#1392)")]
    [InlineData("CON")]
    [InlineData("a:b")]
    [InlineData("salés")]
    [InlineData("../victim")]
    public async Task TryGetDatabase_NameThatBreaksTheRule_ShouldFindNothing(string name)
    {
        // Arrange
        await using var engine = CreateEngine(onDisk: false);

        // Act
        bool found = engine.TryGetDatabase(name, out var database);

        // Assert: no such database is ever open, so the lookup is a miss, not a refusal.
        found.ShouldBeFalse();
        database.ShouldBeNull();
    }

    private BlobDatabaseEngine CreateEngine(bool onDisk)
        => BlobDatabaseEngine.Create("names", onDisk ? new BlobDatabaseEngineOptions { RootPath = _rootPath } : new BlobDatabaseEngineOptions());

    // Closes a database so the next open reads its files: on disk, the engine restarts over the
    // same root; in memory, the holder disposes the database and the engine keeps its files.
    private async Task<BlobDatabaseEngine> CloseAsync(BlobDatabaseEngine engine, BlobDatabase database, bool onDisk)
    {
        if (!onDisk)
        {
            await database.DisposeAsync();
            return engine;
        }

        await engine.DisposeAsync();
        return CreateEngine(onDisk);
    }

    // The directories under the root, exactly: what a case-sensitive file system would see.
    private void AssertDirectories(bool onDisk, params string[] expected)
    {
        if (!onDisk)
        {
            return;
        }

        Directory.EnumerateDirectories(_rootPath).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(expected);
    }

    private static async Task<List<string>> ListAsync(BlobDatabaseEngine engine)
    {
        var names = new List<string>();
        await foreach (var database in engine.GetDatabasesAsync(TestTimeout.Token()))
        {
            names.Add(database.Name.ToString());
        }

        return names;
    }

    // Writes one blob whose bytes are the marker, into a container of its own, and returns its name.
    private static async Task<string> WriteAsync(BlobDatabase database, string marker)
    {
        var container = await AutocommitContainer.CreateAsync(database, "files", TestTimeout.Token());
        await BlobEngineTests.WriteAsync(container, "note", Encoding.UTF8.GetBytes(marker));
        return "note";
    }

    // The marker the blob holds.
    private static async Task<string> ReadAsync(BlobDatabase database, string name)
    {
        var container = await AutocommitContainer.GetAsync(database, "files", TestTimeout.Token());
        return Encoding.UTF8.GetString(await BlobEngineTests.ReadAsync(container, name));
    }
}
