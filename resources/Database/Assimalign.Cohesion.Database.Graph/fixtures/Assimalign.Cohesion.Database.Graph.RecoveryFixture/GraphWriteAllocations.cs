using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.RecoveryFixture;

internal static class GraphWriteAllocations
{
    internal static long[] Measure(int injectedKind, int injectedBytes = 1024)
    {
        var engine = GraphDatabaseEngine.Create("allocation", new()
        {
            CheckpointInterval = TimeSpan.FromHours(1), CheckpointJournalSize = 0,
            PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = engine.CreateDatabaseAsync("graph", CancellationToken.None).GetAwaiter().GetResult();
        var session = database.CreateSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var schema = GraphSchema.Open(database, session);
            schema.SaveLabelAsync(new(new Guid("00000000-0000-0000-0000-000000000001"), "Counter"), CancellationToken.None).GetAwaiter().GetResult();
            schema.SaveLabelAsync(new(new Guid("00000000-0000-0000-0000-000000000002"), "Temporary"), CancellationToken.None).GetAwaiter().GetResult();
            session.ExecuteAsync("CREATE (:Counter {c:0})", cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            string[] queries = ["CREATE (:Temporary {id:1})", "MATCH (n:Counter) SET n.c=n.c+1", "MATCH (n:Temporary) DELETE n"];
            var minima = new[] { long.MaxValue, long.MaxValue, long.MaxValue };
            for (int warmup = 0; warmup < 100; warmup++) { Round(false); }
            for (int round = 0; round < 5; round++) { Round(true); }
            return minima;

            void Round(bool measure)
            {
                for (int i = 0; i < (measure ? 100 : 1); i++)
                {
                    for (int kind = 0; kind < queries.Length; kind++)
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        if (kind == injectedKind) { GC.KeepAlive(new byte[injectedBytes]); }
                        var pending = session.ExecuteAsync(queries[kind], cancellationToken: CancellationToken.None);
                        if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Allocation fixture changed threads."); }
                        var result = pending.GetAwaiter().GetResult();
                        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                        if (result.AffectedCount != 1) { throw new InvalidOperationException("Allocation fixture expected one affected entity."); }
                        if (measure) { minima[kind] = Math.Min(minima[kind], bytes); }
                    }
                }
            }
        }
        finally
        {
            session.DisposeAsync().GetAwaiter().GetResult();
            engine.DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
