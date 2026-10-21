using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Protocol;

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
        var definitions = database.Catalog.GetIndexes(database.Catalog.FindCollection("items", latest).ShouldNotBeNull().Id, latest);
        definitions.Count.ShouldBe(2);
        definitions.Single(index => index.Name == "unique_rank_marker").IsUnique.ShouldBeTrue();
        var physical = new List<BTreeIndexRegistration>();
        using (var registrations = database.DataStorage.GetUnitIterator(1))
        {
            while (registrations.MoveNext())
            {
                var bytes = registrations.Current.Data;
                bytes.Span[16].ShouldBe((byte)5); bytes.Span[17].ShouldBe((byte)2);
                ulong objectId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span[18..]);
                var metadata = definitions.Single(index => index.ObjectId == objectId);
                long rootPage = BinaryPrimitives.ReadInt64LittleEndian(bytes.Span[26..]);
                physical.Add(new BTreeIndexRegistration(objectId, new IndexDefinition(metadata.Name), rootPage));
                bytes.Span[^1].ShouldBe(metadata.IsUnique ? (byte)1 : (byte)0);
            }
        }
        physical.Count.ShouldBe(2);
        var manager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = database.DataStorage,
            TransactionSource = _ => throw new InvalidOperationException("The recovery assertion only reads the tree."),
            ExistingIndexes = physical
        });
        foreach (var metadata in definitions)
        {
            manager.TryGetIndex(metadata.ObjectId, metadata.Name, out var tree).ShouldBeTrue();
            foreach (var (rank, count) in new[] { (1m, 0), (11m, 1), (99m, 0), (2m, 0), (3m, 1), (4m, 0), (14m, 1), (98m, 0) })
            {
                // Documents' v2 component prefix 4 precedes a DatabaseKeyWriter decimal.
                var bytes = new byte[] { 4 }.Concat(IndexKey.From(new DatabaseKeyWriter().AppendDecimal(rank)).Encoded.ToArray()).ToList();
                if (metadata.IsUnique)
                {
                    if (rank is 1m or 11m or 99m)
                    { bytes.Add(4); bytes.AddRange(IndexKey.From(new DatabaseKeyWriter().AppendDecimal(7m)).Encoded.ToArray()); }
                    else { bytes.Add(0); } // trailing MISSING
                }
                var key = new IndexKey(bytes.ToArray());
                await using var cursor = tree.OpenCursor(latest, new IndexKeyRange(key, key, true, true));
                int actual = 0;
                while (await cursor.MoveNextAsync(CancellationToken.None)) { actual++; }
                actual.ShouldBe(count, $"Recovered raw entries in {metadata.Name} for rank {rank}");
            }
            await using var all = tree.OpenCursor(latest, new IndexKeyRange(null, null, true, true));
            int entries = 0;
            while (await all.MoveNextAsync(CancellationToken.None)) { entries++; }
            entries.ShouldBe(3); // no orphaned entries concealed by catalog filtering
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
