using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentFunctionReviewTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: captured sessions refuse reentry before a nested write")]
    public async Task ExecuteAsync_ReentrantCallback_ShouldRefuseBeforeNestedWriteAsync()
    {
        // Arrange
        DocumentDatabaseSession? captured = null;
        DatabaseException? nested = null;
        var builder = DocumentDatabaseEngine.CreateBuilder("reentry");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("reenter", value =>
        {
            try { captured!.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('nested', {n:9})", cancellationToken: CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
            catch (DatabaseException error) { nested = error; }
            throw new InvalidOperationException("outer failed");
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        captured = session;
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "original", "{\"n\":1}"u8.ToArray(), cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT reenter(n) FROM items", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05017");
        nested.ShouldNotBeNull().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK07003");
        await using var later = await database.CreateSessionAsync(CancellationToken.None);
        var laterCollection = await later.GetCollectionAsync("items", CancellationToken.None);
        (await laterCollection.GetAsync(later, "nested", CancellationToken.None)).ShouldBeNull();
    }
}
