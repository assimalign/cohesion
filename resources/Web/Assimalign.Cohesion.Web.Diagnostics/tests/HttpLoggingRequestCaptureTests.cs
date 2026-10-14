using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

public class HttpLoggingRequestCaptureTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - Request capture: Reads through Request.HttpContext are observed without mutating or disposing the original body")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_InterfaceRequestBodyReadThroughContext_ShouldCaptureAndPreserveOwnership(bool fault)
    {
        // Arrange
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        TestPipelineBuilder builder = new();
        builder.UseHttpLogging(loggerFactory, options =>
        {
            options.Fields = HttpLoggingFields.RequestBody | HttpLoggingFields.BytesTransferred;
            options.RequestBodyLimit = 4;
        });
        LoggingTestContext context = new(new HttpConnectionInfo());
        using MemoryStream body = new(Encoding.UTF8.GetBytes("body-content"));
        context.Request.Body = body;
        context.Request.Headers[HttpHeaderKey.ContentType] = "text/plain";
        Stream responseBody = context.Response.Body;
        InvalidOperationException failure = new("pipeline failure");

        async Task ReadAsync(IHttpContext inner)
        {
            inner.Response.ShouldBeSameAs(context.Response);
            inner.Features.ShouldBeSameAs(context.Features);
            inner.Items.ShouldBeSameAs(context.Items);
            inner.ConnectionInfo.ShouldBeSameAs(context.ConnectionInfo);
            inner.Request.Headers.ShouldBeSameAs(context.Request.Headers);
            inner.Request.Query.ShouldBeSameAs(context.Request.Query);
            inner.RequestCancelled.ShouldBe(context.RequestCancelled);
            inner.Version.ShouldBe(context.Version);
            await inner.Request.HttpContext.Request.Body.CopyToAsync(Stream.Null, inner.RequestCancelled);
            if (fault)
            {
                throw failure;
            }
        }

        // Act
        if (fault)
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                () => builder.LastMiddleware!.InvokeAsync(context, ReadAsync))).ShouldBeSameAs(failure);
        }
        else
        {
            await builder.LastMiddleware!.InvokeAsync(context, ReadAsync);
        }

        // Assert
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.RequestBody].ShouldBe("body");
        entry.Attributes[HttpLoggingAttributes.RequestBodyBytes].ShouldBe(body.Length);
        entry.Exception.ShouldBe(fault ? failure : null);
        context.Request.Body.ShouldBeSameAs(body);
        context.Response.Body.ShouldBeSameAs(responseBody);
        body.CanRead.ShouldBeTrue();
        body.Position.ShouldBe(body.Length);
    }
}
