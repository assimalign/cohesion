using System.Collections.Generic;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

public class HttpCookieJsonSerializationTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Serialize: External context round-trips a name as a JSON string")]
    public void Serialize_ExternalContextModel_ShouldRoundTripNameAsString()
    {
        // Arrange
        var model = new HttpCookieNameModel { Name = "session" };
        var typeInfo = HttpCookieValueTypeJsonContext.Default.HttpCookieNameModel;

        // Act
        string json = JsonSerializer.Serialize(model, typeInfo);
        var restored = JsonSerializer.Deserialize(json, typeInfo);

        // Assert
        json.ShouldBe("{\"Name\":\"session\"}");
        restored.ShouldNotBeNull();
        restored.Name.ShouldBe(model.Name);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Serialize: External context round-trips a default name as JSON null")]
    public void Serialize_ExternalContextDefaultName_ShouldRoundTripNull()
    {
        // Arrange
        var model = new HttpCookieNameModel();
        var typeInfo = HttpCookieValueTypeJsonContext.Default.HttpCookieNameModel;

        // Act
        string json = JsonSerializer.Serialize(model, typeInfo);
        var restored = JsonSerializer.Deserialize(json, typeInfo);

        // Assert
        json.ShouldBe("{\"Name\":null}");
        restored.ShouldNotBeNull();
        restored.Name.ShouldBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Deserialize: External context rejects an invalid name with JsonException")]
    public void Deserialize_ExternalContextInvalidName_ShouldThrowJsonException()
    {
        // Arrange
        const string json = "{\"Name\":\"invalid name\"}";
        var typeInfo = HttpCookieValueTypeJsonContext.Default.HttpCookieNameModel;

        // Act / Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, typeInfo));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Serialize: External context round-trips cookie-name dictionary keys")]
    public void Serialize_ExternalContextDictionary_ShouldRoundTripNameKeys()
    {
        // Arrange
        var values = new Dictionary<HttpCookieName, int> { ["session"] = 1 };
        var typeInfo = HttpCookieValueTypeJsonContext.Default.DictionaryHttpCookieNameInt32;

        // Act
        string json = JsonSerializer.Serialize(values, typeInfo);
        var restored = JsonSerializer.Deserialize(json, typeInfo);

        // Assert
        json.ShouldBe("{\"session\":1}");
        restored.ShouldNotBeNull();
        restored.Count.ShouldBe(1);
        restored[(HttpCookieName)"session"].ShouldBe(1);
    }
}
