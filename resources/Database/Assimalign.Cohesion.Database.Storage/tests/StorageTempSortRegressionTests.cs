using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage.Internal;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageTempSortRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-sort-fixes-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public StorageTempSortRegressionTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
    private StorageTempSpace Create() => StorageTempSpace.CreateCore(_directory, 1024L * 1024 * 1024, () => 0, () => long.MaxValue);

    [Theory(DisplayName = "Cohesion Test [Storage] - Streaming sort: several grants match stable directed keys and NULLs through multiple levels")]
    [InlineData(65536, false)]
    [InlineData(65536, true)]
    [InlineData(98304, false)]
    [InlineData(98304, true)]
    [InlineData(131072, false)]
    [InlineData(131072, true)]
    public void Complete_SeveralGrants_ShouldMatchStableSort(int grant, bool descending)
    {
        // Arrange
        const int count = 25000;
        using var space = Create(); using var statement = space.CreateStatement(64 * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, grant, 256);
        var expected = new List<(byte[] Key, int Id)>(count);
        byte[] value = new byte[200];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[5]; key[0] = i % 7 == 0 ? (byte)0 : (byte)1;
            if (key[0] != 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(1), i * 7919 % 97);
                if (descending) { for (int b = 1; b < key.Length; b++) { key[b] = (byte)~key[b]; } }
            }
            BinaryPrimitives.WriteInt32LittleEndian(value, i); sorter.Append(key, value);
            expected.Add((key, i));
        }

        // Act
        using var reader = sorter.Complete(); var actual = new List<int>(count);
        while (reader.MoveNext()) { actual.Add(BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span)); }

        // Assert
        actual.ShouldBe(expected.OrderBy(x => x.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).Select(x => x.Id));
        sorter.MergePasses.ShouldBeGreaterThan(1);
        sorter.MergePasses.ShouldBeLessThanOrEqualTo((int)Math.Ceiling(Math.Log(sorter.RunsCreated, sorter.FanIn)) + 1);
        reader.Dispose(); statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        _output.WriteLine($"grant={grant}; rows={count}; runs={sorter.RunsCreated}; fan-in={sorter.FanIn}; depth={sorter.MergePasses}; writes={sorter.RecordsWritten}; comparisons={sorter.KeyComparisons}");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Streaming sort: at most fan-in runs write each row once and preserve borrowed values")]
    public void Complete_AtMostFanInRuns_ShouldAvoidFinalRewrite()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        byte[] value = new byte[100];
        for (int i = 0; i < 500; i++) { BinaryPrimitives.WriteInt32LittleEndian(value, i); sorter.Append([1], value); }

        // Act
        using var reader = sorter.Complete();
        sorter.RunsCreated.ShouldBeGreaterThan(1); sorter.RunsCreated.ShouldBeLessThanOrEqualTo(sorter.FanIn);
        int count = 0;
        while (reader.MoveNext())
        {
            reader.Key.Span.SequenceEqual(new byte[] { 1 }).ShouldBeTrue();
            BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span).ShouldBe(count++);
        }

        // Assert
        count.ShouldBe(500); sorter.RecordsWritten.ShouldBe(500);
        reader.Dispose(); statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Streaming sort: intermediate merges stay within the measured peak-disk bound")]
    public void Complete_IntermediateMerges_ShouldBoundPeakPhysicalBytes()
    {
        // Arrange
        const int count = 20000, framedLength = 121;
        using var space = Create(); using var statement = space.CreateStatement(32 * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        byte[] value = new byte[100];

        // Act
        for (int i = 0; i < count; i++) { sorter.Append([1, 2, 3, 4, 5], value); }
        using var reader = sorter.Complete();
        long physicalData = ((count * (long)(framedLength + 4) + StorageTempRun.PayloadSize - 1) / StorageTempRun.PayloadSize) * 8192;
        long pageAllowance = (sorter.RunsCreated + 1) * 8192;
        long bound = physicalData + physicalData / sorter.FanIn + pageAllowance;

        // Assert
        sorter.MergePasses.ShouldBeGreaterThan(1);
        statement.PeakBytes.ShouldBeLessThanOrEqualTo(bound);
        _output.WriteLine($"physical data={physicalData}; peak={statement.PeakBytes}; fan-in={sorter.FanIn}; initial runs={sorter.RunsCreated}; bound={bound}; page allowance={pageAllowance}");
        reader.Dispose(); statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Streaming sort: default 64 MiB grant sorts three and a half grants with default statement quota")]
    public void Complete_DefaultOptions_ShouldSortThreeAndHalfBudgets()
    {
        // Arrange: reused arrays, measured physical bytes; no OS-volume-dependent floor or engine quota.
        const int budget = 64 * 1024 * 1024, valueLength = 1000, keyLength = 4;
        const int framedLength = 16 + keyLength + valueLength;
        int count = (int)((7L * budget / 2) / framedLength);
        using var space = Create(); using var statement = space.CreateStatement(4L * budget);
        using var sorter = StorageTempSorter.Create(statement, budget, framedLength);
        byte[] key = new byte[keyLength], value = new byte[valueLength];

        // Act
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(key, count - i);
            BinaryPrimitives.WriteInt32LittleEndian(value, i); sorter.Append(key, value);
        }
        using var reader = sorter.Complete(); int rows = 0, last = int.MinValue;
        while (reader.MoveNext())
        {
            int current = BinaryPrimitives.ReadInt32BigEndian(reader.Key.Span);
            if (current <= last) { throw new InvalidOperationException("sort order"); }
            last = current; rows++;
        }

        // Assert
        rows.ShouldBe(count); statement.PeakBytes.ShouldBeLessThanOrEqualTo(4L * budget);
        _output.WriteLine($"default budget={budget}; rows={rows}; key+value bytes={rows * (long)(keyLength + valueLength)}; framed bytes={rows * (long)framedLength}; framed/budget={rows * (double)framedLength / budget:R}; peak physical bytes={statement.PeakBytes}; records written={sorter.RecordsWritten}; runs={sorter.RunsCreated}; fan-in={sorter.FanIn}");
        reader.Dispose(); statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Streaming sort: empty, one row and the exact record maximum")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(112)]
    public void Complete_BoundaryRecords_ShouldReturnExactBytes(int valueLength)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(8192);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        byte[] value = Enumerable.Range(0, valueLength).Select(x => (byte)x).ToArray();
        if (valueLength > 0) { sorter.Append([], value); }

        // Act
        using var reader = sorter.Complete();

        // Assert
        reader.MoveNext().ShouldBe(valueLength > 0);
        if (valueLength > 0) { reader.Key.IsEmpty.ShouldBeTrue(); reader.Value.ToArray().ShouldBe(value); }
        reader.MoveNext().ShouldBeFalse(); reader.Dispose(); statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Streaming sort: over-maximum append is CK05006 and releases intermediates")]
    public void Append_OverMaximum_ShouldRefuseWithCodeAndRelease()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        for (int i = 0; i < 500; i++) { sorter.Append([1], new byte[100]); }

        // Act
        var error = Should.Throw<StorageTempException>(() => sorter.Append([], new byte[113]));

        // Assert
        error.Code.ShouldBe("CK05006"); statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Streaming sort: cancellation, abandonment and statement disposal release open inputs")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MoveNext_EarlyExit_ShouldReleaseOwnedRuns(int exit)
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        for (int i = 0; i < 500; i++) { sorter.Append([1], new byte[100]); }
        using var reader = sorter.Complete(); reader.MoveNext().ShouldBeTrue();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();

        // Act
        if (exit == 0) { Should.Throw<OperationCanceledException>(() => reader.MoveNext(canceled.Token)); }
        else if (exit == 1) { reader.Dispose(); }
        else { statement.Dispose(); Should.Throw<ObjectDisposedException>(() => reader.MoveNext()); }

        // Assert
        statement.PhysicalBytes.ShouldBe(0); space.PendingBytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Streaming sort: CRC-valid malformed encoded rows fail ValidateRow")]
    public void MoveNext_DamagedEncodedRow_ShouldRefuseAndRelease()
    {
        // Arrange
        using var space = Create(); using var statement = space.CreateStatement(8192);
        var run = statement.CreateRun(); run.Append(new byte[16]); run.Seal();
        byte[] page = new byte[8192];
        RandomAccess.Read(RunFile(run).SafeFileHandle, page, 0).ShouldBe(8192);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(36), 1); // Key+value lengths no longer fit frame.
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), Crc32C.Compute(page));
        RandomAccess.Write(RunFile(run).SafeFileHandle, page, 0);
        using var reader = new StorageTempSortReader(statement, [run], 16, new byte[8192 + 16], true);

        // Act and assert
        Should.Throw<StorageTempException>(() => reader.MoveNext()).Code.ShouldBe("CK05004");
        statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Streaming sort: two sizes bound merge depth and catch a chain merge")]
    public void Complete_TwoSizes_ShouldBoundMergeDepth()
    {
        // Arrange
        using var space = Create();
        var results = new List<(int Count, int Depth, long Writes, long Runs, int FanIn)>();

        // Act
        foreach (int count in new[] { 7000, 28000 })
        {
            using var statement = space.CreateStatement(64 * 1024 * 1024);
            using var sorter = StorageTempSorter.Create(statement, 65536, 128);
            byte[] value = new byte[100];
            for (int i = 0; i < count; i++) { sorter.Append([1], value); }
            using var reader = sorter.Complete();
            while (reader.MoveNext()) { }
            results.Add((count, sorter.MergePasses, sorter.RecordsWritten, sorter.RunsCreated, sorter.FanIn));
        }

        // Assert
        foreach (var r in results)
        { r.Depth.ShouldBeLessThanOrEqualTo((int)Math.Ceiling(Math.Log(r.Runs, r.FanIn)) + 1); }
        (results[1].Depth - results[0].Depth).ShouldBeLessThanOrEqualTo(2);
        results[1].Writes.ShouldBeLessThan(6 * results[0].Writes);
        _output.WriteLine(string.Join("; ", results.Select(r => $"rows={r.Count}, depth={r.Depth}, writes={r.Writes}, initial runs={r.Runs}, fan-in={r.FanIn}")));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Streaming sort: final heap merge has no per-record allocation")]
    public void MoveNext_ReusedBuffers_ShouldAllocateConstantBytes()
    {
        // Arrange
        using var space = Create(); byte[] value = new byte[100]; long minimum = long.MaxValue;
        const int count = 12000;
        for (int round = 0; round < 3; round++)
        {
            using var statement = space.CreateStatement(16 * 1024 * 1024);
            using var sorter = StorageTempSorter.Create(statement, 65536, 128);
            for (int i = 0; i < count; i++) { sorter.Append([1], value); }
            using var reader = sorter.Complete(); reader.MoveNext().ShouldBeTrue();

            // Act
            long before = GC.GetAllocatedBytesForCurrentThread(); int rows = 1;
            while (reader.MoveNext()) { rows++; }
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);

            // Assert
            rows.ShouldBe(count);
        }
        minimum.ShouldBeLessThanOrEqualTo(512);
        _output.WriteLine($"streamed rows per round={count - 1}; allocated bytes={minimum}; minimum of 3 rounds");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_file")]
    private static extern ref FileStream RunFile(StorageTempRun run);
}
