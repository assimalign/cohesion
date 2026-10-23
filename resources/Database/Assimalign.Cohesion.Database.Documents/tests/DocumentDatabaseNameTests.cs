using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

using Assimalign.Cohesion.Database.Documents.Protocol;

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
        opened.Name.ToString().ShouldBe("sales");
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
        recreated.Name.ToString().ShouldBe("Sales");
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

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Names: a directory without a data file is no database: an open does not find it, a create refuses it, a drop clears it (#1392)")]
    public async Task OpenDatabaseAsync_DirectoryWithoutADataFile_ShouldNotFindIt()
    {
        // Arrange: the directory 'sales' maps to, empty, as an interrupted create or drop leaves it.
        await using var engine = CreateEngine(onDisk: true);
        Directory.CreateDirectory(Path.Combine(_rootPath, "sales"));

        // Act
        var open = await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("SALES", Token()));
        var listed = await ListAsync(engine);
        var create = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("sales", Token()));
        await engine.DropDatabaseAsync("Sales", Token());
        var afterDrop = Directory.EnumerateDirectories(_rootPath).ToArray();
        var created = await engine.CreateDatabaseAsync("Sales", Token());

        // Assert: not found rather than a missing file, refused with the remedy, then cleared.
        open.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
        open.Message.ShouldContain("DropDatabaseAsync", Case.Sensitive);
        listed.ShouldBeEmpty();
        create.ShouldNotBeOfType<DatabaseNotFoundException>();
        create.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
        create.Message.ShouldContain("without a database in it", Case.Sensitive);
        afterDrop.ShouldBeEmpty();
        created.Name.ToString().ShouldBe("Sales");
        AssertDirectories(onDisk: true, "sales");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Names: a lookup of a name that breaks the rule finds nothing instead of throwing (#1392)")]
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

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Missing sidecars: open and declared provisioning refuse unchanged files (#1158)")]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task OpenDatabaseAsync_MissingSidecars_ShouldRefuseWithoutWriting(int missing, bool declared)
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true))
        {
            await first.CreateDatabaseAsync("Sales", CancellationToken.None);
        }
        string directory = Path.Combine(_rootPath, "sales");
        if ((missing & 1) != 0) { File.Delete(Path.Combine(directory, "document.log")); }
        if ((missing & 2) != 0) { File.Delete(Path.Combine(directory, "document.bak")); }
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, ReadFile);
        var builder = DocumentDatabaseEngine.CreateBuilder("recovery");
        builder.Options.RootPath = _rootPath;
        if (declared) { builder.AddDatabase("SALES"); }

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            await using var recovered = await builder.BuildAsync(CancellationToken.None);
            await recovered.OpenDatabaseAsync("SALES", CancellationToken.None);
        });

        // Assert
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
        Directory.GetFiles(directory).Select(Path.GetFileName).Order().ShouldBe(before.Keys.Order());
        foreach (var file in before) { File.ReadAllBytes(Path.Combine(directory, file.Key!)).ShouldBe(file.Value); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Interrupted create: explicit create reports the recovery action without claiming existing files (#1421)")]
    public async Task CreateDatabaseAsync_MissingJournal_ShouldReportCodedRecoveryAction()
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true))
        {
            await first.CreateDatabaseAsync("Sales", CancellationToken.None);
        }
        string directory = Path.Combine(_rootPath, "sales");
        string data = Path.Combine(directory, "document.dat");
        byte[] before = await File.ReadAllBytesAsync(data, CancellationToken.None);
        File.Delete(Path.Combine(directory, "document.log"));
        await using var engine = CreateEngine(onDisk: true);

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => engine.CreateDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
        refusal.Message.ShouldContain("OpenDatabaseAsync", Case.Sensitive);
        (await File.ReadAllBytesAsync(data, CancellationToken.None)).ShouldBe(before);
        File.Exists(Path.Combine(directory, "document.log")).ShouldBeFalse();
        (await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask()))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Interrupted create: truncated data and leftover directories have coded, actionable refusals (#1421)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_IncompleteFiles_ShouldReportCodedRecoveryAction(bool truncatedData)
    {
        // Arrange
        string directory = Path.Combine(_rootPath, "sales");
        Directory.CreateDirectory(directory);
        if (truncatedData) { await File.WriteAllBytesAsync(Path.Combine(directory, "document.dat"), [], CancellationToken.None); }
        var builder = DocumentDatabaseEngine.CreateBuilder("recovery");
        builder.Options.RootPath = _rootPath;
        builder.AddDatabase("sales");

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => builder.BuildAsync(CancellationToken.None).AsTask());

        // Assert
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
        refusal.Message.ShouldContain("DropDatabaseAsync", Case.Sensitive);
        File.Exists(Path.Combine(directory, "document.log")).ShouldBeFalse();
        await using var engine = CreateEngine(onDisk: true);
        if (truncatedData)
        {
            var open = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            open.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
        }
        else
        {
            var open = await Should.ThrowAsync<DatabaseNotFoundException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            open.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Partial drop: no subset of removed files can reopen (#1158)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task OpenDatabaseAsync_PartialDrop_ShouldNeverReopen(int removed)
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true)) { await first.CreateDatabaseAsync("sales", CancellationToken.None); }
        string directory = Path.Combine(_rootPath, "sales");
        string[] extensions = ["dat", "log", "bak"];
        for (int index = 0; index < extensions.Length; index++)
        {
            if ((removed & (1 << index)) != 0) { File.Delete(Path.Combine(directory, "document." + extensions[index])); }
        }
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, ReadFile);
        await using var engine = CreateEngine(onDisk: true);

        // Act
        await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        Directory.GetFiles(directory).Select(Path.GetFileName).Order().ShouldBe(before.Keys.Order());
        foreach (var file in before) { File.ReadAllBytes(Path.Combine(directory, file.Key!)).ShouldBe(file.Value); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Crash after a checkpoint: a lost journal is refused, a torn page recovers acknowledged data (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_CommittedCrashImage_ShouldPreserveAcknowledgedData(bool loseJournal)
    {
        // Arrange: retain files before orderly close can checkpoint the acknowledged write.
        await using var first = CreateEngine(onDisk: true);
        var database = await first.CreateDatabaseAsync("sales", CancellationToken.None);
        database.DataStorage.Checkpoint();
        var identity = await WriteAsync(database, "acknowledged");
        string directory = Path.Combine(_rootPath, "sales");
        var crash = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, ReadFile);
        await first.DisposeAsync();
        foreach (var file in crash) { File.WriteAllBytes(Path.Combine(directory, file.Key!), file.Value); }
        if (loseJournal) { File.Delete(Path.Combine(directory, "document.log")); }
        else
        {
            using var data = new FileStream(Path.Combine(directory, "document.dat"), FileMode.Open, FileAccess.Write);
            data.SetLength(data.Length - 1);
        }
        await using var reopened = CreateEngine(onDisk: true);

        // Act / Assert
        if (loseJournal)
        {
            var before = File.ReadAllBytes(Path.Combine(directory, "document.dat"));
            var refusal = await Should.ThrowAsync<DatabaseException>(() => reopened.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06103");
            File.ReadAllBytes(Path.Combine(directory, "document.dat")).ShouldBe(before);
            File.Exists(Path.Combine(directory, "document.log")).ShouldBeFalse();
        }
        else
        {
            (await ReadAsync(await reopened.OpenDatabaseAsync("sales", CancellationToken.None), identity)).ShouldBe("acknowledged");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Unrecoverable data: the coded refusal preserves files and never advises dropping data (#1158)")]
    public async Task OpenDatabaseAsync_ChecksumDamage_ShouldRefuseWithoutDropAdvice()
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true)) { await first.CreateDatabaseAsync("sales", CancellationToken.None); }
        string directory = Path.Combine(_rootPath, "sales");
        string path = Path.Combine(directory, "document.dat");
        byte[] damaged = File.ReadAllBytes(path);
        damaged[Page.Size + Page.HeaderSize] ^= 1;
        File.WriteAllBytes(path, damaged);
        var before = File.ReadAllBytes(path);
        await using var engine = CreateEngine(onDisk: true);

        // Act
        var refusal = await Should.ThrowAsync<DatabaseCorruptionException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        refusal.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldBe(["CK06003", "CD06103"]);
        refusal.Database.ShouldBe((DatabaseName)"sales");
        refusal.PageId.ShouldBe((PageId)1L);
        refusal.InnerException.ShouldBeOfType<StorageCorruptionException>().PageId.ShouldBe((PageId)1L);
        refusal.Message.ShouldContain("page 1", Case.Sensitive);
        refusal.Message.ShouldNotContain("DropDatabaseAsync", Case.Sensitive);
        File.ReadAllBytes(path).ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Recovery: an uncommitted extension torn at the tail preserves committed data (#1158)")]
    public async Task OpenDatabaseAsync_UncommittedExtensionTornAtTail_ShouldPreserveCommittedData()
    {
        // Arrange: steal an extension page before its physical transaction commits.
        await using var first = CreateEngine(onDisk: true);
        var database = await first.CreateDatabaseAsync("sales", CancellationToken.None);
        var identity = await WriteAsync(database, "acknowledged");
        database.DataStorage.Checkpoint();
        string directory = Path.Combine(_rootPath, "sales");
        Dictionary<string, byte[]> crash;
        long extension;
        using (var transaction = database.DataStorage.BeginTransaction())
        {
            using (var page = database.DataStorage.AllocatePageForWrite(transaction, PageType.Index))
            {
                extension = (long)page.Page.Id;
            }
            database.DataStorage.PageManager.FlushAll();
            crash = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, ReadFile);
        }
        await first.DisposeAsync();
        foreach (var file in crash) { File.WriteAllBytes(Path.Combine(directory, file.Key), file.Value); }
        using (var data = new FileStream(Path.Combine(directory, "document.dat"), FileMode.Open, FileAccess.Write))
        {
            data.Length.ShouldBe((extension + 1) * Page.Size);
            data.SetLength(data.Length - 1);
        }
        await using var engine = CreateEngine(onDisk: true);

        // Act
        var recovered = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

        // Assert
        (await ReadAsync(recovered, identity)).ShouldBe("acknowledged");
        await using var session = await recovered.CreateSessionAsync(CancellationToken.None);
        var collection = await session.GetCollectionAsync("notes", CancellationToken.None);
        await collection.PutAsync(session, "next", Encoding.UTF8.GetBytes("{\"marker\":\"after recovery\"}"), cancellationToken: CancellationToken.None);
        (await ReadAsync(recovered, "next")).ShouldBe("after recovery");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Open: a full pinned buffer pool asks for retry and preserves committed data (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_BufferPoolFull_ShouldAllowRetryWithoutDataLossAdvice(bool wrapped)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = DocumentDatabaseEngine.Create("transient", new DocumentDatabaseEngineOptions { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("sales", CancellationToken.None);
        var identity = await WriteAsync(database, "acknowledged");
        await database.DisposeAsync();
        var full = new StorageIOException("Buffer pool is full and all pages are pinned.");
        strategy.OpenFailure = wrapped ? new DatabaseException("Open failed.", full) : full;

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
        strategy.OpenFailure = null;
        var reopened = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

        // Assert
        refusal.Message.ShouldContain("Retry OpenDatabaseAsync", Case.Sensitive);
        refusal.Message.ShouldNotContain("restore");
        refusal.Message.ShouldNotContain("drop");
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06104");
        (await ReadAsync(reopened, identity)).ShouldBe("acknowledged");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - One-page creation: the suggested drop and create completes (#1158)")]
    public async Task CreateDatabaseAsync_OnePageFile_ShouldReportIncompleteAndAllowRecreation()
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true)) { await first.CreateDatabaseAsync("sales", CancellationToken.None); }
        string path = Path.Combine(_rootPath, "sales", "document.dat");
        using (var data = new FileStream(path, FileMode.Open, FileAccess.Write)) { data.SetLength(Page.Size); }
        var before = File.ReadAllBytes(path);
        await using var engine = CreateEngine(onDisk: true);

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
        var createRefusal = await Should.ThrowAsync<DatabaseException>(() => engine.CreateDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert: both operations agree, and the recommended action completes.
        refusal.Message.ShouldBe(createRefusal.Message);
        refusal.Message.ShouldContain("Creation never completed", Case.Sensitive);
        refusal.Message.ShouldContain("DropDatabaseAsync", Case.Sensitive);
        refusal.Message.ShouldNotContain("restore");
        File.ReadAllBytes(path).ShouldBe(before);
        await engine.DropDatabaseAsync("sales", CancellationToken.None);
        await engine.CreateDatabaseAsync("sales", CancellationToken.None);
    }

    private static byte[] ReadFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Names: refused create open and drop preserve a sibling directory and every database file (#1219)")]
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
        await using var engine = DocumentDatabaseEngine.Create("containment", new DocumentDatabaseEngineOptions { RootPath = root });
        var valid = await engine.CreateDatabaseAsync("valid", Token());
        await valid.DisposeAsync();
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        // Act
        await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(name, Token()));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.OpenDatabaseAsync(name, Token()));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.DropDatabaseAsync(name, Token()));

        // Assert: invalid names reach no filesystem operation, including recursive deletion.
        File.ReadAllText(canary).ShouldBe("keep");
        Directory.GetDirectories(root).Select(Path.GetFileName).ShouldBe(["valid"]);
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ShouldBe(before.Keys.Order());
        foreach (var file in before) { File.ReadAllBytes(file.Key).ShouldBe(file.Value); }
        (await engine.OpenDatabaseAsync("valid", Token())).Name.ToString().ShouldBe("valid");
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
