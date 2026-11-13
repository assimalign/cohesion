using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join spill records reject codec-valid metadata and key type damage (#1789)")]
    [InlineData(40, DatabaseType.Float64)] // build ordinal
    [InlineData(44, DatabaseType.Int8)] // NULL flag
    [InlineData(45, DatabaseType.Date)] // padding/width
    [InlineData(55, DatabaseType.Float64)] // numeric key/hash disagreement
    [InlineData(55, DatabaseType.DateTime)] // same-width key from another family
    public async Task ExecuteAsync_HashJoinDamagedRecord_ShouldLocateAndRelease(int offset, DatabaseType tag)
    {
        // Arrange
        await using var engine = await Create("join-damage", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedHashJoin(session);
        bool armed = false, injected = false;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "join-partitioned") { armed = true; } };
        BeforeRead(engine.TempSpace!, () =>
        {
            if (!armed || injected) { return; }
            // FULL's unmatched scan visits the damaged record even when no probe
            // requests its indexed chunk. The checksum remains valid.
            foreach (var run in StatementRuns(engine.TempSpace!).Where(r => r.IsSealed && r.RecordCount > 1))
            {
                var page = new byte[8192]; RandomAccess.Read(Handle(run),page,0); page[offset] = (byte)tag;
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28),0);
                uint crc = uint.MaxValue;
                foreach (byte value in page)
                { crc ^= value; for (int bit=0; bit<8; bit++) { crc = (crc>>1)^((crc&1)==0?0:0x82f63b78u); } }
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28),~crc);
                RandomAccess.Write(Handle(run),page,0); injected = true;
            }
        });
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("SELECT l.id FROM jl l FULL JOIN jr r ON l.id=r.id LIMIT 1"));
            // Assert
            injected.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CK05004");
            failure.Diagnostics[0].Start.ShouldNotBeNull(); failure.Diagnostics[0].End.ShouldNotBeNull();
            TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { BeforeRead(engine.TempSpace!,null); SqlPlanExecutor.ObserveHashSpill = null; }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash join crash images preserve exact durable rows and catalog at every new spill boundary (#1789)")]
    public async Task Reopen_HashJoinBoundaries_ShouldKeepDurableState()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var images = new Dictionary<string,CrashCaptureSqlStorageStrategy>();
        RowValues[] left, right;
        var builder = SqlDatabaseEngine.CreateBuilder("join-crash");
        builder.Options.StorageStrategy = storage; builder.Options.StatementMemoryBudget = 262144;
        builder.Options.TempDirectory = Path.Combine(_root,".join-crash"); builder.Options.StatementTempSpaceQuota = 67108864;
        await using (var engine = await builder.BuildAsync())
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(); await SeedHashJoin(session);
            await using (var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,payload FROM jl ORDER BY id")) { left=await Rows(rows); }
            await using (var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,payload FROM jr ORDER BY id")) { right=await Rows(rows); }
            database.Coordinator.Checkpoint(CancellationToken.None); database.CatalogStorage.Checkpoint();
            SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
            {
                if (phase is "join-input" or "join-partitioned" or "join-ordered" && !images.ContainsKey(phase))
                { images.Add(phase,storage.CaptureDurableImages()); }
            };
            try
            {
                using var recorder = storage.RecordCrashPoints();
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT l.id,r.id FROM jl l FULL JOIN jr r ON l.id=r.id LIMIT 1");
                recorder.Count.ShouldBe(1); images.Count.ShouldBe(3);
            }
            finally { SqlPlanExecutor.ObserveHashSpill = null; }
            TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);
        }
        // Act / Assert
        foreach (var image in images.Values)
        {
            await using var reopened = SqlDatabaseEngine.Create("join-reopened",new(){StorageStrategy=image});
            await using var session = await (await reopened.OpenDatabaseAsync("db")).CreateSessionAsync();
            await using var a = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,payload FROM jl ORDER BY id");
            await using var b = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,payload FROM jr ORDER BY id");
            (await Rows(a)).ShouldBe(left); (await Rows(b)).ShouldBe(right);
            reopened.OfflineDatabases.ShouldBeEmpty();
        }
    }
}
