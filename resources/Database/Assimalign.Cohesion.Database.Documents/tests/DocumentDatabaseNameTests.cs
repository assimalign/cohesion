using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// The document engine maps every spelling of a database name to one database (#1392). Names compare
/// ignoring case; on disk a database is the directory named for the lower case of its name, holding
/// <c>document.*</c>, so a case-variant spelling finds the same files on a case-sensitive file system
/// without enumerating the root, and the engine lists each database by the name it was created with,
/// which its storage header keeps. Each test asserts the exact directory names it leaves on disk,
/// which is what a case-sensitive file system sees.
/// </summary>
public sealed class DocumentDatabaseNameTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-documents-names", Guid.NewGuid().ToString("N"));

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

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Names: a database created as 'sales' and opened again as 'SALES' is the same database, with its data and its created name (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldOpenItWithItsData(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", Token());
        var document = await WriteAsync(created, "sales-document");
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        var opened = await engine.OpenDatabaseAsync("SALES", Token());
        var again = await engine.OpenDatabaseAsync("Sales", Token());

        // Assert: one database, with its document, reporting the spelling it was created with.
        (await ReadAsync(opened, document)).ShouldBe("sales-document");
        again.ShouldBeSameAs(opened);
        opened.Name.ToString().ShouldBe("sales", Case.Sensitive);
        AssertDirectories(onDisk, "sales");
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Names: a create of 'SALES' after 'sales' is refused as existing, open or not (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabaseAsync_CaseVariantOfAnExistingDatabase_ShouldBeRefused(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", Token());
        var document = await WriteAsync(created, "sales-document");

        // Act: refused while it is open, then by its files once it is closed.
        var whileOpen = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("SALES", Token()));
        engine = await CloseAsync(engine, created, onDisk);
        var whileClosed = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("Sales", Token()));

        // Assert: the refusals left the database as it was.
        whileOpen.ShouldNotBeOfType<DatabaseNotFoundException>();
        whileClosed.ShouldNotBeOfType<DatabaseNotFoundException>();
        AssertDirectories(onDisk, "sales");
        (await ReadAsync(await engine.OpenDatabaseAsync("sales", Token()), document)).ShouldBe("sales-document");
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Names: a drop of 'SALES' drops the database created as 'sales' (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DropDatabaseAsync_CaseVariantOfACreatedDatabase_ShouldDropIt(bool onDisk)
    {
        // Arrange
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("sales", Token());
        await WriteAsync(created, "sales-document");
        engine = await CloseAsync(engine, created, onDisk);

        // Act
        await engine.DropDatabaseAsync("SALES", Token());

        // Assert: its files are gone, so the name opens nothing, lists nothing and creates afresh.
        AssertDirectories(onDisk);
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("sales", Token()));
        (await ListAsync(engine)).ShouldBeEmpty();
        var recreated = await engine.CreateDatabaseAsync("Sales", Token());
        recreated.Name.ToString().ShouldBe("Sales", Case.Sensitive);
        await engine.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Names: the engine lists a database once, by the name it was created with, however it was opened (#1392)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetDatabasesAsync_AfterCaseVariantOpens_ShouldReportOneDatabase(bool onDisk)
    {
        // Arrange: created with mixed case and closed.
        var engine = CreateEngine(onDisk);
        var created = await engine.CreateDatabaseAsync("Sales", Token());
        engine = await CloseAsync(engine, created, onDisk);

        // Act: listed from its files alone, then again after opens under two other spellings.
        var fromFiles = await ListAsync(engine);
        await engine.OpenDatabaseAsync("SALES", Token());
        await engine.OpenDatabaseAsync("sales", Token());
        var afterOpens = await ListAsync(engine);

        // Assert: the lower-case directory lists as the spelling the database was created with.
        fromFiles.ShouldBe(["Sales"]);
        afterOpens.ShouldBe(["Sales"]);
        AssertDirectories(onDisk, "sales");
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Names: a database's directory is the lower case of its name, and a directory no name maps to is not listed (#1392)")]
    public async Task CreateDatabaseAsync_UpperCaseName_ShouldCreateTheLowerCaseDirectory()
    {
        // Arrange: a directory made outside the engine that no database name maps to (an upper-case
        // twin a case-sensitive file system could hold), with a data file in it.
        await using var engine = CreateEngine(onDisk: true);
        string stray = Path.Combine(_rootPath, "Stray");
        Directory.CreateDirectory(stray);
        await File.WriteAllBytesAsync(Path.Combine(stray, "document.dat"), [], Token());

        // Act
        await engine.CreateDatabaseAsync("SALES", Token());
        var listed = await ListAsync(engine);

        // Assert: the exact entries on disk, compared ordinally, and only the database is listed.
        AssertDirectories(onDisk: true, "Stray", "sales");
        Directory.EnumerateFiles(Path.Combine(_rootPath, "sales")).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe(["document.bak", "document.dat", "document.log"]);
        listed.ShouldBe(["SALES"]);
    }

    private DocumentDatabaseEngine CreateEngine(bool onDisk)
        => DocumentDatabaseEngine.Create("names", onDisk ? new DocumentDatabaseEngineOptions { RootPath = _rootPath } : new DocumentDatabaseEngineOptions());

    // Closes a database so the next open reads its files: on disk, the engine restarts over the
    // same root; in memory, the holder disposes the database and the engine keeps its files.
    private async Task<DocumentDatabaseEngine> CloseAsync(DocumentDatabaseEngine engine, DocumentDatabase database, bool onDisk)
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

    private static async Task<List<string>> ListAsync(DocumentDatabaseEngine engine)
    {
        var names = new List<string>();
        await foreach (var database in engine.GetDatabasesAsync(Token()))
        {
            names.Add(database.Name.ToString());
        }

        return names;
    }

    // Writes one document whose content is the marker, into a collection of its own, and returns its id.
    private static async Task<string> WriteAsync(DocumentDatabase database, string marker)
    {
        await using var session = await database.CreateSessionAsync(Token());
        var collection = await session.CreateCollectionAsync("notes", Token());
        await collection.PutAsync(session, "note", Encoding.UTF8.GetBytes("{\"marker\":\"" + marker + "\"}"), cancellationToken: Token());
        return "note";
    }

    // The marker the document holds.
    private static async Task<string> ReadAsync(DocumentDatabase database, string id)
    {
        await using var session = await database.CreateSessionAsync(Token());
        var collection = await session.GetCollectionAsync("notes", Token());
        var document = await collection.GetAsync(session, id, Token());
        string json = Encoding.UTF8.GetString(document.ShouldNotBeNull().Content.Span);
        return json["{\"marker\":\"".Length..^"\"}".Length];
    }

    // A bounded token, so a hung call fails the test instead of the run.
    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
}
