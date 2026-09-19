using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobDatabaseScopeTests
{
    [Theory]
    [InlineData("USE other")]
    [InlineData("CREATE DATABASE injected")]
    [InlineData("DROP DATABASE other")]
    [InlineData("SELECT * FROM other.files")]
    [InlineData("GET other/files/item")]
    [InlineData("SHOW DATABASES")]
    public async Task Session_commands_cannot_reach_another_database_or_server(string command)
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var own = (IBlobDatabase)await engine.CreateDatabaseAsync("own");
        var other = (IBlobDatabase)await engine.CreateDatabaseAsync("other");
        var ownContainer = await own.CreateContainerAsync("files");
        var otherContainer = await other.CreateContainerAsync("files");
        await BlobEngineTests.Write(ownContainer, "item", "own"u8.ToArray());
        await BlobEngineTests.Write(otherContainer, "item", "other"u8.ToArray());
        await using var session = await own.CreateSessionAsync();
        session.Database.Name.ShouldBe(own.Name);
        var scoped = (IBlobDatabase)session.Database;
        (await BlobEngineTests.Read(await scoped.GetContainerAsync("files"), "item")).ShouldBe("own"u8.ToArray());
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(command));
        await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("other/files"));
        (await BlobEngineTests.Read(otherContainer, "item")).ShouldBe("other"u8.ToArray());
        engine.TryGetDatabase("other", out _).ShouldBeTrue();
        engine.TryGetDatabase("injected", out _).ShouldBeFalse();
        var boundContainer = await scoped.GetContainerAsync("files");
        await session.DisposeAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await boundContainer.OpenReadAsync("item"));
    }
}
