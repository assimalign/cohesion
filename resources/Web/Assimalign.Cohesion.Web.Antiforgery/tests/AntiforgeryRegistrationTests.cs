using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

/// <summary>
/// <c>AddAntiforgery</c> and the protector it selects: a data-protection provider makes tokens survive a
/// restart and validate across instances that share a key repository, sealed under the antiforgery purpose
/// alone; without one the per-process random key is the (development-only) default; an explicit protector
/// wins; and the last registration is the one exchanges carry and the middleware validates with. Each
/// "instance" is a separate registration, as a restarted or second application would make.
/// </summary>
public class AntiforgeryRegistrationTests
{
    private const string cookieName = "__cohesion-antiforgery";
    private const string headerName = "X-CSRF-TOKEN";

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: Should throw on a null builder")]
    public void AddAntiforgery_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddAntiforgery());
        Should.Throw<ArgumentNullException>(() => builder.AddAntiforgery(DataProtectionProvider.Create(new InMemoryKeyRepository())));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: Should throw on a null data-protection provider")]
    public void AddAntiforgery_NullDataProtectionProvider_ShouldThrow()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddAntiforgery((IDataProtectionProvider)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: Should register the service as an exchange feature")]
    public void AddAntiforgery_Default_ShouldRegisterAntiforgeryFeature()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();

        // Act
        IWebApplicationBuilder returned = builder.AddAntiforgery();

        // Assert — the slot is named for the contract, so an exchange carries one antiforgery service.
        returned.ShouldBeSameAs(builder);
        IHttpFeature feature = builder.Features.ShouldHaveSingleItem();
        feature.ShouldBeAssignableTo<IHttpAntiforgeryFeature>().ShouldNotBeNull().Antiforgery.ShouldNotBeNull();
        feature.Name.ShouldBe(nameof(IHttpAntiforgeryFeature));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: With a data-protection provider, tokens should survive a restart")]
    public async Task AddAntiforgery_WithDataProtectionProvider_TokensShouldValidateAfterRestart()
    {
        // Arrange — the second instance creates its own provider over the same key repository.
        InMemoryKeyRepository keys = new();
        IHttpAntiforgery first = Register(builder => builder.AddAntiforgery(DataProtectionProvider.Create(keys)));
        HttpAntiforgeryTokenSet tokens = first.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        IHttpAntiforgery restarted = Register(builder => builder.AddAntiforgery(DataProtectionProvider.Create(keys)));

        // Act
        bool valid = await restarted.IsRequestValidAsync(Post(tokens));

        // Assert
        valid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: Without a data-protection provider, a restart should invalidate tokens")]
    public async Task AddAntiforgery_WithoutDataProtectionProvider_TokensShouldNotValidateAfterRestart()
    {
        // Arrange — the development default: each registration draws its own random key.
        IHttpAntiforgery first = Register(builder => builder.AddAntiforgery());
        HttpAntiforgeryTokenSet tokens = first.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        IHttpAntiforgery restarted = Register(builder => builder.AddAntiforgery());

        // Act
        bool validOnFirst = await first.IsRequestValidAsync(Post(tokens));
        bool validAfterRestart = await restarted.IsRequestValidAsync(Post(tokens));

        // Assert
        validOnFirst.ShouldBeTrue();
        validAfterRestart.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: Tokens should be sealed under the antiforgery purpose chain")]
    public async Task AddAntiforgery_WithDataProtectionProvider_ShouldProtectUnderTheAntiforgeryPurpose()
    {
        // Arrange — the purpose chain is part of the token format: a service that seals under exactly
        // ("Assimalign.Cohesion.Web.Antiforgery", "v1") interoperates with the registered one.
        IDataProtectionProvider provider = DataProtectionProvider.Create(new InMemoryKeyRepository());
        IHttpAntiforgery registered = Register(builder => builder.AddAntiforgery(provider));
        IHttpAntiforgery samePurpose = HttpAntiforgery.Create(new HttpAntiforgeryOptions
        {
            Protector = new TestDataProtectionProtector(provider.CreateProtector("Assimalign.Cohesion.Web.Antiforgery", "v1")),
        });

        HttpAntiforgeryTokenSet tokens = samePurpose.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        // Act
        bool valid = await registered.IsRequestValidAsync(Post(tokens));

        // Assert
        valid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: Payloads protected for another purpose should not validate as tokens")]
    public async Task AddAntiforgery_WithDataProtectionProvider_ShouldRejectAnotherPurpose()
    {
        // Arrange — same key ring, a different purpose (cookie authentication's): purpose-bound isolation.
        IDataProtectionProvider provider = DataProtectionProvider.Create(new InMemoryKeyRepository());
        IHttpAntiforgery registered = Register(builder => builder.AddAntiforgery(provider));
        IHttpAntiforgery otherPurpose = HttpAntiforgery.Create(new HttpAntiforgeryOptions
        {
            Protector = new TestDataProtectionProtector(provider.CreateProtector("Assimalign.Cohesion.Web.Authentication.Cookie", "Cookies", "v1")),
        });

        HttpAntiforgeryTokenSet tokens = otherPurpose.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        // Act
        bool valid = await registered.IsRequestValidAsync(Post(tokens));

        // Assert
        valid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: A tampered token should be invalid, not an exception")]
    public async Task AddAntiforgery_WithDataProtectionProvider_TamperedTokenShouldBeInvalid()
    {
        // Arrange
        IHttpAntiforgery registered = Register(builder => builder.AddAntiforgery(DataProtectionProvider.Create(new InMemoryKeyRepository())));
        HttpAntiforgeryTokenSet tokens = registered.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        char[] tampered = tokens.CookieToken!.ToCharArray();
        tampered[tampered.Length / 2] = tampered[tampered.Length / 2] == 'A' ? 'B' : 'A';

        AntiforgeryTestContext post = new AntiforgeryTestContext(HttpMethod.Post)
            .WithCookie(cookieName, new string(tampered))
            .WithHeader(headerName, tokens.RequestToken!);

        // Act
        bool valid = await registered.IsRequestValidAsync(post);

        // Assert
        valid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: An explicitly configured protector should take precedence over the provider")]
    public void AddAntiforgery_ExplicitProtector_ShouldWinOverDataProtectionProvider()
    {
        // Arrange
        CountingProtector explicitProtector = new();
        IHttpAntiforgery registered = Register(builder => builder.AddAntiforgery(
            DataProtectionProvider.Create(new InMemoryKeyRepository()),
            options => options.Protector = explicitProtector));

        // Act
        registered.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        // Assert — the cookie token and the request token were both sealed by the explicit protector.
        explicitProtector.ProtectCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: The configure callback should shape the registered service")]
    public void AddAntiforgery_Configure_ShouldApplyOptions()
    {
        // Arrange
        IHttpAntiforgery registered = Register(builder => builder.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.FormFieldName = "csrf";
        }));

        // Act
        HttpAntiforgeryTokenSet tokens = registered.GetTokens(new AntiforgeryTestContext(HttpMethod.Get));

        // Assert
        tokens.HeaderName.ShouldBe("X-XSRF-TOKEN");
        tokens.FormFieldName.ShouldBe("csrf");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: The last registration should be the one the middleware validates with")]
    public async Task AddAntiforgery_CalledTwice_LastRegistrationShouldWin()
    {
        // Arrange
        TestWebApplicationBuilder application = new();
        application.AddAntiforgery();
        IHttpAntiforgery replaced = application.Antiforgery;
        application.AddAntiforgery();
        IHttpAntiforgery last = application.Antiforgery;

        TestPipelineBuilder pipeline = new(new TestWebApplicationContext(application.Features), _ => Task.CompletedTask);
        pipeline.UseAntiforgery();
        IWebApplicationPipeline built = pipeline.Build();

        HttpAntiforgeryTokenSet lastTokens = last.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));
        HttpAntiforgeryTokenSet replacedTokens = replaced.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext withLast = Post(lastTokens);
        withLast.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(AntiforgeryMetadata.Required));
        await using AntiforgeryTestContext withReplaced = Post(replacedTokens);
        withReplaced.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(AntiforgeryMetadata.Required));

        // Act
        await built.ExecuteAsync(withLast, CancellationToken.None);
        await built.ExecuteAsync(withReplaced, CancellationToken.None);

        // Assert
        withLast.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        withReplaced.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static IHttpAntiforgery Register(Action<TestWebApplicationBuilder> register)
    {
        TestWebApplicationBuilder builder = new();
        register(builder);
        return builder.Antiforgery;
    }

    private static AntiforgeryTestContext Post(HttpAntiforgeryTokenSet tokens)
        => new AntiforgeryTestContext(HttpMethod.Post)
            .WithCookie(cookieName, tokens.CookieToken!)
            .WithHeader(headerName, tokens.RequestToken!);
}
