using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Integration;
using Assimalign.Cohesion.OpenApi.Validation;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.OpenApi.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.OpenApi.Tests;

/// <summary>
/// The document options: defaults, validation, read-only capture, document transformers, extra endpoint
/// sources, security-scheme matching, and the composition errors <c>AddOpenApi</c> and the provider report.
/// </summary>
public class OpenApiOptionsTests
{
    private static async Task<OpenApiDocument> StartAndDescribeAsync(WebApplicationTestFactory factory, OpenApiSpecVersion version = OpenApiSpecVersion.V3_1)
    {
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/__start", cancellation.Token);

        return factory.Application.GetOpenApiDescriptionProvider().GetDocument(version);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Options: defaults name the application and target OpenAPI 3.1")]
    public void OpenApiOptions_Defaults_ShouldTarget31()
    {
        // Arrange / Act
        OpenApiOptions options = new();

        // Assert
        options.Title.ShouldNotBeNullOrWhiteSpace();
        options.ApiVersion.ShouldBe("1.0.0");
        options.Description.ShouldBeNull();
        options.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
        options.SecuritySchemes.ShouldBeEmpty();
        options.Tags.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Options: invalid values and duplicate declarations are rejected")]
    public void OpenApiOptions_InvalidValues_ShouldThrow()
    {
        // Arrange
        OpenApiOptions options = new();
        options.AddSecurityScheme(OpenApiTestApplication.BearerScheme);
        options.AddTag(new OpenApiTagMetadata { Name = "orders" });

        // Act / Assert
        Should.Throw<ArgumentException>(() => options.Title = " ");
        Should.Throw<ArgumentException>(() => options.ApiVersion = string.Empty);
        Should.Throw<ArgumentOutOfRangeException>(() => options.SpecVersion = (OpenApiSpecVersion)42);
        Should.Throw<InvalidOperationException>(() => options.AddSecurityScheme(new OpenApiSecuritySchemeMetadata { Name = "Bearer", Type = SecuritySchemeType.Http, Scheme = "bearer" }));
        Should.Throw<InvalidOperationException>(() => options.AddTag(new OpenApiTagMetadata { Name = "orders" }));
        Should.Throw<ArgumentNullException>(() => options.AddEndpointSource(null!));
        Should.Throw<ArgumentNullException>(() => options.AddDocumentTransformer(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - AddOpenApi: the options are read-only once the callback returns")]
    public async Task AddOpenApi_AfterCallback_ShouldMakeOptionsReadOnly()
    {
        // Arrange
        OpenApiOptions? captured = null;

        // Act
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(options => captured = options);

        // Assert
        captured.ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => captured.Title = "Changed");
        Should.Throw<InvalidOperationException>(() => captured.Description = "Changed");
        Should.Throw<InvalidOperationException>(() => captured.AddTag(new OpenApiTagMetadata { Name = "late" }));
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Options: document transformers edit every built document")]
    public async Task AddDocumentTransformer_OnBuild_ShouldEditDocument()
    {
        // Arrange
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(options => options
            .AddDocumentTransformer(document => document.Servers.Add(new OpenApiServer { Url = "https://api.example.com" }))
            .AddDocumentTransformer(document => document.Info.Contact = new OpenApiContact { Name = "Orders team" }));
        factory.Application.UseRouting();
        factory.Application.MapGet("/ping", () => "pong");

        // Act
        OpenApiDocument document = await StartAndDescribeAsync(factory);

        // Assert
        document.Servers.ShouldHaveSingleItem().Url.ShouldBe("https://api.example.com");
        document.Info.Contact!.Name.ShouldBe("Orders team");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Options: an extra endpoint source is composed after the routes")]
    public async Task AddEndpointSource_ExtraSource_ShouldComposeIntoDocument()
    {
        // Arrange
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(options => options.AddEndpointSource(new LegacySource()));
        factory.Application.UseRouting();
        factory.Application.MapGet("/ping", () => "pong");

        // Act
        OpenApiDocument document = await StartAndDescribeAsync(factory);

        // Assert
        document.Paths!.Items.Keys.ShouldBe(["/ping", "/legacy/status"]);
        document.Paths.Items["/legacy/status"].Operations[OperationType.Get].OperationId.ShouldBe("legacyStatus");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: AllowAnonymous on a route clears its group's requirement")]
    public async Task Security_AllowAnonymousInProtectedGroup_ShouldListNoRequirement()
    {
        // Arrange
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(options => options.AddSecurityScheme(OpenApiTestApplication.BearerScheme));
        factory.Application.UseRouting();
        IRouterGroupBuilder admin = factory.Application.MapGroup("admin").RequireAuthorization();
        admin.MapGet("reports", () => "report");
        admin.MapGet("status", () => "up").AllowAnonymous();

        // Act
        OpenApiDocument document = await StartAndDescribeAsync(factory);

        // Assert
        document.Paths!.Items["/admin/reports"].Operations[OperationType.Get].Security.ShouldHaveSingleItem().Schemes.Keys.ShouldBe(["Bearer"]);
        document.Paths.Items["/admin/status"].Operations[OperationType.Get].Security.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: a policy's schemes are listed as alternatives when declared")]
    public async Task Security_PolicySchemes_ShouldListDeclaredSchemes()
    {
        // Arrange — "Legacy" is used but not declared, so it cannot be referenced.
        OpenApiSecuritySchemeMetadata cookies = new() { Name = "Cookies", Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, ParameterName = ".Cohesion.Cookies" };
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(options => options
            .AddSecurityScheme(OpenApiTestApplication.BearerScheme)
            .AddSecurityScheme(cookies));
        factory.Application.UseRouting();
        factory.Application.MapGet("/account", () => "me")
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().AddAuthenticationSchemes("Cookies", "Legacy", "Bearer"));

        // Act
        OpenApiDocument document = await StartAndDescribeAsync(factory);

        // Assert
        IList<OpenApiSecurityRequirement> security = document.Paths!.Items["/account"].Operations[OperationType.Get].Security;
        security.Count.ShouldBe(2);
        security[0].Schemes.Keys.ShouldBe(["Cookies"]);
        security[1].Schemes.Keys.ShouldBe(["Bearer"]);
        document.Validate().IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - GetDocument: a result type without a JSON contract names the endpoint and the fix")]
    public async Task GetDocument_UnregisteredResultType_ShouldThrowNamingEndpoint()
    {
        // Arrange
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/broken/{id:int}", (int id) => new Unregistered(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/__start", cancellation.Token);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => factory.Application.GetOpenApiDescriptionProvider().GetDocument(OpenApiSpecVersion.V3_1));

        // Assert
        exception.Message.ShouldContain("GET /broken/{id}", Case.Sensitive);
        exception.Message.ShouldContain("JsonSerializable", Case.Sensitive);
        exception.Message.ShouldContain(nameof(Unregistered), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - GetOpenApiDescriptionProvider: reading the document without AddOpenApi fails")]
    public async Task GetOpenApiDescriptionProvider_WithoutAddOpenApi_ShouldThrow()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => factory.Application.GetOpenApiDescriptionProvider());
    }

    /// <summary>An endpoint source the router does not map, standing in for a generated attribute registry.</summary>
    private sealed class LegacySource : IOpenApiEndpointSource
    {
        public IReadOnlyList<OpenApiOperationMetadata> Operations { get; } =
        [
            new OpenApiOperationMetadata
            {
                Method = OperationType.Get,
                Path = "/legacy/status",
                OperationId = "legacyStatus",
                Responses = [new OpenApiResponseMetadata { StatusCode = "200", Description = "OK" }]
            }
        ];

        public IReadOnlyList<OpenApiSchemaMetadata> Schemas { get; } = [];

        public IReadOnlyList<OpenApiTagMetadata> Tags { get; } = [];

        public IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes { get; } = [];
    }
}
