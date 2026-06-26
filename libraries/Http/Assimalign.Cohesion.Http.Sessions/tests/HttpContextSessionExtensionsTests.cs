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
    public void Session_BeforeSet_ShouldReturnNull()
    {
        IHttpContext context = new BareHttpContext();

        IHttpSession? session = context.Session;

        session.ShouldBeNull();
    }

    [Fact]
    public void Session_BeforeSet_ShouldNotInstallFeatureOnRead()
    {
        // The getter must remain side-effect-free: reading Session on a context
        // that has never seen session middleware should leave the feature
        // collection untouched so callers can still test for the presence of an
        // IHttpSessionFeature later.
        IHttpContext context = new BareHttpContext();

        _ = context.Session;

        context.Features.Get<IHttpSessionFeature>().ShouldBeNull();
    }

    [Fact]
    public void Session_Set_ShouldRoundTripViaGetter()
    {
        IHttpContext context = new BareHttpContext();
        IHttpSession installed = new HttpSession("session-id");

        context.Session = installed;

        context.Session.ShouldBeSameAs(installed);
    }

    [Fact]
    public void Session_Set_ShouldInstallSessionFeature()
    {
        IHttpContext context = new BareHttpContext();
        IHttpSession installed = new HttpSession("session-id");

        context.Session = installed;

        IHttpSessionFeature? feature = context.Features.Get<IHttpSessionFeature>();
        feature.ShouldNotBeNull();
        feature!.Session.ShouldBeSameAs(installed);
    }

    [Fact]
    public void Session_SetTwice_ShouldReuseFeatureInstance()
    {
        // Subsequent assignments mutate the existing IHttpSessionFeature
        // rather than churning a new one into the collection. This matters for
        // observers that captured the feature reference earlier in the pipeline.
        IHttpContext context = new BareHttpContext();
        context.Session = new HttpSession("first");
        IHttpSessionFeature firstFeature = context.Features.Get<IHttpSessionFeature>()!;

        IHttpSession second = new HttpSession("second");
        context.Session = second;
        IHttpSessionFeature secondFeature = context.Features.Get<IHttpSessionFeature>()!;

        secondFeature.ShouldBeSameAs(firstFeature);
        secondFeature.Session.ShouldBeSameAs(second);
    }

    [Fact]
    public void Session_SetNull_ShouldThrow()
    {
        IHttpContext context = new BareHttpContext();

        Should.Throw<ArgumentNullException>(() => context.Session = null!);
    }

    [Fact]
    public void Session_GetOnNullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => _ = context.Session);
    }

    [Fact]
    public void Session_SetOnNullContext_ShouldThrow()
    {
        IHttpContext context = null!;
        IHttpSession session = new HttpSession("session-id");

        Should.Throw<ArgumentNullException>(() => context.Session = session);
    }

    [Fact]
    public void Session_PreInstalledFeature_ShouldBeObservedByGetter()
    {
        // Middleware that installs the feature directly (rather than via the
        // Session setter) must still be visible through context.Session.
        IHttpContext context = new BareHttpContext();
        IHttpSession session = new HttpSession("preinstalled");
        context.Features.Set<IHttpSessionFeature>(new TestSessionFeature(session));

        IHttpSession? observed = context.Session;

        observed.ShouldBeSameAs(session);
    }

    [Fact]
    public void RequireSession_BeforeSet_ShouldThrow()
    {
        IHttpContext context = new BareHttpContext();

        Should.Throw<InvalidOperationException>(() => _ = context.RequireSession);
    }

    [Fact]
    public void RequireSession_AfterSet_ShouldReturnInstance()
    {
        IHttpContext context = new BareHttpContext();
        IHttpSession installed = new HttpSession("session-id");
        context.Session = installed;

        IHttpSession resolved = context.RequireSession;

        resolved.ShouldBeSameAs(installed);
    }

    [Fact]
    public void RequireSession_OnNullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => _ = context.RequireSession);
    }

    /// <summary>
    /// Test-local <see cref="IHttpSessionFeature"/> stand-in. Used to verify
    /// that the extension getter consults the feature collection rather than
    /// only recognizing the package's internal default implementation.
    /// </summary>
    private sealed class TestSessionFeature : IHttpSessionFeature
    {
        public TestSessionFeature(IHttpSession session)
        {
            Session = session;
        }

        public string Name => nameof(TestSessionFeature);
        public IHttpSession Session { get; set; }
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpContext"/> stub. Only
    /// <see cref="IHttpContext.Features"/> is exercised; the rest of the surface
    /// is stubbed to satisfy the interface contract.
    /// </summary>
    private sealed class BareHttpContext : IHttpContext
    {
        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request => null!;
        public IHttpResponse Response => null!;
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestCancelled => CancellationToken.None;

        public void Cancel()
        {
            
        }

        public Task CancelAsync()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
