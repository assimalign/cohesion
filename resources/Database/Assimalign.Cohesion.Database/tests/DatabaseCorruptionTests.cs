using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class DatabaseCorruptionTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Integrity: lifecycle corruption preserves synchronous throws and translates deferred failures")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateDatabaseAsync_Corruption_ShouldRetainCodePageAndCoreCompletion(bool deferred, bool wrapped)
    {
        // Arrange
        await using var engine = new TestEngine();
        var cause = new StorageCorruptionException((PageId)9L, "Page 9 failed checksum verification.");
        Exception error = wrapped ? new DatabaseException("Open failed.", cause) : cause;
        var completion = new TaskCompletionSource<DatabaseInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.CreateOperation = () => deferred ? new ValueTask<DatabaseInstance>(completion.Task) : throw error;

        // Act
        DatabaseCorruptionException failure;
        if (deferred)
        {
            var pending = engine.CreateDatabaseAsync("broken", CancellationToken.None);
            pending.IsCompleted.ShouldBeFalse();
            completion.SetException(error);
            failure = await Should.ThrowAsync<DatabaseCorruptionException>(() => pending.AsTask());
        }
        else
        {
            failure = Should.Throw<DatabaseCorruptionException>(() => engine.CreateDatabaseAsync("broken", CancellationToken.None));
        }

        // Assert
        failure.Code.ShouldBe("COHDBS003");
        failure.Database.ShouldBe((DatabaseName)"broken");
        failure.PageId.ShouldBe((PageId)9L);
        failure.InnerException.ShouldBeSameAs(cause);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(failure.Code);
        failure.Message.ShouldContain("verified backup", Case.Sensitive);
        engine.CreateOperation = null;
        (await engine.CreateDatabaseAsync("healthy", CancellationToken.None)).Name.ShouldBe((DatabaseName)"healthy");
    }
}
