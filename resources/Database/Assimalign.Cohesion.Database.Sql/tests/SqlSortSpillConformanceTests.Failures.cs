using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32.SafeHandles;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Spill close errors: every owner releases files; session close still joins COPY (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DisposeAsync_TempCloseFails_ShouldReleaseAllOwners(int owner)
    {
        // Arrange
        await using var engine = await Create("close-fault", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await Seed(session);
        var result = (SqlSpilledResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k");
        var run = Runs(Sorted(result)!)[0];
        RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle);
        using var output = new SqlCopyGateStream();
        Task<long>? copying = owner == 1 ? session.CopyToAsync("COPY t TO STDOUT", output).AsTask() : null;
        if (copying is not null) { await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); }

        // Act
        Exception? error;
        try { error = await Record.ExceptionAsync(async () =>
            { if (owner == 0) { await result.DisposeAsync(); } else if (owner == 1) { await session.DisposeAsync(); } else { await engine.DisposeAsync(); } }); }
        finally { output.Release.TrySetResult(); }

        // Assert
        error.ShouldNotBeNull(); ContainsTempFailure(error).ShouldBeTrue();
        if (copying is not null) { await Should.ThrowAsync<OperationCanceledException>(async () => await copying); }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        await result.DisposeAsync();

        static bool ContainsTempFailure(Exception exception) => exception is SqlExecutionException sql
            && sql.Diagnostics[0].Code == "CK05005" || exception is AggregateException aggregate
            && aggregate.InnerExceptions.Any(ContainsTempFailure) || exception.InnerException is { } inner && ContainsTempFailure(inner);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill publication: owner close before result attachment releases every reservation (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OwnerClosesAtComplete_ShouldReleaseBeforeRefusing(bool engineClose)
    {
        // Arrange
        await using var engine = await Create("close-publish", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        bool exercised = false;
        Task closing = Task.CompletedTask;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "complete") { return; }
            exercised = true;
            closing = engineClose ? engine.DisposeAsync().AsTask() : session.DisposeAsync().AsTask();
        };
        try
        {
            // Act / Assert
            await Should.ThrowAsync<ObjectDisposedException>(async () => await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k"));
            await closing;
            exercised.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill errors: CK05 reaches the client with a location and releases every file (#1789)")]
    [InlineData("CK05001")]
    [InlineData("CK05002")]
    [InlineData("CK05003")]
    [InlineData("CK05005")]
    [InlineData("CK05006")]
    public async Task ExecuteAsync_TempRefusal_ShouldLocateAndKeepSessionUsable(string code)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("temp-failure");
        builder.Options.TempDirectory = Path.Combine(_root, ".temp");
        builder.Options.StatementMemoryBudget = 262144;
        if (code == "CK05001") { builder.Options.TempSpaceQuota = 0; }
        if (code == "CK05002") { builder.Options.StatementTempSpaceQuota = 0; }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        var space = engine.TempSpace!;
        if (code == "CK05003") { Available(space) = () => 0; ProbeTick(space) = 0; }
        if (code == "CK05005") { Directory.CreateDirectory(_root); File.WriteAllText(space.DirectoryPath, "unavailable"); }
        if (code == "CK05006") { BeforeCreate(space, () => throw TempException(code, "injected oversized sort record")); }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k"));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        error.Diagnostics[0].Start.ShouldNotBeNull(); error.Diagnostics[0].End.ShouldNotBeNull();
        TempBytes(space).ShouldBe(0); Files(engine).ShouldBe(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill cancellation: every sorter boundary and delivery releases files and reservations (#1789)")]
    [InlineData("handoff")]
    [InlineData("drained")]
    [InlineData("arena")]
    [InlineData("append")]
    [InlineData("complete")]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("delivery")]
    public async Task ExecuteAsync_CancelAtEachBoundary_ShouldUnwind(string point)
    {
        // Arrange
        await using var engine = await Create("cancel", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        using var cancellation = new CancellationTokenSource();
        bool exercised = false;
        void Cancel() { exercised = true; cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        SqlRowCollector.ObserveSpill = name => { if (name == point) { Cancel(); } };
        if (point == "create") { BeforeCreate(engine.TempSpace!, Cancel); }
        if (point == "write") { BeforeWrite(engine.TempSpace!, Cancel); }
        if (point == "read") { BeforeRead(engine.TempSpace!, Cancel); }
        try
        {
            // Act / Assert
            await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k", cancellationToken: cancellation.Token);
                if (point == "delivery") { BeforeRead(engine.TempSpace!, Cancel); }
                await Rows(result, cancellation.Token);
            });
            exercised.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; BeforeCreate(engine.TempSpace!, null); BeforeWrite(engine.TempSpace!, null); BeforeRead(engine.TempSpace!, null); }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill damage: a run changed before delivery raises located CK05004 and releases its owner (#1789)")]
    public async Task GetRowsAsync_DamagedRun_ShouldReportIntegrityAndRelease()
    {
        // Arrange
        await using var engine = await Create("damage", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        await using var result = (SqlSpilledResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k");
        var run = Runs(Sorted(result)!)[0];
        RandomAccess.Write(Handle(run), new byte[] { 255 }, 0);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await Rows(result));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK05004"); failure.Diagnostics[0].Start.ShouldNotBeNull();
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill codecs: record bound and incompatible key families keep CS05067 before and after handoff (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_UnencodableRow_ShouldRefuseAndDelete(bool afterHandoff)
    {
        // Arrange
        await using var engine = await Create("encode-refusal", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var order = new[] { new SqlBoundOrdering(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Types.Collation.Binary)) };
        foreach (bool oversized in new[] { false, true })
        {
            var metrics = new SqlStatementMetrics();
            using var rows = new SqlRowCollector(order, null, null, null, CancellationToken.None, metrics, 262144);
            int input = afterHandoff ? 3000 : 3;
            for (int i = 0; i < input; i++) { rows.Add([i], [i], session); }
            // Act / Assert
            var failure = Should.Throw<DatabaseException>(() =>
            {
                rows.Add(oversized ? [new string('x', 7000)] : [1], oversized ? [1] : ["second-family"], session);
                for (int i = 0; i < 3000; i++) { rows.Add([i], [i], session); }
            });
            failure.Diagnostics[0].Code.ShouldBe("CS05067");
            rows.Dispose(); metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_availableBytes")]
    private static extern ref Func<long> Available(StorageTempSpace space);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_probeTick")]
    private static extern ref long ProbeTick(StorageTempSpace space);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BeforeFileCreate")]
    private static extern void BeforeCreate(StorageTempSpace space, Action? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BeforePageWrite")]
    private static extern void BeforeWrite(StorageTempSpace space, Action? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BeforePageRead")]
    private static extern void BeforeRead(StorageTempSpace space, Action? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern StorageTempException TempException(string code, string message);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sorted")]
    private static extern ref StorageTempSortReader? Sorted(SqlSpilledResultSet result);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runs")]
    private static extern ref StorageTempRun[] Runs(StorageTempSortReader reader);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_handle")]
    private static extern ref SafeFileHandle Handle(StorageTempRun run);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_file")]
    private static extern ref FileStream RunFile(StorageTempRun run);
    private sealed class FailedCloseStream : FileStream
    {
        internal FailedCloseStream(SafeFileHandle handle) : base(handle, FileAccess.ReadWrite) { }
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); if (disposing) { throw new IOException("injected temporary close failure"); } }
    }
}
