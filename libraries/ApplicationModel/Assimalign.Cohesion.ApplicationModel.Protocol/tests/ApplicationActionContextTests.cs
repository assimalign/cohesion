using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Protocol.Tests;

public class ApplicationActionContextTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationActionContext: ";

    [Theory(DisplayName = DisplayPrefix + "An invocation requests exactly one operation")]
    [InlineData((ApplicationActionOperation)0)]
    [InlineData((ApplicationActionOperation)3)]
    [InlineData((ApplicationActionOperation)4)]
    public void Create_WithoutExactlyOneOperation_ShouldThrow(ApplicationActionOperation operation)
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationActionContext.Create(operation, TestSerializer.Json));
    }

    [Fact(DisplayName = DisplayPrefix + "An invocation requires a content serializer")]
    public void Create_WithoutSerializer_ShouldThrow()
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentNullException>(() => ApplicationActionContext.Create(ApplicationActionOperation.Read, null!));
    }

    [Fact(DisplayName = DisplayPrefix + "Input is read with the invocation's serializer")]
    public void ReadInput_WithMatchingContent_ShouldReturnValue()
    {
        // Arrange
        ApplicationActionContext context = ApplicationActionContext.Create(ApplicationActionOperation.Write, TestSerializer.Json, """{"Level":"Debug"}"""u8.ToArray());

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
        ApplicationActionContext context = ApplicationActionContext.Create(ApplicationActionOperation.Write, TestSerializer.Json);

        // Act & Assert
        Should.Throw<ApplicationContentException>(() => context.ReadInput<LogLevelSetting>());
    }
}
