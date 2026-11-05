using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

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
        var expected = TempSortModel.Measure(count, grant, framed, sorter.FanIn);

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
        var expected = TempSortModel.Measure(count, grant, framed, sorter.FanIn);
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
        var cost = TempSortModel.Measure(runs * countPerRun, 65536, 121, sorter.FanIn);
        sorter.RecordsWritten.ShouldBe(cost.Writes);
        statement.PeakBytes.ShouldBeLessThanOrEqualTo(cost.Data + cost.LargestMerge + (runs + 1) * 8192);
        _output.WriteLine($"boundary runs={runs}; peak={statement.PeakBytes}; largest merge={cost.LargestMerge}; writes={sorter.RecordsWritten}");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Deferred merges: two thousand runs bound writes and depth with stable differential output")]
    public void Complete_RepeatedFullDirectories_ShouldBoundWritesAndPreserveStability()
    {
        // Arrange: 216 framed rows fill each initial run at fan-in three.
        const int runs = 2000, count = runs * 216;
        using var space = StorageTempSpace.CreateCore(_directory, 2L * 1024 * 1024 * 1024, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(2L * 1024 * 1024 * 1024);
        using var sorter = StorageTempSorter.Create(statement, 65536, 128);
        byte[] key = new byte[5], value = new byte[100];
        using var expected = Enumerable.Range(0, count).OrderBy(i => i * 7919L % 7).GetEnumerator();

        // Act
        for (int i = 0; i < count; i++)
        {
            key[0] = (byte)(i * 7919L % 7); BinaryPrimitives.WriteInt32LittleEndian(value, i); sorter.Append(key, value);
        }
        using var reader = sorter.Complete(); int rows = 0; bool stable = true;
        while (reader.MoveNext())
        { stable &= expected.MoveNext() && BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span) == expected.Current; rows++; }
        _output.WriteLine($"large directory: runs={sorter.RunsCreated}; rows={rows}; writes={sorter.RecordsWritten}; amplification={(double)sorter.RecordsWritten / count:F6}; depth={sorter.MergePasses}; peak={statement.PeakBytes}");

        // Assert
        sorter.FanIn.ShouldBe(3); sorter.RunsCreated.ShouldBe(runs); rows.ShouldBe(count);
        stable.ShouldBeTrue(); expected.MoveNext().ShouldBeFalse();
        sorter.MergePasses.ShouldBeLessThanOrEqualTo(8);
        sorter.RecordsWritten.ShouldBeLessThanOrEqualTo(7L * count); statement.PhysicalBytes.ShouldBe(0);
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

}
