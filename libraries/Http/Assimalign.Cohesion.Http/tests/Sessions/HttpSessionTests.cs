using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpSessionTests
{
    [Fact]
    public void SetString_TryGetString_RemoveAndClear_ShouldManageSessionState()
    {
        // Arrange
        HttpSession session = new("session-id");
        byte[] expectedBytes = [1, 2, 3];

        // Act
        session.Set("bytes", expectedBytes);
        session.SetString("name", "cohesion");
        bool foundBytes = session.TryGetValue("bytes", out byte[]? bytes);
        bool foundName = session.TryGetString("name", out string? name);
        session.Remove("bytes");
        bool foundAfterRemove = session.TryGetValue("bytes", out _);
        session.Clear();
        bool foundAfterClear = session.TryGetString("name", out _);

        // Assert
        foundBytes.ShouldBeTrue();
        bytes.ShouldBe(expectedBytes);
        foundName.ShouldBeTrue();
        name.ShouldBe("cohesion");
        foundAfterRemove.ShouldBeFalse();
        foundAfterClear.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_CancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        HttpSession session = new("session-id");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act
        var action = () => session.LoadAsync(cancellation.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(action);
    }
}
