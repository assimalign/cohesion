using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public class TransactionSnapshotTests
{
    private static TransactionSnapshot CreateSnapshot(ulong owner, ulong minimum, ulong maximum, params ulong[] active)
    {
        var activeSequences = Array.ConvertAll(active, value => new TransactionSequence(value));
        return new TransactionSnapshot(
            new TransactionSequence(owner),
            new TransactionSequence(minimum),
            new TransactionSequence(maximum),
            activeSequences);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Snapshot: Own writes are always visible")]
    public void IsVisible_OwnWrites_ShouldBeVisible()
    {
        // Arrange
        var snapshot = CreateSnapshot(owner: 10, minimum: 5, maximum: 11, active: 10);

        // Act
        var visible = snapshot.IsVisible(new TransactionSequence(10));

        // Assert
        visible.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Snapshot: Writes at or after the maximum are invisible")]
    public void IsVisible_WriterAtOrAboveMaximum_ShouldBeInvisible()
    {
        // Arrange
        var snapshot = CreateSnapshot(owner: 10, minimum: 5, maximum: 11);

        // Act & Assert
        snapshot.IsVisible(new TransactionSequence(11)).ShouldBeFalse();
        snapshot.IsVisible(new TransactionSequence(42)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Snapshot: Writers below the minimum are visible")]
    public void IsVisible_WriterBelowMinimum_ShouldBeVisible()
    {
        // Arrange
        var snapshot = CreateSnapshot(owner: 10, minimum: 5, maximum: 11, active: 7);

        // Act
        var visible = snapshot.IsVisible(new TransactionSequence(4));

        // Assert
        visible.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Snapshot: In-flight writers between the bounds are invisible")]
    public void IsVisible_ActiveWriterWithinBounds_ShouldBeInvisible()
    {
        // Arrange
        var snapshot = CreateSnapshot(owner: 10, minimum: 5, maximum: 11, active: 7);

        // Act & Assert
        snapshot.IsVisible(new TransactionSequence(7)).ShouldBeFalse();
        snapshot.IsVisible(new TransactionSequence(6)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Snapshot: Minimum above maximum is rejected")]
    public void Constructor_MinimumAboveMaximum_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => CreateSnapshot(owner: 1, minimum: 9, maximum: 3));
    }
}
