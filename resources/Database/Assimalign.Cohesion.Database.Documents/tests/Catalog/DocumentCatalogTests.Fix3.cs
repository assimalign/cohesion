using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Oversized scalar and composite keys refuse within the grant (#1808)")]
    [InlineData(20, 64, false)] [InlineData(1, 4, false)]
    [InlineData(20, 64, true)] [InlineData(1, 4, true)]
    public async Task CreateIndexAsync_Fix3OversizedKey_ShouldRefuseBeforeMaterialization(int mib, int grant, bool composite)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var fixture = await Fixture.Create();
        string json = "{\"w\":1,\"v\":\"" + new string('x', mib * 1024 * 1024) + "\"}";
        await fixture.Write("one", json);
        fixture.Catalog.IndexBuildMemoryBudget = grant * 1024L * 1024;
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        long before = LiveHeap.Read(), additional = -1;
        void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is DocumentCatalogException { Code: "CD02026" } && additional < 0)
            { additional = LiveHeap.Read() - before; }
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            // Act.
            var error = await Should.ThrowAsync<DocumentCatalogException>(async () =>
                await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix",
                    composite ? [DocumentIndexKey.Scalar("w"), DocumentIndexKey.Scalar("v")] : [DocumentIndexKey.Scalar("v")],
                    false, false, context, guard.Token));
            // Assert.
            error.Code.ShouldBe("CD02026");
            _output.WriteLine($"KEY_HEAP|MiB={mib}|composite={composite}|grant={fixture.Catalog.IndexBuildMemoryBudget}|live={additional}");
            additional.ShouldBeInRange(0, fixture.Catalog.IndexBuildMemoryBudget);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
            await fixture.Coordinator.RollbackAsync(context, guard.Token);
            GC.KeepAlive(json);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Raw invalid index content keeps CK06003 (#1808)")]
    [InlineData("{\"v\":", "syntax")]
    [InlineData("{\"v\":1,\"v\":2}", "duplicate")]
    [InlineData("{\"v\":1e100}", "number")]
    [InlineData("{\"v\":1.0000000000000000000000000000001}", "imprecise")]
    public async Task CreateIndexAsync_Fix3RawContent_ShouldReportCorruption(string json, string kind)
    {
        // Arrange: OpenWrite is public and deliberately does not validate JSON.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.Create();
        var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        DocumentContentReference content = default;
        await using (var stream = fixture.Storage.OpenWrite(fixture.Coordinator, writer, value =>
            { content = value; return ValueTask.CompletedTask; }, static () => ValueTask.CompletedTask, guard.Token))
        { await stream.WriteAsync(Encoding.UTF8.GetBytes(json), guard.Token); }
        await fixture.Catalog.SaveDocumentAsync(new(fixture.Collection.Id, "raw", 1, content.Head, content.Length, content.Checksum), writer, guard.Token);
        await fixture.Coordinator.CommitAsync(writer, guard.Token);
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        try
        {
            // Act / Assert.
            var error = await Should.ThrowAsync<StorageCorruptionException>(async () =>
                await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", "v", context, guard.Token));
            error.Code.ShouldBe("CK06003");
            _output.WriteLine($"RAW_CONTENT|{kind}|{error.Code}");
        }
        finally { await fixture.Coordinator.RollbackAsync(context, guard.Token); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Cumulative DOM growth refuses 1.4 million small tokens at the default grant (#1808)")]
    public async Task CreateIndexAsync_Fix3ManyTokens_ShouldRefuseDefaultGrant()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var fixture = await Fixture.Create();
        fixture.Catalog.IndexBuildMemoryBudget = 64 * 1024 * 1024;
        await fixture.Write("one", "{\"v\":1,\"a\":[" + string.Join(',', Enumerable.Repeat("0", 1400000)) + "]}");
        // Act / Assert.
        var error = await Should.ThrowAsync<DocumentCatalogException>(async () => await fixture.CreateIndex("ix", "v"));
        error.Code.ShouldBe("CD05018");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - The UNIQUE cursor reserves its 64 longest keys (#1808)")]
    public async Task CreateIndexAsync_Fix3LongUniqueBatch_ShouldRefuseRemainingGrant()
    {
        // Arrange: extraction/chunks fit; the final cursor's owned 64-key batch does not.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var fixture = await Fixture.Create();
        for (int i = 0; i < 128; i++) { await fixture.Write(i.ToString("D3"), i % 2 == 0 ? "{\"v\":\"" + new string('x', 320) + i.ToString("D3") + "\"}" : "{\"v\":null}"); }
        int chunks = 0;
        fixture.Catalog.AfterIndexRetirementMutation = stage => { if (stage == "build-chunk") { chunks++; } };
        fixture.Catalog.IndexBuildMemoryBudget = 64 * 1024;
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        try
        {
            // Act / Assert.
            var error = await Should.ThrowAsync<DocumentCatalogException>(async () =>
                await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", ["v"], true, context, guard.Token));
            error.Code.ShouldBe("CD05018");
            chunks.ShouldBe(2);
        }
        finally { await fixture.Coordinator.RollbackAsync(context, guard.Token); }
    }
    [Theory(DisplayName = "Cohesion Test [Documents] - Permitted keys just below the encoding limit fit their charge (#1808)")]
    [InlineData(false)] [InlineData(true)]
    public void Read_Fix3PermittedKey_ShouldCoverMeasuredAllocations(bool composite)
    {
        // Arrange.
        string text = new('x', composite ? 337 : 339);
        var metadata = new DocumentIndexMetadata(Guid.NewGuid(), "ix", composite
            ? [DocumentIndexKey.Scalar("w"), DocumentIndexKey.Scalar("v")] : [DocumentIndexKey.Scalar("v")], false, false, 1000);
        byte[] content = Encoding.UTF8.GetBytes("{\"w\":\"a\",\"v\":\"" + text + "\"}");
        for (int i = 0; i < 16; i++) { DocumentIndexKeys.Read(content, metadata, out _, reserve: _ => { }); }
        var expected = composite ? DocumentIndexKeys.EncodePrefix(new object[] { "a", text }) : DocumentIndexKeys.Encode(text);
        var insertion = new HashSet<IndexKey>();
        long insertionStart = GC.GetAllocatedBytesForCurrentThread();
        insertion.Add(expected);
        long insertionBytes = GC.GetAllocatedBytesForCurrentThread() - insertionStart;
        // Act / Assert: all newly owned heap is bounded by these synchronous
        // allocations. Global live-heap deltas can also include runner activity;
        // keep them as diagnostics rather than attributing them to this read.
        for (int round = 0; round < 5; round++)
        {
            var (keys, charge, live, allocated, domAllocated, domCharge, afterAdmission) = MeasurePermittedKey(content, metadata);
            var key = keys.ShouldHaveSingleItem();
            key.Length.ShouldBe(composite ? 1022 : 1021);
            key.ShouldBe(expected);
            allocated.ShouldBeLessThanOrEqualTo(charge - (32 + content.Length));
            // Parsing has its own admission, before key workspace exists. The
            // final admission may leave only the measured set insertion to allocate.
            domAllocated.ShouldBeLessThanOrEqualTo(domCharge);
            afterAdmission.ShouldBeLessThanOrEqualTo(insertionBytes);
            _output.WriteLine($"PERMITTED_KEY|composite={composite}|length={key.Length}|charge={charge}|live={live}|allocated={allocated}");
            GC.KeepAlive(keys);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (HashSet<IndexKey> Keys, long Charge, long Heap, long Allocated, long DomAllocated, long DomCharge, long AfterAdmission) MeasurePermittedKey(byte[] content, DocumentIndexMetadata metadata)
    {
        long charge = 32 + content.Length;
        long allocationStart = 0, lastAdmission = 0, domAllocated = 0, domCharge = 0;
        Action<long> reserve = bytes => { charge += bytes; lastAdmission = GC.GetAllocatedBytesForCurrentThread(); };
        Action<object> parsed = _ =>
        {
            domAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            domCharge = charge - (32 + content.Length);
        };
        long before = LiveHeap.Read();
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var keys = DocumentIndexKeys.Read(content, metadata, out _, reserve: reserve, parsed: parsed);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        long live = LiveHeap.Read() - before;
        return (keys, charge, live, allocated, domAllocated, domCharge, allocationStart + allocated - lastAdmission);
    }
}
