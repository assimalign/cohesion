using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueFunctionReviewTests
{
    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Functions: same-session reentry is refused before a nested PUT")]
    public async Task ExecuteAsync_ReentrantCallback_ShouldCommitNoNestedWritesAsync()
    {
        // Arrange
        KeyValueDatabaseSession? captured = null;
        DatabaseException? nested = null;
        bool wrote = false;
        var builder = KeyValueDatabaseEngine.CreateBuilder("reentry");
        builder.Functions.Add(KeyValueFunction.Create<long>("reenter", () =>
        {
            try { captured!.Database.PutAsync(captured, new byte[] { 1 }, new byte[] { 9 }, cancellationToken: CancellationToken.None).AsTask().GetAwaiter().GetResult(); wrote = true; }
            catch (DatabaseException error) { nested = error; }
            throw new InvalidOperationException("outer callback failed");
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        captured = session;
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await database.PutAsync(session, new byte[] { 2 }, new byte[] { 8 }, cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new KeyValueFunctionRequest("reenter", []), CancellationToken.None));
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV05003");
        await using var later = await database.CreateSessionAsync(CancellationToken.None);
        (await database.ExistsAsync(later, new byte[] { 1 }, CancellationToken.None)).ShouldBeFalse();
        (await database.ExistsAsync(later, new byte[] { 2 }, CancellationToken.None)).ShouldBeTrue();
        wrote.ShouldBeFalse();
        nested.ShouldNotBeNull().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK07003");
    }

    [Theory(DisplayName = "Cohesion Test [KeyValuePair] - Functions: callback I/O cannot recode or abort an explicit transaction")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CallbackIo_ShouldPreserveCodeAndPriorWritesAsync(bool coded)
    {
        // Arrange
        var io = new IOException("HTTP callback failed");
        var cause = coded ? new DatabaseException("Function failed", [new Diagnostic { Code = "CV05003", Message = "Function failed", Severity = DiagnosticSeverity.Error }], io) : (Exception)io;
        var builder = KeyValueDatabaseEngine.CreateBuilder("callback-io");
        builder.Functions.Add(KeyValueFunction.Create<long>("fail_io", () => throw cause));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await database.PutAsync(session, new byte[] { 1 }, new byte[] { 9 }, cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new KeyValueFunctionRequest("fail_io", []), CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV05003");
        failure.InnerException.ShouldBeSameAs(io);
        (await database.ExistsAsync(session, new byte[] { 1 }, CancellationToken.None)).ShouldBeTrue();
        await transaction.CommitAsync(CancellationToken.None);
        await using var later = await database.CreateSessionAsync(CancellationToken.None);
        (await database.ExistsAsync(later, new byte[] { 1 }, CancellationToken.None)).ShouldBeTrue();
    }
}
