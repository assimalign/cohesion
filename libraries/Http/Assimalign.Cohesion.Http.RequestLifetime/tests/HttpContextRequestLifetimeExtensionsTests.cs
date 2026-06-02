using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.RequestLifetime.Tests;

public class HttpContextRequestLifetimeExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - RequestLifetime: Should install a feature on first read")]
    public void RequestLifetime_FirstRead_ShouldInstallFeature()
    {
        IHttpContext context = new BareHttpContext();

        IHttpRequestLifetime lifetime = context.RequestLifetime;

        lifetime.ShouldNotBeNull();
        context.Features.Get<IHttpRequestLifetimeFeature>().ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - RequestLifetime: Repeated reads should return the same instance")]
    public void RequestLifetime_RepeatedReads_ShouldReturnSameInstance()
    {
        IHttpContext context = new BareHttpContext();

        IHttpRequestLifetime first = context.RequestLifetime;
        IHttpRequestLifetime second = context.RequestLifetime;

        second.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - RequestLifetime: Pre-installed feature should be observed")]
    public void RequestLifetime_PreInstalledFeature_ShouldBeObserved()
    {
        IHttpContext context = new BareHttpContext();
        HttpRequestLifetime seeded = new();
        context.Features.Set<IHttpRequestLifetimeFeature>(new TestLifetimeFeature(seeded));

        context.RequestLifetime.ShouldBeSameAs(seeded);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - Abort: The extension helper should trigger the lifetime token")]
    public void Abort_ViaExtension_ShouldTriggerLifetimeToken()
    {
        IHttpContext context = new BareHttpContext();
        IHttpRequestLifetime lifetime = context.RequestLifetime;

        context.Abort();

        lifetime.RequestAborted.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - RequestLifetime: Should throw on a null context")]
    public void RequestLifetime_OnNullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => _ = context.RequestLifetime);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - Abort: Should throw on a null context")]
    public void Abort_OnNullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => context.Abort());
    }

    private sealed class TestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public TestLifetimeFeature(IHttpRequestLifetime requestLifetime)
        {
            RequestLifetime = requestLifetime;
        }

        public string Name => nameof(TestLifetimeFeature);
        public IHttpRequestLifetime RequestLifetime { get; }
    }
}
