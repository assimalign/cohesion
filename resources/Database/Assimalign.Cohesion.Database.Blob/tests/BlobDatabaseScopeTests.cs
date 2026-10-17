using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobDatabaseScopeTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Bound session: Should keep containers inside its database")]
    public async Task GetContainerAsync_BoundSession_ShouldKeepContainersInsideItsDatabase()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var own = await engine.CreateDatabaseAsync("own");
        var other = await engine.CreateDatabaseAsync("other");
        var ownContainer = await AutocommitContainer.CreateAsync(own, "files");
        var otherContainer = await AutocommitContainer.CreateAsync(other, "files");
        await BlobEngineTests.WriteAsync(ownContainer, "item", "own"u8.ToArray());
        await BlobEngineTests.WriteAsync(otherContainer, "item", "other"u8.ToArray());
        await using var session = await own.CreateSessionAsync();

        // Act
        var value = await BlobEngineTests.ReadAsync(await session.GetContainerAsync("files"), "item");

        // Assert: every container lookup stays bound to this database.
        session.Database.Name.ShouldBe(own.Name);
        value.ShouldBe("own"u8.ToArray());
        await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("other/files"));
        (await BlobEngineTests.ReadAsync(otherContainer, "item")).ShouldBe("other"u8.ToArray());
        engine.TryGetDatabase("other", out _).ShouldBeTrue();

        // Act: a container obtained from the session is bound to its lifetime too.
        var boundContainer = await session.GetContainerAsync("files");
        await session.DisposeAsync();

        // Assert
        await Should.ThrowAsync<DatabaseException>(async () => await boundContainer.OpenReadAsync("item"));
    }
}
