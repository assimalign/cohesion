using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

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
        Should.Throw<ArgumentException>(() => ResourceActionContext.Create(operation));
    }

    [Fact(DisplayName = DisplayPrefix + "Input is read through source-generated metadata")]
    public void ReadInput_WithMatchingJson_ShouldReturnValue()
    {
        // Arrange
        ResourceActionContext context = ResourceActionContext.Create(ResourceActionKind.Write, """{"Level":"Debug"}"""u8.ToArray());

        // Act
        LogLevelSetting? setting = context.ReadInput(TestJsonContext.Default.LogLevelSetting);

        // Assert
        setting.ShouldBe(new LogLevelSetting("Debug"));
    }

    [Fact(DisplayName = DisplayPrefix + "Reading absent input throws JsonException")]
    public void ReadInput_WithNoInput_ShouldThrowJsonException()
    {
        // Arrange
        ResourceActionContext context = ResourceActionContext.Create(ResourceActionKind.Write);

        // Act & Assert
        Should.Throw<JsonException>(() => context.ReadInput(TestJsonContext.Default.LogLevelSetting));
    }
}
