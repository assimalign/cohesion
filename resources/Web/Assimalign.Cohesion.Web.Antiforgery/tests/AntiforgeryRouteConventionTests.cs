using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

/// <summary>
/// The endpoint convention verbs over the real router: <c>RequireAntiforgery</c> on a mapped route and on a
/// route group, and <c>DisableAntiforgery</c> exempting one route of a protected group — with and without
/// <c>UseAntiforgery</c> in the pipeline.
/// </summary>
public class AntiforgeryRouteConventionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Conventions: Antiforgery required on a route should protect only that route")]
    public async Task RequireAntiforgery_OnRoute_ShouldProtectOnlyThatRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true, out IRouterBuilder routes);
        routes.Map(CohesionHttpMethod.Post, "/guarded", Ok()).RequireAntiforgery();
        routes.Map(CohesionHttpMethod.Post, "/open", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage guarded = await PostEmptyAsync(client, "/guarded", cancellationToken);
        using HttpResponseMessage open = await PostEmptyAsync(client, "/open", cancellationToken);

        // Assert
        guarded.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        open.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Conventions: A group requirement should protect routes mapped before and after it")]
    public async Task RequireAntiforgery_OnGroup_ShouldProtectRoutesMappedBeforeAndAfter()
    {
        // Arrange — the requirement is attached between the two Map calls; composition happens at build.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true, out IRouterBuilder routes);
        IRouterGroupBuilder forms = routes.MapGroup("/forms");
        forms.Map(CohesionHttpMethod.Post, "early", Ok());
        forms.RequireAntiforgery();
        forms.Map(CohesionHttpMethod.Post, "late", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage early = await PostEmptyAsync(client, "/forms/early", cancellationToken);
        using HttpResponseMessage late = await PostEmptyAsync(client, "/forms/late", cancellationToken);

        // Assert
        early.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        late.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Conventions: Disabling antiforgery on a route should exempt it from its group's requirement")]
    public async Task DisableAntiforgery_OnRouteInProtectedGroup_ShouldExemptTheRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true, out IRouterBuilder routes);
        IRouterGroupBuilder forms = routes.MapGroup("/forms").RequireAntiforgery();
        forms.Map(CohesionHttpMethod.Post, "guarded", Ok());
        forms.Map(CohesionHttpMethod.Post, "webhook", Ok()).DisableAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage guarded = await PostEmptyAsync(client, "/forms/guarded", cancellationToken);
        using HttpResponseMessage webhook = await PostEmptyAsync(client, "/forms/webhook", cancellationToken);

        // Assert
        guarded.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        webhook.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Conventions: A route that disables its group's requirement should run without UseAntiforgery")]
    public async Task DisableAntiforgery_OnRouteWithoutMiddleware_ShouldRunTheRoute()
    {
        // Arrange — no UseAntiforgery at all: the group's route still fails closed, the exempt one runs.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        InvalidOperationException? dispatchFailure = null;
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (InvalidOperationException exception)
            {
                dispatchFailure = exception;
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder forms = routes.MapGroup("/forms").RequireAntiforgery();
        forms.Map(CohesionHttpMethod.Post, "guarded", Ok());
        forms.Map(CohesionHttpMethod.Post, "webhook", Ok()).DisableAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage webhook = await PostEmptyAsync(client, "/forms/webhook", cancellationToken);
        InvalidOperationException? webhookFailure = dispatchFailure;
        using HttpResponseMessage guarded = await PostEmptyAsync(client, "/forms/guarded", cancellationToken);

        // Assert
        webhook.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        webhookFailure.ShouldBeNull();
        guarded.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseAntiforgery()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Conventions: A route that requires antiforgery under a disabling group should be protected")]
    public async Task RequireAntiforgery_OnRouteInDisabledGroup_ShouldProtectTheRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true, out IRouterBuilder routes);
        IRouterGroupBuilder hooks = routes.MapGroup("/hooks").DisableAntiforgery();
        hooks.Map(CohesionHttpMethod.Post, "open", Ok());
        hooks.Map(CohesionHttpMethod.Post, "settings", Ok()).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage open = await PostEmptyAsync(client, "/hooks/open", cancellationToken);
        using HttpResponseMessage settings = await PostEmptyAsync(client, "/hooks/settings", cancellationToken);

        // Assert
        open.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        settings.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Conventions: A verb on a null builder should throw ArgumentNullException")]
    public void RequireAntiforgery_NullBuilder_ShouldThrow()
    {
        // Arrange
        IRouterRouteBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.RequireAntiforgery());
        Should.Throw<ArgumentNullException>(() => builder.DisableAntiforgery());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Conventions: The verbs should attach the shared metadata instances")]
    public void RequireAntiforgery_OnRouteBuilder_ShouldAttachSharedMetadata()
    {
        // Arrange
        RecordingConventionBuilder builder = new();

        // Act
        RecordingConventionBuilder returned = builder.RequireAntiforgery().DisableAntiforgery();

        // Assert — the generic verbs return the receiver's own builder type for chaining.
        returned.ShouldBeSameAs(builder);
        builder.Items.ShouldBe(new object[] { AntiforgeryMetadata.Required, AntiforgeryMetadata.Disabled });
        AntiforgeryMetadata.Required.RequiresValidation.ShouldBeTrue();
        AntiforgeryMetadata.Required.RequiredMiddleware.ShouldBe("UseAntiforgery");
        AntiforgeryMetadata.Disabled.RequiresValidation.ShouldBeFalse();
        AntiforgeryMetadata.Disabled.RequiredMiddleware.ShouldBeNull();
    }

    private static WebApplicationTestFactory CreateFactory(bool useAntiforgery, out IRouterBuilder routes)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAntiforgery();
        routes = factory.Application.UseRouting();

        if (useAntiforgery)
        {
            factory.Application.UseAntiforgery();
        }

        return factory;
    }

    private static Task<HttpResponseMessage> PostEmptyAsync(HttpClient client, string path, CancellationToken cancellationToken)
        => client.PostAsync(path, new StringContent(string.Empty, Encoding.UTF8), cancellationToken);

    private static RouterRouteHandler Ok() => new(context =>
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        return Task.CompletedTask;
    });
}
