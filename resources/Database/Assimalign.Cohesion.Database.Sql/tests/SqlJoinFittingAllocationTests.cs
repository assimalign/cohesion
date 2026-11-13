#if !DEBUG
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
namespace Assimalign.Cohesion.Database.Sql.Tests;
[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlJoinFittingAllocationTests
{
    private readonly ITestOutputHelper _output;
    public SqlJoinFittingAllocationTests(ITestOutputHelper output) { _output = output; }
    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting joins preserve exact base allocations and charges with every temp permission (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_FittingJoinPaths_ShouldKeepBaseAllocations(int configuration)
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "sql-join-alloc-"+Guid.NewGuid().ToString("N"));
        var builder=SqlDatabaseEngine.CreateBuilder("join-fitting");
        if(configuration==1) { builder.Options.TempDirectory=directory; }
        if(configuration==2) { builder.Options.RootPath=directory; }
        builder.Functions.Add(SqlScalarFunction.Create("identity_value",(long value)=>value));
        builder.Functions.Add(SqlScalarFunction.Create("volatile_value",(long value)=>value));
        try
        {
            await using var engine=await builder.BuildAsync(CancellationToken.None);
            await using var session=await (await engine.CreateDatabaseAsync("db",CancellationToken.None)).CreateSessionAsync(CancellationToken.None);
            foreach(string table in new[]{"a","b","c"})
            {
                await session.ExecuteAsync($"CREATE TABLE {table}(k INT,v INT)",cancellationToken:CancellationToken.None);
                await session.ExecuteAsync($"INSERT INTO {table} VALUES(1,2),(1,3),(NULL,4),(2,5)",cancellationToken:CancellationToken.None);
            }
            await using var transaction=await session.BeginTransactionAsync(IsolationLevel.ReadCommitted,CancellationToken.None);
            string[] sql=[
                "SELECT a.k,b.k FROM a INNER JOIN b ON a.k=b.k",
                "SELECT a.k,b.k FROM a LEFT JOIN b ON a.k=b.k",
                "SELECT a.k,b.k FROM a RIGHT JOIN b ON a.k=b.k",
                "SELECT a.k,b.k FROM a FULL JOIN b ON a.k=b.k",
                "SELECT a.k,b.k,c.k FROM a LEFT JOIN b ON a.k=b.k LEFT JOIN c ON b.k=c.k",
                "SELECT a.k,b.k,c.k FROM a FULL JOIN b ON a.k=b.k LEFT JOIN c ON b.k=c.k",
                "SELECT identity_value(a.k),b.k FROM a FULL JOIN b ON a.k=b.k",
                "SELECT a.k,b.k FROM a RIGHT JOIN b ON a.k=b.k WHERE a.v+b.v>0",
                "SELECT a.k,b.k FROM a FULL JOIN b ON a.k=b.k WHERE a.v+b.v>0",
                "SELECT a.k,b.k FROM a LEFT JOIN b ON a.k=b.k ORDER BY b.v",
                "SELECT a.k,b.k FROM a LEFT JOIN b ON a.k=b.k LIMIT 3",
                "SELECT DISTINCT a.k,b.k FROM a LEFT JOIN b ON a.k=b.k",
                "SELECT a.k,COUNT(b.k) FROM a LEFT JOIN b ON a.k=b.k GROUP BY a.k",
                "SELECT volatile_value(a.k),b.k FROM a LEFT JOIN b ON a.k=b.k ORDER BY b.v",
                "SELECT a.k,d.k FROM a LEFT JOIN (SELECT k,v FROM b) d ON a.k=d.k",
            ];
            // b5602544d, .NET 10.0.13, ARM64 and x64; exact bytes per 1,000 executions.
            long[] originalBytes=[19104000,19024000,20416000,20304000,28512000,30824000,21968000,23104000,22952000,20288000,19280000,19360000,25272000,22336000,22888000];
            long[] originalPeaks=[1464,1536,2160,2360,3232,4056,2856,2088,2216,1888,1192,1568,1808,4352,2048];
            for(int index=0;index<sql.Length;index++)
            {
                var request=SqlQueryRequest.FromSql(sql[index]); Measure(256);
                // Act
                long minimum=long.MaxValue;
                for(int round=0;round<3;round++) { minimum=Math.Min(minimum,Measure(1000)); }
                // Assert
                _output.WriteLine($"JOIN_FITTING_ALLOC|config={configuration}|index={index}|bytes={minimum}|peak={session.LastStatementMetrics!.PeakHashMemoryBytes}|sql={sql[index]}");
                minimum.ShouldBe(originalBytes[index]);
                session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(originalPeaks[index]);
                session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
                session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
                long Measure(int count)
                {
                    long before=GC.GetAllocatedBytesForCurrentThread();
                    for(int i=0;i<count;i++)
                    {
                        var pending=session.ExecuteAsync(request,CancellationToken.None);
                        pending.IsCompletedSuccessfully.ShouldBeTrue();
                        var disposal=((DatabaseResultSet)pending.Result).DisposeAsync();
                        disposal.IsCompletedSuccessfully.ShouldBeTrue(); disposal.GetAwaiter().GetResult();
                    }
                    return GC.GetAllocatedBytesForCurrentThread()-before;
                }
            }
        }
        finally { if(Directory.Exists(directory)) { Directory.Delete(directory,true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Suspending join root keeps exact base allocation before lock completion (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FittingJoinWait_ShouldKeepBaseAllocation(bool temp)
    {
        // Arrange: count on the initiating thread through the forced suspension. The
        // later lock release and continuation run outside this allocation interval.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string directory = Path.Combine(Path.GetTempPath(), "sql-join-wait-" + Guid.NewGuid().ToString("N"));
        var builder = SqlDatabaseEngine.CreateBuilder("fitting-wait");
        if (temp) { builder.Options.TempDirectory = directory; }
        builder.Options.MaintenanceInterval = TimeSpan.FromHours(1);
        builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        try
        {
            await using var engine = await builder.BuildAsync(guard.Token);
            var database = await engine.CreateDatabaseAsync("db", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE a(k INT)", cancellationToken: guard.Token);
            await session.ExecuteAsync("CREATE TABLE b(k INT)", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO a VALUES(1)", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO b VALUES(1)", cancellationToken: guard.Token);
            database.Catalog.TryGetTable("dbo", "a", out var table).ShouldBeTrue();
            var request = SqlQueryRequest.FromSql("SELECT a.k,b.k FROM a FULL JOIN b ON a.k=b.k");
            long minimum = long.MaxValue;
            for (int round = 0; round < 4; round++)
            {
                long bytes = 0;
                for (int i = 0; i < 100; i++)
                {
                    var holder = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
                    await database.Coordinator.LockManager.AcquireAsync(holder.Sequence, LockResource.Object(table.ObjectId), LockMode.Exclusive, guard.Token);
                    // Act
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var pending = session.ExecuteAsync(request, guard.Token);
                    bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    pending.IsCompleted.ShouldBeFalse();
                    await database.Coordinator.RollbackAsync(holder, guard.Token);
                    await using var result = (DatabaseResultSet)await pending;
                }
                if (round != 0) { minimum = Math.Min(minimum, bytes); }
            }
            // Assert: pinned after measurement in the b5602544d temporary copy.
            _output.WriteLine($"JOIN_WAIT_ALLOC|temp={temp}|bytes={minimum}|peak={session.LastStatementMetrics!.PeakHashMemoryBytes}");
            minimum.ShouldBe(temp ? 1320800 : 1316800);
            session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBe(824);
            session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
}
#endif
