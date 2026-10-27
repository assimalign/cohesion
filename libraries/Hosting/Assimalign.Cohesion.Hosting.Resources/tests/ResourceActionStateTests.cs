using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceActionStateTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ResourceActionState: ";

    [Fact(DisplayName = DisplayPrefix + "Creation applies nothing: the code-defined value is already in effect")]
    public void Create_WithInitialValue_ShouldNotApply()
    {
        // Arrange
        var applied = new List<LogLevelSetting>();

        // Act
        var state = ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), TestJsonContext.Default.LogLevelSetting, applied.Add);

        // Assert
        state.Value.Level.ShouldBe("Information");
        applied.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A set value is applied, then becomes current")]
    public async Task SetAsync_WithValue_ShouldApplyThenCommit()
    {
        // Arrange
        var applied = new List<LogLevelSetting>();
        var state = ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), TestJsonContext.Default.LogLevelSetting, applied.Add);

        // Act
        await state.SetAsync(new LogLevelSetting("Debug"), CancellationToken.None);

        // Assert
        state.Value.Level.ShouldBe("Debug");
        applied.ShouldHaveSingleItem().Level.ShouldBe("Debug");
    }

    [Fact(DisplayName = DisplayPrefix + "A value whose apply fails never becomes current")]
    public async Task SetAsync_WhenApplyThrows_ShouldKeepPreviousValue()
    {
        // Arrange
        var state = ResourceActionState<LogLevelSetting>.Create(
            new LogLevelSetting("Information"),
            TestJsonContext.Default.LogLevelSetting,
            (_, _) => throw new InvalidOperationException("The logging pipeline refused the level."));

        // Act
        await Should.ThrowAsync<InvalidOperationException>(async () => await state.SetAsync(new LogLevelSetting("Debug"), CancellationToken.None));

        // Assert
        state.Value.Level.ShouldBe("Information");
        Encoding.UTF8.GetString(state.GetSnapshot().Span).ShouldBe("""{"Level":"Information"}""");
    }

    [Fact(DisplayName = DisplayPrefix + "A snapshot restored into another instance's state applies the same value")]
    public async Task RestoreAsync_WithAnotherInstancesSnapshot_ShouldApplyItsValue()
    {
        // Arrange
        var source = ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Warning"), TestJsonContext.Default.LogLevelSetting, _ => { });
        var applied = new List<LogLevelSetting>();
        var target = ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), TestJsonContext.Default.LogLevelSetting, applied.Add);

        // Act
        await target.RestoreAsync(source.GetSnapshot(), CancellationToken.None);

        // Assert
        target.Value.ShouldBe(source.Value);
        applied.ShouldHaveSingleItem().Level.ShouldBe("Warning");
    }

    [Theory(DisplayName = DisplayPrefix + "A snapshot that is not a value of the state's type is refused")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("""{"Level":5}""")]
    public async Task RestoreAsync_WithInvalidSnapshot_ShouldThrowJsonException(string snapshot)
    {
        // Arrange
        var applied = new List<LogLevelSetting>();
        var state = ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), TestJsonContext.Default.LogLevelSetting, applied.Add);

        // Act
        await Should.ThrowAsync<JsonException>(async () => await state.RestoreAsync(Encoding.UTF8.GetBytes(snapshot), CancellationToken.None));

        // Assert
        state.Value.Level.ShouldBe("Information");
        applied.ShouldBeEmpty();
    }
}
