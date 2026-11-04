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
public sealed class StorageTempRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-temp-fixes-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public StorageTempRegressionTests(ITestOutputHelper output) { _output = output; }
    private string DirectoryPath => Path.Combine(_root, "spill");
    private StorageTempSpace Create() => StorageTempSpace.CreateCore(DirectoryPath, 32 * 1024 * 1024, () => 0, () => long.MaxValue);
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    [LinkFact(DisplayName = "Cohesion Test [Storage] - Temp lease: a planted symbolic link cannot damage its target")]
    public void Create_LinkedLease_ShouldPreserveTargetBytes()
    {
        // Arrange
        Directory.CreateDirectory(DirectoryPath);
        string target = Path.Combine(_root, "victim");
        byte[] bytes = [3, 1, 4, 1, 5]; File.WriteAllBytes(target, bytes);
        File.CreateSymbolicLink(Path.Combine(DirectoryPath, ".cohesion-temp.lock"), target);
        using var events = new StorageEventRecorder(EventLevel.Warning);

        // Act
        using var space = Create();
        using var statement = space.CreateStatement(8192);
        var error = Should.Throw<StorageTempException>(() => statement.CreateRun());

        // Assert
        error.Code.ShouldBe("CK05005"); error.InnerException.ShouldBeOfType<IOException>();
        events.Events.ShouldContain(e => e.EventId == 23 && e.Level == EventLevel.Warning);
        File.ReadAllBytes(target).ShouldBe(bytes); space.Bytes.ShouldBe(0);
        _output.WriteLine("File symbolic link created and safety regression executed on " + Environment.OSVersion);
    }

    [LinkFact(DisplayName = "Cohesion Test [Storage] - Temp directory: a linked directory refuses before opening the lease")]
    public void Create_LinkedDirectory_ShouldPreserveTargetBytes()
    {
        // Arrange
        Directory.CreateDirectory(_root);
        string target = Path.Combine(_root, "target"); Directory.CreateDirectory(target);
        string victim = Path.Combine(target, ".cohesion-temp.lock"); File.WriteAllBytes(victim, [8, 9]);
        Directory.CreateSymbolicLink(DirectoryPath, target);

        // Act
        using var space = Create(); using var statement = space.CreateStatement(8192);
        Should.Throw<StorageTempException>(() => statement.CreateRun()).Code.ShouldBe("CK05005");

        // Assert
        File.ReadAllBytes(victim).ShouldBe([8, 9]); space.Bytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp availability: startup warns, first run retries, later recovery fixes quota")]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_UnavailableProbe_ShouldRetryWithoutStoppingEngine(bool denied)
    {
        // Arrange
        bool fail = true; int probes = 0;
        Exception cause = denied ? new UnauthorizedAccessException("probe denied") : new IOException("probe unavailable");
        using var events = new StorageEventRecorder(EventLevel.Warning);
        using var space = StorageTempSpace.CreateCore(DirectoryPath, null, () => 0,
            () => { probes++; if (fail) { throw cause; } return 1024 * 1024; });
        using var statement = space.CreateStatement(8192);

        // Act
        space.EngineQuota.ShouldBe(0);
        var error = Should.Throw<StorageTempException>(() => statement.CreateRun());
        fail = false; using var run = statement.CreateRun(); run.Append([1]); run.Seal();

        // Assert
        error.Code.ShouldBe("CK05005"); error.InnerException.ShouldBeSameAs(cause);
        space.EngineQuota.ShouldBe(256 * 1024); probes.ShouldBe(3);
        events.Events.ShouldContain(e => e.EventId == 23);
        run.Dispose(); space.Bytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp quota: sweep precedes the lifetime free-space measurement and overflow is unlimited")]
    public void Create_QuotaProbe_ShouldMeasureAfterSweepAndAcceptOverflow()
    {
        // Arrange
        Directory.CreateDirectory(DirectoryPath);
        string orphan = Path.Combine(DirectoryPath, "cohesion-" + Guid.NewGuid().ToString("N") + ".ctmp");
        File.WriteAllBytes(orphan, [1]);

        // Act
        using var space = StorageTempSpace.CreateCore(DirectoryPath, null, () => 0, () => File.Exists(orphan) ? 40 : 400);
        using var unlimited = StorageTempSpace.CreateCore(Path.Combine(_root, "other"), null, () => 0, () => throw new OverflowException());

        // Assert
        space.EngineQuota.ShouldBe(100); unlimited.EngineQuota.ShouldBe(10L * 1024 * 1024 * 1024);
        Should.Throw<ArgumentException>(() => StorageTempSpace.Create(default, null, () => 0));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp security: Unix directory, lease and run are private and writable public directories refuse")]
    public void Create_UnixModes_ShouldProtectFilesAndRefuseWritableDirectory()
    {
        // Arrange
        if (OperatingSystem.IsWindows()) { return; }
        using var space = Create(); using var statement = space.CreateStatement(8192);
        using var run = statement.CreateRun(); run.Append([1]);

        // Act and assert
        File.GetUnixFileMode(DirectoryPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(RunFile(run).Name).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(Path.Combine(DirectoryPath, ".cohesion-temp.lock")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string unsafePath = Path.Combine(_root, "unsafe"); Directory.CreateDirectory(unsafePath);
        File.SetUnixFileMode(unsafePath, (UnixFileMode)511);
        using var refused = StorageTempSpace.Create(unsafePath, null, () => 0);
        using var refusedStatement = refused.CreateStatement(8192);
        Should.Throw<StorageTempException>(() => refusedStatement.CreateRun()).Code.ShouldBe("CK05005");
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp integrity: retry after damage cannot return buffered bytes")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryRead_ValidChecksumDamage_ShouldStayFailed(int damage)
    {
        // Arrange: checksum-valid ordinal, alien run, invalid used length and invalid record length.
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun(); run.Append(new byte[20000]); run.Seal();
        byte[] page = new byte[8192]; var file = RunFile(run);
        RandomAccess.Read(file.SafeFileHandle, page, 0).ShouldBe(8192);
        if (damage == 0) { BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(8), 1); }
        else if (damage == 1)
        {
            using var alien = statement.CreateRun(); alien.Append(new byte[20000]); alien.Seal();
            RandomAccess.Read(RunFile(alien).SafeFileHandle, page, 0).ShouldBe(8192);
        }
        else if (damage == 2) { BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(16), 9000); }
        else { BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(32), -1); }
        Stamp(page); RandomAccess.Write(file.SafeFileHandle, page, 0);
        using var reader = run.OpenReader(8192); byte[] row = new byte[20000];

        // Act and assert
        Should.Throw<StorageTempException>(() => reader.TryRead(row, out _)).Code.ShouldBe("CK05004");
        int reads = 0; space.BeforePageRead = () => reads++;
        Should.Throw<StorageTempException>(() => reader.TryRead(row, out _)).Code.ShouldBe("CK05004");
        reads.ShouldBe(0); run.Dispose(); statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp integrity: payload damage stays failed on retry")]
    public void TryRead_PayloadDamage_ShouldStayFailed()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(8192);
        using var run = statement.CreateRun(); run.Append(new byte[100]); run.Seal();
        RandomAccess.Write(RunFile(run).SafeFileHandle, new byte[] { 123 }, 64);
        using var reader = run.OpenReader(8192); byte[] row = new byte[100];

        // Act and assert
        Should.Throw<StorageTempException>(() => reader.TryRead(row, out _)).Code.ShouldBe("CK05004");
        Should.Throw<StorageTempException>(() => reader.TryRead(row, out _)).Code.ShouldBe("CK05004");
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp cancellation: before a record or between its pages permanently fails the reader")]
    [InlineData(false)]
    [InlineData(true)]
    public void TryRead_Cancellation_ShouldStayFailed(bool betweenPages)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun(); run.Append(new byte[20000]); run.Seal();
        using var reader = run.OpenReader(8192); using var cancellation = new CancellationTokenSource();
        int reads = 0;
        if (betweenPages) { space.BeforePageRead = () => { if (++reads == 2) { cancellation.Cancel(); } }; }
        else { cancellation.Cancel(); }
        byte[] row = new byte[20000];

        // Act
        Should.Throw<OperationCanceledException>(() => reader.TryRead(row, out _, cancellation.Token));
        int before = reads;
        Should.Throw<StorageTempException>(() => reader.TryRead(row, out _)).Code.ShouldBe("CK05004");

        // Assert
        reads.ShouldBe(before); reads.ShouldBe(betweenPages ? 2 : 0);
        statement.Dispose(); space.Bytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Temp IO: read and write failures keep a coded cause and release all accounting")]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_IoFailure_ShouldRetainCodedCause(bool reading)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var run = statement.CreateRun(); var cause = new IOException("injected ENOSPC or read failure");
        StorageTempException error;
        if (reading)
        {
            run.Append([1]); run.Seal(); space.BeforePageRead = () => throw cause;
            using var reader = run.OpenReader(8192);
            // Act
            error = Should.Throw<StorageTempException>(() => reader.TryRead(new byte[1], out _));
        }
        else
        {
            space.BeforePageWrite = () => throw cause;
            // Act
            error = Should.Throw<StorageTempException>(() => run.Append(new byte[20000]));
        }
        // Assert
        error.Code.ShouldBe("CK05005"); error.InnerException.ShouldBeSameAs(cause);
        statement.Dispose(); space.Bytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        statement.PeakBytes.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp concurrency: a blocked write does not stall another statement's reads, cancel, disposal or creation")]
    public async Task Append_BlockedWrite_ShouldAllowIndependentStatementProgress()
    {
        // Arrange
        using var space = Create(); using var first = space.CreateStatement(1024 * 1024);
        using var second = space.CreateStatement(1024 * 1024);
        using var a = first.CreateRun(); using var b = second.CreateRun(); b.Append([1]); b.Seal();
        using var blocked = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        space.BeforePageWrite = () => { blocked.Set(); release.Wait(timeout.Token); };
        var writer = Task.Factory.StartNew(() => a.Append(new byte[9000]), timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? progress = null;

        // Act
        try
        {
            blocked.Wait(timeout.Token);
            progress = Task.Factory.StartNew(() =>
            {
                using var reader = b.OpenReader(8192);
                reader.TryRead(new byte[1], out _).ShouldBeTrue();
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                Should.Throw<OperationCanceledException>(() => reader.TryRead(new byte[1], out _, canceled.Token));
                second.Dispose();
                using var other = space.CreateStatement(8192);
                using var created = other.CreateRun();
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await progress.WaitAsync(timeout.Token);
        }
        finally
        {
            release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(35));
            if (progress is not null) { await progress.WaitAsync(TimeSpan.FromSeconds(35)); }
            space.BeforePageWrite = null;
        }

        // Assert
        first.Dispose(); space.Bytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        Directory.GetFiles(DirectoryPath, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp concurrency: readers race run, statement and space disposal without leaks")]
    public async Task TryRead_DisposalRace_ShouldFinishAndReleaseAccounting()
    {
        // Arrange
        long reads = 0, disposed = 0, rounds = 0;
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        // Act: progress decides completion after a minimum exercise interval.
        do
        {
            using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
            using var run = statement.CreateRun();
            for (int i = 0; i < 100; i++) { run.Append(new byte[100]); }
            run.Seal();
            using var ready = new CountdownEvent(4); using var disposeNow = new ManualResetEventSlim();
            var actors = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
            {
                using var reader = run.OpenReader(8192); byte[] row = new byte[100];
                reader.TryRead(row, out _).ShouldBeTrue(); Interlocked.Increment(ref reads); ready.Signal();
                disposeNow.Wait(timeout.Token);
                try { while (reader.TryRead(row, out _)) { Interlocked.Increment(ref reads); } }
                catch (ObjectDisposedException) { Interlocked.Increment(ref disposed); }
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            ready.Wait(timeout.Token); disposeNow.Set();
            switch (rounds++ % 3) { case 0: run.Dispose(); break; case 1: statement.Dispose(); break; default: space.Dispose(); break; }
            await Task.WhenAll(actors).WaitAsync(timeout.Token);
            space.Dispose(); space.Bytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        } while (watch.Elapsed < TimeSpan.FromSeconds(1) || reads < 100 || disposed < 4 || rounds < 12);

        // Assert
        reads.ShouldBeGreaterThanOrEqualTo(100); disposed.ShouldBeGreaterThanOrEqualTo(4);
        Directory.GetFiles(DirectoryPath).ShouldBeEmpty();
        _output.WriteLine($"race rounds={rounds}; records read={reads}; disposed observations={disposed}");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp events: callbacks run outside accounting and damage has its own keyword and counter")]
    public void Events_KeywordAndLock_ShouldIsolateTempDiagnostics()
    {
        // Arrange
        using var events = new GateRecorder();
        using var checkpointOnly = new StorageEventRecorder(EventLevel.Verbose, StorageEventSource.Keywords.Checkpoints);
        using var space = Create(); events.Gate = space.Gate;
        using var statement = space.CreateStatement(8192); using var run = statement.CreateRun();
        long damage = StorageEventSource.Log.TempDamage;

        // Act
        run.Append([1]); run.Seal();
        RandomAccess.Write(RunFile(run).SafeFileHandle, new byte[] { 123 }, 64);
        using var reader = run.OpenReader(8192);
        Should.Throw<StorageTempException>(() => reader.TryRead(new byte[1], out _));
        run.Dispose();

        // Assert
        events.UnderGate.ShouldBe(0); events.TempEvents.ShouldBeGreaterThanOrEqualTo(3);
        (StorageEventSource.Log.TempDamage - damage).ShouldBe(1);
        checkpointOnly.Events.ShouldNotContain(e => e.EventId >= 21 && e.EventId <= 24);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp probe: one free-space syscall per 128 pages and none under accounting")]
    public void Append_ProbeWindow_ShouldAmortizeOutsideGate()
    {
        // Arrange
        StorageTempSpace? captured = null; int probes = 0, underGate = 0;
        using var space = StorageTempSpace.CreateCore(DirectoryPath, 16 * 1024 * 1024, () => 0, () =>
        {
            probes++;
            if (captured is not null && Monitor.IsEntered(captured.Gate)) { underGate++; }
            return long.MaxValue;
        });
        captured = space;
        using var statement = space.CreateStatement(16 * 1024 * 1024); using var run = statement.CreateRun();

        // Act
        run.Append(new byte[260 * StorageTempRun.PayloadSize - 4]); run.Seal();

        // Assert
        probes.ShouldBe(3); underGate.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        statement.PhysicalBytes.ShouldBe(260 * 8192); statement.PeakBytes.ShouldBe(statement.PhysicalBytes);
        run.Dispose(); space.Bytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    private static void Stamp(Span<byte> page)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(page[28..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[28..], Crc32C.Compute(page));
    }
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_file")]
    private static extern ref FileStream RunFile(StorageTempRun run);

    private sealed class GateRecorder : EventListener
    {
        public object? Gate { get; set; }
        public int UnderGate { get; private set; }
        public int TempEvents { get; private set; }
        public GateRecorder() { EnableEvents(StorageEventSource.Log, EventLevel.Verbose); }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId is >= 21 and <= 24)
            {
                TempEvents++;
                if (Gate is { } gate && Monitor.IsEntered(gate)) { UnderGate++; }
            }
        }
    }
}
