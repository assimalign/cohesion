using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

public sealed class StaticFilesSnapshotValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Start: Mutated content types fail before the fallback route's web-root snapshot")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_ContentTypeMutatedAfterRegistration_ShouldThrowBeforeServing(bool mapping)
    {
        // Arrange — MapFallbackToFile snapshots its options when the pipeline is built, so it validates
        // them again there.
        await using var factory = new WebApplicationTestFactory();
        factory.Builder.Services.AddRouting();
        StaticFilesOptions? retainedOptions = null;
        factory.Application.MapFallbackToFile("index.html", options => retainedOptions = options);

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

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - AddStaticFiles: Content types mutated after registration should not reach a response")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddStaticFiles_ContentTypeMutatedAfterRegistration_ShouldKeepTheRegisteredSnapshot(bool mapping)
    {
        // Arrange — AddStaticFiles builds the middleware when it registers it, so options changed afterwards
        // are never read: they cannot inject a header or change a type.
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using InMemoryFileSystem site = StaticSite.Create(("index.html", "<html></html>"), ("data.unknown", "data"));
        await using var factory = new WebApplicationTestFactory();
        StaticFilesOptions? retainedOptions = null;
        factory.Builder.Services.AddStaticFiles(files =>
        {
            files.UseFileSystem(site);
            files.Options.ServeUnknownContentTypes = true;
            retainedOptions = files.Options;
        });
        factory.Application.UseStaticFiles();

        if (mapping)
        {
            retainedOptions!.ContentTypeMappings[".html"] = "text/html\r\nX-Injected: true";
        }
        else
        {
            retainedOptions!.FallbackContentType = "text/plain\r\nX-Injected: true";
        }

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync(mapping ? "/index.html" : "/data.unknown", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(mapping ? "text/html" : "application/octet-stream");
        response.Headers.Contains("X-Injected").ShouldBeFalse();
        response.Content.Headers.Any(header => header.Key == "X-Injected").ShouldBeFalse();
    }
}
