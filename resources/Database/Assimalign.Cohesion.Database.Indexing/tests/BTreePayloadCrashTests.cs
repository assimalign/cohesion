using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public sealed class BTreePayloadCrashTests
{
    private readonly ITestOutputHelper _output;

    public BTreePayloadCrashTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Indexing] - Payload: every whole/torn write of replacement and recursive splits replays exactly (#1809)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public async Task Recovery_EveryPayloadWrite_ShouldConvergeToDurableCommit(int sectors)
    {
        // Arrange: dry run counts writes and checks images at each named durability boundary.
        var dryPoint = new CrashPoint();
        var dry = await RunAsync(dryPoint, 0, verifyBoundaries: true);
        int writes = dryPoint.Writes - dry.CreationWrites;
        writes.ShouldBeGreaterThan(20);

        // Act / Assert: the exact oracle follows the durable commit marker, including
        // a commit that reached media before Commit returned, and a truncated checkpoint log.
        for (int write = 1; write <= writes; write++)
        {
            var point = new CrashPoint { DurableSectors = sectors };
            var outcome = await RunAsync(point, write, verifyBoundaries: false);
            point.HasCrashed.ShouldBeTrue($"write {write}");
            using var log = StorageJournal.Create(new MemoryStream(outcome.Journal));
            bool committed = outcome.Acknowledged || log.ReadAll().Any(record =>
                record.Type == JournalRecordType.CommitTransaction && record.TransactionSequence == outcome.Sequence);
            await VerifyAsync(outcome.Data, outcome.Journal, outcome.Registrations, committed);
        }
        _output.WriteLine($"{writes} crash writes, {sectors} durable torn sectors; 4 durability boundary images.");
    }

    private static async Task<Outcome> RunAsync(CrashPoint point, int crashAtWrite, bool verifyBoundaries)
    {
        var data = new CrashSimulationStream(writeThrough: true, point, "data");
        var journal = new CrashSimulationStream(writeThrough: false, point, "journal");
        var harness = new IndexTestHarness(data, journal);
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload"));
        await harness.CommitAsync(setup);
        var registrations = harness.IndexManager.ExportRegistrations();
        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < 7; i++)
            { await index.InsertVersionAsync(build, BTreePayloadTests.Key(i), (ulong)i, default, default, BTreePayloadTests.Payload(i).AsMemory(0, 17)); }
            build.Commit();
        }
        harness.Storage.Checkpoint();
        int creationWrites = point.Writes;
        if (crashAtWrite > 0) { point.CrashAtWrite = creationWrites + crashAtWrite; }
        bool acknowledged = false;
        long sequence = 0;
        try
        {
            using var change = harness.Storage.BeginTransaction();
            sequence = change.Sequence;
            for (int i = 0; i < 7; i++)
            { await index.ReplacePayloadAsync(change, BTreePayloadTests.Key(i), (ulong)i, BTreePayloadTests.Payload(i)); }
            for (int i = 7; i < 40; i++)
            { await index.InsertVersionAsync(change, BTreePayloadTests.Key(i), (ulong)i, default, default, BTreePayloadTests.Payload(i)); }
            BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBeGreaterThanOrEqualTo(3);
            harness.Storage.PageManager.FlushAll();
            harness.Storage.Journal.FlushDurable();
            if (verifyBoundaries) { await VerifyAsync(data.CaptureDurable(), journal.CaptureDurable(), registrations, false); }
            change.Commit();
            acknowledged = true;
            if (verifyBoundaries) { await VerifyAsync(data.CaptureDurable(), journal.CaptureDurable(), registrations, true); }
            harness.Storage.PageManager.FlushAll();
            harness.Storage.Journal.FlushDurable();
            if (verifyBoundaries) { await VerifyAsync(data.CaptureDurable(), journal.CaptureDurable(), registrations, true); }
            harness.Storage.Checkpoint();
            if (verifyBoundaries) { await VerifyAsync(data.CaptureDurable(), journal.CaptureDurable(), registrations, true); }
        }
        catch (Exception) when (point.HasCrashed)
        {
            // Storage can wrap the injected power loss while fencing its failed bracket.
            // The fixture's shared point proves this is the scheduled loss, not a test error.
        }
        try { await harness.DisposeAsync(); }
        catch (Exception) when (point.HasCrashed) { } // Failed storage cannot flush during disposal.
        return new Outcome(data.CaptureDurable(), journal.CaptureDurable(), registrations, creationWrites, sequence, acknowledged);
    }

    private static async Task VerifyAsync(byte[] data, byte[] journal, IReadOnlyList<BTreeIndexRegistration> registrations, bool committed)
    {
        await using var recovered = IndexTestHarness.Reopen(data, journal, registrations);
        recovered.IndexManager.TryGetIndex(1, "payload", out var index).ShouldBeTrue();
        await BTreePayloadTests.CheckAsync(index, committed ? 40 : 7,
            i => committed ? BTreePayloadTests.Payload(i) : BTreePayloadTests.Payload(i).AsSpan(0, 17).ToArray());
        BTreeShape.Read(recovered.Storage, index.RootPageId).Height.ShouldBe(committed ? 3 : 1);
    }

    private sealed record Outcome(byte[] Data, byte[] Journal, IReadOnlyList<BTreeIndexRegistration> Registrations,
        int CreationWrites, long Sequence, bool Acknowledged);
}
