using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Internal;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

public class OutputCacheReplayTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Invoke: Store header values are immutable before replay")]
    public async Task Invoke_StoreSourceArrayChanged_ShouldReplayValidatedSnapshot()
    {
        // Arrange
        HttpFieldValue?[] values = ["safe", null, ""];
        OutputCacheHeader header = new("X-Test", values);
        OutputCacheEntry entry = new(HttpStatusCode.Ok, [header], [], DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1), [], []);
        OutputCacheOptions options = new();
        options.AddBasePolicy(_ => { });
        OutputCacheMiddleware middleware = new(new FixedOutputCacheStore(entry), options);
        OutputCacheTestContext context = new();
        bool downstreamCalled = false;

        // Act — a store's retained array cannot introduce an uninitialized value after validation.
        values[0] = default(HttpFieldValue);
        await middleware.InvokeAsync(context, _ =>
        {
            downstreamCalled = true;
            return Task.CompletedTask;
        });

        // Assert
        downstreamCalled.ShouldBeFalse();
        context.Response.Headers["X-Test"][0].ShouldBe("safe");
        header.Values[1].ShouldBeNull();
        header.Values[2]!.Value.Value.ShouldBe("");
        Should.Throw<NotSupportedException>(() => ((IList<HttpFieldValue?>)header.Values)[0] = default(HttpFieldValue));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - OutputCacheHeader: Default values are refused while null remains absent")]
    public void Constructor_DefaultPresentField_ShouldThrow()
    {
        // Arrange
        HttpFieldValue?[] values = [default(HttpFieldValue)];

        // Act and assert
        Should.Throw<ArgumentException>(() => _ = new OutputCacheHeader(default, []));
        Should.Throw<ArgumentException>(() => _ = new OutputCacheHeader("X-Test", values));
    }
}
