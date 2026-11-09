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
            ];
            // b5602544d, .NET 10.0.13, ARM64 and x64; exact bytes per 1,000 executions.
            long[] originalBytes=[19104000,19024000,20416000,20304000,28512000,30824000,21968000];
            long[] originalPeaks=[1464,1536,2160,2360,3232,4056,2856];
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
}
#endif