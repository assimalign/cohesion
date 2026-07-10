using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the execution glue: <c>context.ExecuteResultAsync(result)</c> delegation, argument
/// validation, and the request-lifetime cancellation default.
/// </summary>
public class HttpContextResultExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - ExecuteResultAsync: delegates to the result with the same context")]
    public async Task ExecuteResultAsync_WithResult_DelegatesToResult()
    {
        // Arrange
        TestHttpContext context = new();
        RecordingResult result = new();

        // Act
        await context.ExecuteResultAsync(result);

        // Assert
        result.ExecutionCount.ShouldBe(1);
        result.Context.ShouldBeSameAs(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ExecuteResultAsync: an omitted token defaults to RequestCancelled")]
    public async Task ExecuteResultAsync_OmittedToken_UsesRequestCancelled()
    {
        // Arrange
        using CancellationTokenSource requestLifetime = new();
        TestHttpContext context = new() { RequestCancelled = requestLifetime.Token };
        RecordingResult result = new();

        // Act
        await context.ExecuteResultAsync(result);

        // Assert — the result write is bound to the exchange's own lifetime.
        result.Token.ShouldBe(requestLifetime.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ExecuteResultAsync: an explicit token is passed through unchanged")]
    public async Task ExecuteResultAsync_ExplicitToken_PassesThrough()
    {
        // Arrange
        using CancellationTokenSource requestLifetime = new();
        using CancellationTokenSource caller = new();
        TestHttpContext context = new() { RequestCancelled = requestLifetime.Token };
        RecordingResult result = new();

        // Act
        await context.ExecuteResultAsync(result, caller.Token);

        // Assert
        result.Token.ShouldBe(caller.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ExecuteResultAsync: a null result is rejected")]
    public async Task ExecuteResultAsync_NullResult_Throws()
    {
        // Arrange
        TestHttpContext context = new();

        // Act + Assert
        await Should.ThrowAsync<ArgumentNullException>(() => context.ExecuteResultAsync(null!));
    }

    private sealed class RecordingResult : IResult
    {
        public int ExecutionCount { get; private set; }

        public IHttpContext? Context { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            Context = context;
            Token = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
