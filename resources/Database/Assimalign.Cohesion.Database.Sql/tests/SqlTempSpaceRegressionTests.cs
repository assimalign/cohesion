using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlTempSpaceRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-sql-temp-fixes-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Temp availability: invalid optional storage warns while databases stay open")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_UnavailableTempDirectory_ShouldKeepDatabasesOnline(bool heldLease)
    {
        // Arrange
        string temp = Path.Combine(_root, ".cohesion-temp"); Directory.CreateDirectory(_root);
        FileStream? lease = null;
        if (heldLease)
        {
            Directory.CreateDirectory(temp);
            lease = new FileStream(Path.Combine(temp, ".cohesion-temp.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        else { File.WriteAllBytes(temp, [1]); }
        using var events = new TempEvents();
        var options = new SqlDatabaseEngineOptions { RootPath = _root, CheckpointJournalSize = 65536 };
        try
        {
            // Act
            await using (var seed = SqlDatabaseEngine.Create("seed", options))
            { await seed.CreateDatabaseAsync("online", CancellationToken.None); }
            await using var engine = SqlDatabaseEngine.Create("reopen", options);
            var database = await engine.OpenDatabaseAsync("online", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE t(a INT)", cancellationToken: CancellationToken.None);
            using var statement = engine.TempSpace!.CreateStatement(8192);
            var error = Should.Throw<StorageTempException>(() => statement.CreateRun());

            // Assert
            error.Code.ShouldBe("CK05005"); error.InnerException.ShouldNotBeNull();
            error.Message.ShouldContain("TempDirectory", Case.Sensitive);
            events.Warnings.Count.ShouldBeGreaterThanOrEqualTo(2);
            engine.OfflineDatabases.ShouldBeEmpty();
        }
        finally { lease?.Dispose(); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Temp lease: two engines share a root until the second spills, then recovery retries")]
    public async Task Create_TwoEngines_ShouldRefuseOnlyTheSecondSpill()
    {
        // Arrange
        var options = new SqlDatabaseEngineOptions { RootPath = _root, CheckpointJournalSize = 65536 };
        await using var first = SqlDatabaseEngine.Create("first", options);
        await using var second = SqlDatabaseEngine.Create("second", options);
        await first.CreateDatabaseAsync("a", CancellationToken.None);
        await second.CreateDatabaseAsync("b", CancellationToken.None);
        using var firstStatement = first.TempSpace!.CreateStatement(8192);
        using var firstRun = firstStatement.CreateRun(); firstRun.Append([1]);
        using var secondStatement = second.TempSpace!.CreateStatement(8192);

        // Act
        var error = Should.Throw<StorageTempException>(() => secondStatement.CreateRun());
        await first.DisposeAsync();
        using var retry = secondStatement.CreateRun(); retry.Append([2]); retry.Seal();

        // Assert
        error.Code.ShouldBe("CK05005"); error.Message.ShouldContain("TempDirectory", Case.Sensitive);
        second.OfflineDatabases.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Temp options: database-named segments and default paths name engine and option")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Create_BadTempOptions_ShouldRefuseWithEngineAndOption(int option)
    {
        // Arrange
        (string Option, SqlDatabaseEngineOptions Options)[] cases =
        [
            ("TempDirectory", new() { RootPath = _root, TempDirectory = Path.Combine(_root, "sales", "spill") }),
            ("TempDirectory", new() { RootPath = _root, TempDirectory = Path.Combine(_root, "sales") }),
            ("TempDirectory", new() { TempDirectory = default(FileSystemPath) }),
            ("RootPath", new() { RootPath = default(FileSystemPath) }),
            ("TempSpaceQuota", new() { TempSpaceQuota = -1 }),
            ("StatementTempSpaceQuota", new() { StatementTempSpaceQuota = -1 })
        ];

        // Act and assert
        var entry = cases[option];
        var error = Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("option-owner", entry.Options));
        error.ParamName.ShouldBe(entry.Option); error.Message.ShouldContain("SQL engine 'option-owner'", Case.Sensitive);
        error.Message.ShouldContain(entry.Option, Case.Sensitive);
    }

    [LinkFact(DisplayName = "Cohesion Test [Database.Sql] - Temp options: a link component below the root refuses before startup")]
    public void Create_LinkedTempComponent_ShouldNameEngineAndOption()
    {
        // Arrange
        Directory.CreateDirectory(_root);
        string target = Path.Combine(_root, ".target"); Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(_root, ".link"), target);

        // Act
        var error = Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("linked", new()
        { RootPath = _root, TempDirectory = Path.Combine(_root, ".link", "spill") }));

        // Assert
        error.ParamName.ShouldBe("TempDirectory"); error.Message.ShouldContain("SQL engine 'linked'", Case.Sensitive);
        Directory.GetFiles(target).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Temp floor: memory engines reserve only one MiB and file defaults retain the conservative reserve")]
    public async Task Create_FloorByBacking_ShouldSeparateMemoryAndFileReserves()
    {
        // Arrange
        await using var memory = SqlDatabaseEngine.Create("memory", new() { TempDirectory = Path.Combine(_root, "memory") });
        await using var file = SqlDatabaseEngine.Create("file", new() { RootPath = Path.Combine(_root, "file") });

        // Act
        long memoryFloor = Floor(memory), fileFloor = Floor(file);

        // Assert
        memoryFloor.ShouldBe(1024 * 1024);
        fileFloor.ShouldBeGreaterThan(254L * 1024 * 1024 * 1024 / 100);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Temp lease: disabled Unix locking warns, opens databases and codes the later spill refusal")]
    public async Task Create_DisabledUnixLocking_ShouldKeepEngineOnline()
    {
        // Arrange: runtime switch is cached, so use the already-built real model fixture.
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FormatProbeFixture", "Assimalign.Cohesion.Database.Storage.FormatProbeFixture.dll"));
        start.ArgumentList.Add("temp-lease-probe"); start.ArgumentList.Add(_root);
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
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
        if (OperatingSystem.IsWindows()) { (await output).ShouldContain("TEMP_LEASE_OK online", Case.Sensitive); }
        else
        {
            (await output).ShouldContain("TEMP_LEASE_REFUSED CK05005 online", Case.Sensitive);
            (await output).ShouldContain("exclusive file lease", Case.Sensitive);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TempFreeSpaceFloor")]
    private static extern long Floor(SqlDatabaseEngine engine);
    private sealed class TempEvents : EventListener
    {
        public ConcurrentQueue<EventWrittenEventArgs> Warnings { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Assimalign.Cohesion.Database.Storage") { EnableEvents(source, EventLevel.Warning); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        { if (data.EventId == 23) { Warnings.Enqueue(data); } }
    }
}
