using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Internal;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

/// <summary>
/// Covers "Add configures, Use places": <see cref="SessionsBuilder"/>, which
/// <c>builder.Services.AddSessions(...)</c> builds, validates the options before any middleware
/// exists and snapshots them, and <c>UseSessions()</c> places the last registered middleware, or one
/// with the defaults when none is registered.
/// </summary>
public class SessionPipelineExtensionsTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: An invalid CookiePath fails in the configuration assignment")]
    [InlineData(null)]
    [InlineData("/tenant;x")]
    [InlineData("/café")]
    [InlineData("/a\r\nb")]
    public void AddSessions_CookiePathOutsideGrammar_ShouldThrowAtAssignment(string? cookiePath)
    {
        // Arrange
        IWebApplicationMiddleware? middleware = null;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => middleware = BuildSessions(options => options.CookiePath = cookiePath!));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.InnerException.ShouldBeNull();
        middleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: A default CookiePath fails before the middleware is built")]
    [InlineData(false)]
    [InlineData(true)]
    public void AddSessions_DefaultCookiePath_ShouldThrowAtRegistration(bool explicitStore)
    {
        // Arrange
        IWebApplicationMiddleware? middleware = null;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => middleware = BuildSessions(options => options.CookiePath = default, explicitStore));

        // Assert
        exception.ParamName.ShouldBe("Options");
        exception.Message.ShouldBe(new ArgumentException(
            "HttpSessionOptions.CookiePath is not an RFC 6265 path-value: it must be US-ASCII with no ';' and no control characters. Percent-encode any other character.",
            "Options").Message);
        exception.InnerException.ShouldBeNull();
        middleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: A CookiePath in path-value builds the middleware")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/app/v1")]
    [InlineData("/caf%C3%A9")]
    public void AddSessions_CookiePathInGrammar_ShouldBuildMiddleware(string cookiePath)
    {
        // Act
        IWebApplicationMiddleware middleware = BuildSessions(options => options.CookiePath = cookiePath);

        // Assert
        middleware.ShouldBeOfType<SessionMiddleware>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: A non-positive idle timeout fails before the middleware is built")]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddSessions_NonPositiveIdleTimeout_ShouldThrowAtRegistration(int seconds)
    {
        // Arrange
        IWebApplicationMiddleware? middleware = null;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => middleware = BuildSessions(options => options.IdleTimeout = TimeSpan.FromSeconds(seconds)));

        // Assert
        exception.ShouldBeOfType<ArgumentException>();
        exception.ParamName.ShouldBe("Options");
        exception.Message.ShouldStartWith("HttpSessionOptions.IdleTimeout must be positive", Case.Sensitive);
        middleware.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: UseStore refuses a null store and chains")]
    public void UseStore_NullOrStore_ShouldRefuseNullAndReturnTheBuilder()
    {
        // Arrange
        SessionsBuilder sessions = new();

        // Act
        SessionsBuilder returned = sessions.UseStore(new InMemoryHttpSessionStore());

        // Assert
        returned.ShouldBeSameAs(sessions);
        Should.Throw<ArgumentNullException>(() => sessions.UseStore(null!)).ParamName.ShouldBe("store");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: Options changed after Build should not reach the built middleware")]
    public async Task Build_OptionsChangedAfterBuild_ShouldNotReachTheMiddleware()
    {
        // Arrange — the middleware reads its options on every request, so Build hands it a copy.
        SessionsBuilder sessions = new();
        sessions.Options.CookieName = "before";
        IWebApplicationMiddleware middleware = sessions.Build();
        sessions.Options.CookieName = "after";
        sessions.Options.IdleTimeout = TimeSpan.Zero;
        SessionTestContext context = new();

        // Act
        await middleware.InvokeAsync(context, TouchSessionAsync);

        // Assert
        context.Response.Cookies.ShouldHaveSingleItem().Name.ShouldBe<HttpCookieName>("before");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: Should place the middleware the application registered")]
    public async Task UseSessions_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange
        RecordingSessionStore store = new();
        TestPipelineBuilder pipeline = new() { Terminal = TouchSessionAsync };
        pipeline.Registered.Add(BuildSessions(options => options.CookieName = "sid", store));
        SessionTestContext context = new();

        // Act
        IWebApplicationPipelineBuilder returned = pipeline.UseSessions();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert — the registered cookie name, and the registered store committed the new session.
        returned.ShouldBeSameAs(pipeline);
        context.Response.Cookies.ShouldHaveSingleItem().Name.ShouldBe<HttpCookieName>("sid");
        store.SetCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: Should place the last registration when sessions are registered twice")]
    public async Task UseSessions_WithTwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange
        RecordingSessionStore firstStore = new();
        RecordingSessionStore lastStore = new();
        TestPipelineBuilder pipeline = new() { Terminal = TouchSessionAsync };
        pipeline.Registered.Add(BuildSessions(options => options.CookieName = "first", firstStore));
        pipeline.Registered.Add(BuildSessions(options => options.CookieName = "last", lastStore));
        SessionTestContext context = new();

        // Act
        pipeline.UseSessions();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        context.Response.Cookies.ShouldHaveSingleItem().Name.ShouldBe<HttpCookieName>("last");
        lastStore.SetCount.ShouldBe(1);
        firstStore.SetCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: Should place the defaults when the application registered none")]
    public async Task UseSessions_WithoutRegistration_ShouldPlaceTheDefaults()
    {
        // Arrange
        TestPipelineBuilder pipeline = new() { Terminal = TouchSessionAsync };
        SessionTestContext context = new();

        // Act
        pipeline.UseSessions();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        HttpCookie cookie = context.Response.Cookies.ShouldHaveSingleItem();
        cookie.Name.ShouldBe<HttpCookieName>(".Cohesion.Session");
        cookie.Options.HttpOnly.ShouldBeTrue();
        cookie.Options.Path.ShouldBe<HttpCookiePath?>("/");
    }

    private static IWebApplicationMiddleware BuildSessions(Action<HttpSessionOptions>? configure, bool explicitStore = false)
        => BuildSessions(configure, explicitStore ? new InMemoryHttpSessionStore() : null);

    private static IWebApplicationMiddleware BuildSessions(Action<HttpSessionOptions>? configure, IHttpSessionStore? store)
    {
        SessionsBuilder sessions = new();
        if (store is not null)
        {
            sessions.UseStore(store);
        }

        configure?.Invoke(sessions.Options);
        return sessions.Build();
    }

    private static async Task TouchSessionAsync(IHttpContext context)
    {
        IHttpSession session = await context.LoadSessionAsync();
        session.SetString("user", "alice");
    }
}
