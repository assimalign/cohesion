using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// How the shared engine opens and copies a file while the file system moves underneath it: several
/// responses reading one file at once, a file deleted while it is served or before it is read, and a
/// file whose content no longer matches the size its validators and <c>Content-Length</c> came from.
/// </summary>
public class StaticFilesFileAccessTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private static IWebApplicationPipeline CreatePipeline(IFileSystem site)
    {
        var builder = new TestPipelineBuilder();
        builder.UseStaticFiles(site);
        return builder.Build();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Invoke: concurrent requests for one file should both be served")]
    public async Task Invoke_ConcurrentRequestsForSameFile_ShouldServeBoth()
    {
        // Arrange — the first response is held mid-copy, so its file stays open while the second
        // request opens the same file.
        using InMemoryFileSystem site = StaticSite.Create(("app.js", "let app = 1;"));
        IWebApplicationPipeline pipeline = CreatePipeline(site);
        var gate = new GatedResponseBody();
        var first = new TestHttpContext("/app.js", HttpMethod.Get);
        first.Response.Body = gate;
        Task firstResponse = pipeline.ExecuteAsync(first);
        await gate.FirstWrite.WaitAsync(_testTimeout);

        // Act
        var second = new TestHttpContext("/app.js", HttpMethod.Get);
        await pipeline.ExecuteAsync(second);
        gate.Release();
        await firstResponse.WaitAsync(_testTimeout);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.ReadResponseBody().ShouldBe("let app = 1;");
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        gate.ReadWritten().ShouldBe("let app = 1;");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: concurrent sends of one file should both complete")]
    public async Task SendFileAsync_ConcurrentSendsOfSameFile_ShouldServeBoth()
    {
        // Arrange
        using InMemoryFileSystem site = StaticSite.Create(("report.txt", "annual report"));
        IFileSystemFile file = site.GetFile(FileSystemPath.Parse("report.txt"));
        var gate = new GatedResponseBody();
        var first = new TestHttpContext("/report", HttpMethod.Get);
        first.Response.Body = gate;
        Task firstResponse = first.Response.SendFileAsync(file);
        await gate.FirstWrite.WaitAsync(_testTimeout);

        // Act
        var second = new TestHttpContext("/report", HttpMethod.Get);
        await second.Response.SendFileAsync(file);
        gate.Release();
        await firstResponse.WaitAsync(_testTimeout);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.ReadResponseBody().ShouldBe("annual report");
        gate.ReadWritten().ShouldBe("annual report");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a file deleted while it is served should finish sending its content")]
    public async Task SendFileAsync_FileDeletedWhileServed_ShouldCompleteResponse()
    {
        // Arrange — a deployment that deletes or renames a file in use must not fail, and the response
        // in flight keeps the content its validators describe.
        using InMemoryFileSystem site = StaticSite.Create(("report.txt", "annual report"));
        var gate = new GatedResponseBody();
        var inFlight = new TestHttpContext("/report", HttpMethod.Get);
        inFlight.Response.Body = gate;
        Task response = inFlight.Response.SendFileAsync(site, "report.txt");
        await gate.FirstWrite.WaitAsync(_testTimeout);

        // Act
        site.DeleteFile(FileSystemPath.Parse("report.txt"));
        gate.Release();
        await response.WaitAsync(_testTimeout);
        var afterDelete = new TestHttpContext("/report", HttpMethod.Get);
        await afterDelete.Response.SendFileAsync(site, "report.txt");

        // Assert
        inFlight.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        gate.ReadWritten().ShouldBe("annual report");
        afterDelete.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a file deleted before it is read should respond 404")]
    public async Task SendFileAsync_InMemoryFileDeletedBeforeSend_ShouldRespond404()
    {
        // Arrange — the handler resolved the file, then it went away.
        using InMemoryFileSystem site = StaticSite.Create(("report.txt", "annual report"));
        IFileSystemFile file = site.GetFile(FileSystemPath.Parse("report.txt"));
        site.DeleteFile(FileSystemPath.Parse("report.txt"));
        var context = new TestHttpContext("/report", HttpMethod.Get);

        // Act
        await context.Response.SendFileAsync(file);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: a physical file deleted before it is read should respond 404")]
    public async Task SendFileAsync_PhysicalFileDeletedBeforeSend_ShouldRespond404()
    {
        // Arrange — the physical mount reports a vanished file as a FileNotFoundException when its
        // size is read, before it is ever opened.
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cohesion-file-access-{Guid.NewGuid():N}")).FullName;
        try
        {
            string path = Path.Combine(root, "report.txt");
            await File.WriteAllTextAsync(path, "annual report");
            using var mount = new PhysicalFileSystem(new PhysicalFileSystemOptions
            {
                Root = FileSystemPath.Parse(root),
                IsReadOnly = true,
            });
            IFileSystemFile file = mount.GetFile(FileSystemPath.Parse("report.txt"));
            File.Delete(path);
            var context = new TestHttpContext("/report", HttpMethod.Get);

            // Act
            await context.Response.SendFileAsync(file);

            // Assert
            context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            context.ReadResponseBody().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: content that outgrew the reported size should be cut at the declared length")]
    public async Task SendFileAsync_ContentLongerThanSize_ShouldSendDeclaredLength()
    {
        // Arrange — Content-Length says 4; writing 10 bytes would desynchronize HTTP/1.1 framing.
        var file = new MisreportedSizeFile("data.txt", "0123456789", reportedSize: 4);
        var context = new TestHttpContext("/data", HttpMethod.Get);

        // Act
        await context.Response.SendFileAsync(file);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue contentLength).ShouldBeTrue();
        ((string)contentLength).ShouldBe("4");
        context.ReadResponseBody().ShouldBe("0123");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: content shorter than the reported size should abort the response")]
    public async Task SendFileAsync_ContentShorterThanSize_ShouldAbortWithEndOfStream()
    {
        // Arrange — completing would leave the client waiting for bytes that never come.
        var file = new MisreportedSizeFile("data.txt", "0123456789", reportedSize: 20);
        var context = new TestHttpContext("/data", HttpMethod.Get);

        // Act & Assert
        await Should.ThrowAsync<EndOfStreamException>(() => context.Response.SendFileAsync(file));
    }
}
