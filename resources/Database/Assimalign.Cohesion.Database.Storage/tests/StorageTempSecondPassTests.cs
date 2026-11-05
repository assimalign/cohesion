using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32.SafeHandles;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(nameof(StorageEventSourceCollection))]
public sealed class StorageTempSecondPassTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-temp-second-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly byte[] _pageRecord = new byte[StorageTempRun.PayloadSize - 4];
    public StorageTempSecondPassTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    private StorageTempSpace Create(Func<long>? probe = null, long floor = 0)
        => StorageTempSpace.CreateCore(Path.Combine(_root, "spill"), 64 * 1024 * 1024, () => floor, probe ?? (() => long.MaxValue));

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp floor: page 129 observes a drop at the 128-page probe window")]
    public void Append_DroppedFreeSpace_ShouldRefuseAtProbeWindow()
    {
        // Arrange
        int probes = 0;
        using var space = Create(() => ++probes == 1 ? long.MaxValue : 8192, 8192);
        using var statement = space.CreateStatement(16 * 1024 * 1024); using var run = statement.CreateRun();
        for (int i = 0; i < 128; i++) { run.Append(_pageRecord); }

        // Act
        Should.Throw<StorageTempException>(() => run.Append(_pageRecord)).Code.ShouldBe("CK05003");

        // Assert
        probes.ShouldBe(3); statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp floor: disk freed after refusal permits the next reservation")]
    public void Append_FreedAfterRefusal_ShouldRefreshAndRecover()
    {
        // Arrange
        int probes = 0;
        using var space = Create(() => ++probes <= 2 ? 8192 : 3 * 8192, 8192);
        using var statement = space.CreateStatement(16384); using var refused = statement.CreateRun();
        Should.Throw<StorageTempException>(() => refused.Append([1])).Code.ShouldBe("CK05003");

        // Act
        using var retry = statement.CreateRun(); retry.Append([2]); retry.Seal();

        // Assert
        probes.ShouldBe(3); statement.PhysicalBytes.ShouldBe(8192);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp floor: releases before the probe window credit admission")]
    public void Append_ReleaseBeforeWindow_ShouldCreditFreeSpace()
    {
        // Arrange
        int probes = 0;
        using var space = Create(() => { probes++; return 3 * 8192; }, 8192);
        using var statement = space.CreateStatement(32768);
        using var first = statement.CreateRun(); using var second = statement.CreateRun();
        first.Append(_pageRecord); second.Append(_pageRecord); first.Dispose();

        // Act
        using var third = statement.CreateRun(); third.Append(_pageRecord);

        // Assert
        probes.ShouldBe(1); statement.PhysicalBytes.ShouldBe(16384);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp floor: releases during a probe remain credited exactly once")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Append_ReleaseDuringProbe_ShouldRetainCredit(bool includesRelease)
    {
        // Arrange: a probe can observe either side of the concurrent close.
        using var entered = new ManualResetEventSlim(); using var released = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        int probes = 0;
        using var space = Create(() =>
        {
            if (Interlocked.Increment(ref probes) == 1) { return 129 * 8192; }
            entered.Set(); released.Wait(timeout.Token); return includesRelease ? 129 * 8192 : 8192;
        }, 8192);
        using var statement = space.CreateStatement(16 * 1024 * 1024);
        using var prior = statement.CreateRun();
        for (int i = 0; i < 128; i++) { prior.Append(_pageRecord); }
        using var next = statement.CreateRun();
        var writer = Task.Factory.StartNew(() => next.Append(_pageRecord), timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Act
        try { entered.Wait(timeout.Token); prior.Dispose(); }
        finally { released.Set(); }
        await writer.WaitAsync(timeout.Token);

        // Assert
        probes.ShouldBe(2); statement.PhysicalBytes.ShouldBe(8192); space.PendingBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp floor: an aged startup sample refreshes before admission")]
    public void Append_OldSample_ShouldRefreshBeforeAdmission()
    {
        // Arrange: time only makes the sample eligible; the probe sequence decides the result.
        int probes = 0;
        using var space = Create(() => ++probes == 1 ? long.MaxValue : 8192, 8192);
        using var statement = space.CreateStatement(8192); using var run = statement.CreateRun();
        long started = Environment.TickCount64;
        while (Environment.TickCount64 - started <= 1100) { Thread.Sleep(10); }

        // Act and assert
        Should.Throw<StorageTempException>(() => run.Append([1])).Code.ShouldBe("CK05003");
        probes.ShouldBe(3); statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp probe: a stalled refresh lets another statement use bounded stale headroom")]
    public async Task Append_ContendedProbe_ShouldAllowIndependentReservation()
    {
        // Arrange
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        int probes = 0;
        using var space = Create(() =>
        {
            if (Interlocked.Increment(ref probes) > 1) { entered.Set(); release.Wait(timeout.Token); }
            return long.MaxValue;
        });
        using var first = space.CreateStatement(16 * 1024 * 1024); using var second = space.CreateStatement(8192);
        using var a = first.CreateRun(); using var b = second.CreateRun();
        for (int i = 0; i < 128; i++) { a.Append(_pageRecord); }
        var writer = Task.Factory.StartNew(() => a.Append(_pageRecord), timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? progress = null;

        // Act
        try
        {
            entered.Wait(timeout.Token);
            progress = Task.Factory.StartNew(() => b.Append(_pageRecord), timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await progress.WaitAsync(timeout.Token);
        }
        finally
        {
            release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(35));
            if (progress is not null) { await progress.WaitAsync(TimeSpan.FromSeconds(35)); }
        }

        // Assert
        probes.ShouldBe(2); second.PhysicalBytes.ShouldBe(8192);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp cleanup: close failures cannot replace the initiating cancellation")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Dispose_FailureCleanup_ShouldPreserveFirstException(int operation)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cause = new IOException("injected close failure"); long refusals = StorageEventSource.Log.TempRefusals;
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        using var partitions = StorageTempPartitions.Create(statement, 2, 32768);
        Action act;
        if (operation < 2)
        {
            for (int i = 0; i < 300; i++) { sorter.Append([1], new byte[100]); }
            var run = SortRuns(sorter).First(r => r is not null)!;
            RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle, cause);
            act = operation == 0 ? () => sorter.Append([1], [1], cancellation.Token) : () => sorter.Complete(cancellation.Token);
        }
        else if (operation == 2)
        {
            var run = statement.CreateRun(); run.Append(new byte[16]); run.Seal();
            RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle, cause);
            var reader = new StorageTempSortReader(statement, [run], 16, new byte[8208], true);
            act = () => reader.MoveNext(cancellation.Token);
        }
        else
        {
            partitions.Append(0, [1]);
            var run = PartitionRuns(partitions)[0]!;
            RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle, cause);
            act = operation == 3 ? () => partitions.Append(1, [1], cancellation.Token) : () => partitions.Seal(cancellation.Token);
        }

        // Act and assert
        Should.Throw<OperationCanceledException>(act);
        statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        (StorageEventSource.Log.TempRefusals - refusals).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp reader: exhausted owned inputs return quota before final EOF")]
    public void MoveNext_ExhaustedInput_ShouldReleaseRunImmediately()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(16384);
        var first = statement.CreateRun(); var second = statement.CreateRun();
        byte[] row = new byte[17]; BinaryPrimitives.WriteInt32LittleEndian(row, 1);
        first.Append(row); first.Seal(); row[16] = 1; second.Append(row); second.Append(row); second.Seal();
        using var reader = new StorageTempSortReader(statement, [first, second], 17, new byte[2 * 8209], true);
        reader.MoveNext().ShouldBeTrue(); statement.PhysicalBytes.ShouldBe(16384);

        // Act
        reader.MoveNext().ShouldBeTrue();

        // Assert
        statement.PhysicalBytes.ShouldBe(8192);
        reader.MoveNext().ShouldBeTrue(); reader.MoveNext().ShouldBeFalse(); statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp concurrency: CreateRun racing statement disposal releases every file")]
    public async Task CreateRun_StatementDisposalRace_ShouldReleaseEveryFile()
    {
        // Arrange
        long created = 0, refused = 0, rounds = 0;
        using var space = Create(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var watch = Stopwatch.StartNew();

        // Act: minimum exercise time, then recorded outcomes decide completion.
        do
        {
            using var statement = space.CreateStatement(8192);
            using var start = new ManualResetEventSlim(); using var ready = new CountdownEvent(2);
            var creator = Task.Factory.StartNew(() =>
            {
                using (var seed = statement.CreateRun()) { Interlocked.Increment(ref created); }
                ready.Signal(); start.Wait(timeout.Token);
                try
                {
                    while (true) { using var run = statement.CreateRun(); Interlocked.Increment(ref created); }
                }
                catch (ObjectDisposedException) { Interlocked.Increment(ref refused); }
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var disposer = Task.Factory.StartNew(() =>
            {
                ready.Signal(); start.Wait(timeout.Token); statement.Dispose();
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            ready.Wait(timeout.Token); start.Set(); await Task.WhenAll(creator, disposer).WaitAsync(timeout.Token); rounds++;
            statement.PhysicalBytes.ShouldBe(0); space.Bytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        } while (watch.Elapsed < TimeSpan.FromSeconds(1) || created < 100 || refused < 100);

        // Assert
        Directory.GetFiles(Path.Combine(_root, "spill"), "*.ctmp").ShouldBeEmpty();
        created.ShouldBeGreaterThanOrEqualTo(100); refused.ShouldBeGreaterThanOrEqualTo(100);
        _output.WriteLine($"create/dispose rounds={rounds}; created={created}; refused={refused}");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp handles: injected EMFILE is CK05005 and releases all deferred runs")]
    public void Append_FileHandleExhaustion_ShouldRetainCauseAndReleaseEverything()
    {
        // Arrange: real ENOSPC and EMFILE remain injected; no host-volume or process limit changes.
        using var space = Create(); using var statement = space.CreateStatement(64 * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        var cause = new IOException("injected EMFILE", 24); int opens = 0;
        space.BeforeFileCreate = () => { if (++opens == 140) { throw cause; } };

        // Act
        var error = Should.Throw<StorageTempException>(() =>
        { for (int i = 0; i < 40000; i++) { sorter.Append(new byte[5], new byte[100]); } });

        // Assert
        error.Code.ShouldBe("CK05005"); error.InnerException.ShouldBeSameAs(cause); opens.ShouldBe(140);
        statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        Directory.GetFiles(Path.Combine(_root, "spill"), "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp availability: lazy setup warns on first failure and includes the cause type")]
    public void CreateRun_LazyDirectoryFailure_ShouldWarnWithCauseType()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(8192);
        Directory.CreateDirectory(_root); File.WriteAllBytes(Path.Combine(_root, "spill"), [1]);
        using var events = new StorageEventRecorder(System.Diagnostics.Tracing.EventLevel.Warning);

        // Act
        var error = Should.Throw<StorageTempException>(() => statement.CreateRun());

        // Assert
        var warning = events.Events.Single(e => e.EventId == 23);
        warning.PayloadNames.ShouldNotBeNull(); warning.Payload.ShouldNotBeNull();
        warning.PayloadNames!.ShouldContain("exceptionType");
        warning.Payload!.ShouldContain(error.InnerException!.GetType().FullName);
        statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp quota: a failed close returns accounting without claiming reclaimed disk")]
    public void Dispose_FailedClose_ShouldAvoidCreditingUnconfirmedDisk()
    {
        // Arrange
        int probes = 0;
        using var space = Create(() => ++probes == 1 ? 16384 : 8192, 8192);
        using var statement = space.CreateStatement(16384); using var first = statement.CreateRun();
        first.Append(_pageRecord);
        RunFile(first) = new FailedCloseStream(RunFile(first).SafeFileHandle, new IOException("unconfirmed close"));

        // Act
        Should.Throw<StorageTempException>(() => first.Dispose()).Code.ShouldBe("CK05005");
        using var next = statement.CreateRun();
        Should.Throw<StorageTempException>(() => next.Append(_pageRecord)).Code.ShouldBe("CK05003");

        // Assert
        probes.ShouldBe(2); statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp cleanup: a close failure cannot replace a statement quota refusal")]
    public void Append_QuotaAndCloseFailure_ShouldKeepQuotaIdentity()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(4 * 8192);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        byte[] key = new byte[5], value = new byte[100];
        for (int i = 0; i < 300; i++) { sorter.Append(key, value); }
        var run = SortRuns(sorter).First(r => r is not null)!;
        RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle, new IOException("injected close failure"));

        // Act
        var error = Should.Throw<StorageTempException>(() =>
        { for (int i = 0; i < 300; i++) { sorter.Append(key, value); } });

        // Assert
        error.Code.ShouldBe("CK05002"); statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [WindowsFact(DisplayName = "Cohesion Test [Storage] - Temp links: unprivileged directory junction refuses and preserves target")]
    public void Create_JunctionDirectory_ShouldPreserveTarget()
    {
        // Arrange
        string target = Path.Combine(_root, "target"); Directory.CreateDirectory(target);
        string victim = Path.Combine(target, ".cohesion-temp.lock"); File.WriteAllBytes(victim, [8, 9]);
        string link = Path.Combine(_root, "spill"); CreateJunction(link, target);
        try
        {
            // Act
            using var space = Create(); using var statement = space.CreateStatement(8192);
            Should.Throw<StorageTempException>(() => statement.CreateRun()).Code.ShouldBe("CK05005");

            // Assert
            File.ReadAllBytes(victim).ShouldBe([8, 9]); Directory.GetFiles(target).Length.ShouldBe(1);
            _output.WriteLine("Directory junction safety regression executed.");
        }
        finally { Directory.Delete(link); }
    }

    [WindowsFact(DisplayName = "Cohesion Test [Storage] - Temp links: unprivileged lease junction refuses and preserves target")]
    public void Create_JunctionLease_ShouldPreserveTarget()
    {
        // Arrange
        string temp = Path.Combine(_root, "spill"); Directory.CreateDirectory(temp);
        string target = Path.Combine(_root, "target"); Directory.CreateDirectory(target);
        string victim = Path.Combine(target, "data"); File.WriteAllBytes(victim, [3, 1, 4]);
        string link = Path.Combine(temp, ".cohesion-temp.lock"); CreateJunction(link, target);
        try
        {
            // Act
            using var space = Create(); using var statement = space.CreateStatement(8192);
            Should.Throw<StorageTempException>(() => statement.CreateRun()).Code.ShouldBe("CK05005");

            // Assert
            File.ReadAllBytes(victim).ShouldBe([3, 1, 4]); Directory.GetFiles(target).Length.ShouldBe(1);
            _output.WriteLine("Lease-path junction safety regression executed.");
        }
        finally { Directory.Delete(link); }
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("junction creation"); }
        process.ExitCode.ShouldBe(0, output + error);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_file")]
    private static extern ref FileStream RunFile(StorageTempRun run);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_levels")]
    private static extern ref StorageTempRun?[] SortRuns(StorageTempSorter sorter);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runs")]
    private static extern ref StorageTempRun?[] PartitionRuns(StorageTempPartitions partitions);

    private sealed class FailedCloseStream : FileStream
    {
        private readonly Exception _cause;
        public FailedCloseStream(SafeFileHandle handle, Exception cause) : base(handle, FileAccess.ReadWrite) { _cause = cause; }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { throw _cause; } }
    }
}
