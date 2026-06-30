using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Forms.Tests;

public class HttpFormFeatureTests
{
    [Fact]
    public async Task ReadFormAsync_NoRequest_ShouldReturnEmptyCollection()
    {
        IHttpRequest request = new BareHttpRequest();

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form.ShouldNotBeNull();
        form.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ReadFormAsync_UrlEncodedBody_ShouldParseAllPairs()
    {
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded",
            Body = BodyOf("name=alice&role=admin&note=hello%20world"),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form.Count.ShouldBe(3);
        form["name"].Value.ShouldBe("alice");
        form["role"].Value.ShouldBe("admin");
        form["note"].Value.ShouldBe("hello world");
    }

    [Fact]
    public async Task ReadFormAsync_UrlEncodedBody_ShouldDecodePlusAsSpace()
    {
        // HTML 4.01 §17.13.4.1 — application/x-www-form-urlencoded uses '+'
        // as a sentinel for the space character. Uri.UnescapeDataString does
        // not handle this convention, so the parser must unswap explicitly.
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded",
            Body = BodyOf("greeting=hello+world"),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form["greeting"].Value.ShouldBe("hello world");
    }

    [Fact]
    public async Task ReadFormAsync_UrlEncodedBody_ShouldRespectCharsetParameter()
    {
        // The Content-Type may carry a charset parameter; the urlencoded
        // detection should still succeed because IsUrlEncoded checks the
        // base media type only.
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded; charset=utf-8",
            Body = BodyOf("name=cohesion"),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form["name"].Value.ShouldBe("cohesion");
    }

    [Fact]
    public async Task ReadFormAsync_MultipartBody_ShouldParseFieldsAndFiles()
    {
        const string boundary = "----WebKitFormBoundaryABCDEF";
        string body =
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"field1\"\r\n" +
            "\r\n" +
            "value1\r\n" +
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"avatar\"; filename=\"avatar.png\"\r\n" +
            "Content-Type: image/png\r\n" +
            "\r\n" +
            "\x89PNG\r\n\x1a\n" +
            $"\r\n--{boundary}--\r\n";

        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf(body),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form["field1"].Value.ShouldBe("value1");
        form.Files.Count.ShouldBe(1);
        form.Files.TryGetValue("avatar", out IHttpFormFile? file).ShouldBeTrue();
        file!.FileName.ShouldBe("avatar.png");
        file.ContentType.ShouldBe("image/png");

        using Stream stream = file.OpenReadStream();
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        copy.ToArray().ShouldBe(Encoding.UTF8.GetBytes("\x89PNG\r\n\x1a\n"));
    }

    [Fact]
    public async Task ReadFormAsync_MultipartBody_WithQuotedBoundary_ShouldParse()
    {
        // RFC 2046 §5.1.1 — the boundary parameter MAY be DQUOTE-wrapped.
        const string boundary = "edge-case";
        string body =
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"k\"\r\n" +
            "\r\n" +
            "v\r\n" +
            $"--{boundary}--\r\n";

        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary=\"{boundary}\"",
            Body = BodyOf(body),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form["k"].Value.ShouldBe("v");
    }

    [Fact]
    public async Task ReadFormAsync_MultipartBody_WithEmptyFilePart_ShouldExposeZeroLengthFile()
    {
        const string boundary = "B";
        string body =
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"upload\"; filename=\"empty.bin\"\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "\r\n" +
            $"\r\n--{boundary}--\r\n";

        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf(body),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form.Files.TryGetValue("upload", out IHttpFormFile? file).ShouldBeTrue();
        file!.Length.ShouldBe(0);
    }

    [Fact]
    public async Task ReadFormAsync_UnknownContentType_ShouldReturnEmptyCollection()
    {
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/json",
            Body = BodyOf("{\"name\":\"cohesion\"}"),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form.Count.ShouldBe(0);
        form.Files.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ReadFormAsync_SecondCall_ShouldReturnCachedInstance()
    {
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded",
            Body = BodyOf("name=cohesion"),
        };
        HttpFormFeature feature = new(request);

        IHttpFormCollection first = await feature.ReadFormAsync();
        IHttpFormCollection second = await feature.ReadFormAsync();

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task ReadFormAsync_CancelledToken_ShouldThrow()
    {
        IHttpRequest request = new BareHttpRequest();
        HttpFormFeature feature = new(request);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => feature.ReadFormAsync(cts.Token));
    }

    [Fact]
    public async Task ReadFormAsync_BodyExceedingLimit_ShouldThrow()
    {
        // Streamed body (non-seekable) that overruns the urlencoded value-
        // length cap that the underlying HttpFormReader enforces (defaulted
        // to 4 MB per value). Anything bigger throws InvalidDataException
        // before it can balloon memory.
        const int OverLimit = 8 * 1024 * 1024;
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded",
            Body = new OversizedStream(OverLimit),
        };
        HttpFormFeature feature = new(request);

        await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync());
    }

    [Fact]
    public async Task ReadFormAsync_ParsedFeatureInstalledOnContext_ShouldBeObservedViaFormProperty()
    {
        // After a caller installs the feature on the context and triggers a
        // parse, request.Form should expose the parsed collection (the Form
        // extension property reads through whatever feature is installed).
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded",
            Body = BodyOf("k=v"),
        };
        HttpFormFeature feature = new(request);
        request.HttpContext.Features.Set(feature);

        IHttpFormCollection parsed = await feature.ReadFormAsync();
        IHttpFormCollection viaProperty = request.Form;

        viaProperty.ShouldBeSameAs(parsed);
        viaProperty["k"].Value.ShouldBe("v");
    }

    private static MemoryStream BodyOf(string content) => new(Encoding.UTF8.GetBytes(content));

    /// <summary>Non-seekable stream that just yields zero bytes up to a configured length.</summary>
    private sealed class OversizedStream : Stream
    {
        private long _remaining;

        public OversizedStream(long length)
        {
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            int n = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, n);
            _remaining -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Bare-bones request/context pair so the feature can resolve through
    /// <see cref="IHttpRequest.HttpContext"/> to install the cookie /
    /// form feature without dragging in a transport.
    /// </summary>
    private sealed class BareHttpRequest : IHttpRequest
    {
        private readonly BareHttpContext _context;

        public BareHttpRequest()
        {
            _context = new BareHttpContext(this);
        }

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method => HttpMethod.Post;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext => _context;
        public Stream Body { get; init; } = Stream.Null;

        public string? ContentType
        {
            init
            {
                if (value is not null)
                {
                    Headers[HttpHeaderKey.ContentType] = value;
                }
            }
        }
    }

    private sealed class BareHttpContext : IHttpContext
    {
        public BareHttpContext(IHttpRequest request)
        {
            Request = request;
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
