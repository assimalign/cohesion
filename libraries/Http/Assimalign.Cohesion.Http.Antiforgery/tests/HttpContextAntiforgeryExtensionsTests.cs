using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

public class HttpContextAntiforgeryExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Antiforgery: Should return null before a feature is installed")]
    public void Antiforgery_BeforeSet_ShouldReturnNull()
    {
        IHttpContext context = new TestHttpContext();

        context.Antiforgery.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Antiforgery: Should round-trip through the setter")]
    public void Antiforgery_Set_ShouldRoundTripViaGetter()
    {
        IHttpContext context = new TestHttpContext();
        IHttpAntiforgery service = HttpAntiforgery.Create();

        context.Antiforgery = service;

        context.Antiforgery.ShouldBeSameAs(service);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Antiforgery: Should install an antiforgery feature on set")]
    public void Antiforgery_Set_ShouldInstallFeature()
    {
        IHttpContext context = new TestHttpContext();

        context.Antiforgery = HttpAntiforgery.Create();

        context.Features.Get<IHttpAntiforgeryFeature>().ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Antiforgery: Should reject a null assignment")]
    public void Antiforgery_SetNull_ShouldThrow()
    {
        IHttpContext context = new TestHttpContext();

        Should.Throw<ArgumentNullException>(() => context.Antiforgery = null);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - RequireAntiforgery: Should throw before set")]
    public void RequireAntiforgery_BeforeSet_ShouldThrow()
    {
        IHttpContext context = new TestHttpContext();

        Should.Throw<InvalidOperationException>(() => _ = context.RequireAntiforgery);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - RequireAntiforgery: Should return the service after set")]
    public void RequireAntiforgery_AfterSet_ShouldReturnService()
    {
        IHttpContext context = new TestHttpContext();
        IHttpAntiforgery service = HttpAntiforgery.Create();
        context.Antiforgery = service;

        context.RequireAntiforgery.ShouldBeSameAs(service);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Antiforgery: Should throw on a null context")]
    public void Antiforgery_OnNullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => _ = context.Antiforgery);
    }
}
