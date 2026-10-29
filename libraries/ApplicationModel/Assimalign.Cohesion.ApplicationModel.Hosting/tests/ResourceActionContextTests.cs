using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class ResourceActionContextTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ResourceActionContext: ";

    [Theory(DisplayName = DisplayPrefix + "An invocation requests exactly one operation")]
    [InlineData(ResourceActionKind.None)]
    [InlineData(ResourceActionKind.ReadWrite)]
    [InlineData((ResourceActionKind)4)]
    public void Create_WithoutExactlyOneOperation_ShouldThrow(ResourceActionKind operation)
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentException>(() => ResourceActionContext.Create(operation, TestSerializer.Json));
    }

    [Fact(DisplayName = DisplayPrefix + "An invocation requires a content serializer")]
    public void Create_WithoutSerializer_ShouldThrow()
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentNullException>(() => ResourceActionContext.Create(ResourceActionKind.Read, null!));
    }

    [Fact(DisplayName = DisplayPrefix + "Input is read with the invocation's serializer")]
    public void ReadInput_WithMatchingContent_ShouldReturnValue()
    {
        // Arrange
        ResourceActionContext context = ResourceActionContext.Create(ResourceActionKind.Write, TestSerializer.Json, """{"Level":"Debug"}"""u8.ToArray());

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
        ResourceActionContext context = ResourceActionContext.Create(ResourceActionKind.Write, TestSerializer.Json);

        // Act & Assert
        Should.Throw<ResourceContentException>(() => context.ReadInput<LogLevelSetting>());
    }
}
