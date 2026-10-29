using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class JsonResourceContentSerializerTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - JsonResourceContentSerializer: ";

    [Fact(DisplayName = DisplayPrefix + "Each type is written with the options of the context that declares it")]
    public void Serialize_TypesFromDifferentContexts_ShouldKeepEachContextsNaming()
    {
        // Arrange
        ResourceManifest manifest = ResourceControlPlane.Create(new TestResource(), TestSerializer.Json).Manifest;

        // Act
        string setting = Encoding.UTF8.GetString(TestSerializer.Json.Serialize(new LogLevelSetting("Debug")).Span);
        string published = Encoding.UTF8.GetString(TestSerializer.Json.Serialize(manifest).Span);

        // Assert
        TestSerializer.Json.MediaType.ShouldBe("application/json");
        setting.ShouldBe("""{"Level":"Debug"}""");
        published.ShouldContain("\"provider\":\"Web\"", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A value survives a round trip")]
    public void Deserialize_SerializedValue_ShouldRoundTrip()
    {
        // Arrange
        ReadOnlyMemory<byte> content = TestSerializer.Json.Serialize(new LogLevelSetting("Warning"));

        // Act
        LogLevelSetting? read = TestSerializer.Json.Deserialize<LogLevelSetting>(content);

        // Assert
        read.ShouldBe(new LogLevelSetting("Warning"));
    }

    [Fact(DisplayName = DisplayPrefix + "A type no context declares is not supported")]
    public void Serialize_UndeclaredType_ShouldThrowNotSupported()
    {
        // Arrange
        JsonResourceContentSerializer serializer = JsonResourceContentSerializer.Create();

        // Act & Assert
        Should.Throw<NotSupportedException>(() => serializer.Serialize(new LogLevelSetting("Debug")));
        Should.Throw<NotSupportedException>(() => serializer.Deserialize<LogLevelSetting>("""{"Level":"Debug"}"""u8.ToArray()));
    }

    [Theory(DisplayName = DisplayPrefix + "Content that is not a value of the type is refused content")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"Level":5}""")]
    public void Deserialize_MalformedContent_ShouldThrowResourceContentException(string content)
    {
        // Act
        ResourceContentException exception = Should.Throw<ResourceContentException>(() =>
            TestSerializer.Json.Deserialize<LogLevelSetting>(Encoding.UTF8.GetBytes(content)));

        // Assert
        exception.InnerException.ShouldBeOfType<JsonException>();
        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = DisplayPrefix + "Creation refuses a null context")]
    public void Create_WithNullContext_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => JsonResourceContentSerializer.Create(null!));
        Should.Throw<ArgumentException>(() => JsonResourceContentSerializer.Create(TestJsonContext.Default, (JsonSerializerContext)null!));
    }
}
