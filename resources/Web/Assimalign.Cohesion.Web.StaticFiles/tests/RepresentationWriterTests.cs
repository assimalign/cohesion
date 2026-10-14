using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.StaticFiles.Internal;
using Assimalign.Cohesion.Web.Testing;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

public class RepresentationWriterTests
{
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - TrySelectContent: An unusable Date reads the clock once and clamps future modification times")]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task TrySelectContent_UnusableDate_ShouldClampUsingOneClockRead(string? date)
    {
        // Arrange
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, 789, TimeSpan.Zero);
        var clock = new CountingTimeProvider(now);
        var context = new TestHttpContext("/file", HttpMethod.Get);
        context.Response.Headers[HttpHeaderKey.Date] = new HttpHeaderValue(date);
        context.Request.Headers[HttpHeaderKey.Range] = "bytes=0-0";
        context.Request.Headers[HttpHeaderKey.IfRange] = HttpDate.Format(now);
        var representation = new RepresentationMetadata
        {
            ContentType = "text/plain",
            Length = 2,
            LastModified = now.AddDays(1),
        };
        using var content = new MemoryStream(new byte[] { 1, 2 });

        // Act
        bool selected = RepresentationWriter.TrySelectContent(context, ref representation, out HttpRangeSlice? slice, clock);
        await RepresentationWriter.WriteContentAsync(context, representation, slice, content, CancellationToken.None);

        // Assert — the clamped timestamp is not old enough for a date range.
        selected.ShouldBeTrue();
        clock.ReadCount.ShouldBe(1);
        representation.LastModified.ShouldBe(RepresentationMetadata.TruncateToSeconds(now));
        slice.ShouldBeNull();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        ((string?)context.Response.Headers[HttpHeaderKey.LastModified]).ShouldBe(HttpDate.Format(now));
        ((string?)context.Response.Headers[HttpHeaderKey.Date]).ShouldBe(date ?? string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - TrySelectContent: A valid response Date wins without reading the clock")]
    public void TrySelectContent_ValidDate_ShouldAvoidClockRead()
    {
        // Arrange
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new CountingTimeProvider(now.AddDays(-1));
        var context = new TestHttpContext("/file", HttpMethod.Get);
        context.Response.Headers[HttpHeaderKey.Date] = HttpDate.Format(now);
        context.Request.Headers[HttpHeaderKey.Range] = "bytes=0-0";
        context.Request.Headers[HttpHeaderKey.IfRange] = HttpDate.Format(now.AddSeconds(-1));
        var representation = new RepresentationMetadata
        {
            ContentType = "text/plain",
            Length = 2,
            LastModified = now.AddSeconds(-1),
        };

        // Act
        bool selected = RepresentationWriter.TrySelectContent(context, ref representation, out HttpRangeSlice? slice, clock);

        // Assert
        selected.ShouldBeTrue();
        clock.ReadCount.ShouldBe(0);
        representation.LastModified.ShouldBe(now.AddSeconds(-1));
        slice.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - SendFileAsync: Date opt-out preserves modification validators over HTTP/1.1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendFileAsync_DateHeaderSetting_ShouldPreserveDateValidators(bool addDateHeader)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        DateTimeOffset modified = new(1994, 11, 6, 8, 49, 37, TimeSpan.Zero);
        var file = new FixedTimestampFile("data.txt", "0123456789", modified.UtcDateTime);
        await using var factory = new WebApplicationTestFactory();
        factory.Builder.Server.UseServer(options => options.AddDateHeader = addDateHeader);
        factory.Application.Use((context, next) => context.Response.SendFileAsync(file, cancellationToken: context.RequestCancelled));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage full = await client.GetAsync("/file", cancellation.Token);
        using var conditional = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/file");
        conditional.Headers.IfModifiedSince = modified;
        using HttpResponseMessage cached = await client.SendAsync(conditional, cancellation.Token);
        using var range = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/file");
        range.Headers.Range = new RangeHeaderValue(0, 2);
        range.Headers.IfRange = new RangeConditionHeaderValue(modified);
        using HttpResponseMessage partial = await client.SendAsync(range, cancellation.Token);

        // Assert
        full.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        full.Content.Headers.LastModified.ShouldBe(modified);
        (await full.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("0123456789");
        cached.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotModified);
        cached.Content.Headers.LastModified.ShouldBe(modified);
        partial.StatusCode.ShouldBe(System.Net.HttpStatusCode.PartialContent);
        partial.Content.Headers.LastModified.ShouldBe(modified);
        (await partial.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("012");
        full.Headers.Contains("Date").ShouldBe(addDateHeader);
        cached.Headers.Contains("Date").ShouldBe(addDateHeader);
        partial.Headers.Contains("Date").ShouldBe(addDateHeader);
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public CountingTimeProvider(DateTimeOffset now) => _now = now;

        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return _now.AddDays(ReadCount - 1);
        }
    }
}
