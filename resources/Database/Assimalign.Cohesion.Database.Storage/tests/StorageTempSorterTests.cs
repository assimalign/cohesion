using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageTempSorterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-sort-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public StorageTempSorterTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }

    [Theory(DisplayName = "Cohesion Test [Storage] - External sort: ten grants, stable ties, NULL markers and directed keys")]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_TenTimesGrant_ShouldMatchStableInMemorySort(bool descending)
    {
        // Arrange
        const int grant = 65536, count = 7000;
        using var space = StorageTempSpace.Create(_directory, 32 * 1024 * 1024, () => 0);
        using var statement = space.CreateStatement(32 * 1024 * 1024);
        using var sort = StorageTempSorter.Create(statement, grant, 128);
        var expected = new List<(byte[] Key, int Value)>();
        var payload = new byte[100];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[5]; key[0] = i % 7 == 0 ? (byte)0 : (byte)1;
            if (key[0] != 0) { BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(1), (i * 7919 % 97) ^ int.MinValue); }
            if (descending && key[0] != 0) { for (int b = 0; b < key.Length; b++) { key[b] = (byte)~key[b]; } }
            BinaryPrimitives.WriteInt32LittleEndian(payload, i);
            sort.Append(key, payload);
            expected.Add((key, i));
        }

        // Act
        using var reader = sort.Complete();
        var actual = new List<int>();
        while (reader.MoveNext()) { actual.Add(BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span)); }

        // Assert
        actual.ShouldBe(expected.OrderBy(row => row.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).Select(row => row.Value));
        sort.RunsCreated.ShouldBeGreaterThan(sort.FanIn);
        sort.MergePasses.ShouldBeGreaterThan(1);
        // Deterministic complexity guard against scanning every active run or all earlier rows.
        sort.KeyComparisons.ShouldBeLessThan(count * 40L);
        sort.RecordsWritten.ShouldBeLessThanOrEqualTo(count * (long)(sort.MergePasses + 1));
        _output.WriteLine($"rows={count}; fan-in={sort.FanIn}; initial runs={sort.RunsCreated}; passes={sort.MergePasses}; comparisons={sort.KeyComparisons}; records written={sort.RecordsWritten}");
        reader.Dispose(); statement.Bytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - External sort: empty and grant-dependent fan-in")]
    public void Complete_EmptyInput_ShouldReturnEmptyRunWithinGrant()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, null, () => 0); using var statement = space.CreateStatement(1024 * 1024);
        using var small = StorageTempSorter.Create(statement, 65536, 128);
        using var large = StorageTempSorter.Create(statement, 131072, 128);

        // Act
        using var reader = small.Complete();

        // Assert
        reader.MoveNext().ShouldBeFalse(); small.MergePasses.ShouldBe(0);
        large.FanIn.ShouldBeGreaterThan(small.FanIn);
        Should.Throw<ArgumentOutOfRangeException>(() => StorageTempSorter.Create(statement, 65536, 32768));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp children: parent disposal invalidates buffered sorts and partition access")]
    public void Append_DisposedStatement_ShouldRefuseBufferedChildren()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, null, () => 0);
        using var statement = space.CreateStatement(1024 * 1024);
        using var sort = StorageTempSorter.Create(statement, 65536, 128);
        using var partitions = StorageTempPartitions.Create(statement, 2, 17408);
        sort.Append([1], [2]); partitions.Append(0, [3]); partitions.Seal();
        statement.Dispose();

        // Act and assert
        Should.Throw<ObjectDisposedException>(() => sort.Append([4], [5]));
        Should.Throw<ObjectDisposedException>(() => sort.Complete());
        Should.Throw<ObjectDisposedException>(() => partitions.GetPartition(0));
        Should.Throw<ObjectDisposedException>(() => StorageTempSorter.Create(statement, 65536, 128));
        Should.Throw<ObjectDisposedException>(() => StorageTempPartitions.Create(statement, 2, 17408));
        space.Bytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - External sort: failure and cancellation delete all intermediate files")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Complete_EarlyExit_ShouldReleaseAllIntermediateRuns(int exit)
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, exit == 0 ? 8192 : 16 * 1024 * 1024, () => 0);
        using var statement = space.CreateStatement(16 * 1024 * 1024);
        using var sort = StorageTempSorter.Create(statement, 65536, 128);
        using var cancellation = new CancellationTokenSource();

        // Act
        if (exit == 0)
        { Should.Throw<StorageTempException>(() => { for (int i = 0; i < 1000; i++) { sort.Append([1], new byte[100]); } }); }
        else
        {
            for (int i = 0; i < 1000; i++) { sort.Append([1], new byte[100]); }
            if (exit == 1) { cancellation.Cancel(); Should.Throw<OperationCanceledException>(() => sort.Complete(cancellation.Token)); }
            else { sort.Dispose(); sort.Dispose(); }
        }

        // Assert
        statement.Bytes.ShouldBe(0); space.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - External sort: cancellation during merge releases inputs and partial output")]
    public void Complete_MidMergeCancellation_ShouldDeleteAllRuns()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, 16 * 1024 * 1024, () => 0);
        using var statement = space.CreateStatement(16 * 1024 * 1024);
        using var sort = StorageTempSorter.Create(statement, 65536, 128);
        for (int i = 0; i < 5000; i++) { sort.Append([1], new byte[100]); }
        using var cancellation = new CancellationTokenSource();
        int pageWrites = 0;
        space.BeforePageWrite = () => { if (++pageWrites == 5) { cancellation.Cancel(); } };

        // Act
        Should.Throw<OperationCanceledException>(() => sort.Complete(cancellation.Token));

        // Assert
        pageWrites.ShouldBe(5); statement.Bytes.ShouldBe(0); space.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Hash partitions: deterministic membership and independent readers")]
    public void Partitions_HashMembership_ShouldReadAllRecordsAndRelease()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, null, () => 0); using var statement = space.CreateStatement(1024 * 1024);
        using var partitions = StorageTempPartitions.Create(statement, 7, 7 * 8704);
        for (int i = 0; i < 1000; i++)
        { byte[] value = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(value, i); partitions.Append((ulong)i, value); }

        // Act
        partitions.Seal();
        int count = 0;
        for (int p = 0; p < partitions.PartitionCount; p++)
        {
            using var reader = partitions.GetPartition(p)!.OpenReader(8192);
            byte[] row = new byte[4];
            while (reader.TryRead(row, out int length)) { length.ShouldBe(4); (BinaryPrimitives.ReadInt32LittleEndian(row) % 7).ShouldBe(p); count++; }
        }

        // Assert
        count.ShouldBe(1000); partitions.Dispose(); statement.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Hash partitions: cancellation releases every partition")]
    public void Partitions_CanceledSeal_ShouldReleaseAllFiles()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, null, () => 0); using var statement = space.CreateStatement(1024 * 1024);
        using var partitions = StorageTempPartitions.Create(statement, 2, 17408);
        partitions.Append(0, [1]); partitions.Append(1, [2]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();

        // Act
        Should.Throw<OperationCanceledException>(() => partitions.Seal(canceled.Token));

        // Assert
        space.Bytes.ShouldBe(0); Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Hash partitions: refusal releases earlier partition reservations")]
    public void Partitions_QuotaRefusal_ShouldReleaseEarlierPartitions()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, 8192, () => 0);
        using var statement = space.CreateStatement(16384);
        using var partitions = StorageTempPartitions.Create(statement, 2, 17408);
        partitions.Append(0, [1]);

        // Act
        var error = Should.Throw<StorageTempException>(() => partitions.Append(1, [2]));

        // Assert
        error.Code.ShouldBe("CK05001"); space.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp reader: reused destinations eliminate per-record allocations")]
    public void TryRead_ReusedDestination_ShouldAllocateConstantBytes()
    {
        // Arrange
        const int count = 10000;
        using var space = StorageTempSpace.Create(_directory, null, () => 0);
        using var statement = space.CreateStatement(4 * 1024 * 1024);
        using var run = statement.CreateRun();
        byte[] record = new byte[100];
        for (int i = 0; i < count; i++) { run.Append(record); }
        run.Seal();
        long baseline = long.MaxValue, reused = long.MaxValue;

        // Act: minimum of three rounds, with reader construction and first-page/JIT warmup
        // outside each measurement. Both paths visit exactly the same remaining records.
        for (int round = 0; round < 3; round++)
        {
            using (var reader = run.OpenReader(8192))
            {
                reader.TryRead(record, out _).ShouldBeTrue();
                long before = GC.GetAllocatedBytesForCurrentThread();
                int visited = 0;
                while (reader.TryRead(new byte[100], out _)) { visited++; }
                baseline = Math.Min(baseline, GC.GetAllocatedBytesForCurrentThread() - before);
                visited.ShouldBe(count - 1);
            }
            using (var reader = run.OpenReader(8192))
            {
                reader.TryRead(record, out _).ShouldBeTrue();
                long before = GC.GetAllocatedBytesForCurrentThread();
                int visited = 0;
                while (reader.TryRead(record, out _)) { visited++; }
                reused = Math.Min(reused, GC.GetAllocatedBytesForCurrentThread() - before);
                visited.ShouldBe(count - 1);
            }
        }

        // Assert
        reused.ShouldBeLessThanOrEqualTo(512);
        _output.WriteLine($"reader rows={count - 1}; control allocating destinations={baseline} bytes; after reusing destination={reused} bytes; minimum of 3 rounds");
    }
}
