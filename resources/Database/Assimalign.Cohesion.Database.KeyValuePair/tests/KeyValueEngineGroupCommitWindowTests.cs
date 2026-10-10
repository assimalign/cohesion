using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// The engine refuses a group-commit window the storage would refuse, at engine creation and
/// before any file is touched (owner decision 26 of 2026-10-06). Before, the window reached the
/// storage setter unchecked, so every database create or open failed late, with the setter's
/// parameter name, and left a created file set on disk.
/// </summary>
public sealed class KeyValueEngineGroupCommitWindowTests
{
    [Theory(DisplayName = "Cohesion Test [KeyValueEngine] - Options: a non-positive or over-long group-commit window is refused at engine creation")]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-TimeSpan.TicksPerMillisecond)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Create_GroupCommitWindowOutOfRange_ShouldThrowBeforeTouchingAnyFile(long ticks)
    {
        // Arrange: Timeout.InfiniteTimeSpan is -1 ms, and TimeSpan.MaxValue is long.MaxValue ticks.
        var root = Path.Combine(Path.GetTempPath(), "cohesion-keyvalue-group-commit-window", Guid.NewGuid().ToString("N"));
        var options = new KeyValueDatabaseEngineOptions
        {
            RootPath = root,
            GroupCommitWindow = TimeSpan.FromTicks(ticks),
        };

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => KeyValueDatabaseEngine.Create("keyvalue-engine", options))
            .ParamName.ShouldBe(nameof(KeyValueDatabaseEngineOptions.GroupCommitWindow));
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [KeyValueEngine] - Options: one tick past the storage's longest group-commit window is refused, the longest is accepted")]
    public async Task Create_GroupCommitWindowAtTheStorageBound_ShouldAcceptOnlyUpToIt()
    {
        // Arrange
        var longest = Assimalign.Cohesion.Database.Storage.Storage.MaximumGroupCommitWindow;

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => KeyValueDatabaseEngine.Create("keyvalue-engine", new KeyValueDatabaseEngineOptions
        {
            GroupCommitWindow = longest + TimeSpan.FromTicks(1),
        })).ParamName.ShouldBe(nameof(KeyValueDatabaseEngineOptions.GroupCommitWindow));

        await using var engine = KeyValueDatabaseEngine.Create("keyvalue-engine", new KeyValueDatabaseEngineOptions { GroupCommitWindow = longest });
        engine.EngineOptions.GroupCommitWindow.ShouldBe(longest);
    }
}
