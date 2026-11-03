using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageCheckpointRetryTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint refusal tracks the original blockers across intervening brackets (#1283)")]
    public void Checkpoint_BusyRefusal_ShouldTrackOnlyItsOriginalBrackets()
    {
        // Arrange
        using var storage = TornStorage.Create();
        using var outside = storage.BeginTransaction("Outside");
        // Act: the refusal owns a snapshot, not an active-count or last-completion token.
        var refusal = Should.Throw<StorageTransactionException>(() => storage.Checkpoint());
        Func<bool> blocked = refusal.Data.Values.OfType<Func<bool>>().ShouldHaveSingleItem();
        using (var intervening = storage.BeginTransaction("Statement"))
        {
            blocked().ShouldBeTrue();
            intervening.Rollback();
        }
        blocked().ShouldBeTrue();
        outside.Rollback();
        using var replacement = storage.BeginTransaction("Replacement");
        var nextRefusal = Should.Throw<StorageTransactionException>(() => storage.Checkpoint());
        Func<bool> nextBlocked = nextRefusal.Data.Values.OfType<Func<bool>>().ShouldHaveSingleItem();
        // Assert: replacing a blocker keeps the count unchanged but ends the first refusal.
        blocked().ShouldBeFalse();
        nextBlocked().ShouldBeTrue();
        replacement.Rollback();
        nextBlocked().ShouldBeFalse();
        storage.Checkpoint();
    }
}
