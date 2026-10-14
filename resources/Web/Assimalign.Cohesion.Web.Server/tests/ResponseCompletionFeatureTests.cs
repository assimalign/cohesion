using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Web.Internal;

namespace Assimalign.Cohesion.Web.Server.Tests;

public class ResponseCompletionFeatureTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Server] - Completion: Every callback runs in order and every failure is retained")]
    public async Task CompleteAsync_WhenCallbacksThrow_ShouldRunAllAndAggregateFailures()
    {
        // Arrange
        ResponseCompletionFeature feature = new();
        List<int> calls = [];
        InvalidOperationException first = new("first callback");
        OperationCanceledException second = new("second callback");
        feature.Register(() => { calls.Add(1); throw first; });
        feature.Register(async () => { await Task.Yield(); calls.Add(2); throw second; });
        feature.Register(() => { calls.Add(3); return ValueTask.CompletedTask; });

        // Act
        AggregateException failure = await Should.ThrowAsync<AggregateException>(
            () => feature.CompleteAsync().AsTask());
        await feature.CompleteAsync();

        // Assert
        calls.ShouldBe(new[] { 1, 2, 3 });
        failure.InnerExceptions.ShouldBe(new Exception[] { first, second });
    }
}
