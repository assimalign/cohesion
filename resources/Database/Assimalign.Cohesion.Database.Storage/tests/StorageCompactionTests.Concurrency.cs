using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed partial class StorageCompactionTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Compaction: unlatched readers wait through an automatic page rewrite (#1161)")]
    [InlineData(false, "direct")]
    [InlineData(false, "try")]
    [InlineData(false, "scan")]
    [InlineData(true, "direct")]
    [InlineData(true, "try")]
    [InlineData(true, "scan")]
    public async Task Write_AutomaticCompaction_ShouldKeepReadersOutsideTheRewrite(bool insert, string readKind)
    {
        // Arrange
        using var storage = RecordStorage.Create();
        byte[] small = Filled(insert ? 4000 : 1000, 0x11);
        byte[] neighbour = Filled(insert ? 3000 : 5800, 0x33);
        byte[] changed = Filled(insert ? 1500 : 2000, 0x77);
        (PageId PageId, int SlotIndex) target, kept;
        using (var bracket = storage.BeginTransaction())
        {
            target = storage.Insert(bracket, ownerValue, small);
            kept = storage.Insert(bracket, ownerValue, neighbour);
            bracket.Commit();
        }
        if (insert)
        {
            using var bracket = storage.BeginTransaction();
            storage.Delete(bracket, target.PageId, target.SlotIndex);
            bracket.Commit();
        }
        var wanted = insert ? kept : target;
        byte[] expected = insert ? neighbour : changed;
        using var pin = storage.PageManager.GetPage(target.PageId);
        long before = pin.Entry.BeginRead();
        using var waiting = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        pin.Entry.ReadWaitStarted = waiting.Set;
        Task<byte[]>? reader = null;
        int signal = -1, rewrites = 0;
        storage.RecordPageCompacted = page =>
        {
            page.ShouldBe(target.PageId);
            rewrites++;
            reader = DedicatedTestTask.Run(() =>
            {
                try
                {
                    if (readKind == "direct") { return storage.Read(wanted.PageId, wanted.SlotIndex).ToArray(); }
                    if (readKind == "try")
                    {
                        storage.TryReadRecord(wanted.PageId, wanted.SlotIndex, ownerValue, out var bytes).ShouldBeTrue();
                        return bytes.ToArray();
                    }
                    using var scan = storage.GetUnitIterator(ownerValue);
                    scan.MoveNext().ShouldBeTrue();
                    scan.Current.SlotIndex.ShouldBe(wanted.SlotIndex);
                    return scan.Current.Data.ToArray();
                }
                finally { finished.Set(); }
            });
            // Thirty seconds is only a hang guard. Actual frame wait or completion decides.
            signal = WaitHandle.WaitAny([finished.WaitHandle, waiting.WaitHandle], TimeSpan.FromSeconds(30));
        };
        try
        {
            // Act: the update callback is between retiring the old target and publishing the new one.
            using (var bracket = storage.BeginTransaction())
            {
                if (insert) { storage.Insert(bracket, ownerValue, changed).PageId.ShouldBe(target.PageId); }
                else { storage.Update(bracket, target.PageId, target.SlotIndex, changed); }
                bracket.Commit();
            }

            // Assert
            rewrites.ShouldBe(1);
            signal.ShouldBe(1);
            (await reader.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(30))).ShouldBe(expected);
            pin.Entry.IsUnchangedSince(before).ShouldBeFalse();
        }
        finally
        {
            storage.RecordPageCompacted = null;
            pin.Entry.ReadWaitStarted = null;
            if (reader is { IsCompleted: false }) { await reader.WaitAsync(TimeSpan.FromSeconds(30)); }
        }
    }
}
