using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Sessions.Tests;

public class HttpContextSessionExtensionsTests
{
    [Fact]
    public void GetSession_BeforeSetSession_ShouldReturnNull()
    {
        IHttpContext context = new BareHttpContext();

        IHttpSession? session = context.GetSession();

        session.ShouldBeNull();
    }

    [Fact]
    public void SetSession_ThenGetSession_ShouldReturnInstalledSession()
    {
        IHttpContext context = new BareHttpContext();
        IHttpSession installed = new HttpSession("session-id");

        context.SetSession(installed);

        context.GetSession().ShouldBeSameAs(installed);
    }

    [Fact]
    public void RequireSession_BeforeSetSession_ShouldThrow()
    {
        IHttpContext context = new BareHttpContext();

        Should.Throw<InvalidOperationException>(() => context.RequireSession());
    }

    [Fact]
    public void RequireSession_AfterSetSession_ShouldReturnInstance()
    {
        IHttpContext context = new BareHttpContext();
        IHttpSession installed = new HttpSession("session-id");
        context.SetSession(installed);

        IHttpSession resolved = context.RequireSession();

        resolved.ShouldBeSameAs(installed);
    }

    [Fact]
    public void SetSession_Twice_ShouldOverwrite()
    {
        IHttpContext context = new BareHttpContext();
        IHttpSession first = new HttpSession("first");
        IHttpSession second = new HttpSession("second");

        context.SetSession(first);
        context.SetSession(second);

        context.GetSession().ShouldBeSameAs(second);
    }

    [Fact]
    public void GetSession_NullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => context.GetSession());
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpContext"/> stub so the extension methods can be exercised
    /// without pulling the full transport stack. Only the <see cref="IHttpContext.Items"/>
    /// dictionary backs the session storage; everything else is unused for these tests.
    /// </summary>
    private sealed class BareHttpContext : IHttpContext
    {
        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request => null!;
        public IHttpResponse Response => null!;
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestAborted => CancellationToken.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
