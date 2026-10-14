using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Validation;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.OpenApi.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.OpenApi.Tests;

/// <summary>
/// Security requirements derived from each endpoint's effective authorization policy, the one
/// <c>UseAuthorization</c> applies (#1205): the fallback policy on endpoints without authorization metadata,
/// named and default policies resolved to their schemes, the most specific <c>AllowAnonymous</c> in both
/// directions, an application without <c>AddAuthorization</c>, and an unregistered policy name. The
/// application's default authenticate scheme is <c>Bearer</c>; the document declares <c>Bearer</c> and
/// <c>ApiKey</c>.
/// </summary>
public class OpenApiSecurityRequirementTests
{
    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: the fallback policy protects endpoints without authorization metadata")]
    public async Task Describe_FallbackPolicy_ShouldProtectEndpointsWithoutAuthorizationMetadata()
    {
        // Arrange — FallbackPolicy = DefaultPolicy names no scheme, so the default authenticate scheme applies.
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(
            DeclareSchemes,
            options => options.FallbackPolicy = options.DefaultPolicy);

        factory.Application.UseRouting();
        factory.Application.MapGet("/count", () => 42);
        factory.Application.MapGet("/status", () => "up").AllowAnonymous();

        // Act
        OpenApiDocument document = await GetServedDocumentAsync(factory);

        // Assert
        GetRequiredSchemes(document, "/count", OperationType.Get).ShouldBe(["Bearer"]);
        GetRequiredSchemes(document, "/status", OperationType.Get).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: named and default policies are resolved to their own schemes")]
    public async Task Describe_RegisteredPolicies_ShouldListTheirSchemes()
    {
        // Arrange — partners selects ApiKey; admins names no scheme; the reconfigured default policy selects ApiKey.
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(DeclareSchemes, options =>
        {
            options.AddPolicy("partners", policy => policy.AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser());
            options.AddPolicy("admins", policy => policy.RequireRole("admin"));
            options.DefaultPolicy = new AuthorizationPolicyBuilder().AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser().Build();
        });

        factory.Application.UseRouting();
        factory.Application.MapGet("/partners", () => 42).RequireAuthorization("partners");
        factory.Application.MapGet("/admins", () => 42).RequireAuthorization("admins");
        factory.Application.MapGet("/profile", () => 42).RequireAuthorization();

        // Act
        OpenApiDocument document = await GetServedDocumentAsync(factory);
        OpenApiValidationResult validation = document.Validate();

        // Assert — a scheme the policy names replaces the default authenticate scheme, as it does for the middleware.
        validation.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        GetRequiredSchemes(document, "/partners", OperationType.Get).ShouldBe(["ApiKey"]);
        GetRequiredSchemes(document, "/admins", OperationType.Get).ShouldBe(["Bearer"]);
        GetRequiredSchemes(document, "/profile", OperationType.Get).ShouldBe(["ApiKey"]);
        document.Components!.SecuritySchemes["ApiKey"].Type.ShouldBe(SecuritySchemeType.ApiKey);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: an AllowAnonymous route inside a protected group is open")]
    public async Task Describe_AllowAnonymousInProtectedGroup_ShouldBeOpen()
    {
        // Arrange
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(
            DeclareSchemes,
            options => options.AddPolicy("partners", policy => policy.AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser()));

        factory.Application.UseRouting();
        IRouterGroupBuilder partners = factory.Application.MapGroup("/partners").RequireAuthorization("partners");
        partners.MapGet("catalog", () => 42).AllowAnonymous();
        partners.MapGet("orders", () => 42);

        // Act
        OpenApiDocument document = await GetServedDocumentAsync(factory);

        // Assert
        GetRequiredSchemes(document, "/partners/catalog", OperationType.Get).ShouldBeEmpty();
        GetRequiredSchemes(document, "/partners/orders", OperationType.Get).ShouldBe(["ApiKey"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: a protected route inside an AllowAnonymous group stays protected")]
    public async Task Describe_RequirementInAnonymousGroup_ShouldStayProtected()
    {
        // Arrange — the group's AllowAnonymous also keeps the fallback policy away from its other routes.
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(DeclareSchemes, options =>
        {
            options.AddPolicy("partners", policy => policy.AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser());
            options.FallbackPolicy = options.DefaultPolicy;
        });

        factory.Application.UseRouting();
        IRouterGroupBuilder open = factory.Application.MapGroup("/open").AllowAnonymous();
        open.MapGet("settings", () => 42).RequireAuthorization("partners");
        open.MapGet("about", () => 42);

        // Act
        OpenApiDocument document = await GetServedDocumentAsync(factory);

        // Assert
        GetRequiredSchemes(document, "/open/settings", OperationType.Get).ShouldBe(["ApiKey"]);
        GetRequiredSchemes(document, "/open/about", OperationType.Get).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: an application without AddAuthorization is described with the default options")]
    public async Task Describe_WithoutAddAuthorization_ShouldDescribeTheDefaults()
    {
        // Arrange — no fallback policy, so an unannotated endpoint is open; a declared requirement is never
        // described as open, although without the middleware it fails at dispatch.
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactoryWithoutAuthorization(DeclareSchemes);

        factory.Application.UseRouting();
        factory.Application.MapGet("/count", () => 42);
        factory.Application.MapGet("/profile", () => 42).RequireAuthorization();
        factory.Application.MapGet("/status", () => "up").AllowAnonymous();

        // Act
        OpenApiDocument document = await GetServedDocumentAsync(factory);

        // Assert
        GetRequiredSchemes(document, "/count", OperationType.Get).ShouldBeEmpty();
        GetRequiredSchemes(document, "/profile", OperationType.Get).ShouldBe(["Bearer"]);
        GetRequiredSchemes(document, "/status", OperationType.Get).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Security: an unregistered policy name fails the document and names the endpoint")]
    public async Task Describe_UnregisteredPolicyName_ShouldFailNamingTheEndpoint()
    {
        // Arrange — the same composition error fails every request to the endpoint at run time.
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory(DeclareSchemes);

        factory.Application.UseRouting();
        factory.Application.MapGet("/ping", () => "pong");
        factory.Application.MapGet("/admin", () => 42).RequireAuthorization("admins");

        // Starting the application builds the router, closing the route table.
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage ping = await client.GetAsync("/ping", cancellation.Token);

        // Act
        Action act = () => factory.Application.GetOpenApiDescriptionProvider().GetDocument(OpenApiSpecVersion.V3_1);

        // Assert
        ping.IsSuccessStatusCode.ShouldBeTrue();
        InvalidOperationException exception = act.ShouldThrow<InvalidOperationException>();
        exception.Message.ShouldContain("GET /admin", Case.Sensitive);
        exception.Message.ShouldContain("'admins'", Case.Sensitive);
    }

    private static void DeclareSchemes(OpenApiOptions options)
    {
        options.AddSecurityScheme(OpenApiTestApplication.BearerScheme);
        options.AddSecurityScheme(OpenApiTestApplication.ApiKeyScheme);
    }

    // Maps the document, starts the application, and reads the served JSON back.
    private static async Task<OpenApiDocument> GetServedDocumentAsync(WebApplicationTestFactory factory)
    {
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        factory.Application.MapOpenApi();

        using HttpClient client = factory.CreateClient();

        return await OpenApiTestApplication.GetDocumentAsync(client, OpenApiWebApplicationExtensions.DefaultDocumentPattern, cancellation.Token);
    }

    // The schemes an operation lists, in order: one requirement object (an alternative) per scheme, each
    // with no scopes.
    private static List<string> GetRequiredSchemes(OpenApiDocument document, string path, OperationType method)
    {
        List<string> schemes = [];

        foreach (OpenApiSecurityRequirement requirement in document.Paths!.Items[path].Operations[method].Security)
        {
            KeyValuePair<string, IList<string>> scheme = requirement.Schemes.ShouldHaveSingleItem();
            scheme.Value.ShouldBeEmpty();
            schemes.Add(scheme.Key);
        }

        return schemes;
    }
}
