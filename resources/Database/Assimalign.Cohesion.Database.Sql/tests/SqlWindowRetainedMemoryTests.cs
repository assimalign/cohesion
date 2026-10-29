using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlWindowRetainedMemoryTests
{
    private readonly ITestOutputHelper _output;
    public SqlWindowRetainedMemoryTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window memory: identity accounting, default-budget limits and partition releases are deterministic")]
    public async Task Execute_RetainedWindowObjects_ShouldReportBudgetBoundaryAsync()
    {
        // Arrange
        await using var engine=SqlDatabaseEngine.Create("window-memory-limit",new());
        var db=await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session=await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT,p INT,k INT)");
        const int maximum=262144;
        for(int first=0;first<maximum;first+=4096)
        {
            await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES "+string.Join(",",Enumerable.Range(first,4096).Select(i=>$"({i},{i%8},{i%1000})")));
        }
        string[] queries=[
            "SELECT ROW_NUMBER() OVER(PARTITION BY p ORDER BY k,id),RANK() OVER(PARTITION BY p ORDER BY k) FROM t WHERE id<{0}",
            "SELECT AVG(k) OVER(ORDER BY id ROWS 99 PRECEDING) FROM t WHERE id<{0}",
        ];
        // Act / Assert
        foreach(string query in queries)
        {
            await RunAsync(10000);
            long sample=session.LastStatementMetrics!.PeakHashMemoryBytes;
            int admitted=0,refused=maximum;
            (await RunAsync(refused)).ShouldBeFalse();
            while(refused-admitted>1)
            {
                int middle=admitted+(refused-admitted)/2;
                if(await RunAsync(middle)) { admitted=middle; } else { refused=middle; }
            }
            (await RunAsync(admitted)).ShouldBeTrue();
            long peak=session.LastStatementMetrics!.PeakHashMemoryBytes;
            (await RunAsync(refused)).ShouldBeFalse();
            _output.WriteLine($"Query: {query}; 10,000-row peak={sample} ({sample/10000d:F4} B/row); default grant row limit={admitted}; boundary peak={peak} ({peak/(double)admitted:F4} B/row); next row={refused} refused.");
            admitted.ShouldBeGreaterThan(74234);

            async Task<bool> RunAsync(int count)
            {
                bool success=true;
                try
                {
                    await using var result = (await session.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture,query,count), cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
                }
                catch(DatabaseException failure) { failure.Diagnostics.ShouldContain(d=>d.Code=="CS05079"); success=false; }
                session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
                SqlWindowMemory.Current.ShouldBeNull();
                return success;
            }
        }
        await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT AVG(k) OVER(ORDER BY id ROWS 99 PRECEDING) FROM t WHERE id<10000 LIMIT 1");
        long whole=session.LastStatementMetrics!.PeakHashMemoryBytes;
        await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT AVG(k) OVER(PARTITION BY p ORDER BY id ROWS 99 PRECEDING) FROM t WHERE id<10000 LIMIT 1");
        long partitioned=session.LastStatementMetrics!.PeakHashMemoryBytes;
        _output.WriteLine($"Moving cache and state peak: one partition={whole}; eight partitions={partitioned}.");
        partitioned.ShouldBeLessThan(whole);
    }
}
