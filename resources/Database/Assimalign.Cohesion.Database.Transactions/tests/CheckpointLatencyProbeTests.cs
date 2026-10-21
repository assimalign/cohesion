using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Journal enrollment")]
public sealed class CheckpointLatencyProbeTests
{
    private readonly ITestOutputHelper _output;

    public CheckpointLatencyProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Checkpoint probe: report reader p99 over physical files and a 32 MiB pool (#1269)")]
    public async Task Checkpoint_PhysicalFiles_ShouldReportReaderLatency()
    {
        // Arrange: this reports latency, never asserts a machine-dependent time limit.
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var storage = new ProbeStorage(directory);
            await using var coordinator = new TransactionCoordinator(storage, storage.Log, new ProbeRecordSpace());
            var pages = new List<PageId>();
            for (int i = 0; i < Storage.Storage.DefaultBufferPoolCapacity - 1; i++)
            {
                using var page = storage.PageManager.AllocatePage(PageType.Data);
                pages.Add(page.Id);
            }

            var latencies = new List<double>();
            int reads = 0;
            int checkpoints = 0;
            int finished = 0;
            int checkpointEpoch = 0;
            int overlappingReads = 0;
            using var ready = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var reader = Task.Factory.StartNew(() =>
            {
                ready.Set();
                do
                {
                    int epoch = Volatile.Read(ref checkpointEpoch);
                    long started = Stopwatch.GetTimestamp();
                    var context = coordinator.BeginAsync(IsolationLevel.Snapshot, stop.Token).AsTask().GetAwaiter().GetResult();
                    using (var page = storage.PageManager.GetPage(pages[reads % pages.Count]))
                    {
                        page.Page.Type.ShouldBe(PageType.Data);
                    }
                    coordinator.CommitAsync(context, stop.Token).AsTask().GetAwaiter().GetResult();
                    if ((epoch & 1) != 0 || Volatile.Read(ref checkpointEpoch) != epoch)
                    {
                        latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        Interlocked.Increment(ref overlappingReads);
                    }
                    Interlocked.Increment(ref reads);
                    Thread.Yield();
                }
                while (Volatile.Read(ref finished) == 0 && !stop.IsCancellationRequested);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Act: dirty the resident working set before each sharp checkpoint.
            try
            {
                ready.Wait(stop.Token);
                var elapsed = Stopwatch.StartNew();
                do
                {
                    foreach (var pageId in pages)
                    {
                        using var page = storage.PageManager.GetPage(pageId);
                        page.Page.AsBodySpan()[0] = (byte)((checkpoints % 255) + 1);
                        page.MarkDirty();
                    }

                    Interlocked.Increment(ref checkpointEpoch);
                    coordinator.Checkpoint();
                    Interlocked.Increment(ref checkpointEpoch);
                    checkpoints++;
                }
                while (!stop.IsCancellationRequested && (checkpoints < 5 ||
                    elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || Volatile.Read(ref overlappingReads) == 0));
            }
            finally
            {
                Volatile.Write(ref finished, 1);
                await reader;
            }

            // Assert: the workload ran; percentile values are evidence for controlled runs.
            checkpoints.ShouldBeGreaterThanOrEqualTo(5);
            reads.ShouldBeGreaterThan(0);
            latencies.Count.ShouldBeGreaterThan(0);
            latencies.Sort();
            // Nearest rank also reports the maximum when a stalled baseline yields only
            // a handful of overlapping samples; it must not understate that sparse tail.
            int p99Index = (int)Math.Ceiling(latencies.Count * 0.99) - 1;
            _output.WriteLine($"32 MiB physical pool: checkpoints={checkpoints}, reads={reads}, overlappingReads={latencies.Count}, p99={latencies[p99Index]:F3} ms, max={latencies[^1]:F3} ms");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ProbeStorage : Storage.Storage
    {
        internal ProbeStorage(string directory)
            : base(StorageModel.KeyValue, StorageStream.FromFile(Path.Combine(directory, "data.db")),
                StorageStream.FromFile(Path.Combine(directory, "journal.db")), StorageStream.FromInMemory())
        {
            InitializeNew((Name)"checkpoint-probe");
        }

        internal StorageJournal Log => WriteAheadLog;
    }

    private sealed class ProbeRecordSpace : TransactionRecordSpace
    {
        protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => ReadOnlyMemory<byte>.Empty;
        protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record) => throw new NotSupportedException();
        protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex) => throw new NotSupportedException();
        protected override ulong PackLocationCore(PageId pageId, int slotIndex) => ((ulong)(long)pageId << 16) | (ushort)slotIndex;
        protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));
    }
}
