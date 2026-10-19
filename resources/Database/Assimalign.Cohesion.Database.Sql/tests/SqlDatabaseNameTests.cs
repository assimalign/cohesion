using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL engine maps every spelling of a database name to one database (#1392). Names compare
/// ignoring case; on disk a database is the directory named for the lower case of its name, holding
/// its data set (<c>sql.*</c>) and its catalog set (<c>catalog.*</c>), so a case-variant spelling
/// finds the same files on a case-sensitive file system. Before #1392 the engine built the
/// directory from the spelling the caller used, and on Linux an open of <c>SALES</c> after a create
/// of <c>sales</c> found nothing, so the open-or-create of a declared database created a second,
/// empty one. Each test asserts the exact directory names it leaves on disk, which is what a
/// case-sensitive file system sees, so the suite proves the mapping on Windows and macOS too.
/// </summary>
public sealed class SqlDatabaseNameTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-names", Guid.NewGuid().ToString("N"));

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

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Names: a database created as 'sales' and opened again as 'SALES' is the same database, with its data and its created name (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldOpenItWithItsData(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(created, 11);
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        var opened = await engine.OpenDatabaseAsync("SALES", TestTimeout.Token());
        var again = await engine.OpenDatabaseAsync("Sales", TestTimeout.Token());

        // Assert: one database, with its row, reporting the spelling it was created with.
        (await ReadAsync(opened)).ShouldBe([11]);
        again.ShouldBeSameAs(opened);
        opened.Name.ToString().ShouldBe("sales");
        AssertDirectories(onDisk, "sales");
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Names: a declaration of 'SALES' opens the database an earlier build created as 'sales' (#1392)")]
    public async Task BuildAsync_DeclarationInAnotherCase_ShouldOpenTheExistingDatabase()
    {
        // Arrange: the key-value model failed exactly this on Ubuntu before #1392, and SQL built
        // its directory from the declared spelling the same way.
        await using (var first = await Declare("sales").BuildAsync(TestTimeout.Token()))
        {
            first.TryGetDatabase("sales", out SqlDatabase? created).ShouldBeTrue();
            await WriteAsync(created!, 11);
        }

        // Act
        await using var second = await Declare("SALES").BuildAsync(TestTimeout.Token());

        // Assert: opened, not created again beside it.
        second.TryGetDatabase("SALES", out SqlDatabase? opened).ShouldBeTrue();
        (await ReadAsync(opened!)).ShouldBe([11]);
        opened!.Name.ToString().ShouldBe("sales");
        AssertDirectories(onDisk: true, "sales");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Names: a create of 'SALES' after 'sales' is refused as existing, open or not (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabaseAsync_CaseVariantOfAnExistingDatabase_ShouldBeRefused(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(created, 11);

        // Act: refused while it is open, then by its files once it is closed.
        var whileOpen = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("SALES", TestTimeout.Token()));
        engine = await CloseAsync(engine, created, onDisk);
        var whileClosed = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("Sales", TestTimeout.Token()));

        // Assert: the refusals left the database as it was.
        whileOpen.ShouldNotBeOfType<DatabaseNotFoundException>();
        whileClosed.ShouldNotBeOfType<DatabaseNotFoundException>();
        AssertDirectories(onDisk, "sales");
        (await ReadAsync(await engine.OpenDatabaseAsync("sales", TestTimeout.Token()))).ShouldBe([11]);
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Names: a drop of 'SALES' drops the database created as 'sales' (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DropDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldDropIt(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(created, 11);
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        await engine.DropDatabaseAsync("SALES", TestTimeout.Token());

        // Assert: both file sets are gone, so the name opens nothing and creates afresh.
        AssertDirectories(onDisk);
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("sales", TestTimeout.Token()));
        var recreated = await engine.CreateDatabaseAsync("Sales", TestTimeout.Token());
        recreated.Name.ToString().ShouldBe("Sales");
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Names: opens under two spellings list one database, by the name it was created with (#1392)")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Names: a database's directory is the lower case of its name and holds both file sets, as a case-sensitive file system sees them (#1392)")]
    public async Task CreateDatabaseAsync_UpperCaseName_ShouldCreateTheLowerCaseDirectory()
    {
        // Arrange
        await using var engine = CreateEngine(onDisk: true);

        // Act
        await engine.CreateDatabaseAsync("SALES", TestTimeout.Token());

        // Assert: the exact entries on disk, compared ordinally.
        AssertDirectories(onDisk: true, "sales");
        Directory.EnumerateFiles(Path.Combine(_rootPath, "sales")).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe(["catalog.bak", "catalog.dat", "catalog.log", "sql.bak", "sql.dat", "sql.log"]);
        Directory.EnumerateFiles(_rootPath).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Names: a database named 'sales.catalog' beside 'sales' keeps its own files, apart from the catalog of 'sales' (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabaseAsync_NamedLikeAnotherDatabasesCatalog_ShouldKeepTheirFilesApart(bool onDisk)
    {
        // Arrange: until #1392 the catalog of 'sales' was the storage named 'sales.catalog'.
        var engine = CreateEngine(onDisk);
        var sales = await engine.CreateDatabaseAsync("sales", TestTimeout.Token());
        await WriteAsync(sales, 11);

        // Act
        var lookalike = await engine.CreateDatabaseAsync("sales.catalog", TestTimeout.Token());
        await WriteAsync(lookalike, 22);
        await sales.DisposeAsync();
        engine = await CloseAsync(engine, lookalike, onDisk);

        // Assert: both reopen with their own rows, and a drop of one leaves the other.
        AssertDirectories(onDisk, "sales", "sales.catalog");
        (await ReadAsync(await engine.OpenDatabaseAsync("sales", TestTimeout.Token()))).ShouldBe([11]);
        (await ReadAsync(await engine.OpenDatabaseAsync("sales.catalog", TestTimeout.Token()))).ShouldBe([22]);
        await engine.DropDatabaseAsync("sales", TestTimeout.Token());
        AssertDirectories(onDisk, "sales.catalog");
        (await ReadAsync(await engine.OpenDatabaseAsync("SALES.CATALOG", TestTimeout.Token()))).ShouldBe([22]);
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Names: a directory without a data file is no database: an open does not find it, a create refuses it, a drop clears it (#1392)")]
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

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Names: a lookup of a name that breaks the rule finds nothing instead of throwing (#1392)")]
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

    private SqlDatabaseEngine CreateEngine(bool onDisk)
        => SqlDatabaseEngine.Create("names", onDisk ? new SqlDatabaseEngineOptions { RootPath = _rootPath } : new SqlDatabaseEngineOptions());

    private SqlDatabaseEngineBuilder Declare(string database)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("names");
        builder.Options.RootPath = _rootPath;
        return builder.AddDatabase(database);
    }

    // Closes a database so the next open reads its files: on disk, the engine restarts over the
    // same root; in memory, the holder disposes the database and the engine keeps its files.
    private async Task<SqlDatabaseEngine> CloseAsync(SqlDatabaseEngine engine, SqlDatabase database, bool onDisk)
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

    private static async Task WriteAsync(SqlDatabase database, int id)
    {
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        await session.ExecuteAsync("CREATE TABLE notes (id INT NOT NULL)", cancellationToken: TestTimeout.Token());
        await session.ExecuteAsync($"INSERT INTO notes (id) VALUES ({id})", cancellationToken: TestTimeout.Token());
    }

    private static async Task<int[]> ReadAsync(SqlDatabase database)
    {
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var set = (await session.ExecuteAsync("SELECT id FROM notes", cancellationToken: TestTimeout.Token()))
            .ShouldBeAssignableTo<DatabaseResultSet>().ShouldNotBeNull();
        var ids = new List<int>();
        await using (set)
        {
            await foreach (var row in set.GetRowsAsync())
            {
                ids.Add(Convert.ToInt32(row.GetValue(0)));
            }
        }

        return [.. ids];
    }
}
