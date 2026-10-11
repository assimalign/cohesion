using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.Validation.Internal;
using Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Validation.Tests;

public sealed class ValidationJsonPathTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Validation] - Error map: Should use the registered JSON contract")]
    [InlineData("order => order.DisplayName", "display_name")]
    [InlineData("order => order.Address.City", "ship_to.city")]
    [InlineData("order => order.Items[2].Quantity", "items[2].quantity")]
    [InlineData("order => order.Batches[1].Items[2].Quantity", "batches[1].items[2].quantity")]
    [InlineData("order => order.Items[2]", "items[2]")]
    [InlineData("order => order.Matrix[1][2].Quantity", "matrix[1][2].quantity")]
    [InlineData("order => order.EmptyNamedAddress.City", ".city")]
    public void Create_RegisteredContract_ShouldUseJsonNamesAndIndexes(string source, string expected)
    {
        // Arrange
        IHttpContentSerializationFeature serialization = new ContentSerializationBuilder()
            .AddJson(ValidationOrderJsonContext.Default, options => options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
            .Build();
        IValidationError[] errors = [new ValidationError { Source = source, Message = "invalid" }];

        // Act
        Dictionary<string, object?> map = ValidationErrorMap.Create(errors, typeof(ValidationOrder), serialization);

        // Assert
        map.Keys.ShouldBe([expected]);
        map[expected].ShouldBe(new[] { "invalid" });
    }

    [Theory(DisplayName = "Cohesion Test [Web.Validation] - Error map: Should preserve explicit sources and CLR fallback")]
    [InlineData(false, "order => order.Items[2].Quantity", "Items[2].Quantity")]
    [InlineData(false, "order => order.Address.City", "Address.City")]
    [InlineData(true, "Address.City", "Address.City")]
    [InlineData(true, "chosen_field", "chosen_field")]
    [InlineData(true, null, "")]
    [InlineData(true, "order => order.Items[bad].Quantity", "Items[bad].Quantity")]
    [InlineData(true, "order => order.Items[-1].Quantity", "Items[-1].Quantity")]
    public void Create_UnregisteredOrExplicitSource_ShouldPreserveKey(bool registered, string? source, string expected)
    {
        // Arrange
        ContentSerializationBuilder builder = new();
        if (registered)
        {
            builder.AddJson(ValidationOrderJsonContext.Default);
        }
        IValidationError[] errors = [new ValidationError { Source = source!, Message = "invalid" }];

        // Act
        Dictionary<string, object?> map = ValidationErrorMap.Create(errors, typeof(ValidationOrder), builder.Build());

        // Assert
        map.Keys.ShouldBe([expected]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Error map: Should isolate application naming policies")]
    public void Create_DifferentApplicationContracts_ShouldUseEachRegisteredPolicy()
    {
        // Arrange
        IHttpContentSerializationFeature camel = new ContentSerializationBuilder()
            .AddJson(ValidationOrderJsonContext.Default).Build();
        IHttpContentSerializationFeature unchanged = new ContentSerializationBuilder()
            .AddJson(ValidationOrderJsonContext.Default, options => options.PropertyNamingPolicy = null).Build();
        IValidationError[] errors = [new ValidationError { Source = "order => order.Items[2].Quantity", Message = "invalid" }];

        // Act
        Dictionary<string, object?> camelMap = ValidationErrorMap.Create(errors, typeof(ValidationOrder), camel);
        Dictionary<string, object?> unchangedMap = ValidationErrorMap.Create(errors, typeof(ValidationOrder), unchanged);

        // Assert
        camelMap.Keys.ShouldBe(["items[2].quantity"]);
        unchangedMap.Keys.ShouldBe(["Items[2].Quantity"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Error map: Should honor an application's modified contract")]
    public void Create_CustomContractResolver_ShouldUseFinalPropertyName()
    {
        // Arrange
        IHttpContentSerializationFeature serialization = new ContentSerializationBuilder()
            .AddJson(new RenamedContractResolver()).Build();
        IValidationError[] errors = [new ValidationError { Source = "order => order.DisplayName", Message = "invalid" }];

        // Act
        Dictionary<string, object?> map = ValidationErrorMap.Create(errors, typeof(ValidationOrder), serialization);

        // Assert
        map.Keys.ShouldBe(["caption"]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Validation] - Typed: Should return JSON names and distinct collection indexes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapPost_InvalidJsonBody_ShouldReportJsonPaths(bool throwOnFailure)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        IValidator validator = Validator.Create(builder =>
        {
            builder.AddProfile(new ValidationOrderProfile());
            builder.AddOptions(options => options.ThrowExceptionOnFailure = throwOnFailure);
        });
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddJsonSerialization(ValidationOrderJsonContext.Default,
            options => options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        factory.Builder.Services.AddValidation(validation => validation.Options.AddValidator(validator));
        factory.Application.UseRouting();
        int invocations = 0;
        factory.Application.MapPost("/orders", (ValidationOrder order) =>
        {
            invocations++;
            return "accepted";
        });
        using HttpClient client = factory.CreateClient();
        using StringContent body = new(
            """{"display_name":"","ship_to":{"city":""},"items":[{"quantity":0},{"quantity":3},{"quantity":-1}]}""",
            Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/orders", body, cancellation.Token);

        // Assert — RFC 9457 standard members and an object-valued errors extension coexist.
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
        problem.RootElement.GetProperty("type").GetString().ShouldBe("about:blank");
        JsonElement errors = problem.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(error => error.Name)
            .ShouldBe(["display_name", "ship_to.city", "items[0].quantity", "items[2].quantity"]);
        foreach (JsonProperty error in errors.EnumerateObject())
        {
            error.Value.GetArrayLength().ShouldBe(1);
            error.Value[0].GetString().ShouldNotBeNullOrEmpty();
        }
        invocations.ShouldBe(0);
    }

    private sealed class RenamedContractResolver : IJsonTypeInfoResolver, IValidationJsonPropertyResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            JsonTypeInfo? contract = ((IJsonTypeInfoResolver)ValidationOrderJsonContext.Default).GetTypeInfo(type, options);
            if (contract?.Type == typeof(ValidationOrder))
            {
                contract.Properties.Single(property => property.Name == "display_name").Name = "caption";
            }
            return contract;
        }

        public JsonPropertyInfo? GetProperty(JsonTypeInfo typeInfo, string memberName)
        {
            return typeInfo.Type == typeof(ValidationOrder) && memberName == nameof(ValidationOrder.DisplayName)
                ? typeInfo.Properties.Single(property => property.Name == "caption")
                : null;
        }
    }
}
