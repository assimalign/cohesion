using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping.AotGuard;

internal static class Program
{
    private static async Task Main()
    {
        var mapper = new GuardEntityMapper();
        var entity = mapper.Read(new object?[] { 1, "before", new byte[] { 1, 2 } });
        var target = new object?[3];
        mapper.Write(entity, target);
        Require((int)target[0]! == 1 && (string)target[1]! == "before"
            && ((byte[])target[2]!).AsSpan().SequenceEqual(new byte[] { 1, 2 }), "Generated round-trip failed.");
        Require(mapper.GetKey(entity) == 1, "Generated key handling failed.");

        var snapshot = mapper.Capture(entity);
        entity.Payload[0] = 9;
        Require(!mapper.AreEqual(snapshot, mapper.Capture(entity)), "In-place binary change was not detected.");
        entity.Payload[0] = 1;
        Require(mapper.AreEqual(snapshot, mapper.Capture(entity)), "Equal binary contents were not recognized.");

        var store = new GuardStore();
        store.Rows[1] = snapshot;
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(mapper, new GuardWriter());
        entities.Attach(entity);
        var duplicate = mapper.Read(new object?[] { 1, "remote", new byte[] { 7 } });
        Require(ReferenceEquals(entity, entities.Attach(duplicate)), "Tracked identity was not preserved.");

        entity.Name = "after";
        entity.Payload[1] = 8;
        var added = new GuardEntity { Id = 2, Name = "added", Payload = [3, 4] };
        entities.Add(added);
        Require(await work.SaveChangesAsync(CancellationToken.None) == 2, "Initial save did not persist both changes.");
        Require(store.Rows[1].Name == "after" && store.Rows[1].Payload.AsSpan().SequenceEqual(new byte[] { 1, 8 }),
            "Generated snapshots did not persist scalar and binary values.");
        Require(await work.SaveChangesAsync(CancellationToken.None) == 0, "Successful save did not accept snapshots.");

        store.FailSecondWrite = true;
        entity.Name = "retry-first";
        added.Name = "retry-second";
        bool failed = false;
        try
        {
            await work.SaveChangesAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception) when (exception.Message == "Injected atomicity failure.")
        {
            failed = true;
        }

        Require(failed && store.Rollbacks == 1, "Failed save did not roll back.");
        Require(store.Rows[1].Name == "after" && store.Rows[2].Name == "added", "Failed save leaked partial writes.");
        store.FailSecondWrite = false;
        Require(await work.SaveChangesAsync(CancellationToken.None) == 2, "Failed save lost retryable changes.");
        Require(store.Rows[1].Name == "retry-first" && store.Rows[2].Name == "retry-second", "Retry did not persist changes.");
        entities.Remove(added);
        Require(await work.SaveChangesAsync(CancellationToken.None) == 1 && !store.Rows.ContainsKey(2)
            && entities.Find(2) is null, "Delete did not release persisted and tracked identity.");

        Console.WriteLine("Mapping NativeAOT guard passed: round-trip, identity, scalar/binary tracking, atomic rollback, retry and delete.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
