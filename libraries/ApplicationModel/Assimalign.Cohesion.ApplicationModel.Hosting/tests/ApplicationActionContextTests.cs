using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class ApplicationActionContextTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ApplicationActionContext: ";

    [Theory(DisplayName = DisplayPrefix + "An invocation requests exactly one operation")]
    [InlineData(ApplicationActionKind.None)]
    [InlineData(ApplicationActionKind.ReadWrite)]
    [InlineData((ApplicationActionKind)4)]
    public void Create_WithoutExactlyOneOperation_ShouldThrow(ApplicationActionKind operation)
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationActionContext.Create(operation, TestSerializer.Json));
    }

    [Fact(DisplayName = DisplayPrefix + "An invocation requires a content serializer")]
    public void Create_WithoutSerializer_ShouldThrow()
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentNullException>(() => ApplicationActionContext.Create(ApplicationActionKind.Read, null!));
    }

    [Fact(DisplayName = DisplayPrefix + "Input is read with the invocation's serializer")]
    public void ReadInput_WithMatchingContent_ShouldReturnValue()
    {
        // Arrange
        ApplicationActionContext context = ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, """{"Level":"Debug"}"""u8.ToArray());

        // Act
        LogLevelSetting? setting = context.ReadInput<LogLevelSetting>();

        // Assert
        context.Serializer.ShouldBeSameAs(TestSerializer.Json);
        setting.ShouldBe(new LogLevelSetting("Debug"));
    }

    [Fact(DisplayName = DisplayPrefix + "Reading absent input is refused content")]
    public void ReadInput_WithNoInput_ShouldThrowResourceContentException()
    {
        // Arrange
        ApplicationActionContext context = ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json);

        // Act & Assert
        Should.Throw<ApplicationContentException>(() => context.ReadInput<LogLevelSetting>());
    }
}
