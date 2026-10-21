using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public sealed class CrashSimulationStreamTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Crash fixture: a torn write retains only its selected durable sector")]
    [InlineData(0)]
    [InlineData(1)]
    public void Write_ScheduledTear_ShouldPreserveOnlySelectedSector(int sector)
    {
        // Arrange
        var point = new CrashPoint { CrashAtWrite = 1, DurableSectorOffset = sector, DurableSectors = 1 };
        var original = new byte[CrashSimulationStream.SectorSize * 2];
        Array.Fill(original, (byte)0x11);
        using var data = new CrashSimulationStream(original, writeThrough: true, point);
        using var journal = new CrashSimulationStream(writeThrough: false, point);
        var replacement = new byte[original.Length];
        Array.Fill(replacement, (byte)0x77);

        // Act
        Should.Throw<SimulatedPowerLossException>(() => data.Write(replacement, 0));
        Should.Throw<SimulatedPowerLossException>(() => journal.Flush(durable: true));

        // Assert
        replacement.AsSpan(sector * CrashSimulationStream.SectorSize, CrashSimulationStream.SectorSize)
            .CopyTo(original.AsSpan(sector * CrashSimulationStream.SectorSize));
        data.CaptureDurable().ShouldBe(original);
        point.HasCrashed.ShouldBeTrue();
        point.Writes.ShouldBe(1);
    }
}
