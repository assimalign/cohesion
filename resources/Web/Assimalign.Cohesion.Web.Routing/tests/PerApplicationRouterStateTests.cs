using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// Regression tests for the per-application router state fix (#789). Before the fix, <c>UseRouting</c>
/// returned a process-wide static <c>RouterBuilder.Shared</c> while <c>AddRouting</c> registered a
/// per-application builder, so routes mapped through one application leaked into every other
/// application in the process. These tests prove two applications hosted in one process keep fully
/// isolated route tables and that <c>AddRouting</c>/<c>UseRouting</c> resolve the same per-application
/// builder.
/// </summary>
public class PerApplicationRouterStateTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Routing] - PerApp: Two applications in one process have isolated route tables")]
    public async Task TwoApplications_InOneProcess_ShouldHaveIsolatedRouteTables()
    {
        // Arrange — app 1 knows only /alpha; app 2 knows only /beta.
        RecordingRouterRouteHandler alphaHandler = new();
        RecordingRouterRouteHandler betaHandler = new();

        TestWebApplication app1 = new();
        app1.AddRouting();
        app1.UseRouting().Map(new Route(HttpMethod.Get, "/alpha", alphaHandler));

        TestWebApplication app2 = new();
        app2.AddRouting();
        app2.UseRouting().Map(new Route(HttpMethod.Get, "/beta", betaHandler));

        // Track fall-through so a 404 (no leak) is observable.
        bool app1FellThrough = false;
        bool app2FellThrough = false;
        app1.Use((ctx, next) => { app1FellThrough = true; return next.Invoke(ctx); });
        app2.Use((ctx, next) => { app2FellThrough = true; return next.Invoke(ctx); });

        // Act & Assert — app 1 serves /alpha but 404s /beta.
        await app1.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/alpha"));
        alphaHandler.WasInvoked.ShouldBeTrue();
        app1FellThrough.ShouldBeFalse();

        app1FellThrough = false;
        await app1.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/beta"));
        app1FellThrough.ShouldBeTrue();          // /beta is NOT in app 1's table
        betaHandler.WasInvoked.ShouldBeFalse();  // app 2's route never leaked into app 1

        // app 2 serves /beta but 404s /alpha.
        await app2.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/beta"));
        betaHandler.WasInvoked.ShouldBeTrue();
        app2FellThrough.ShouldBeFalse();

        app2FellThrough = false;
        await app2.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/alpha"));
        app2FellThrough.ShouldBeTrue();          // /alpha is NOT in app 2's table
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - PerApp: UseRouting returns the same builder AddRouting registered")]
    public void UseRouting_ShouldReturnTheApplicationsRegisteredBuilder()
    {
        // Arrange
        TestWebApplication app = new();
        app.AddRouting();

        IRouterFeature feature = app.Context.Features.OfType<IRouterFeature>().Single();

        // Act
        IRouterBuilder fromUseRouting = app.UseRouting();

        // Assert — UseRouting hands back the per-application feature's own builder, not a shared one.
        fromUseRouting.ShouldBeSameAs(feature.Builder);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - PerApp: Distinct applications get distinct router builders")]
    public void TwoApplications_ShouldGetDistinctRouterBuilders()
    {
        // Arrange
        TestWebApplication app1 = new();
        app1.AddRouting();
        TestWebApplication app2 = new();
        app2.AddRouting();

        // Act
        IRouterBuilder builder1 = app1.UseRouting();
        IRouterBuilder builder2 = app2.UseRouting();

        // Assert — no process-wide shared builder: each application owns its own.
        builder1.ShouldNotBeSameAs(builder2);
    }
}
