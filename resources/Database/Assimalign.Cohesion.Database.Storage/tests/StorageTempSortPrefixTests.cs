using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageTempSortPrefixTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sort-prefix-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp creation: injected cancellation releases directory ownership before cross-thread disposal (#1789)")]
    public async Task CreateRun_CanceledCallback_ShouldAllowDisposalOnAnotherThread()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, 1048576, () => 0);
        using var statement = space.CreateStatement(1048576);
        space.BeforeFileCreate = () =>
        {
            Monitor.IsEntered(DirectoryGate(space)).ShouldBeFalse();
            throw new OperationCanceledException();
        };

        // Act
        Should.Throw<OperationCanceledException>(() => statement.CreateRun());
        await Task.Run(space.Dispose).WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        statement.PhysicalBytes.ShouldBe(0); space.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_directoryGate")]
    private static extern ref object DirectoryGate(StorageTempSpace space);

    [Fact(DisplayName = "Cohesion Test [Storage] - Sort prefix: drain before arena creation and independent readers preserve stable ties (#1789)")]
    public async Task Complete_SortedPrefix_ShouldSupportConcurrentRewind()
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, 16 * 1024 * 1024, () => 0);
        using var statement = space.CreateStatement(16 * 1024 * 1024);
        bool drained = false;
        using var sort = StorageTempSorter.CreateWithSortedPrefix(statement, 262144, 6000, Prefix());
        drained.ShouldBeTrue();
        for (int i = 1000; i < 8000; i++) { sort.Append([1], BitConverter.GetBytes(i)); }

        // Act
        using var owner = sort.Complete();
        // Fork the template serially; only the independent cursor reads run concurrently.
        var first = owner.OpenReader(); var second = owner.OpenReader(); var third = owner.OpenReader();
        var rounds = await Task.WhenAll(Task.Run(() => Read(first)), Task.Run(() => Read(second)), Task.Run(() => Read(third)));

        // Assert
        foreach (var rows in rounds) { rows.ShouldBe(Enumerable.Range(0, 8000)); }
        owner.BufferBytes.ShouldBeLessThan(100000); // actual 21-byte records, rather than declared 6000-byte slots
        sort.RunsCreated.ShouldBeGreaterThan(1); sort.MergePasses.ShouldBeGreaterThan(0);
        statement.PeakBytes.ShouldBe(212992);
        owner.Dispose(); statement.PhysicalBytes.ShouldBe(0); space.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();

        IEnumerable<(ReadOnlyMemory<byte>, ReadOnlyMemory<byte>)> Prefix()
        {
            for (int i = 0; i < 1000; i++) { yield return (new byte[] { 1 }, BitConverter.GetBytes(i)); }
            drained = true;
        }
        int[] Read(StorageTempSortReader reader)
        {
            using var cursor = reader;
            var values = new List<int>();
            while (reader.MoveNext()) { values.Add(BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span)); }
            return values.ToArray();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Sort prefix: cancellation, iterator errors and oversized rows release the initial run (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CreateWithSortedPrefix_Failure_ShouldReleaseEveryPage(int mode)
    {
        // Arrange
        using var space = StorageTempSpace.Create(_directory, 16 * 1024 * 1024, () => 0);
        using var statement = space.CreateStatement(16 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();

        // Act / Assert
        if (mode == 0) { Should.Throw<OperationCanceledException>(() => Create()); }
        else if (mode == 1) { Should.Throw<InvalidOperationException>(() => Create()); }
        else { Should.Throw<StorageTempException>(() => Create()).Code.ShouldBe("CK05006"); }
        statement.PhysicalBytes.ShouldBe(0); space.Bytes.ShouldBe(0);
        Directory.GetFiles(_directory, "*.ctmp").ShouldBeEmpty();

        void Create() => StorageTempSorter.CreateWithSortedPrefix(statement, 262144, 128, Prefix(), cancellation.Token).Dispose();
        IEnumerable<(ReadOnlyMemory<byte>, ReadOnlyMemory<byte>)> Prefix()
        {
            yield return (new byte[] { 1 }, new byte[100]);
            if (mode == 0) { cancellation.Cancel(); }
            else if (mode == 1) { throw new InvalidOperationException("prefix failed"); }
            yield return (new byte[] { 2 }, new byte[200]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Sort handles: an explicit run ceiling exercises early merges and bounds open files (#1789)")]
    public void Append_RunCeiling_ShouldMergeBeforeOpeningTooManyFiles()
    {
        // Arrange
        const int count = 20000;
        using var space = StorageTempSpace.Create(_directory, 32 * 1024 * 1024, () => 0);
        using var statement = space.CreateStatement(32 * 1024 * 1024);
        int peak = 0;
        space.BeforeFileCreate = () => peak = Math.Max(peak, Directory.GetFiles(_directory, "*.ctmp").Length + 1);
        using var sort = StorageTempSorter.Create(statement, 65536, 128, 64);
        byte[] value = new byte[100];

        // Act
        for (int i = 0; i < count; i++) { BinaryPrimitives.WriteInt32LittleEndian(value, i); sort.Append([1], value); }
        using var reader = sort.Complete();
        int visited = 0;
        while (reader.MoveNext()) { BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span).ShouldBe(visited++); }

        // Assert: progress pins that the ceiling was exercised, rather than an input that never filled it.
        peak.ShouldBe(64); visited.ShouldBe(count); sort.RunsCreated.ShouldBeGreaterThan(64);
        sort.KeyComparisons.ShouldBeLessThan(count * 100L); sort.RecordsWritten.ShouldBeLessThan(count * 20L);
        reader.Dispose(); statement.PhysicalBytes.ShouldBe(0); space.Bytes.ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => StorageTempSorter.Create(statement, 262144, 128, 16));
    }
}
