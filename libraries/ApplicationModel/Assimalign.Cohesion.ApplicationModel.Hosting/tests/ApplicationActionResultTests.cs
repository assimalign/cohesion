using System;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class ApplicationActionResultTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ApplicationActionResult: ";

    [Fact(DisplayName = DisplayPrefix + "A typed output is written by the invocation's serializer")]
    public void Success_WithTypedOutput_ShouldCarrySerializedContent()
    {
        // Arrange & Act
        ApplicationActionResult result = ApplicationActionResult.Success(new LogLevelSetting("Debug"), TestSerializer.Json);

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        Encoding.UTF8.GetString(result.Output.Span).ShouldBe("""{"Level":"Debug"}""");
        result.State.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A rejection carries its reason and no output")]
    public void Rejected_WithReason_ShouldCarryReason()
    {
        // Arrange & Act
        ApplicationActionResult result = ApplicationActionResult.Rejected("A log level is required.");

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Rejected);
        result.Reason.ShouldBe("A log level is required.");
        result.Output.IsEmpty.ShouldBeTrue();
    }

    [Theory(DisplayName = DisplayPrefix + "A rejection requires a reason")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejected_WithoutReason_ShouldThrow(string reason)
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationActionResult.Rejected(reason));
    }
}
