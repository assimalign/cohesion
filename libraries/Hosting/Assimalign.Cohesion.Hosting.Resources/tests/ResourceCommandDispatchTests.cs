using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public sealed class ResourceCommandDispatchTests
{
    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Commands: replay, ownership, and deletion share one dispatch seam")]
    public async Task ExecuteCommandAsync_WithReplayAndConflictingOwner_ShouldPreserveOwnership()
    {
        // Arrange
        IResourceControlPlane plane = ResourceControlPlane.Create(["test.set"]);
        var handler = new RecordingResourceCommandHandler();
        plane.RegisterCommandHandler(handler);
        var command = new ResourceCommand("first", "test.set", "appa", "key", Encoding.UTF8.GetBytes("value"));

        // Act
        await plane.ExecuteCommandAsync(command, CancellationToken.None);
        await plane.ExecuteCommandAsync(command, CancellationToken.None);
        ResourceCommandRejectedException conflict = await Should.ThrowAsync<ResourceCommandRejectedException>(
            () => plane.ExecuteCommandAsync(command with { Owner = "appb" }, CancellationToken.None).AsTask());
        await Should.ThrowAsync<ResourceCommandRejectedException>(
            () => plane.DeleteCommandAsync(command with { Owner = "appb" }, CancellationToken.None).AsTask());

        // Assert
        handler.Executions.ShouldBe(1);
        conflict.Detail.ShouldContain("appa", Case.Sensitive);
        conflict.Detail.ShouldContain("key", Case.Sensitive);
        ResourceCommandRejectedException reusedId = await Should.ThrowAsync<ResourceCommandRejectedException>(
            () => plane.ExecuteCommandAsync(command with { Owner = "appb", Key = "different" }, CancellationToken.None).AsTask());
        reusedId.Detail.ShouldContain("id 'first'", Case.Sensitive);
        plane.Commands.Count.ShouldBe(1);
        await plane.DeleteCommandAsync(command, CancellationToken.None);
        await plane.DeleteCommandAsync(command, CancellationToken.None);
        handler.Deletions.ShouldBe(1);
        plane.Commands.ShouldBeEmpty();
        await plane.ExecuteCommandAsync(command with { Owner = "appb" }, CancellationToken.None);
        handler.Executions.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Commands: invalid, unsupported, and refused commands are distinct")]
    public async Task ExecuteCommandAsync_WithInvalidOrRefusedCommand_ShouldRetainTypedDetail()
    {
        // Arrange
        IResourceControlPlane plane = ResourceControlPlane.Create(["test.set"]);
        var handler = new RecordingResourceCommandHandler { Reject = true };
        plane.RegisterCommandHandler(handler);
        var command = new ResourceCommand("first", "test.set", "appa", "key", ReadOnlyMemory<byte>.Empty);

        // Act / Assert
        await Should.ThrowAsync<ArgumentException>(() => plane.ExecuteCommandAsync(command with { Key = " " }, CancellationToken.None).AsTask());
        await Should.ThrowAsync<NotSupportedException>(() => plane.ExecuteCommandAsync(command with { Kind = "unknown" }, CancellationToken.None).AsTask());
        ResourceCommandRejectedException refused = await Should.ThrowAsync<ResourceCommandRejectedException>(
            () => plane.ExecuteCommandAsync(command, CancellationToken.None).AsTask());
        refused.Detail.ShouldBe("Provider rejected key 'key'.");
        plane.Commands.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Commands: updates cannot be deleted by stale declarations")]
    public async Task DeleteCommandAsync_AfterOwnedUpdate_ShouldRefuseStaleIdentity()
    {
        // Arrange
        IResourceControlPlane plane = ResourceControlPlane.Create(["test.set"]);
        var handler = new RecordingResourceCommandHandler();
        plane.RegisterCommandHandler(handler);
        var original = new ResourceCommand("first", "test.set", "appa", "key", Encoding.UTF8.GetBytes("old"));
        var updated = original with { Id = "second", Payload = Encoding.UTF8.GetBytes("new") };

        // Act
        await plane.ExecuteCommandAsync(original, CancellationToken.None);
        await plane.ExecuteCommandAsync(updated, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ResourceCommandRejectedException>(() => plane.DeleteCommandAsync(original, CancellationToken.None).AsTask());
        await Should.ThrowAsync<ResourceCommandRejectedException>(() => plane.ExecuteCommandAsync(updated with { Payload = original.Payload }, CancellationToken.None).AsTask());
        handler.Executions.ShouldBe(2);
        handler.Deletions.ShouldBe(0);
        plane.Commands[0].Id.ShouldBe("second");
    }
}
