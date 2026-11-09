using System;
using System.Threading;

using Assimalign.Cohesion.Database.Documents;

namespace Assimalign.Cohesion.Database.Documents.RecoveryFixture;

internal static class DocumentWriteAllocations
{
    internal static void Run(int injectedBytes, bool indexed)
    {
        var options = new DocumentDatabaseEngineOptions
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1) };
        var engine = DocumentDatabaseEngine.Create("write-allocations", options);
        var database = engine.CreateDatabaseAsync("test", CancellationToken.None).GetAwaiter().GetResult();
        var session = database.CreateSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var collection = session.CreateCollectionAsync("items", CancellationToken.None).GetAwaiter().GetResult();
            if (indexed) { session.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: CancellationToken.None).GetAwaiter().GetResult(); }
            byte[] content = "{\"v\":1}"u8.ToArray();
            long put = long.MaxValue, delete = long.MaxValue;
            for (int round = 0; round < 6; round++)
            {
                for (int i = 0; i < 100; i++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    if (injectedBytes != 0) { GC.KeepAlive(new byte[injectedBytes]); }
                    var pendingPut = collection.PutAsync(session, "one", content, cancellationToken: CancellationToken.None);
                    if (!pendingPut.IsCompletedSuccessfully) { throw new InvalidOperationException("PUT allocation measurement changed threads."); }
                    pendingPut.GetAwaiter().GetResult();
                    long putBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    before = GC.GetAllocatedBytesForCurrentThread();
                    var pendingDelete = collection.DeleteAsync(session, "one", cancellationToken: CancellationToken.None);
                    if (!pendingDelete.IsCompletedSuccessfully) { throw new InvalidOperationException("DELETE allocation measurement changed threads."); }
                    pendingDelete.GetAwaiter().GetResult();
                    long deleteBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (round != 0) { put = Math.Min(put, putBytes); delete = Math.Min(delete, deleteBytes); }
                }
            }
            Console.WriteLine($"allocations={put},{delete}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        }
        finally { session.DisposeAsync().GetAwaiter().GetResult(); engine.DisposeAsync().GetAwaiter().GetResult(); }
    }
}
