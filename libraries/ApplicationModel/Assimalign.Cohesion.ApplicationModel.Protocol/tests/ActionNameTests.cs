using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Protocol.Tests;

public class ActionNameTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ActionName: ";

    [Theory(DisplayName = DisplayPrefix + "Action names are hyphen-separated lowercase words")]
    [InlineData("a")]
    [InlineData("log-level")]
    [InlineData("flush-cache-2")]
    public void ActionName_TryCreate_WithWords_ShouldSucceed(string value)
    {
        // Arrange & Act
        bool created = ActionName.TryCreate(value, out _);

        // Assert
        created.ShouldBeTrue();
    }

    [Theory(DisplayName = DisplayPrefix + "Action names reject empty words and other characters")]
    [InlineData("")]
    [InlineData("Log")]
    [InlineData("-log")]
    [InlineData("log-")]
    [InlineData("log--level")]
    [InlineData("log_level")]
    [InlineData("log/level")]
    public void ActionName_TryCreate_WithMalformedWord_ShouldFail(string value)
    {
        // Arrange & Act
        bool created = ActionName.TryCreate(value, out _);

        // Assert
        created.ShouldBeFalse();
    }
}
