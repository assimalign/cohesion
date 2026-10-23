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
        open.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
        open.Message.ShouldContain("DropDatabaseAsync", Case.Sensitive);
        listed.ShouldBeEmpty();
        create.ShouldNotBeOfType<DatabaseNotFoundException>();
        create.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
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

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Missing sidecars: open and declared provisioning refuse unchanged files (#1158)")]
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
        if ((missing & 1) != 0) { File.Delete(Path.Combine(directory, "blob.log")); }
        if ((missing & 2) != 0) { File.Delete(Path.Combine(directory, "blob.bak")); }
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, ReadFile);
        var builder = BlobDatabaseEngine.CreateBuilder("recovery");
        builder.Options.RootPath = _rootPath;
        if (declared) { builder.AddDatabase("SALES"); }

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            await using var recovered = await builder.BuildAsync(CancellationToken.None);
            await recovered.OpenDatabaseAsync("SALES", CancellationToken.None);
        });

        // Assert
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
        Directory.GetFiles(directory).Select(Path.GetFileName).Order().ShouldBe(before.Keys.Order());
        foreach (var file in before) { File.ReadAllBytes(Path.Combine(directory, file.Key!)).ShouldBe(file.Value); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Interrupted create: explicit create reports the recovery action without claiming existing files (#1421)")]
    public async Task CreateDatabaseAsync_MissingJournal_ShouldReportCodedRecoveryAction()
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true))
        {
            await first.CreateDatabaseAsync("Sales", CancellationToken.None);
        }
        string directory = Path.Combine(_rootPath, "sales");
        string data = Path.Combine(directory, "blob.dat");
        byte[] before = await File.ReadAllBytesAsync(data, CancellationToken.None);
        File.Delete(Path.Combine(directory, "blob.log"));
        await using var engine = CreateEngine(onDisk: true);

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => engine.CreateDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
        refusal.Message.ShouldContain("OpenDatabaseAsync", Case.Sensitive);
        (await File.ReadAllBytesAsync(data, CancellationToken.None)).ShouldBe(before);
        File.Exists(Path.Combine(directory, "blob.log")).ShouldBeFalse();
        (await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask()))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Interrupted create: truncated data and leftover directories have coded, actionable refusals (#1421)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_IncompleteFiles_ShouldReportCodedRecoveryAction(bool truncatedData)
    {
        // Arrange
        string directory = Path.Combine(_rootPath, "sales");
        Directory.CreateDirectory(directory);
        if (truncatedData) { await File.WriteAllBytesAsync(Path.Combine(directory, "blob.dat"), [], CancellationToken.None); }
        var builder = BlobDatabaseEngine.CreateBuilder("recovery");
        builder.Options.RootPath = _rootPath;
        builder.AddDatabase("sales");

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => builder.BuildAsync(CancellationToken.None).AsTask());

        // Assert
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
        refusal.Message.ShouldContain("DropDatabaseAsync", Case.Sensitive);
        File.Exists(Path.Combine(directory, "blob.log")).ShouldBeFalse();
        await using var engine = CreateEngine(onDisk: true);
        if (truncatedData)
        {
            var open = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            open.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
        }
        else
        {
            var open = await Should.ThrowAsync<DatabaseNotFoundException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            open.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Partial drop: no subset of removed files can reopen (#1158)")]
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
            if ((removed & (1 << index)) != 0) { File.Delete(Path.Combine(directory, "blob." + extensions[index])); }
        }
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, ReadFile);
        await using var engine = CreateEngine(onDisk: true);

        // Act
        await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        Directory.GetFiles(directory).Select(Path.GetFileName).Order().ShouldBe(before.Keys.Order());
        foreach (var file in before) { File.ReadAllBytes(Path.Combine(directory, file.Key!)).ShouldBe(file.Value); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Crash after a checkpoint: a lost journal is refused, a torn page recovers acknowledged data (#1158)")]
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
        if (loseJournal) { File.Delete(Path.Combine(directory, "blob.log")); }
        else
        {
            using var data = new FileStream(Path.Combine(directory, "blob.dat"), FileMode.Open, FileAccess.Write);
            data.SetLength(data.Length - 1);
        }
        await using var reopened = CreateEngine(onDisk: true);

        // Act / Assert
        if (loseJournal)
        {
            var before = File.ReadAllBytes(Path.Combine(directory, "blob.dat"));
            var refusal = await Should.ThrowAsync<DatabaseException>(() => reopened.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB004");
            File.ReadAllBytes(Path.Combine(directory, "blob.dat")).ShouldBe(before);
            File.Exists(Path.Combine(directory, "blob.log")).ShouldBeFalse();
        }
        else
        {
            (await ReadAsync(await reopened.OpenDatabaseAsync("sales", CancellationToken.None), identity)).ShouldBe("acknowledged");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Unrecoverable data: the coded refusal preserves files and never advises dropping data (#1158)")]
    public async Task OpenDatabaseAsync_ChecksumDamage_ShouldRefuseWithoutDropAdvice()
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true)) { await first.CreateDatabaseAsync("sales", CancellationToken.None); }
        string directory = Path.Combine(_rootPath, "sales");
        string path = Path.Combine(directory, "blob.dat");
        byte[] damaged = File.ReadAllBytes(path);
        damaged[Page.Size + Page.HeaderSize] ^= 1;
        File.WriteAllBytes(path, damaged);
        var before = File.ReadAllBytes(path);
        await using var engine = CreateEngine(onDisk: true);

        // Act
        var refusal = await Should.ThrowAsync<DatabaseCorruptionException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        refusal.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldBe(["CK06003", "COHDBB004"]);
        refusal.Database.ShouldBe((DatabaseName)"sales");
        refusal.PageId.ShouldBe((PageId)1L);
        refusal.InnerException.ShouldBeOfType<StorageCorruptionException>().PageId.ShouldBe((PageId)1L);
        refusal.Message.ShouldContain("page 1", Case.Sensitive);
        refusal.Message.ShouldNotContain("DropDatabaseAsync", Case.Sensitive);
        File.ReadAllBytes(path).ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Recovery: an uncommitted extension torn at the tail preserves committed data (#1158)")]
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
        using (var data = new FileStream(Path.Combine(directory, "blob.dat"), FileMode.Open, FileAccess.Write))
        {
            data.Length.ShouldBe((extension + 1) * Page.Size);
            data.SetLength(data.Length - 1);
        }
        await using var engine = CreateEngine(onDisk: true);

        // Act
        var recovered = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

        // Assert
        (await ReadAsync(recovered, identity)).ShouldBe("acknowledged");
        var container = await AutocommitContainer.GetAsync(recovered, "files", CancellationToken.None);
        await BlobEngineTests.WriteAsync(container, "next", Encoding.UTF8.GetBytes("after recovery"));
        (await ReadAsync(recovered, "next")).ShouldBe("after recovery");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Open: a full pinned buffer pool asks for retry and preserves committed data (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_BufferPoolFull_ShouldAllowRetryWithoutDataLossAdvice(bool wrapped)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = BlobDatabaseEngine.Create("transient", new BlobDatabaseEngineOptions { StorageStrategy = strategy });
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
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB005");
        (await ReadAsync(reopened, identity)).ShouldBe("acknowledged");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - One-page creation: the suggested drop and create completes (#1158)")]
    public async Task CreateDatabaseAsync_OnePageFile_ShouldReportIncompleteAndAllowRecreation()
    {
        // Arrange
        await using (var first = CreateEngine(onDisk: true)) { await first.CreateDatabaseAsync("sales", CancellationToken.None); }
        string path = Path.Combine(_rootPath, "sales", "blob.dat");
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

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Names: refused create open and drop preserve a sibling directory and every database file (#1219)")]
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
        await using var engine = BlobDatabaseEngine.Create("containment", new BlobDatabaseEngineOptions { RootPath = root });
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
