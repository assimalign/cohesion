using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Validation.Tests;

/// <summary>
/// Source-generated typed endpoints in an application that references Web.Validation: the bound
/// request-body model is validated before the handler runs, an invalid one is answered with a
/// <c>400</c> problem carrying an <c>errors</c> map, and the application, a group or an endpoint turns
/// validation off or on.
/// </summary>
public class ValidationEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private const string validCustomer = """{"name":"Ada","age":36,"address":{"city":"London"}}""";
    private const string invalidCustomer = """{"name":"","age":12}""";

    // Every member fails, and the empty user name fails both of its chained rules.
    private const string invalidSignup = """{"userName":"","age":12,"address":{"city":""}}""";

    private static WebApplicationTestFactory CreateFactory(Action<EndpointValidationOptions>? configure = null, bool registerValidation = true)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddJsonSerialization(ValidationTestJsonContext.Default);

        if (registerValidation)
        {
            factory.Builder.AddValidation(options =>
            {
                options.AddProfile(new CustomerProfile());
                configure?.Invoke(options);
            });
        }

        factory.Application.UseRouting();
        return factory;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadErrorsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("One or more validation errors occurred.");
        return problem.RootElement.GetProperty("errors").Clone();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Typed: a valid body reaches the handler")]
    public async Task MapPost_ValidBody_ShouldRunHandler()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/customers", (Customer customer) => customer.Name);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json(validCustomer), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("Ada");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Typed: an invalid body is answered with 400 and an errors map, and the handler does not run")]
    public async Task MapPost_InvalidBody_ShouldAnswer400WithErrors()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        int invocations = 0;
        factory.Application.MapPost("/customers", (Customer customer) =>
        {
            invocations++;
            return customer.Name;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json(invalidCustomer), cancellation.Token);

        // Assert — each failing member, keyed by its path, with its messages as an array.
        JsonElement errors = await ReadErrorsAsync(response, cancellation.Token);
        errors.GetProperty("Name").GetArrayLength().ShouldBe(1);
        errors.GetProperty("Name")[0].GetString().ShouldNotBeNullOrEmpty();
        errors.GetProperty("Age")[0].GetString()!.ShouldContain("18", Case.Sensitive);
        errors.TryGetProperty("Address.City", out _).ShouldBeFalse();
        invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Typed: a nested member's error is reported under its member path")]
    public async Task MapPost_InvalidNestedMember_ShouldReportMemberPath()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/customers", (Customer customer) => customer.Name);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json("""{"name":"Ada","age":36,"address":{"city":""}}"""), cancellation.Token);

        // Assert
        JsonElement errors = await ReadErrorsAsync(response, cancellation.Token);
        errors.GetProperty("Address.City").GetArrayLength().ShouldBe(1);
        errors.TryGetProperty("Name", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Typed: a body type with no registered validator is not validated")]
    public async Task MapPost_TypeWithoutValidator_ShouldRunHandler()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/notes", (Note note) => note.Text ?? "none");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/notes", Json("""{"text":""}"""), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Typed: a null body is not validated")]
    public async Task MapPost_NullBody_ShouldSkipValidation()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/customers", (Customer? customer) => customer is null ? "null" : "value");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json("null"), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("null");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Typed: a binding failure is answered before the body is validated")]
    public async Task MapPost_BindingFailureAndInvalidBody_ShouldAnswerBindingFailure()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/customers", (Customer customer, int page) => customer.Name);

        using HttpClient client = factory.CreateClient();

        // Act — the required page is missing and the body is invalid.
        using HttpResponseMessage response = await client.PostAsync("/customers", Json(invalidCustomer), cancellation.Token);

        // Assert — the binding problem, keyed by the parameter, not the validation problem.
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("One or more binding errors occurred.");
        problem.RootElement.GetProperty("errors").TryGetProperty("page", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Opt-out: validation turned off for the application lets an invalid body through")]
    public async Task MapPost_GlobalDisabled_ShouldSkipValidation()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options => options.Enabled = false);
        factory.Application.MapPost("/customers", (Customer customer) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json(invalidCustomer), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("accepted");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Opt-in: an endpoint that requires validation is validated with the application default off")]
    public async Task MapPost_GlobalDisabledEndpointRequires_ShouldValidate()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options => options.Enabled = false);
        factory.Application.MapPost("/strict", (Customer customer) => "accepted").RequireValidation();
        factory.Application.MapPost("/lenient", (Customer customer) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage strict = await client.PostAsync("/strict", Json(invalidCustomer), cancellation.Token);
        using HttpResponseMessage lenient = await client.PostAsync("/lenient", Json(invalidCustomer), cancellation.Token);

        // Assert
        (await ReadErrorsAsync(strict, cancellation.Token)).TryGetProperty("Name", out _).ShouldBeTrue();
        lenient.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Opt-out: an endpoint that disables validation lets an invalid body through")]
    public async Task MapPost_EndpointDisabled_ShouldSkipValidation()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/drafts", (Customer customer) => "draft").DisableValidation();
        factory.Application.MapPost("/customers", (Customer customer) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage draft = await client.PostAsync("/drafts", Json(invalidCustomer), cancellation.Token);
        using HttpResponseMessage customer = await client.PostAsync("/customers", Json(invalidCustomer), cancellation.Token);

        // Assert — only the opted-out endpoint skips it.
        draft.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await draft.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("draft");
        customer.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Opt-out: a group that disables validation covers its endpoints, and a route can require it again")]
    public async Task MapPost_GroupDisabled_ShouldSkipValidationUnlessTheRouteRequiresIt()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        IRouterGroupBuilder imports = factory.Application.MapGroup("imports");
        imports.DisableValidation();
        imports.MapPost("customers", (Customer customer) => "imported");
        imports.MapPost("strict", (Customer customer) => "imported").RequireValidation();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage imported = await client.PostAsync("/imports/customers", Json(invalidCustomer), cancellation.Token);
        using HttpResponseMessage strict = await client.PostAsync("/imports/strict", Json(invalidCustomer), cancellation.Token);

        // Assert — the route-level declaration is more specific than the group's.
        imported.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        strict.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Registration: without AddValidation nothing is validated")]
    public async Task MapPost_WithoutAddValidation_ShouldRunHandler()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(registerValidation: false);
        factory.Application.MapPost("/customers", (Customer customer) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json(invalidCustomer), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Registration: a validator from AddProfile reports every failing rule of every member")]
    public async Task MapPost_SeveralInvalidMembersThroughAddProfile_ShouldReportEveryRuleOfEveryMember()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options => options.AddProfile(new SignupProfile()));
        factory.Application.MapPost("/signups", (Signup signup) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/signups", Json(invalidSignup), cancellation.Token);

        // Assert — every failing member in declaration order, and both of the user name's failing rules in
        // the order they are chained: NotEmpty, then MinLength.
        JsonElement errors = await ReadErrorsAsync(response, cancellation.Token);
        errors.EnumerateObject().Select(member => member.Name).ShouldBe(["UserName", "Age", "Address.City"]);
        errors.GetProperty("UserName").GetArrayLength().ShouldBe(2);
        errors.GetProperty("UserName")[0].GetString()!.ShouldEndWith("was empty.", Case.Sensitive);
        errors.GetProperty("UserName")[1].GetString()!.ShouldContain("minimum length", Case.Sensitive);
        errors.GetProperty("Age").GetArrayLength().ShouldBe(1);
        errors.GetProperty("Address.City").GetArrayLength().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Registration: a validator with default options from AddValidator reports every failing member, one message each")]
    public async Task MapPost_SeveralInvalidMembersThroughDefaultOptionsValidator_ShouldReportEveryMember()
    {
        // Arrange — a validator built with ObjectValidation's default options, registered as it is.
        using CancellationTokenSource cancellation = new(_testTimeout);
        IValidator validator = Validator.Create(builder => builder.AddProfile(new SignupProfile()));
        await using WebApplicationTestFactory factory = CreateFactory(options => options.AddValidator(validator));
        factory.Application.MapPost("/signups", (Signup signup) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/signups", Json(invalidSignup), cancellation.Token);

        // Assert — every failing member in declaration order; the user name's chain stops at its first
        // failing rule, NotEmpty.
        JsonElement errors = await ReadErrorsAsync(response, cancellation.Token);
        errors.EnumerateObject().Select(member => member.Name).ShouldBe(["UserName", "Age", "Address.City"]);
        errors.GetProperty("UserName").GetArrayLength().ShouldBe(1);
        errors.GetProperty("UserName")[0].GetString()!.ShouldEndWith("was empty.", Case.Sensitive);
        errors.GetProperty("Age").GetArrayLength().ShouldBe(1);
        errors.GetProperty("Address.City").GetArrayLength().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Typed: the errors map lists members in declaration order, a nested profile's members in place")]
    public async Task MapPost_SeveralInvalidNestedMembers_ShouldListMembersInDeclarationOrder()
    {
        // Arrange — the label and both of the destination's members fail.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options => options.AddProfile(new ParcelProfile()));
        factory.Application.MapPost("/parcels", (Parcel parcel) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/parcels", Json("""{"label":"","destination":{"city":"","zip":""}}"""), cancellation.Token);

        // Assert
        JsonElement errors = await ReadErrorsAsync(response, cancellation.Token);
        errors.EnumerateObject().Select(member => member.Name).ShouldBe(["Label", "Destination.City", "Destination.Zip"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Handlers: a handler validates a value it bound itself through context.ValidateAsync")]
    public async Task ValidateAsync_FromMiddlewareHandler_ShouldAnswer400()
    {
        // Arrange — a raw middleware endpoint binds nothing; it validates a value of its own.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        bool proceeded = false;
        factory.Application.MapPost("/raw", async (IHttpContext context) =>
        {
            Customer customer = new() { Name = "Ada", Age = 3 };
            if (!await context.ValidateAsync(customer, context.RequestCancelled))
            {
                return;
            }

            proceeded = true;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/raw", Json("{}"), cancellation.Token);

        // Assert
        JsonElement errors = await ReadErrorsAsync(response, cancellation.Token);
        errors.TryGetProperty("Age", out _).ShouldBeTrue();
        proceeded.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Handlers: a validator that throws on failure still answers 400")]
    public async Task MapPost_ValidatorThrowingOnFailure_ShouldAnswer400WithEmptyErrors()
    {
        // Arrange — ThrowExceptionOnFailure makes the validator throw an exception that carries no errors.
        using CancellationTokenSource cancellation = new(_testTimeout);
        IValidator throwing = Validator.Create(builder =>
        {
            builder.AddOptions(options => options.ThrowExceptionOnFailure = true);
            builder.AddProfile(new CustomerProfile());
        });

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddJsonSerialization(ValidationTestJsonContext.Default);
        factory.Builder.AddValidation(options => options.AddValidator(throwing));
        factory.Application.UseRouting();
        factory.Application.MapPost("/customers", (Customer customer) => "accepted");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json(invalidCustomer), cancellation.Token);

        // Assert
        JsonElement errors = await ReadErrorsAsync(response, cancellation.Token);
        errors.ValueKind.ShouldBe(JsonValueKind.Object);
        errors.EnumerateObject().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Handlers: a validator whose rule throws faults the request instead of running the handler")]
    public async Task MapPost_ValidatorRuleThrows_ShouldFaultWithoutRunningHandler()
    {
        // Arrange — a nested rule that throws was recorded as not invoked, which let the body through to the
        // handler (#1292). The fault now reaches the exception boundary.
        using CancellationTokenSource cancellation = new(_testTimeout);
        IValidator faulting = Validator.Create(builder => builder.AddProfile(new FaultingCustomerProfile()));

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddJsonSerialization(ValidationTestJsonContext.Default);
        factory.Builder.AddValidation(options => options.AddValidator(faulting));

        InvalidOperationException? fault = null;
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (InvalidOperationException exception)
            {
                fault = exception;
                context.Response.StatusCode = HttpStatusCode.InternalServerError;
            }
        });
        factory.Application.UseRouting();

        bool handlerRan = false;
        factory.Application.MapPost("/customers", (Customer customer) =>
        {
            handlerRan = true;
            return "accepted";
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/customers", Json(validCustomer), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        fault.ShouldNotBeNull();
        fault.Message.ShouldBe("nested rule fault");
        handlerRan.ShouldBeFalse();
    }
}
