using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Tests;

public class HttpContextAuthenticationExtensionsTests
{
    [Fact]
    public void User_NoFeatureAttached_ShouldReturnEmptyPrincipal()
    {
        // Arrange
        IHttpContext context = new BareHttpContext();

        // Act
        ClaimsPrincipal user = context.User;

        // Assert
        user.ShouldNotBeNull();
        user.Identity.ShouldNotBeNull();
        user.Identity!.IsAuthenticated.ShouldBeFalse();
        user.Identity.Name.ShouldBeNull();
        user.Claims.ShouldBeEmpty();
    }

    [Fact]
    public void User_NoFeatureAttached_ShouldNotInstallFeatureOnRead()
    {
        // The getter must remain side-effect-free: reading User on a context
        // that has never seen authentication middleware should leave the
        // feature collection untouched so callers can still test for the
        // presence of an IHttpAuthenticationFeature later.
        // Arrange
        IHttpContext context = new BareHttpContext();

        // Act
        _ = context.User;

        // Assert
        context.Features.Get<IHttpAuthenticationFeature>().ShouldBeNull();
    }

    [Fact]
    public void User_Set_ShouldRoundTripViaGetter()
    {
        // Arrange
        IHttpContext context = new BareHttpContext();
        ClaimsIdentity identity = new("test");
        identity.AddClaim(new Claim(ClaimTypes.Name, "alice"));
        ClaimsPrincipal principal = new(identity);

        // Act
        context.User = principal;
        ClaimsPrincipal observed = context.User;

        // Assert
        observed.ShouldBeSameAs(principal);
        observed.Identity!.IsAuthenticated.ShouldBeTrue();
        observed.Identity.Name.ShouldBe("alice");
    }

    [Fact]
    public void User_Set_ShouldInstallAuthenticationFeature()
    {
        // Arrange
        IHttpContext context = new BareHttpContext();
        ClaimsPrincipal principal = new(new ClaimsIdentity("test"));

        // Act
        context.User = principal;

        // Assert
        IHttpAuthenticationFeature? feature = context.Features.Get<IHttpAuthenticationFeature>();
        feature.ShouldNotBeNull();
        feature!.User.ShouldBeSameAs(principal);
    }

    [Fact]
    public void User_SetTwice_ShouldReuseFeatureInstance()
    {
        // Subsequent assignments should mutate the existing IHttpAuthenticationFeature
        // rather than churning a new one into the collection. This matters for
        // observers that captured the feature reference earlier in the pipeline.
        // Arrange
        IHttpContext context = new BareHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity("first"));
        IHttpAuthenticationFeature firstFeature = context.Features.Get<IHttpAuthenticationFeature>()!;

        // Act
        ClaimsPrincipal second = new(new ClaimsIdentity("second"));
        context.User = second;
        IHttpAuthenticationFeature secondFeature = context.Features.Get<IHttpAuthenticationFeature>()!;

        // Assert
        secondFeature.ShouldBeSameAs(firstFeature);
        secondFeature.User.ShouldBeSameAs(second);
    }

    [Fact]
    public void User_SetNull_ShouldThrow()
    {
        // Arrange
        IHttpContext context = new BareHttpContext();

        // Act + Assert
        Should.Throw<ArgumentNullException>(() => context.User = null!);
    }

    [Fact]
    public void User_GetOnNullContext_ShouldThrow()
    {
        // Arrange
        IHttpContext context = null!;

        // Act + Assert
        Should.Throw<ArgumentNullException>(() => _ = context.User);
    }

    [Fact]
    public void User_SetOnNullContext_ShouldThrow()
    {
        // Arrange
        IHttpContext context = null!;
        ClaimsPrincipal principal = new(new ClaimsIdentity());

        // Act + Assert
        Should.Throw<ArgumentNullException>(() => context.User = principal);
    }

    [Fact]
    public void User_PreInstalledFeature_ShouldBeObservedByGetter()
    {
        // Authentication middleware that installs the feature directly (rather than
        // via the User setter) must still be visible through context.User.
        // Arrange
        IHttpContext context = new BareHttpContext();
        ClaimsPrincipal principal = new(new ClaimsIdentity("preinstalled"));
        context.Features.Set<IHttpAuthenticationFeature>(new TestAuthenticationFeature(principal));

        // Act
        ClaimsPrincipal observed = context.User;

        // Assert
        observed.ShouldBeSameAs(principal);
    }

    /// <summary>
    /// Test-local <see cref="IHttpAuthenticationFeature"/> stand-in. Used to verify
    /// that the extension getter consults the feature collection rather than only
    /// recognizing the package's internal default implementation.
    /// </summary>
    private sealed class TestAuthenticationFeature : IHttpAuthenticationFeature
    {
        public TestAuthenticationFeature(ClaimsPrincipal user)
        {
            User = user;
        }

        public ClaimsPrincipal User { get; set; }
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpContext"/> stub. Only <see cref="IHttpContext.Features"/>
    /// is exercised; the rest of the surface is stubbed to satisfy the interface contract.
    /// </summary>
    private sealed class BareHttpContext : IHttpContext
    {
        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request => null!;
        public IHttpResponse Response => null!;
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpProtocolUpgrade? Upgrade => null;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestAborted => CancellationToken.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
