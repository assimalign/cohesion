using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(nameof(StorageEventSourceCollection))]
public sealed class StorageTempSeekTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "temp-seek-" + Guid.NewGuid().ToString("N"));

    [Theory(DisplayName = "Cohesion Test [Storage] - Saved temp positions read records on and across page boundaries (#1789)")]
    [InlineData(0)]
    [InlineData(8152)]
    [InlineData(8154)]
    [InlineData(8156)]
    [InlineData(20000)]
    public void OpenReader_SavedPositions_ShouldReadExactSuffix(int prefix)
    {
        // Arrange
        using var space = StorageTempSpace.CreateCore(_directory, 1048576, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(1048576);
        using var run = statement.CreateRun();
        run.Append(new byte[prefix]);
        long position = run.Position;
        byte[] payload = Enumerable.Range(0, 20000).Select(i => (byte)i).ToArray();
        run.Append(payload); long empty = run.Position; run.Append([]);
        long tail = run.Position; run.Append([3, 1, 4]); long end = run.Position; run.Seal();
        var buffer = new byte[run.MaximumRecordLength];
        // Act / Assert
        using var reader = run.OpenReader(8192, position);
        reader.TryRead(buffer, out int size).ShouldBeTrue(); size.ShouldBe(payload.Length); buffer.ShouldBe(payload);
        reader.TryRead(buffer, out size).ShouldBeTrue(); size.ShouldBe(0);
        reader.TryRead(buffer, out size).ShouldBeTrue(); buffer[..size].ShouldBe(new byte[] { 3, 1, 4 });
        reader.TryRead(buffer, out _).ShouldBeFalse();
        using var atEmpty = run.OpenReader(8192, empty); atEmpty.TryRead(buffer, out size).ShouldBeTrue(); size.ShouldBe(0);
        using var atTail = run.OpenReader(8192, tail); atTail.TryRead(buffer, out size).ShouldBeTrue(); size.ShouldBe(3);
        using var atEnd = run.OpenReader(8192, end); atEnd.TryRead(buffer, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp seek validates the visited page and permanently fails after damage (#1789)")]
    public void OpenReader_DamageAfterSeek_ShouldRefuse()
    {
        // Arrange
        using var space = StorageTempSpace.CreateCore(_directory, 1048576, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(1048576);
        using var run = statement.CreateRun(); run.Append(new byte[20000]);
        long position = run.Position; run.Append(new byte[20000]); run.Seal();
        using var reader = run.OpenReader(8192, position);
        RandomAccess.Write(RunFile(run).SafeFileHandle, new byte[] { 123 }, 2 * 8192 + 64);
        // Act / Assert
        Should.Throw<StorageTempException>(() => reader.TryRead(new byte[20000], out _)).Code.ShouldBe("CK05004");
        Should.Throw<StorageTempException>(() => reader.TryRead(new byte[20000], out _)).Code.ShouldBe("CK05004");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Temp seek refuses unsealed, disposed and out of range positions (#1789)")]
    public void OpenReader_InvalidState_ShouldRefuse()
    {
        // Arrange
        using var space = StorageTempSpace.CreateCore(_directory, 1048576, () => 0, () => long.MaxValue);
        using var statement = space.CreateStatement(1048576);
        using var run = statement.CreateRun(); run.Append([1]);
        // Act / Assert
        Should.Throw<InvalidOperationException>(() => run.OpenReader(8192, 0));
        run.Seal();
        Should.Throw<ArgumentOutOfRangeException>(() => run.OpenReader(8191, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => run.OpenReader(8192, -1));
        Should.Throw<ArgumentOutOfRangeException>(() => run.OpenReader(8192, run.Position + 1));
        run.Dispose();
        Should.Throw<ObjectDisposedException>(() => run.OpenReader(8192, 0));
        Should.Throw<ObjectDisposedException>(() => _ = run.Position);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_file")]
    private static extern ref FileStream RunFile(StorageTempRun run);
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
}
