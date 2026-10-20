using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The key-value engine maps every spelling of a database name to one database (#1392). Names
/// compare ignoring case; on disk a database is the directory named for the lower case of its name,
/// holding its data set (<c>keyvalue.*</c>) and its catalog set (<c>catalog.*</c>), so a case-variant
/// spelling finds the same files on a case-sensitive file system. Ubuntu CI failed
/// <c>KeyValueEngineDeclarationTests.BuildAsync_DeclaredDatabaseExists_ShouldOpenItWithItsData</c>
/// on exactly this before #1392: the declaration of <c>SALES</c> did not find <c>sales</c> and
/// created a second, empty database. Each test asserts the exact directory names it leaves on
/// disk, which is what a case-sensitive file system sees.
/// </summary>
public sealed class KeyValueDatabaseNameTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-kv-names", Guid.NewGuid().ToString("N"));

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

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a database created as 'sales' and opened again as 'SALES' is the same database, with its data and its created name (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldOpenItWithItsData(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(created, "sales-entry");
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        var opened = await engine.OpenDatabaseAsync("SALES", TestTimeout.Token());
        var again = await engine.OpenDatabaseAsync("Sales", TestTimeout.Token());

        // Assert: one database, with its entry, reporting the spelling it was created with.
        (await ReadAsync(opened)).ShouldBe("sales-entry");
        again.ShouldBeSameAs(opened);
        opened.Name.ToString().ShouldBe("sales");
        AssertDirectories(onDisk, "sales");
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a declaration of 'SALES' opens the database an earlier build created as 'sales' (#1392)")]
    public async Task BuildAsync_DeclarationInAnotherCase_ShouldOpenTheExistingDatabase()
    {
        // Arrange
        await using (var first = await Declare("sales").BuildAsync(TestTimeout.Token()))
        {
            first.TryGetDatabase("sales", out KeyValueDatabase? created).ShouldBeTrue();
            await WriteAsync(created!, "kept");
        }

        // Act
        await using var second = await Declare("SALES").BuildAsync(TestTimeout.Token());

        // Assert: opened, not created again beside it.
        second.TryGetDatabase("SALES", out KeyValueDatabase? opened).ShouldBeTrue();
        (await ReadAsync(opened!)).ShouldBe("kept");
        opened!.Name.ToString().ShouldBe("sales");
        AssertDirectories(onDisk: true, "sales");
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a create of 'SALES' after 'sales' is refused as existing, open or not (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabaseAsync_CaseVariantOfAnExistingDatabase_ShouldBeRefused(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(created, "sales-entry");

        // Act: refused while it is open, then by its files once it is closed.
        var whileOpen = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("SALES", TestTimeout.Token()));
        engine = await CloseAsync(engine, created, onDisk);
        var whileClosed = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("Sales", TestTimeout.Token()));

        // Assert: the refusals left the database as it was.
        whileOpen.ShouldNotBeOfType<DatabaseNotFoundException>();
        whileClosed.ShouldNotBeOfType<DatabaseNotFoundException>();
        AssertDirectories(onDisk, "sales");
        (await ReadAsync(await engine.OpenDatabaseAsync("sales", TestTimeout.Token()))).ShouldBe("sales-entry");
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a drop of 'SALES' drops the database created as 'sales' (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DropDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldDropIt(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(created, "sales-entry");
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        await engine.DropDatabaseAsync("SALES", TestTimeout.Token());

        // Assert: both file sets are gone, so the name opens nothing and creates afresh.
        AssertDirectories(onDisk);
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("sales", TestTimeout.Token()));
        var recreated = await engine.CreateDatabaseAsync("Sales", TestTimeout.Token());
        recreated.Name.ToString().ShouldBe("Sales");
        (await ReadAsync(recreated)).ShouldBeNull();
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: opens under two spellings list one database, by the name it was created with (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetDatabasesAsync_AfterCaseVariantOpens_ShouldReportOneDatabase(bool onDisk)
    {
        // Arrange: created with mixed case, then opened as two other spellings.
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("Sales", TestTimeout.Token());
        engine = await CloseAsync(engine, created, onDisk);
        await engine.OpenDatabaseAsync("SALES", TestTimeout.Token());
        await engine.OpenDatabaseAsync("sales", TestTimeout.Token());

        // Act
        var names = new List<string>();
        await foreach (var database in engine.GetDatabasesAsync(TestTimeout.Token()))
        {
            names.Add(database.Name.ToString());
        }

        // Assert
        names.ShouldBe(["Sales"]);
        AssertDirectories(onDisk, "sales");
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a database's directory is the lower case of its name and holds both file sets, as a case-sensitive file system sees them (#1392)")]
    public async Task CreateDatabaseAsync_UpperCaseName_ShouldCreateTheLowerCaseDirectory()
    {
        // Arrange
        await using var engine = CreateEngine(onDisk: true);

        // Act
        await engine.CreateDatabaseAsync("SALES", TestTimeout.Token());

        // Assert: the exact entries on disk, compared ordinally.
        AssertDirectories(onDisk: true, "sales");
        Directory.EnumerateFiles(Path.Combine(_rootPath, "sales")).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe(["catalog.bak", "catalog.dat", "catalog.log", "keyvalue.bak", "keyvalue.dat", "keyvalue.log"]);
        Directory.EnumerateFiles(_rootPath).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a database named 'sales.catalog' beside 'sales' keeps its own files, apart from the catalog of 'sales' (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabaseAsync_NamedLikeAnotherDatabasesCatalog_ShouldKeepTheirFilesApart(bool onDisk)
    {
        // Arrange: until #1392 the catalog of 'sales' was the storage named 'sales.catalog'.
        var engine = CreateEngine(onDisk);
        var sales = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(sales, "sales-entry");

        // Act
        var lookalike = await engine.CreateDatabaseAsync("sales.catalog", TestTimeout.Token());
        await WriteAsync(lookalike, "lookalike-entry");
        await sales.DisposeAsync();
        engine = await CloseAsync(engine, lookalike, onDisk);

        // Assert: both reopen with their own entries, and a drop of one leaves the other.
        AssertDirectories(onDisk, "sales", "sales.catalog");
        (await ReadAsync(await engine.OpenDatabaseAsync("sales", TestTimeout.Token()))).ShouldBe("sales-entry");
        (await ReadAsync(await engine.OpenDatabaseAsync("sales.catalog", TestTimeout.Token()))).ShouldBe("lookalike-entry");
        await engine.DropDatabaseAsync("sales", TestTimeout.Token());
        AssertDirectories(onDisk, "sales.catalog");
        (await ReadAsync(await engine.OpenDatabaseAsync("SALES.CATALOG", TestTimeout.Token()))).ShouldBe("lookalike-entry");
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a directory without a data file is no database: an open does not find it, a create refuses it, a drop clears it (#1392)")]
    public async Task OpenDatabaseAsync_DirectoryWithoutADataFile_ShouldNotFindIt()
    {
        // Arrange: the directory 'sales' maps to, holding only a catalog set, as an interrupted
        // drop can leave it.
        await using var engine = CreateEngine(onDisk: true);
        string directory = Path.Combine(_rootPath, "sales");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "catalog.dat"), [], TestTimeout.Token());

        // Act
        var open = await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("SALES", TestTimeout.Token()));
        var create = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("sales", TestTimeout.Token()));
        var afterCreate = Directory.EnumerateFiles(directory).Select(Path.GetFileName).ToArray();
        await engine.DropDatabaseAsync("Sales", TestTimeout.Token());
        var afterDrop = Directory.EnumerateDirectories(_rootPath).ToArray();
        var created = await engine.CreateDatabaseAsync("Sales", TestTimeout.Token());

        // Assert: not found, refused with the remedy and without touching the stale catalog set,
        // then cleared by the drop.
        open.Message.ShouldBe("Database 'SALES' does not exist.");
        create.ShouldNotBeOfType<DatabaseNotFoundException>();
        create.Message.ShouldContain("without a database in it", Case.Sensitive);
        afterCreate.ShouldBe(["catalog.dat"]);
        afterDrop.ShouldBeEmpty();
        created.Name.ToString().ShouldBe("Sales");
        AssertDirectories(onDisk: true, "sales");
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: a lookup of a name that breaks the rule finds nothing instead of throwing (#1392)")]
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

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Partial drop: every subset of removed files refuses unchanged storage (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_PartialDrop_ShouldNeverReopen(bool populated)
    {
        // Arrange: all 63 nonempty subsets, including the legacy sidecar-first deletion states.
        string[] names = ["keyvalue.dat", "keyvalue.log", "keyvalue.bak", "catalog.dat", "catalog.log", "catalog.bak"];
        int refused = 0;
        for (int removed = 1; removed < 64; removed++)
        {
            string name = "drop" + removed;
            await using (var first = CreateEngine(onDisk: true))
            {
                var database = await first.CreateDatabaseAsync(name, TestTimeout.Token());
                if (populated) { await WriteAsync(database, "acknowledged"); }
            }
            string directory = Path.Combine(_rootPath, name);
            for (int index = 0; index < names.Length; index++)
            {
                if ((removed & (1 << index)) != 0) { File.Delete(Path.Combine(directory, names[index])); }
            }
            var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
            await using var engine = CreateEngine(onDisk: true);

            // Act
            await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync(name, TestTimeout.Token()).AsTask());

            // Assert
            Directory.GetFiles(directory).Select(Path.GetFileName).Order().ShouldBe(before.Keys.Order());
            foreach (var file in before) { File.ReadAllBytes(Path.Combine(directory, file.Key!)).ShouldBe(file.Value); }
            refused++;
        }
        refused.ShouldBe(63);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Names: refused create open and drop preserve a sibling directory and every database file (#1219)")]
    [InlineData("../victim")]
    [InlineData("..\\victim")]
    [InlineData("rooted")]
    [InlineData("..")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public async Task DatabaseOperations_OutsideRootName_ShouldRefuseWithoutTouchingDisk(string input)
    {
        // Arrange: the canary is a sibling of the engine's data root, all inside this test's directory.
        string root = Path.Combine(_rootPath, "data");
        string victim = Path.Combine(_rootPath, "victim");
        Directory.CreateDirectory(victim);
        string canary = Path.Combine(victim, "keep.txt");
        File.WriteAllText(canary, "keep");
        string name = input == "rooted" ? victim : input;
        await using var engine = KeyValueDatabaseEngine.Create("containment", new KeyValueDatabaseEngineOptions { RootPath = root });
        var valid = await engine.CreateDatabaseAsync("valid", TestTimeout.Token());
        await valid.DisposeAsync();
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        // Act
        await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(name, TestTimeout.Token()));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.OpenDatabaseAsync(name, TestTimeout.Token()));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.DropDatabaseAsync(name, TestTimeout.Token()));

        // Assert: invalid names reach no filesystem operation, including recursive deletion.
        File.ReadAllText(canary).ShouldBe("keep");
        Directory.GetDirectories(root).Select(Path.GetFileName).ShouldBe(["valid"]);
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ShouldBe(before.Keys.Order());
        foreach (var file in before) { File.ReadAllBytes(file.Key).ShouldBe(file.Value); }
        (await engine.OpenDatabaseAsync("valid", TestTimeout.Token())).Name.ToString().ShouldBe("valid");
    }

    private KeyValueDatabaseEngine CreateEngine(bool onDisk)
        => KeyValueDatabaseEngine.Create("names", onDisk ? new KeyValueDatabaseEngineOptions { RootPath = _rootPath } : new KeyValueDatabaseEngineOptions());

    private KeyValueDatabaseEngineBuilder Declare(string database)
    {
        var builder = KeyValueDatabaseEngine.CreateBuilder("names");
        builder.Options.RootPath = _rootPath;
        return builder.AddDatabase(database);
    }

    // Closes a database so the next open reads its files: on disk, the engine restarts over the
    // same root; in memory, the holder disposes the database and the engine keeps its files.
    private async Task<KeyValueDatabaseEngine> CloseAsync(KeyValueDatabaseEngine engine, KeyValueDatabase database, bool onDisk)
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

    private static async Task WriteAsync(KeyValueDatabase database, string value)
    {
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("key"), Bytes(value), cancellationToken: TestTimeout.Token());
    }

    private static async Task<string?> ReadAsync(KeyValueDatabase database)
    {
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var entry = await database.GetAsync(session, Bytes("key"), TestTimeout.Token());
        return entry is { } found ? Text(found.Value) : null;
    }
}
