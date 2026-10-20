using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class DatabaseCorruptionTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Integrity: lifecycle corruption preserves synchronous throws and translates deferred failures")]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public async Task CreateDatabaseAsync_Corruption_ShouldRetainCodePageAndCoreCompletion(bool deferred, int wrapper)
    {
        // Arrange
        await using var engine = new TestEngine();
        var cause = new StorageCorruptionException((PageId)9L, "Page 9 failed checksum verification.");
        var coded = new DatabaseException("Open failed.", [new Diagnostic
        { Code = "COHDBG018", Message = "Open failed.", Severity = DiagnosticSeverity.Error }], cause);
        Exception error = wrapper switch
        {
            1 => coded,
            2 => new AggregateException(new InvalidOperationException("Cleanup failed."), coded),
            _ => cause,
        };
        if (wrapper == 3)
        {
            // Arbitrarily deep coded wraps must not turn corruption into a different failure.
            error = coded;
            for (int depth = 0; depth < 128; depth++)
            {
                error = new DatabaseException("Storage wrapper.", coded.Diagnostics, error);
            }
        }
        else if (wrapper == 4)
        {
            // The first branch reaches the shared exception deeply; the second reaches it directly.
            // A depth cutoff combined with a visited set could incorrectly discard the second path.
            Exception deep = coded;
            for (int depth = 0; depth < 62; depth++)
            {
                deep = new InvalidOperationException("Cleanup wrapper.", deep);
            }
            error = new AggregateException(deep, coded);
        }
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
        failure.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldBe(wrapper != 0 ? ["COHDBS003", "COHDBG018"] : ["COHDBS003"]);
        failure.Message.ShouldContain("verified backup", Case.Sensitive);
        engine.CreateOperation = null;
        (await engine.CreateDatabaseAsync("healthy", CancellationToken.None)).Name.ShouldBe((DatabaseName)"healthy");
    }
}
