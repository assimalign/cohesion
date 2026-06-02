using System.Threading;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.RequestLifetime.Tests;

public class HttpRequestLifetimeTests
{
    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - Abort: Should trigger the RequestAborted token")]
    public void Abort_ShouldTriggerRequestAborted()
    {
        HttpRequestLifetime lifetime = new();
        lifetime.RequestAborted.IsCancellationRequested.ShouldBeFalse();

        lifetime.Abort();

        lifetime.RequestAborted.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - Abort: Should be idempotent")]
    public void Abort_CalledTwice_ShouldBeIdempotent()
    {
        HttpRequestLifetime lifetime = new();

        lifetime.Abort();
        Should.NotThrow(() => lifetime.Abort());

        lifetime.RequestAborted.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - Abort: Should invoke a registered callback")]
    public void Abort_ShouldInvokeRegisteredCallback()
    {
        HttpRequestLifetime lifetime = new();
        bool invoked = false;
        using CancellationTokenRegistration registration = lifetime.RequestAborted.Register(() => invoked = true);

        lifetime.Abort();

        invoked.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - Abort: Should be a no-op after dispose")]
    public void Abort_AfterDispose_ShouldNotThrow()
    {
        HttpRequestLifetime lifetime = new();
        lifetime.Dispose();

        Should.NotThrow(() => lifetime.Abort());
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - Dispose: Should be idempotent")]
    public void Dispose_CalledTwice_ShouldNotThrow()
    {
        HttpRequestLifetime lifetime = new();

        lifetime.Dispose();
        Should.NotThrow(() => lifetime.Dispose());
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLifetime] - RequestAborted: Should be settable to an external token")]
    public void RequestAborted_Settable_ShouldReturnAssignedToken()
    {
        HttpRequestLifetime lifetime = new();
        using CancellationTokenSource external = new();

        lifetime.RequestAborted = external.Token;

        lifetime.RequestAborted.ShouldBe(external.Token);
    }
}
