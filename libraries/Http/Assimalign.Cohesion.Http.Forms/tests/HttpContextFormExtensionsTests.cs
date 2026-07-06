using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Forms.Tests;

/// <summary>
/// Tests for the <c>request.Form</c> extension property (get/set) and the
/// <c>context.ReadFormAsync(...)</c> extension method exposed by
/// <see cref="HttpContextFormExtensions"/>. The streaming urlencoded /
/// multipart parsers these extensions drive live in-tree
/// (<c>HttpFormReader</c>, <c>KeyValueAccumulator</c>,
/// <c>HttpMultipartFormReader</c>) and are exercised directly by
/// <c>HttpFormFeatureTests</c>; this suite covers the convenience surface that
/// installs and caches the <see cref="IHttpFormFeature"/>.
/// </summary>
public class HttpContextFormExtensionsTests
{
    [Fact]
    public void Form_NoFeatureAttached_ShouldReturnEmptyCollection()
    {
        IHttpContext context = new BareHttpContext();

        IHttpFormCollection form = context.Request.Form;

        form.ShouldNotBeNull();
        form.Count.ShouldBe(0);
    }

    [Fact]
    public void Form_PreInstalledFeatureWithoutForm_ShouldReturnEmptyCollection()
    {
        IHttpContext context = new BareHttpContext();
        context.Features.Set<IHttpFormFeature>(new TestFormFeature());

        IHttpFormCollection form = context.Request.Form;

        form.Count.ShouldBe(0);
    }

    [Fact]
    public void Form_PreInstalledFeatureWithFormAttached_ShouldReturnInstalledCollection()
    {
        IHttpContext context = new BareHttpContext();
        HttpFormCollection installed = new();
        installed.Add("name", "cohesion");
        context.Features.Set<IHttpFormFeature>(new TestFormFeature { Form = installed });

        IHttpFormCollection observed = context.Request.Form;

        observed.ShouldBeSameAs(installed);
    }

    [Fact]
    public void Form_GetOnNullRequest_ShouldThrow()
    {
        IHttpRequest request = null!;

        Should.Throw<ArgumentNullException>(() => _ = request.Form);
    }

    [Fact]
    public async Task ReadFormAsync_NoFeatureInstalled_ShouldInstallFeatureAndParseBody()
    {
        IHttpContext context = new BareHttpContext(
            "application/x-www-form-urlencoded",
            BodyOf("name=cohesion&role=admin"));
        context.Features.Get<IHttpFormFeature>().ShouldBeNull();

        IHttpFormCollection form = await context.ReadFormAsync();

        context.Features.Get<IHttpFormFeature>().ShouldNotBeNull();
        form["name"].Value.ShouldBe("cohesion");
        form["role"].Value.ShouldBe("admin");
    }

    [Fact]
    public async Task ReadFormAsync_FeaturePreInstalled_ShouldUseInstalledFeature()
    {
        IHttpContext context = new BareHttpContext();
        HttpFormCollection prebuilt = new();
        prebuilt.Add("name", "cohesion");
        context.Features.Set<IHttpFormFeature>(new HttpFormFeature(prebuilt));

        IHttpFormCollection form = await context.ReadFormAsync();

        form.ShouldBeSameAs(prebuilt);
    }

    [Fact]
    public async Task ReadFormAsync_CalledTwice_ShouldReturnCachedIdentity()
    {
        IHttpContext context = new BareHttpContext(
            "application/x-www-form-urlencoded",
            BodyOf("name=cohesion"));

        IHttpFormCollection first = await context.ReadFormAsync();
        IHttpFormCollection second = await context.ReadFormAsync();

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task ReadFormAsync_OnNullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        await Should.ThrowAsync<ArgumentNullException>(() => context.ReadFormAsync());
    }

    [Fact]
    public async Task Form_SetOnRequest_ShouldPreAttachCollectionForGetterAndReadFormAsync()
    {
        // The body is never read: pre-attaching short-circuits the parse.
        IHttpContext context = new BareHttpContext(
            "application/x-www-form-urlencoded",
            new ThrowingStream());
        HttpFormCollection prebuilt = new();
        prebuilt.Add("name", "cohesion");

        context.Request.Form = prebuilt;

        context.Request.Form.ShouldBeSameAs(prebuilt);
        IHttpFormCollection viaRead = await context.ReadFormAsync();
        viaRead.ShouldBeSameAs(prebuilt);
    }

    [Fact]
    public void Form_SetOnRequestWithExistingFeature_ShouldUpdateThatFeature()
    {
        IHttpContext context = new BareHttpContext();
        TestFormFeature feature = new();
        context.Features.Set<IHttpFormFeature>(feature);
        HttpFormCollection prebuilt = new();
        prebuilt.Add("k", "v");

        context.Request.Form = prebuilt;

        feature.Form.ShouldBeSameAs(prebuilt);
        context.Request.Form.ShouldBeSameAs(prebuilt);
    }

    [Fact]
    public void Form_SetToNull_ShouldThrow()
    {
        IHttpContext context = new BareHttpContext();

        Should.Throw<ArgumentNullException>(() => context.Request.Form = null!);
    }

    [Fact]
    public void Form_SetOnNullRequest_ShouldThrow()
    {
        IHttpRequest request = null!;

        Should.Throw<ArgumentNullException>(() => request.Form = new HttpFormCollection());
    }

    private static MemoryStream BodyOf(string content) => new(Encoding.UTF8.GetBytes(content));

    private sealed class TestFormFeature : IHttpFormFeature
    {
        public string Name => nameof(TestFormFeature);

        public IHttpFormCollection? Form { get; set; }

        public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Form ??= new HttpFormCollection();
            return Task.FromResult(Form);
        }
    }

    /// <summary>Stream that fails if read, proving a pre-attached form never touches the body.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Body must not be read when a form is pre-attached.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BareHttpContext : IHttpContext
    {
        public BareHttpContext(string? contentType = null, Stream? body = null)
        {
            Request = new BareHttpRequest(this, contentType, body ?? Stream.Null);
            Response = new BareHttpResponse(this);
        }

        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestCancelled => CancellationToken.None;
        public void Cancel()
        {
            // Bare double: form parsing never cancels the exchange.
        }
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BareHttpRequest : IHttpRequest
    {
        public BareHttpRequest(IHttpContext context, string? contentType, Stream body)
        {
            HttpContext = context;
            Body = body;
            if (contentType is not null)
            {
                Headers[HttpHeaderKey.ContentType] = contentType;
            }
        }

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method => HttpMethod.Post;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; }
    }

    private sealed class BareHttpResponse : IHttpResponse
    {
        public BareHttpResponse(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = Stream.Null;
    }
}
