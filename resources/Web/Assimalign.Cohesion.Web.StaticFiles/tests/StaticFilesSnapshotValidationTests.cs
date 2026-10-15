using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

public sealed class StaticFilesSnapshotValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Start: Mutated content types fail before the web-root snapshot")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StartAsync_ContentTypeMutatedAfterRegistration_ShouldThrowBeforeServing(bool fallbackRoute, bool mapping)
    {
        // Arrange
        await using var factory = new WebApplicationTestFactory();
        factory.Builder.Services.AddRouting();
        StaticFilesOptions? retainedOptions = null;
        if (fallbackRoute)
        {
            factory.Application.MapFallbackToFile("index.html", options => retainedOptions = options);
        }
        else
        {
            factory.Application.UseStaticFiles(options => retainedOptions = options);
        }

        if (mapping)
        {
            retainedOptions!.ContentTypeMappings[".html"] = "text/html\r\nX-Injected: true";
        }
        else
        {
            retainedOptions!.FallbackContentType = "text/plain\r\nX-Injected: true";
        }

        // Act / Assert
        await Should.ThrowAsync<ArgumentException>(() => factory.StartAsync(CancellationToken.None));
    }
}
