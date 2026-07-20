using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Internal;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

/// <summary>
/// Drives <see cref="SessionMiddleware"/> directly over an in-memory context so
/// the cookie posture, lazy establishment, HTTPS Secure flag, head-committed
/// guard, and id regeneration can be asserted precisely.
/// </summary>
public class SessionMiddlewareTests
{
    private const string DefaultCookieName = ".Cohesion.Session";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: An untouched session establishes no cookie and persists nothing")]
    public async Task Invoke_SessionNeverAccessed_ShouldNotEstablishCookie()
    {
        // Arrange
        SessionTestContext context = new();
        InMemoryHttpSessionStore store = new();

        // Act — handler ignores the session entirely
        await RunAsync(context, store, _ => Task.CompletedTask);

        // Assert
        context.Response.Cookies.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: A new session establishes a hardened, session-scoped cookie")]
    public async Task Invoke_NewSession_ShouldEstablishHardenedCookie()
    {
        // Arrange
        SessionTestContext context = new(HttpScheme.Http);
        InMemoryHttpSessionStore store = new();

        // Act
        await RunAsync(context, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            session.SetString("user", "alice");
        });

        // Assert
        HttpCookie cookie = context.Response.Cookies.ShouldHaveSingleItem();
        cookie.Name.ShouldBe(DefaultCookieName);
        cookie.Options.HttpOnly.ShouldBeTrue();
        cookie.Options.SameSite.ShouldBe(HttpCookieSameSiteMode.Lax);
        cookie.Options.Secure.ShouldBeFalse();        // plaintext request
        cookie.Options.Path.ShouldBe("/");
        cookie.Options.Expires.ShouldBeNull();         // session-scoped
        cookie.Options.MaxAge.ShouldBeNull();

        // The state round-trips through the store under the cookie's id.
        (await ReadStoredStringAsync(store, cookie.Value, "user")).ShouldBe("alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: An HTTPS request marks the session cookie Secure")]
    public async Task Invoke_HttpsRequest_ShouldMarkCookieSecure()
    {
        // Arrange
        SessionTestContext context = new(HttpScheme.Https);
        InMemoryHttpSessionStore store = new();

        // Act
        await RunAsync(context, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            session.SetString("k", "v");
        });

        // Assert
        HttpCookie cookie = context.Response.Cookies.ShouldHaveSingleItem();
        cookie.Options.Secure.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: An existing session cookie is loaded without re-issuing a cookie")]
    public async Task Invoke_ExistingCookie_ShouldLoadWithoutReissuingCookie()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        await SeedAsync(store, "existing-id", ("user", "bob"));
        SessionTestContext context = new(HttpScheme.Http, requestCookieHeader: $"{DefaultCookieName}=existing-id");
        string? observed = null;

        // Act
        await RunAsync(context, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            observed = session.GetString("user");
        });

        // Assert
        observed.ShouldBe("bob");
        context.Response.Cookies.ShouldBeEmpty(); // client already holds the id
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: A modified session persists across a store round-trip")]
    public async Task Invoke_ModifiedSession_ShouldPersistToStore()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        SessionTestContext write = new();

        // Act — first request writes
        await RunAsync(write, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            session.SetString("theme", "dark");
        });

        string id = write.Response.Cookies.ShouldHaveSingleItem().Value;

        // Second request presents the same cookie and reads it back
        SessionTestContext read = new(HttpScheme.Http, requestCookieHeader: $"{DefaultCookieName}={id}");
        string? observed = null;
        await RunAsync(read, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            observed = session.GetString("theme");
        });

        // Assert
        observed.ShouldBe("dark");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: A honored custom cookie name and path flow to the cookie")]
    public async Task Invoke_CustomOptions_ShouldHonorCookieNameAndPath()
    {
        // Arrange
        SessionTestContext context = new();
        InMemoryHttpSessionStore store = new();

        // Act
        await RunAsync(
            context,
            store,
            async c => (await c.LoadSessionAsync()).SetString("k", "v"),
            options =>
            {
                options.CookieName = "app.sid";
                options.CookiePath = "/app";
                options.CookieHttpOnly = false;
            });

        // Assert
        HttpCookie cookie = context.Response.Cookies.ShouldHaveSingleItem();
        cookie.Name.ShouldBe("app.sid");
        cookie.Options.Path.ShouldBe("/app");
        cookie.Options.HttpOnly.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: Regeneration mints a new id, removes the old, and replaces the cookie")]
    public async Task Invoke_RegenerateId_ShouldRotateIdRemoveOldAndReplaceCookie()
    {
        // Arrange — an established session presenting an existing cookie
        InMemoryHttpSessionStore store = new();
        await SeedAsync(store, "old-id", ("user", "alice"));
        SessionTestContext context = new(HttpScheme.Http, requestCookieHeader: $"{DefaultCookieName}=old-id");
        string? newId = null;

        // Act
        await RunAsync(context, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            session.GetString("user").ShouldBe("alice");
            await c.RegenerateSessionIdAsync();
            newId = session.Id;
        });

        // Assert
        newId.ShouldNotBeNull();
        newId.ShouldNotBe("old-id");

        HttpCookie cookie = context.Response.Cookies.ShouldHaveSingleItem();
        cookie.Value.ShouldBe(newId);

        (await ReadStoredStringAsync(store, "old-id", "user")).ShouldBeNull();  // old id evicted
        (await ReadStoredStringAsync(store, newId!, "user")).ShouldBe("alice"); // state under new id
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: Cookie establishment is skipped once the response head has started")]
    public async Task Invoke_ResponseHeadStarted_ShouldSkipCookieEstablishment()
    {
        // Arrange — a committed head cannot carry a new Set-Cookie
        SessionTestContext context = new();
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(hasStarted: true));
        RecordingSessionStore store = new();

        // Act
        await RunAsync(context, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            session.SetString("k", "v");
        });

        // Assert — no cookie despite the session being accessed (best-effort establishment), and no
        // store persist either: the client can never present the undelivered id, so committing the
        // orphaned session would only litter the store until the idle timeout reaped it.
        context.Response.Cookies.ShouldBeEmpty();
        store.SetCount.ShouldBe(0);
        store.RefreshCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: An established session presented by cookie still commits after the head starts")]
    public async Task Invoke_ExistingSessionHeadStarted_ShouldStillCommit()
    {
        // Arrange — the client already holds the id, so a committed head suppresses nothing
        RecordingSessionStore store = new();
        await SeedAsync(store, "held-id", ("user", "alice"));
        SessionTestContext context = new(HttpScheme.Http, requestCookieHeader: $"{DefaultCookieName}=held-id");
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(hasStarted: true));

        // Act
        await RunAsync(context, store, async c =>
        {
            IHttpSession session = await c.LoadSessionAsync();
            session.SetString("user", "bob");
        });

        // Assert — the post-next commit touches only the store, never headers, so it proceeds
        store.SetCount.ShouldBeGreaterThan(1); // the seed write plus the commit
        (await ReadStoredStringAsync(store, "held-id", "user")).ShouldBe("bob");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Middleware: Regenerating before any session access throws")]
    public async Task Invoke_RegenerateWithoutAccess_ShouldThrow()
    {
        // Arrange
        SessionTestContext context = new();
        InMemoryHttpSessionStore store = new();

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await RunAsync(context, store, c => c.RegenerateSessionIdAsync().AsTask()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Context: LoadSessionAsync without UseSessions throws")]
    public async Task LoadSessionAsync_WithoutUseSessions_ShouldThrow()
    {
        // Arrange — no session middleware installed the feature
        SessionTestContext context = new();

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await context.LoadSessionAsync());
    }

    private static async Task RunAsync(
        SessionTestContext context,
        IHttpSessionStore store,
        Func<IHttpContext, Task> handler,
        Action<HttpSessionOptions>? configure = null)
    {
        HttpSessionOptions options = new();
        configure?.Invoke(options);

        SessionMiddleware middleware = new(store, options);
        await middleware.InvokeAsync(context, ctx => handler(ctx));
    }

    private static async Task SeedAsync(IHttpSessionStore store, string id, params (string Key, string Value)[] entries)
    {
        System.Collections.Generic.Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
        foreach ((string key, string value) in entries)
        {
            values[key] = Encoding.UTF8.GetBytes(value);
        }

        await store.SetAsync(id, HttpSessionSerializer.Serialize(values), IdleTimeout);
    }

    private static async Task<string?> ReadStoredStringAsync(IHttpSessionStore store, string id, string key)
    {
        byte[]? frame = await store.GetAsync(id);
        if (frame is null || !HttpSessionSerializer.TryDeserialize(frame, out System.Collections.Generic.Dictionary<string, byte[]>? values))
        {
            return null;
        }

        return values.TryGetValue(key, out byte[]? bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }
}
