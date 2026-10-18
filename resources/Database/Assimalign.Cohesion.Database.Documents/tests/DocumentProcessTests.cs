using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentProcessTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Recovery: committed mutations and raw index entries survive while abandoned writes disappear")]
    public async Task Run_ProcessTermination_ShouldRecoverContentAndRawIndexAsync()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-document-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Act
            await RunAsync("seed", root);
            await RunAsync("verify", root);
            // Assert
            await VerifyRawIndexAsync(root);
            // The recovery checkpoint must itself leave a clean reusable file set.
            await RunAsync("verify", root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyRawIndexAsync(string root)
    {
        // SearchIndexAsync filters catalog tombstones; only a raw cursor can expose leftover old keys.
        await using var engine = DocumentDatabaseEngine.Create("document-recovery-index", new() { RootPath = root });
        var database = await engine.OpenDatabaseAsync("crash", CancellationToken.None);
        var latest = new TransactionSnapshot(TransactionSequence.None, TransactionSequence.None, new TransactionSequence(ulong.MaxValue), []);
        var metadata = database.Catalog.GetIndexes(database.Catalog.FindCollection("items", latest).ShouldNotBeNull().Id, latest).ShouldHaveSingleItem();
        using var registrations = database.DataStorage.GetUnitIterator(1);
        registrations.MoveNext().ShouldBeTrue();
        var bytes = registrations.Current.Data;
        bytes.Span[16].ShouldBe((byte)5);
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span[18..]).ShouldBe(metadata.ObjectId);
        long rootPage = BinaryPrimitives.ReadInt64LittleEndian(bytes.Span[26..]);
        registrations.MoveNext().ShouldBeFalse();
        var manager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = database.DataStorage,
            TransactionSource = _ => throw new InvalidOperationException("The recovery assertion only reads the tree."),
            ExistingIndexes = [new BTreeIndexRegistration(metadata.ObjectId, new IndexDefinition(metadata.Name), rootPage)]
        });
        manager.TryGetIndex(metadata.ObjectId, metadata.Name, out var tree).ShouldBeTrue();
        foreach (var (rank, count) in new[] { (1m, 0), (11m, 1), (99m, 0), (2m, 0), (3m, 1) })
        {
            var key = IndexKey.From(new DatabaseKeyWriter().AppendDecimal(rank));
            await using var cursor = tree.OpenCursor(latest, new IndexKeyRange(key, key, true, true));
            int actual = 0;
            while (await cursor.MoveNextAsync(CancellationToken.None)) { actual++; }
            actual.ShouldBe(count, $"Recovered raw entries for rank {rank}");
        }
    }

    private static async Task RunAsync(string mode, string root)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "DocumentRecoveryFixture", "Assimalign.Cohesion.Database.Documents.RecoveryFixture.dll");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(fixture);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(root);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start document recovery fixture.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        process.ExitCode.ShouldBe(0, await output + await error);
    }
}
