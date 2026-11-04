using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(nameof(StorageEventSourceCollection))]
public sealed class StorageTempSpaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-spill-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public StorageTempSpaceTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
    private StorageTempSpace Create(long quota = 16 * 1024 * 1024)
        => StorageTempSpace.CreateCore(_directory, quota, () => 0, () => long.MaxValue);

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp volume: an unavailable ancestor refuses rather than looping")]
    public async Task AvailableBytes_MissingAncestor_ShouldRefuseWithoutLooping()
    {
        // Arrange: an empty internal probe path has no existing ancestor, like an unavailable
        // drive/share root. The public factory always normalizes a nonempty full path.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var failure = await Task.Run(() => Should.Throw<IOException>(() =>
            StorageTempVolumeSpace.AvailableBytes(string.Empty)), timeout.Token).WaitAsync(timeout.Token);

        // Assert
        failure.Message.ShouldContain("free-space probe failed", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp runs: independent readers, multi-page records and disposal")]
    public void Run_MultipleReaders_ShouldReadIdenticalRecordsAndRelease()
    {
        // Arrange
        using var space = Create();
        using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun();
        byte[][] records = [[], [1, 2, 3], Enumerable.Range(0, 25000).Select(i => (byte)i).ToArray()];
        long reads = StorageEventSource.Log.PageReads, writes = StorageEventSource.Log.PageWrites;
        long storages = StorageEventSource.Log.CurrentStorages;
        foreach (byte[] record in records) { run.Append(record); }
        Should.Throw<InvalidOperationException>(() => run.OpenReader(8192));
        run.Seal();
        using var first = run.OpenReader(8192);
        using var second = run.OpenReader(8192);
        byte[] buffer = new byte[run.MaximumRecordLength];

        // Act
        foreach (byte[] record in records)
        {
            first.TryRead(buffer, out int length).ShouldBeTrue(); buffer[..length].ShouldBe(record);
            second.TryRead(buffer, out length).ShouldBeTrue(); buffer[..length].ShouldBe(record);
        }

        // Assert
        first.TryRead(buffer, out _).ShouldBeFalse(); second.TryRead(buffer, out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => run.Append([0]));
        space.Bytes.ShouldBe(4 * 8192);
        statement.Dispose(); statement.Dispose(); run.Dispose();
        space.Bytes.ShouldBe(0); statement.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
        Should.Throw<ObjectDisposedException>(() => first.TryRead(buffer, out _));
        StorageEventSource.Log.PageReads.ShouldBe(reads); StorageEventSource.Log.PageWrites.ShouldBe(writes);
        StorageEventSource.Log.CurrentStorages.ShouldBe(storages);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp disposal: every statement and run releases its reservation once")]
    public void Dispose_ManyStatementsAndRuns_ShouldReleaseEveryLifetime()
    {
        // Arrange
        using var space = Create();
        var statements = Enumerable.Range(0, 40).Select(_ => space.CreateStatement(3 * 8192)).ToArray();
        foreach (var statement in statements)
        {
            for (int runIndex = 0; runIndex < 3; runIndex++)
            { var run = statement.CreateRun(); run.Append([1]); }
        }
        space.Bytes.ShouldBe(120 * 8192);

        // Act
        space.Dispose(); space.Dispose();
        foreach (var statement in statements) { statement.Dispose(); }

        // Assert
        statements.ShouldAllBe(statement => statement.Bytes == 0);
        space.Bytes.ShouldBe(0); Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp quotas: exact coded refusal releases the failed run")]
    [InlineData(0, 8192, "CK05001")]
    [InlineData(8192, 0, "CK05002")]
    [InlineData(8192, 8192, "CK05002")]
    public void Append_QuotaBoundary_ShouldRefuseAndRelease(long engineQuota, long statementQuota, string code)
    {
        // Arrange
        using var space = Create(engineQuota);
        using var statement = space.CreateStatement(statementQuota);
        using var run = statement.CreateRun();

        // Act
        var failure = Should.Throw<StorageTempException>(() => run.Append(new byte[8192]));

        // Assert
        failure.Code.ShouldBe(code); failure.Message.ShouldNotContain(code, Case.Sensitive);
        space.Bytes.ShouldBe(0); statement.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp floor: pending tails and live reserve prevent growth")]
    public void Reserve_PendingPagesAndChangingFloor_ShouldRefuseBeforeWrite()
    {
        // Arrange
        long free = 3 * 8192, floor = 8192;
        using var space = StorageTempSpace.CreateCore(_directory, null, () => floor, () => free);
        space.EngineQuota.ShouldBe(free / 4); // Snapshot before sweeping, never recomputed.
        using var largeSpace = StorageTempSpace.CreateCore(Path.Combine(_directory, "child"), 1024 * 1024, () => floor, () => free);
        using var statement = largeSpace.CreateStatement(1024 * 1024);
        using var first = statement.CreateRun(); using var second = statement.CreateRun(); using var third = statement.CreateRun();
        first.Append([1]); second.Append([2]); // Two charged but not yet written pages.

        // Act
        var failure = Should.Throw<StorageTempException>(() => third.Append([3]));
        floor = free; // An engine reserve can rise after its space was created.
        var next = statement.CreateRun();
        var changed = Should.Throw<StorageTempException>(() => next.Append([4]));

        // Assert
        failure.Code.ShouldBe(StorageTempException.FreeSpaceCode); changed.Code.ShouldBe(failure.Code);
        largeSpace.Bytes.ShouldBe(2 * 8192); statement.Dispose(); largeSpace.Bytes.ShouldBe(0);
        free = 0; space.EngineQuota.ShouldBe(3 * 8192 / 4);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp cancellation: append and seal release all pages")]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_Cancellation_ShouldReleaseEveryPage(bool seal)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun(); run.Append([1]);
        using var source = new CancellationTokenSource(); source.Cancel();

        // Act
        Should.Throw<OperationCanceledException>(() =>
        { if (seal) { run.Seal(source.Token); } else { run.Append([2], source.Token); } });

        // Assert
        space.Bytes.ShouldBe(0); statement.Bytes.ShouldBe(0);
        Should.Throw<OperationCanceledException>(() => statement.CreateRun(source.Token));
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp page write: exception and mid-append cancellation release reservations")]
    public void Append_WriteFailureAndCancellation_ShouldReleaseAllReservations()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        var fault = new IOException("injected write failure");
        space.BeforePageWrite = () => throw fault;
        using var run = statement.CreateRun();

        // Act
        Should.Throw<IOException>(() => run.Append(new byte[20000])).ShouldBeSameAs(fault);
        using var source = new CancellationTokenSource();
        space.BeforePageWrite = source.Cancel;
        using var canceled = statement.CreateRun();
        Should.Throw<OperationCanceledException>(() => canceled.Append(new byte[20000], source.Token));

        // Assert
        space.Bytes.ShouldBe(0); statement.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp reserve: a failing floor probe releases the entire run")]
    public void Append_FloorProbeThrows_ShouldReleaseEarlierPages()
    {
        // Arrange
        bool fail = false;
        var fault = new IOException("injected space probe failure");
        using var space = StorageTempSpace.CreateCore(_directory, 1024 * 1024,
            () => fail ? throw fault : 0, () => long.MaxValue);
        using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun(); run.Append(new byte[8000]); fail = true;

        // Act
        Should.Throw<IOException>(() => run.Append(new byte[8000])).ShouldBeSameAs(fault);

        // Assert
        space.Bytes.ShouldBe(0); statement.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp read: cancellation leaves ownership with the statement")]
    public void Read_Cancellation_ShouldReleaseAtStatementDisposal()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun(); run.Append([1]); run.Seal();
        using var reader = run.OpenReader(8192);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        // Act
        Should.Throw<OperationCanceledException>(() => reader.TryRead(new byte[1], out _, cancellation.Token));
        space.Bytes.ShouldBe(8192);
        statement.Dispose();

        // Assert
        space.Bytes.ShouldBe(0); Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp integrity: framing, payload and truncation are detected")]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(28)]
    [InlineData(256)]
    [InlineData(-1)]
    public void Read_DamagedPage_ShouldRaiseCodedFailure(int offset)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun(); run.Append(new byte[20000]); run.Seal();
        var file = RunFile(run);
        if (offset < 0) { file.SetLength(100); }
        else { file.Position = offset; file.WriteByte(0xA5); }
        using var reader = run.OpenReader(8192);

        // Act
        var failure = Should.Throw<StorageTempException>(() => reader.TryRead(new byte[20000], out _));

        // Assert
        failure.Code.ShouldBe("CK05004"); run.Dispose(); space.Bytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp integrity: trailing bytes are refused, including empty runs")]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_TrailingBytes_ShouldRefuseBeforeReturningRows(bool empty)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun();
        if (!empty) { run.Append([1]); }
        run.Seal();
        var file = RunFile(run); file.SetLength(file.Length + 1);
        using var reader = run.OpenReader(8192);

        // Act
        var failure = Should.Throw<StorageTempException>(() => reader.TryRead(new byte[1], out _));

        // Assert
        failure.Code.ShouldBe("CK05004"); run.Dispose(); space.Bytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp directory: exclusive lease, exact orphan sweep and lazy creation")]
    public void Create_DirectoryOwnership_ShouldSweepOnlyOwnedOrphans()
    {
        // Arrange
        using (var absent = Create()) { Directory.Exists(_directory).ShouldBeFalse(); }
        Directory.CreateDirectory(_directory);
        string orphan = Path.Combine(_directory, "cohesion-" + Guid.NewGuid().ToString("N") + ".ctmp");
        string unrelated = Path.Combine(_directory, "cohesion-user.ctmp");
        File.WriteAllText(orphan, "orphan"); File.WriteAllText(unrelated, "keep");

        // Act
        using (var space = Create())
        {
            File.Exists(orphan).ShouldBeFalse();
            File.ReadAllText(unrelated).ShouldBe("keep");
            File.Exists(Path.Combine(_directory, ".cohesion-temp.lock")).ShouldBeFalse();
            using var statement = space.CreateStatement(8192);
            using var run = statement.CreateRun();
            run.Append([1]);
            Should.Throw<IOException>(() => Create());
        }

        // Assert
        File.Exists(Path.Combine(_directory, ".cohesion-temp.lock")).ShouldBeFalse();
        Directory.GetFiles(_directory).ShouldBe([unrelated]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp crash: kill a real executable, reopen and sweep leftovers")]
    public async Task Create_AfterKilledProcess_ShouldSweepEveryOwnedFile()
    {
        // Arrange
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TempFixture", "Assimalign.Cohesion.Database.Storage.TempFixture.dll"));
        start.ArgumentList.Add(_directory);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        // Act
        try
        {
            (await process.StandardOutput.ReadLineAsync(timeout.Token)).ShouldBe("READY");
            Directory.GetFiles(_directory, "*.ctmp").Length.ShouldBeGreaterThanOrEqualTo(1);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
        using (var reopened = Create()) { Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty(); }

        // Assert
        (await stderr).ShouldBeEmpty();
        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp sweep: disabled Unix file locking cannot delete a potential live orphan")]
    public async Task Create_DisabledUnixFileLocking_ShouldRefuseBeforeSweep()
    {
        // Arrange: the runtime caches this switch, so isolate it in the real fixture process.
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TempFixture", "Assimalign.Cohesion.Database.Storage.TempFixture.dll"));
        start.ArgumentList.Add(_directory); start.ArgumentList.Add("lease-probe");
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);

        // Act
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }

        // Assert
        process.ExitCode.ShouldBe(0, await error);
        if (OperatingSystem.IsWindows())
        {
            (await output).Trim().ShouldBe("LEASE_OK"); Directory.GetFiles(_directory).ShouldBeEmpty();
        }
        else
        {
            (await output).ShouldStartWith("LEASE_REFUSED");
            (await output).ShouldContain("exclusive file lease", Case.Sensitive);
            Directory.GetFiles(_directory).Select(Path.GetFileName).ShouldBe(["cohesion-00000000000000000000000000000001.ctmp"]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp events: byte gauge, refusal and writes have exact counters and payloads")]
    public async Task Events_TempLifetimes_ShouldCountAndPublish()
    {
        // Arrange
        using var recorder = new StorageEventRecorder(EventLevel.Verbose, counterInterval: TimeSpan.FromMilliseconds(100));
        long baseline = StorageEventSource.Log.TempBytes, refused = StorageEventSource.Log.TempRefusals;
        long written = StorageEventSource.Log.TempPagesWritten;
        using var space = Create(8192); using var statement = space.CreateStatement(8192);

        // Act
        using (var run = statement.CreateRun()) { run.Append([1]); run.Seal(); }
        using (var run = statement.CreateRun()) { Should.Throw<StorageTempException>(() => run.Append(new byte[8192])); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await recorder.WaitForCountersAsync(["current-temp-bytes", "temp-pages-written-per-second", "temp-refusals-per-second"], timeout.Token);

        // Assert
        StorageEventSource.Log.TempBytes.ShouldBe(baseline);
        (StorageEventSource.Log.TempRefusals - refused).ShouldBe(1);
        (StorageEventSource.Log.TempPagesWritten - written).ShouldBe(2);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        var changes = recorder.Events.Where(e => e.EventId == 21).ToArray();
        changes.Length.ShouldBe(4);
        changes.ShouldAllBe(e => e.PayloadNames!.SequenceEqual(new[] { "deltaBytes", "currentBytes" }));
        var refusal = recorder.Events.Single(e => e.EventId == 22);
        refusal.PayloadNames.ShouldBe(["code"]); refusal.Payload.ShouldBe(["CK05002"]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp concurrency: statements race atomic engine quota and release after every exit")]
    public async Task Reserve_ConcurrentStatements_ShouldNeverOversubscribeOrLeak()
    {
        // Arrange
        using var space = Create(4 * 8192);
        long successes = 0, refusals = 0, cancellations = 0, attempts = 0, overlaps = 0;
        int active = 0;
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        // Act: a minimum exercise time, then progress counters decide completion. Duration only
        // controls coverage and the hang guard; it is not a performance assertion.
        do
        {
            var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
            {
                using var statement = space.CreateStatement(2 * 8192);
                using var run = statement.CreateRun(); Interlocked.Increment(ref attempts);
                if (Interlocked.Increment(ref active) > 1) { Interlocked.Increment(ref overlaps); }
                try
                {
                    run.Append([1]); Interlocked.Increment(ref successes);
                    await Task.Yield(); // Hold a pending page while other statements reserve.
                    lock (space.Gate) { space.Bytes.ShouldBeLessThanOrEqualTo(space.EngineQuota); }
                    using var canceled = new CancellationTokenSource(); canceled.Cancel();
                    Should.Throw<OperationCanceledException>(() => run.Seal(canceled.Token));
                    Interlocked.Increment(ref cancellations);
                }
                catch (StorageTempException error)
                { error.Code.ShouldBe("CK05001"); Interlocked.Increment(ref refusals); }
                finally { Interlocked.Decrement(ref active); }
            }, timeout.Token)).ToArray();
            await Task.WhenAll(tasks).WaitAsync(timeout.Token);
        } while (watch.Elapsed < TimeSpan.FromSeconds(1) || attempts < 100 || successes == 0 || refusals == 0 || cancellations == 0 || overlaps == 0);

        // Assert
        successes.ShouldBeGreaterThan(0); refusals.ShouldBeGreaterThan(0); cancellations.ShouldBe(successes);
        overlaps.ShouldBeGreaterThan(0);
        space.Bytes.ShouldBe(0); Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
        _output.WriteLine($"attempts={attempts}; successful reservations={successes}; refusals={refusals}; cancellations={cancellations}; overlaps={overlaps}");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_file")]
    private static extern ref FileStream RunFile(StorageTempRun run);
}
