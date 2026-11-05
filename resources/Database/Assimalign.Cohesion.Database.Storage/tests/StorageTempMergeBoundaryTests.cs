using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageTempMergeBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-merge-second-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public StorageTempMergeBoundaryTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }

    [Theory(DisplayName = "Cohesion Test [Storage] - Deferred merges: counterexamples obey data plus largest merge plus page rounding")]
    [InlineData(864, 65536, 5, 100, 128)]
    [InlineData(2808, 65536, 5, 100, 128)]
    [InlineData(8640, 65536, 5, 100, 128)]
    [InlineData(20000, 65536, 5, 100, 128)]
    [InlineData(25000, 98304, 5, 200, 256)]
    [InlineData(894336, 67108864, 4, 1000, 1020)]
    public void Complete_Counterexamples_ShouldUseMinimalWritesAndBoundPeak(int count, int grant, int keyLength, int valueLength, int maximumRecord)
    {
        // Arrange
        using var space = StorageTempSpace.CreateCore(_directory, 4L * 1024 * 1024 * 1024, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(4L * 1024 * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, grant, maximumRecord);
        byte[] key = new byte[keyLength], value = new byte[valueLength]; int framed = 16 + keyLength + valueLength;
        var expected = Model(count, grant, framed, sorter.FanIn);

        // Act
        for (int i = 0; i < count; i++) { sorter.Append(key, value); }
        using var reader = sorter.Complete();
        long bound = expected.Data + expected.LargestMerge + (sorter.RunsCreated + 1) * 8192;
        _output.WriteLine($"rows={count}; grant={grant}; fan-in={sorter.FanIn}; runs={sorter.RunsCreated}; data={expected.Data}; peak={statement.PeakBytes}; writes={sorter.RecordsWritten}; largest merge={expected.LargestMerge}; bound={bound}; depth={sorter.MergePasses}");

        // Assert
        statement.PeakBytes.ShouldBeLessThanOrEqualTo(bound);
        sorter.RecordsWritten.ShouldBe(expected.Writes);
        reader.Dispose(); statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Deferred merges: seventeen default-grant runs finish inside a one GiB statement quota")]
    public void Complete_SeventeenDefaultRuns_ShouldFinishWithinOneGiB()
    {
        // Arrange
        const int count = 894336, grant = 64 * 1024 * 1024, framed = 1020;
        using var space = StorageTempSpace.CreateCore(_directory, 2L * 1024 * 1024 * 1024, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(1024L * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, grant, framed);
        byte[] key = new byte[4], value = new byte[1000];

        // Act
        for (int i = 0; i < count; i++)
        { BinaryPrimitives.WriteInt32LittleEndian(value, i); sorter.Append(key, value); }
        using var reader = sorter.Complete(); int rows = 0;
        while (reader.MoveNext()) { BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span).ShouldBe(rows++); }

        // Assert
        rows.ShouldBe(count); sorter.RunsCreated.ShouldBe(17); sorter.RecordsWritten.ShouldBe(999552);
        var expected = Model(count, grant, framed, sorter.FanIn);
        statement.PeakBytes.ShouldBeLessThanOrEqualTo(expected.Data + expected.LargestMerge + 18 * 8192);
        statement.PhysicalBytes.ShouldBe(0);
        _output.WriteLine($"one GiB quota: rows={rows}; peak={statement.PeakBytes}; writes={sorter.RecordsWritten}; depth={sorter.MergePasses}");
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Deferred merges: FanIn, FanIn plus one and twice FanIn minus one preserve ordinal stability")]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Complete_RunBoundaries_ShouldPreserveStabilityAndDepth(int runs)
    {
        // Arrange: exactly 216 rows fill each initial run at this grant and framing.
        const int countPerRun = 216;
        using var space = StorageTempSpace.CreateCore(_directory, 16 * 1024 * 1024, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(16 * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        byte[] key = new byte[5], value = new byte[100];
        var expected = new List<(int Key, int Ordinal)>();
        for (int i = 0; i < runs * countPerRun; i++)
        {
            int group = i * 7919 % 7; key[0] = (byte)group;
            BinaryPrimitives.WriteInt32LittleEndian(value, i); sorter.Append(key, value); expected.Add((group, i));
        }

        // Act
        using var reader = sorter.Complete(); var actual = new List<int>();
        while (reader.MoveNext()) { actual.Add(BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span)); }

        // Assert
        actual.ShouldBe(expected.OrderBy(x => x.Key).Select(x => x.Ordinal)); sorter.RunsCreated.ShouldBe(runs);
        sorter.MergePasses.ShouldBe(runs == sorter.FanIn ? 1 : 2);
        sorter.RecordsWritten.ShouldBe(Model(runs * countPerRun, 65536, 121, sorter.FanIn).Writes);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Deferred merges: a full directory bounds live handles and preserves ordinal stability")]
    public void Append_FullDirectory_ShouldBoundHandlesAndKeepStableOrder()
    {
        // Arrange
        const int count = 60000;
        using var space = StorageTempSpace.CreateCore(_directory, 64 * 1024 * 1024, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(64 * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        int peakHandles = 0;
        space.BeforeFileCreate = () => peakHandles = Math.Max(peakHandles, Directory.GetFiles(_directory, "*.ctmp").Length + 1);
        byte[] key = new byte[5], value = new byte[100];

        // Act
        for (int i = 0; i < count; i++) { BinaryPrimitives.WriteInt32LittleEndian(value, i); sorter.Append(key, value); }
        using var reader = sorter.Complete(); int rows = 0;
        while (reader.MoveNext()) { BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span).ShouldBe(rows++); }

        // Assert
        rows.ShouldBe(count); peakHandles.ShouldBe(64 * sorter.FanIn);
        sorter.MergePasses.ShouldBeLessThanOrEqualTo((int)Math.Ceiling(Math.Log(sorter.RunsCreated, sorter.FanIn)) + 1);
        statement.PhysicalBytes.ShouldBe(0);
        _output.WriteLine($"full directory: rows={rows}; handles={peakHandles}; runs={sorter.RunsCreated}; depth={sorter.MergePasses}; writes={sorter.RecordsWritten}");
    }

    private static (long Data, long LargestMerge, long Writes) Model(int count, int grant, int framed, int fanIn)
    {
        int workspace = grant - 32768, arena = (int)(workspace * 4L / 5) & ~3;
        int perRun = Math.Min(arena / framed, (workspace - arena) / 4);
        var remaining = new List<long>();
        for (int rows = count; rows > 0; rows -= Math.Min(perRun, rows)) { remaining.Add(Math.Min(perRun, rows)); }
        long writes = count, largest = 0;
        while (remaining.Count > fanIn)
        {
            remaining.Sort(); int remainder = (remaining.Count - fanIn) % (fanIn - 1);
            int take = remainder == 0 ? fanIn : remainder + 1;
            long merged = remaining.Take(take).Sum(); remaining.RemoveRange(0, take); remaining.Add(merged);
            writes += merged; largest = Math.Max(largest, Pages(merged * (framed + 4L)));
        }
        return (Pages(count * (framed + 4L)), largest, writes);
    }
    private static long Pages(long bytes) => (bytes + StorageTempRun.PayloadSize - 1) / StorageTempRun.PayloadSize * 8192;
}
