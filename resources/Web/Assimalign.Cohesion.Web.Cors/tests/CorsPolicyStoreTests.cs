using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Cors.Tests;

public class CorsPolicyStoreTests
{
    private const string App = "https://app.example";
    private const string Other = "https://other.example";

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Store: Creation should normalize and detach every configured collection")]
    public void Create_ConfiguredPolicies_ShouldNormalizeAndDetachInput()
    {
        // Arrange
        CorsPolicyConfiguration policy = new()
        {
            Origins = ["HTTPS://APP.EXAMPLE:443", App],
            Methods = ["put", "PUT"],
            Headers = ["Content-Type", "content-type"],
            ExposedHeaders = ["ETag", "etag"],
            AllowCredentials = true,
            PreflightMaxAgeSeconds = 600,
        };
        CorsConfiguration configuration = new()
        {
            DefaultPolicy = policy,
            Policies = new Dictionary<string, CorsPolicyConfiguration> { ["spa"] = policy },
        };

        // Act
        CorsPolicyStore store = CorsPolicyStore.Create(configuration);
        policy.Origins[0] = Other;
        policy.Methods[0] = "DELETE";
        policy.Headers[0] = "Authorization";
        policy.ExposedHeaders[0] = "Location";
        configuration.Policies.Clear();
        CorsConfiguration observed = store.GetConfiguration();

        // Assert
        observed.DefaultPolicy.ShouldNotBeNull();
        observed.DefaultPolicy.Origins.ShouldBe([App]);
        observed.DefaultPolicy.Methods.ShouldBe(["PUT"]);
        observed.DefaultPolicy.Headers.ShouldBe(["Content-Type"]);
        observed.DefaultPolicy.ExposedHeaders.ShouldBe(["ETag"]);
        observed.DefaultPolicy.AllowCredentials.ShouldBeTrue();
        observed.DefaultPolicy.PreflightMaxAgeSeconds.ShouldBe(600L);
        observed.Policies["spa"].Origins.ShouldBe([App]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Store: Reads should return detached configuration objects and collections")]
    public void GetConfiguration_MutatedResult_ShouldLeavePublishedPoliciesUnchanged()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(App));
        CorsConfiguration read = store.GetConfiguration();

        // Act
        read.DefaultPolicy!.Origins[0] = Other;
        read.Policies["spa"].Origins[0] = Other;
        read.Policies.Clear();
        CorsConfiguration reread = store.GetConfiguration();

        // Assert
        reread.DefaultPolicy!.Origins.ShouldBe([App]);
        reread.Policies["spa"].Origins.ShouldBe([App]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Store: An invalid named replacement should preserve the complete published configuration")]
    public void Replace_InvalidNamedPolicy_ShouldPreserveEveryPublishedPolicy()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(App));
        CorsConfiguration replacement = NamedConfiguration(Other);
        replacement.Policies["spa"] = new CorsPolicyConfiguration
        {
            AllowAnyOrigin = true,
            AllowCredentials = true,
        };

        // Act
        Action replace = () => store.Replace(replacement);

        // Assert
        replace.ShouldThrow<ArgumentException>();
        CorsConfiguration observed = store.GetConfiguration();
        observed.DefaultPolicy!.Origins.ShouldBe([App]);
        observed.Policies["spa"].Origins.ShouldBe([App]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Store: Existing names should remain resolvable, including names added by a replacement")]
    public void Replace_OmittedNamedPolicy_ShouldRejectWithoutPublishing()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(App));
        CorsConfiguration withAdditionalPolicy = NamedConfiguration(Other);
        withAdditionalPolicy.Policies.Add("partner", new CorsPolicyConfiguration { Origins = [Other] });
        store.Replace(withAdditionalPolicy);

        // Act
        Action removeOriginal = () => store.Replace(new CorsConfiguration());
        Action removeAdded = () => store.Replace(NamedConfiguration(App));

        // Assert
        removeOriginal.ShouldThrow<ArgumentException>();
        removeAdded.ShouldThrow<ArgumentException>().Message.ShouldContain("partner", Case.Sensitive);
        store.GetConfiguration().Policies.Keys.ShouldBe(["spa", "partner"], ignoreOrder: true);
        store.GetConfiguration().DefaultPolicy!.Origins.ShouldBe([Other]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Store: Replacements should permit removing the default policy")]
    public void Replace_NoDefaultPolicy_ShouldRemoveDefaultAndRetainNamedPolicy()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(App));
        CorsConfiguration replacement = NamedConfiguration(Other);
        replacement.DefaultPolicy = null;

        // Act
        store.Replace(replacement);

        // Assert
        store.GetConfiguration().DefaultPolicy.ShouldBeNull();
        store.GetConfiguration().Policies["spa"].Origins.ShouldBe([Other]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Store: A cache lifetime outside TimeSpan should be rejected before publication")]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void Replace_InvalidCacheLifetime_ShouldPreservePublishedConfiguration(long seconds)
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(App));
        CorsConfiguration replacement = NamedConfiguration(Other);
        replacement.DefaultPolicy!.PreflightMaxAgeSeconds = seconds;

        // Act
        Action replace = () => store.Replace(replacement);

        // Assert
        replace.ShouldThrow<ArgumentOutOfRangeException>();
        store.GetConfiguration().DefaultPolicy!.Origins.ShouldBe([App]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Store: JSON should round-trip the complete normalized configuration without reflection")]
    public void JsonSerializer_NormalizedConfiguration_ShouldRoundTripAllPolicyFields()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(new CorsConfiguration
        {
            DefaultPolicy = new CorsPolicyConfiguration
            {
                Origins = [App],
                AllowAnyMethod = true,
                AllowAnyHeader = true,
                ExposedHeaders = ["ETag"],
                AllowCredentials = true,
                PreflightMaxAgeSeconds = 60,
            },
            Policies = new Dictionary<string, CorsPolicyConfiguration>
            {
                ["public"] = new() { AllowAnyOrigin = true, Methods = ["PUT"], Headers = ["Content-Type"] },
            },
        });

        // Act
        string json = JsonSerializer.Serialize(store.GetConfiguration(), CorsJsonContext.Default.CorsConfiguration);
        CorsConfiguration restored = JsonSerializer.Deserialize(json, CorsJsonContext.Default.CorsConfiguration)!;
        CorsConfiguration observed = CorsPolicyStore.Create(restored).GetConfiguration();

        // Assert
        json.ShouldContain("\"defaultPolicy\"", Case.Sensitive);
        json.ShouldContain("\"preflightMaxAgeSeconds\":60", Case.Sensitive);
        observed.DefaultPolicy!.Origins.ShouldBe([App]);
        observed.DefaultPolicy.AllowAnyMethod.ShouldBeTrue();
        observed.DefaultPolicy.AllowAnyHeader.ShouldBeTrue();
        observed.DefaultPolicy.ExposedHeaders.ShouldBe(["ETag"]);
        observed.DefaultPolicy.AllowCredentials.ShouldBeTrue();
        observed.Policies["public"].AllowAnyOrigin.ShouldBeTrue();
        observed.Policies["public"].Methods.ShouldBe(["PUT"]);
        observed.Policies["public"].Headers.ShouldBe(["Content-Type"]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Store: JSON should reject unknown fields and null collections")]
    [InlineData("{\"defaultPolicy\":{\"allowAnyOrigin\":true,\"allowCredential\":true}}")]
    [InlineData("{\"defaultPolicy\":{\"origins\":null}}")]
    [InlineData("{\"policies\":null}")]
    public void JsonSerializer_InvalidConfiguration_ShouldRejectInsteadOfIgnoringInput(string json)
    {
        // Arrange / Act
        Action deserialize = () => JsonSerializer.Deserialize(json, CorsJsonContext.Default.CorsConfiguration);

        // Assert
        deserialize.ShouldThrow<JsonException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Dynamic: A replacement during the request should retain the selected policy through head commit")]
    public async Task UseDynamicCors_ReplacementDuringRequest_ShouldKeepSelectedPolicyUntilCommit()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(App));
        TestPipelineBuilder builder = new();
        builder.UseDynamicCors(store);
        builder.Use(_ => context =>
        {
            store.Replace(NamedConfiguration(Other));
            return Task.CompletedTask;
        });
        IWebApplicationPipeline pipeline = builder.Build();
        await using CorsTestContext before = new(HttpMethod.Get, origin: App);
        await using CorsTestContext after = new(HttpMethod.Get, origin: Other);

        // Act
        await pipeline.ExecuteAsync(before, CancellationToken.None);
        await pipeline.ExecuteAsync(after, CancellationToken.None);

        // Assert
        before.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
        after.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Dynamic: A named policy replacement should change subsequent preflight grants")]
    public async Task UseDynamicCors_NamedReplacement_ShouldChangePreflightGrant()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(App));
        TestPipelineBuilder builder = new();
        builder.Use(next => context =>
        {
            context.Features.Set<IRouteMatchFeature>(
                new FakeRouteMatchFeature(new CorsMetadata("spa")) { IsPreflight = true });
            return next.Invoke(context);
        });
        builder.UseDynamicCors(store);
        IWebApplicationPipeline pipeline = builder.Build();
        await using CorsTestContext before = CorsTestContext.Preflight(Other, "DELETE", "Authorization");
        await using CorsTestContext after = CorsTestContext.Preflight(Other, "DELETE", "Authorization");

        // Act
        await pipeline.ExecuteAsync(before, CancellationToken.None);
        CorsConfiguration replacement = NamedConfiguration(Other);
        replacement.Policies["spa"].Methods = ["DELETE"];
        replacement.Policies["spa"].Headers = ["Authorization"];
        replacement.Policies["spa"].PreflightMaxAgeSeconds = 120;
        store.Replace(replacement);
        await pipeline.ExecuteAsync(after, CancellationToken.None);

        // Assert
        before.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
        after.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        after.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
        after.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBe("DELETE");
        after.ResponseHeader(HttpHeaderKey.AccessControlAllowHeaders).ShouldBe("Authorization");
        after.ResponseHeader(HttpHeaderKey.AccessControlMaxAge).ShouldBe("120");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Dynamic: Independent stores should isolate application policies")]
    public async Task UseDynamicCors_IndependentStores_ShouldKeepApplicationsIsolated()
    {
        // Arrange
        CorsPolicyStore first = CorsPolicyStore.Create(NamedConfiguration(App));
        CorsPolicyStore second = CorsPolicyStore.Create(NamedConfiguration(App));
        TestPipelineBuilder firstBuilder = new();
        TestPipelineBuilder secondBuilder = new();
        firstBuilder.UseDynamicCors(first);
        secondBuilder.UseDynamicCors(second);
        IWebApplicationPipeline firstPipeline = firstBuilder.Build();
        IWebApplicationPipeline secondPipeline = secondBuilder.Build();
        await using CorsTestContext firstRequest = new(HttpMethod.Get, origin: Other);
        await using CorsTestContext secondRequest = new(HttpMethod.Get, origin: Other);

        // Act
        first.Replace(NamedConfiguration(Other));
        await firstPipeline.ExecuteAsync(firstRequest, CancellationToken.None);
        await secondPipeline.ExecuteAsync(secondRequest, CancellationToken.None);

        // Assert
        firstRequest.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
        secondRequest.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Dynamic: Inline and disabled endpoint policies should retain precedence")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseDynamicCors_EndpointOverride_ShouldRetainPrecedence(bool disabled)
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(NamedConfiguration(Other));
        CorsMetadata metadata = disabled ? CorsMetadata.Disabled : new(new CorsPolicyBuilder().WithOrigins(App).Build());
        TestPipelineBuilder builder = new();
        builder.Use(next => context =>
        {
            context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(metadata));
            return next.Invoke(context);
        });
        builder.UseDynamicCors(store);
        builder.Use(_ => context =>
        {
            context.Response.Headers[HttpHeaderKey.AccessControlAllowOrigin] = Other;
            return Task.CompletedTask;
        });
        IWebApplicationPipeline pipeline = builder.Build();
        await using CorsTestContext request = new(HttpMethod.Get, origin: App);

        // Act
        await pipeline.ExecuteAsync(request, CancellationToken.None);

        // Assert
        request.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(disabled ? Other : App);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Static: Retained options should not change an existing UseCors pipeline")]
    public async Task UseCors_OptionsChangedAfterRegistration_ShouldKeepStaticSnapshot()
    {
        // Arrange
        CorsOptions? retained = null;
        TestPipelineBuilder builder = new();
        builder.UseCors(options => retained = options);
        retained!.AddDefaultPolicy(policy => policy.WithOrigins(App));
        await using CorsTestContext request = new(HttpMethod.Get, origin: App);

        // Act
        await builder.Build().ExecuteAsync(request, CancellationToken.None);

        // Assert
        request.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
    }

    private static CorsConfiguration NamedConfiguration(string origin)
    {
        return new CorsConfiguration
        {
            DefaultPolicy = new CorsPolicyConfiguration { Origins = [origin] },
            Policies = new Dictionary<string, CorsPolicyConfiguration>
            {
                ["spa"] = new() { Origins = [origin] },
            },
        };
    }
}
