using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

/// <summary>
/// The policy model over <see cref="ClaimsPrincipal"/>: each built-in requirement, custom and delegate
/// requirements (sync and async), all-must-pass evaluation with short-circuit, and the builder's and
/// policy's validation and immutability.
/// </summary>
public class AuthorizationPolicyTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: An authenticated-user requirement should reject an anonymous principal")]
    public async Task RequireAuthenticatedUser_AnonymousPrincipal_ShouldNotBeSatisfied()
    {
        // Arrange
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        // Act
        bool anonymous = await policy.EvaluateAsync(Context(Anonymous()));
        bool unauthenticatedIdentity = await policy.EvaluateAsync(Context(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")]))));
        bool authenticated = await policy.EvaluateAsync(Context(User("alice")));

        // Assert
        anonymous.ShouldBeFalse();
        unauthenticatedIdentity.ShouldBeFalse();
        authenticated.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: An authenticated-user requirement should accept a principal with any authenticated identity")]
    public async Task RequireAuthenticatedUser_SecondIdentityAuthenticated_ShouldBeSatisfied()
    {
        // Arrange — a principal combined from two schemes, only the second of which authenticated.
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        ClaimsPrincipal user = new([new ClaimsIdentity(), new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Second")]);

        // Act
        bool satisfied = await policy.EvaluateAsync(Context(user));

        // Assert
        satisfied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A role requirement should accept any one of its roles")]
    public async Task RequireRole_PrincipalInOneRole_ShouldBeSatisfied()
    {
        // Arrange
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireRole("admin", "ops").Build();

        // Act
        bool ops = await policy.EvaluateAsync(Context(User("alice", roles: ["ops"])));
        bool reader = await policy.EvaluateAsync(Context(User("bob", roles: ["reader"])));
        bool none = await policy.EvaluateAsync(Context(User("carol")));

        // Assert
        ops.ShouldBeTrue();
        reader.ShouldBeFalse();
        none.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A claim requirement should match the type case-insensitively and the value exactly")]
    public async Task RequireClaim_WithAllowedValues_ShouldCompareTypeIgnoringCaseAndValueOrdinal()
    {
        // Arrange
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireClaim("department", "sales", "support").Build();

        // Act
        bool exact = await policy.EvaluateAsync(Context(User("alice", claims: [new Claim("Department", "sales")])));
        bool wrongCase = await policy.EvaluateAsync(Context(User("bob", claims: [new Claim("department", "Sales")])));
        bool otherValue = await policy.EvaluateAsync(Context(User("carol", claims: [new Claim("department", "finance")])));
        bool missing = await policy.EvaluateAsync(Context(User("dave")));

        // Assert
        exact.ShouldBeTrue();
        wrongCase.ShouldBeFalse();
        otherValue.ShouldBeFalse();
        missing.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A claim requirement without values should accept the claim with any value")]
    public async Task RequireClaim_WithoutValues_ShouldAcceptAnyValue()
    {
        // Arrange
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireClaim("employee_id").Build();

        // Act
        bool present = await policy.EvaluateAsync(Context(User("alice", claims: [new Claim("employee_id", "1234")])));
        bool missing = await policy.EvaluateAsync(Context(User("bob")));

        // Assert
        present.ShouldBeTrue();
        missing.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: An empty allowed-value list should be rejected instead of meaning any value")]
    public void RequireClaim_EmptyAllowedValues_ShouldThrowArgumentException()
    {
        // Arrange
        AuthorizationPolicyBuilder builder = new();

        // Act
        Action act = () => builder.RequireClaim("department", new List<string>());

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A role requirement with no role should be rejected")]
    public void RequireRole_NoRoles_ShouldThrowArgumentException()
    {
        // Arrange
        AuthorizationPolicyBuilder builder = new();

        // Act
        Action empty = () => builder.RequireRole();
        Action blank = () => builder.RequireRole("admin", " ");

        // Assert
        empty.ShouldThrow<ArgumentException>();
        blank.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A synchronous assertion should see the principal and the exchange")]
    public async Task RequireAssertion_Synchronous_ShouldEvaluateOverTheContext()
    {
        // Arrange
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireAssertion(context => context.User.Identity?.Name == "alice" && context.HttpContext.Request.Path.Value == "/")
            .Build();

        // Act
        bool alice = await policy.EvaluateAsync(Context(User("alice")));
        bool bob = await policy.EvaluateAsync(Context(User("bob")));

        // Assert
        alice.ShouldBeTrue();
        bob.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: An asynchronous assertion should receive the evaluation's cancellation token")]
    public async Task RequireAssertion_Asynchronous_ShouldReceiveCancellationToken()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        CancellationToken observed = default;
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireAssertion(async (context, cancellationToken) =>
            {
                observed = cancellationToken;
                await Task.Yield();
                return context.User.IsInRole("admin");
            })
            .Build();

        // Act
        bool admin = await policy.EvaluateAsync(Context(User("alice", roles: ["admin"])), cancellation.Token);
        bool reader = await policy.EvaluateAsync(Context(User("bob", roles: ["reader"])), cancellation.Token);

        // Assert
        admin.ShouldBeTrue();
        reader.ShouldBeFalse();
        observed.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A custom requirement should take part in evaluation")]
    public async Task AddRequirements_CustomRequirement_ShouldBeEvaluated()
    {
        // Arrange
        RecordingRequirement custom = new(result: true);
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(custom)
            .Build();

        // Act
        bool satisfied = await policy.EvaluateAsync(Context(User("alice")));

        // Assert
        satisfied.ShouldBeTrue();
        custom.Evaluations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: Every requirement should have to pass, and evaluation should stop at the first failure")]
    public async Task EvaluateAsync_FailingRequirement_ShouldFailAndSkipTheRest()
    {
        // Arrange
        RecordingRequirement first = new(result: true);
        RecordingRequirement failing = new(result: false);
        RecordingRequirement last = new(result: true);
        AuthorizationPolicy policy = new([first, failing, last]);

        // Act
        bool satisfied = await policy.EvaluateAsync(Context(User("alice")));

        // Assert
        satisfied.ShouldBeFalse();
        first.Evaluations.ShouldBe(1);
        failing.Evaluations.ShouldBe(1);
        last.Evaluations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: Evaluation should continue past an asynchronous requirement and still stop at the first failure")]
    public async Task EvaluateAsync_AsynchronousRequirementThenFailure_ShouldStopAtTheFailure()
    {
        // Arrange
        RecordingRequirement failing = new(result: false);
        RecordingRequirement last = new(result: true);
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireAssertion(async (_, _) =>
            {
                await Task.Yield();
                return true;
            })
            .AddRequirements(failing, last)
            .Build();

        // Act
        bool satisfied = await policy.EvaluateAsync(Context(User("alice")));

        // Assert
        satisfied.ShouldBeFalse();
        failing.Evaluations.ShouldBe(1);
        last.Evaluations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: Building a policy without a requirement should throw")]
    public void Build_NoRequirement_ShouldThrowInvalidOperationException()
    {
        // Arrange — schemes alone would authorize every request.
        AuthorizationPolicyBuilder builder = new AuthorizationPolicyBuilder().AddAuthenticationSchemes("Bearer");

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A policy constructed without a requirement should throw")]
    public void Constructor_NoRequirement_ShouldThrowArgumentException()
    {
        // Act
        Action act = () => _ = new AuthorizationPolicy(Array.Empty<IAuthorizationRequirement>());

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: Schemes should keep their first-seen order without duplicates")]
    public void Build_DuplicateSchemes_ShouldDeduplicateInOrder()
    {
        // Arrange
        AuthorizationPolicyBuilder builder = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes("Bearer", "Cookies")
            .AddAuthenticationSchemes("Bearer")
            .RequireAuthenticatedUser();

        // Act
        AuthorizationPolicy policy = builder.Build();

        // Assert
        policy.AuthenticationSchemes.ShouldBe(new[] { "Bearer", "Cookies" });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: Combining a policy should add its requirements and schemes")]
    public async Task Combine_Policy_ShouldAddRequirementsAndSchemes()
    {
        // Arrange
        AuthorizationPolicy admins = new AuthorizationPolicyBuilder().AddAuthenticationSchemes("Bearer").RequireRole("admin").Build();

        // Act
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireClaim("department", "sales")
            .Combine(admins)
            .Build();

        // Assert
        policy.Requirements.Count.ShouldBe(2);
        policy.AuthenticationSchemes.ShouldBe(new[] { "Bearer" });
        (await policy.EvaluateAsync(Context(User("alice", roles: ["admin"], claims: [new Claim("department", "sales")])))).ShouldBeTrue();
        (await policy.EvaluateAsync(Context(User("bob", roles: ["admin"])))).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A built policy should not change when its builder does")]
    public void Build_ThenMutateBuilder_ShouldLeaveThePolicyUnchanged()
    {
        // Arrange
        AuthorizationPolicyBuilder builder = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();
        AuthorizationPolicy policy = builder.Build();

        // Act
        builder.RequireRole("admin").AddAuthenticationSchemes("Bearer");

        // Assert
        policy.Requirements.Count.ShouldBe(1);
        policy.AuthenticationSchemes.ShouldBeEmpty();
        policy.Requirements.ShouldBeAssignableTo<System.Collections.ObjectModel.ReadOnlyCollection<IAuthorizationRequirement>>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Policy: A blank scheme name should be rejected")]
    public void AddAuthenticationSchemes_BlankName_ShouldThrowArgumentException()
    {
        // Arrange
        AuthorizationPolicyBuilder builder = new();

        // Act
        Action act = () => builder.AddAuthenticationSchemes("Bearer", "");

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    private static AuthorizationContext Context(ClaimsPrincipal user) => new(TestHttpContext.Create(), user);

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal User(string name, string[]? roles = null, Claim[]? claims = null)
    {
        ClaimsIdentity identity = new("Test");
        identity.AddClaim(new Claim(ClaimTypes.Name, name));

        foreach (string role in roles ?? [])
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        identity.AddClaims(claims ?? []);

        return new ClaimsPrincipal(identity);
    }
}
