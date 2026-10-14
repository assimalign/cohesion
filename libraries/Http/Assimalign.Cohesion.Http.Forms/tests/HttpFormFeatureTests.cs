using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Forms.Tests;

public class HttpFormFeatureTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Forms] - ReadFormAsync: both encodings preserve repeated fields and read first")]
    [InlineData("first", "second")]
    [InlineData("first", "", "last")]
    [InlineData("", "second", "last")]
    [InlineData("x,y", "a+b", "café", "x&y=z", "")]
    public async Task ReadFormAsync_RepeatedFieldsInBothEncodings_ShouldKeepIdenticalOrderedValues(params string[] expected)
    {
        // Arrange — encode the same interleaved fields using the BCL's two form writers.
        List<KeyValuePair<string, string>> fields = new();
        for (int index = 0; index < expected.Length; index++)
        {
            fields.Add(new KeyValuePair<string, string>("tag", expected[index]));
            if (index == 0)
            {
                fields.Add(new KeyValuePair<string, string>("other", "kept"));
            }
        }
        fields.Add(new KeyValuePair<string, string>("other", "again"));
        using FormUrlEncodedContent urlencoded = new(fields);
        using MultipartFormDataContent multipart = new("cohesion-repeated-fields");
        foreach (KeyValuePair<string, string> field in fields)
        {
            multipart.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
        }
        using Stream urlencodedBody = await urlencoded.ReadAsStreamAsync(CancellationToken.None);
        using Stream multipartBody = await multipart.ReadAsStreamAsync(CancellationToken.None);
        IHttpRequest urlencodedRequest = new BareHttpRequest
        {
            ContentType = urlencoded.Headers.ContentType!.ToString(),
            Body = urlencodedBody,
        };
        IHttpRequest multipartRequest = new BareHttpRequest
        {
            ContentType = multipart.Headers.ContentType!.ToString(),
            Body = multipartBody,
        };

        // Act
        IHttpFormCollection first = await new HttpFormFeature(urlencodedRequest).ReadFormAsync(CancellationToken.None);
        IHttpFormCollection second = await new HttpFormFeature(multipartRequest).ReadFormAsync(CancellationToken.None);

        // Assert — compare complete collections as well as the specified per-key values.
        first.Count.ShouldBe(2);
        second.Count.ShouldBe(first.Count);
        foreach (IHttpFormCollection form in new[] { first, second })
        {
            form["tag"].ToArray().ShouldBe(expected);
            form["tag"].Value.ShouldBe(expected[0]);
            form["other"].ToArray().ShouldBe(new[] { "kept", "again" });
            form.Files.Count.ShouldBe(0);
        }
        foreach (KeyValuePair<string, HttpQueryValue> entry in first)
        {
            second.TryGetValue(entry.Key, out HttpQueryValue values).ShouldBeTrue();
            values.ShouldBe(entry.Value);
        }
        second.Select(entry => entry.Key).ShouldBe(first.Select(entry => entry.Key));
    }

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
    public async Task ReadFormAsync_UrlEncodedBodyWithCharsetParameter_ShouldStillDetectMediaType()
    {
        // The Content-Type may carry a charset parameter; the urlencoded
        // detection should still succeed because IsUrlEncoded checks the
        // base media type only (it does not require a bare media type).
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded; charset=utf-8",
            Body = BodyOf("name=cohesion"),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form["name"].Value.ShouldBe("cohesion");
    }

    [Fact]
    public async Task ReadFormAsync_UrlEncodedBodyWithLatin1Charset_ShouldDecodeUsingThatCharset()
    {
        // A raw high byte (0xE9 = 'é' in ISO-8859-1) is decoded per the
        // Content-Type charset parameter, not as UTF-8 (where 0xE9 alone is
        // invalid and would become U+FFFD). This proves the charset is honored.
        byte[] latin1Body = { (byte)'n', (byte)'a', (byte)'m', (byte)'e', (byte)'=', (byte)'c', (byte)'a', (byte)'f', 0xE9 };
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded; charset=iso-8859-1",
            Body = new MemoryStream(latin1Body),
        };

        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        form["name"].Value.ShouldBe("café");
    }

    [Fact]
    public async Task ReadFormAsync_MultipartBoundaryExceedingLimit_ShouldThrow()
    {
        // A boundary longer than MultipartBoundaryLengthLimit is rejected before
        // the body is read, guarding against an unbounded look-ahead buffer.
        const string boundary = "boundary-that-is-too-long";
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf($"--{boundary}--\r\n"),
        };
        HttpFormFeature feature = new(request, new HttpFormOptions { MultipartBoundaryLengthLimit = 8 });

        await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync());
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

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Limits: a multipart section over the body limit names the limit as the cause")]
    public async Task ReadFormAsync_MultipartSectionOverBodyLimit_ShouldThrowWithLimitCause()
    {
        // Arrange
        const string boundary = "B";
        string body =
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"upload\"; filename=\"big.bin\"\r\n" +
            "\r\n" +
            "0123456789abcdef" +
            $"\r\n--{boundary}--\r\n";

        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf(body),
        };
        HttpFormFeature feature = new(request, new HttpFormOptions { MultipartBodyLengthLimit = 8 });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync());

        // Assert — still an InvalidDataException for existing callers, with the limit as its cause.
        HttpFormLimitExceededException limit = exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>();
        limit.Message.ShouldBe(exception.Message);
        limit.Code.ShouldBe(HttpErrorCode.ReadingError);
        limit.Limit.ShouldBe(HttpFormLimit.MultipartBodyLength);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Limits: too many urlencoded entries names the limit as the cause")]
    public async Task ReadFormAsync_ValueCountOverLimit_ShouldThrowWithLimitCause()
    {
        // Arrange
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = "application/x-www-form-urlencoded",
            Body = BodyOf("a=1&b=2&c=3"),
        };
        HttpFormFeature feature = new(request, new HttpFormOptions { ValueCountLimit = 2 });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync());

        // Assert
        exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>().Limit.ShouldBe(HttpFormLimit.ValueCount);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Limits: an overlong boundary names the limit as the cause")]
    public async Task ReadFormAsync_BoundaryOverLimit_ShouldThrowWithLimitCause()
    {
        // Arrange
        const string boundary = "boundary-that-is-too-long";
        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf($"--{boundary}--\r\n"),
        };
        HttpFormFeature feature = new(request, new HttpFormOptions { MultipartBoundaryLengthLimit = 8 });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync());

        // Assert
        exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>().Limit.ShouldBe(HttpFormLimit.MultipartBoundaryLength);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Limits: a malformed multipart header is not a limit")]
    public async Task ReadFormAsync_MalformedMultipartHeader_ShouldThrowWithoutLimitCause()
    {
        // Arrange — a header line with no colon.
        const string boundary = "B";
        string body =
            $"--{boundary}\r\n" +
            "this is not a header\r\n" +
            "\r\n" +
            "value" +
            $"\r\n--{boundary}--\r\n";

        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf(body),
        };

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => new HttpFormFeature(request).ReadFormAsync());

        // Assert
        exception.InnerException.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Limits: a header block that ends exactly at the headers-length limit still parses")]
    public async Task ReadFormAsync_HeaderBlockAtTheLimit_ShouldParseAndRejectOneMoreLine()
    {
        // Arrange — the limit is the length of the one header line, so its CRLF runs the budget two
        // bytes past the limit: the blank line that ends the block must still be read, and any further
        // header line is over the limit.
        const string boundary = "B";
        const string header = "Content-Disposition: form-data; name=\"k\"";

        string fits = $"--{boundary}\r\n{header}\r\n\r\nv\r\n--{boundary}--\r\n";
        string over = $"--{boundary}\r\n{header}\r\nContent-Type: text/plain\r\n\r\nv\r\n--{boundary}--\r\n";

        HttpFormOptions options = new() { MultipartHeadersLengthLimit = header.Length };
        HttpFormFeature fitting = new(new BareHttpRequest { ContentType = $"multipart/form-data; boundary={boundary}", Body = BodyOf(fits) }, options);
        HttpFormFeature overflowing = new(new BareHttpRequest { ContentType = $"multipart/form-data; boundary={boundary}", Body = BodyOf(over) }, options);

        // Act
        IHttpFormCollection form = await fitting.ReadFormAsync();
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => overflowing.ReadFormAsync());

        // Assert
        form["k"].Value.ShouldBe("v");
        exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>().Limit.ShouldBe(HttpFormLimit.MultipartHeadersLength);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Files: every part of a multiple-file field is kept, in order")]
    public async Task ReadFormAsync_FilesSharingAName_ShouldKeepEveryFile()
    {
        // Arrange — RFC 7578 §4.3 sends each file of a multiple-file field as its own part, same name.
        const string boundary = "B";
        string body =
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"photos\"; filename=\"one.png\"\r\n" +
            "\r\n" +
            "1" +
            $"\r\n--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"photos\"; filename=\"two.png\"\r\n" +
            "\r\n" +
            "22" +
            $"\r\n--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"avatar\"; filename=\"me.png\"\r\n" +
            "\r\n" +
            "333" +
            $"\r\n--{boundary}--\r\n";

        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf(body),
        };

        // Act
        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        // Assert — before, the second photo replaced the first.
        form.Files.Count.ShouldBe(3);
        List<string> names = new();
        foreach (IHttpFormFile file in form.Files)
        {
            names.Add(file.FileName);
        }

        names.ShouldBe(["one.png", "two.png", "me.png"]);
        form.Files.TryGetValue("PHOTOS", out IHttpFormFile? first).ShouldBeTrue();
        first!.FileName.ShouldBe("one.png");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Files: an empty optional file input is read as a value, not a file")]
    public async Task ReadFormAsync_FilePartWithEmptyFileName_ShouldReadAsValue()
    {
        // Arrange — the part a browser sends for an <input type="file"> left empty.
        const string boundary = "B";
        string body =
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"title\"\r\n" +
            "\r\n" +
            "holiday" +
            $"\r\n--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"avatar\"; filename=\"\"\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "\r\n" +
            $"\r\n--{boundary}--\r\n";

        IHttpRequest request = new BareHttpRequest
        {
            ContentType = $"multipart/form-data; boundary={boundary}",
            Body = BodyOf(body),
        };

        // Act
        IHttpFormCollection form = await new HttpFormFeature(request).ReadFormAsync();

        // Assert — before, the empty file name failed the whole parse with an ArgumentException.
        form.Files.Count.ShouldBe(0);
        form["title"].Value.ShouldBe("holiday");
        form.TryGetValue("avatar", out HttpQueryValue avatar).ShouldBeTrue();
        avatar.Value.ShouldBe(string.Empty);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Forms] - ReadFormAsync: equal key and value caps still identify the exceeded limit")]
    [InlineData("abcd=v", HttpFormLimit.KeyLength)]
    [InlineData("a=bcde", HttpFormLimit.ValueLength)]
    public async Task ReadFormAsync_UrlEncodedLengthOverLimit_ShouldNameParserLimit(string content, HttpFormLimit expected)
    {
        // Arrange
        using MemoryStream body = BodyOf(content);
        var request = new BareHttpRequest { ContentType = "application/x-www-form-urlencoded", Body = body };
        var feature = new HttpFormFeature(request, new HttpFormOptions { KeyLengthLimit = 3, ValueLengthLimit = 3 });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync(CancellationToken.None));

        // Assert — neither the numeric cap nor the shared message identifies the parser state.
        exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>().Limit.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - ReadFormAsync: too many multipart headers identify the header count limit")]
    public async Task ReadFormAsync_MultipartHeadersOverCountLimit_ShouldNameHeaderCount()
    {
        // Arrange
        using MemoryStream body = BodyOf("--B\r\nContent-Disposition: form-data; name=\"k\"\r\nX-Test: value\r\n\r\nv\r\n--B--\r\n");
        var request = new BareHttpRequest { ContentType = "multipart/form-data; boundary=B", Body = body };
        var feature = new HttpFormFeature(request, new HttpFormOptions { MultipartHeadersCountLimit = 1 });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync(CancellationToken.None));

        // Assert
        exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>().Limit.ShouldBe(HttpFormLimit.MultipartHeadersCount);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Forms] - ReadFormAsync: overlong preamble lines and totals identify the headers length limit")]
    [InlineData("123456789\r\n--B--\r\n")]
    [InlineData("123456\r\n123456\r\n123456\r\n--B--\r\n")]
    public async Task ReadFormAsync_MultipartPreambleOverLimit_ShouldNameHeadersLength(string content)
    {
        // Arrange
        using MemoryStream body = BodyOf(content);
        var request = new BareHttpRequest { ContentType = "multipart/form-data; boundary=B", Body = body };
        var feature = new HttpFormFeature(request, new HttpFormOptions { MultipartHeadersLengthLimit = 8 });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync(CancellationToken.None));

        // Assert
        exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>().Limit.ShouldBe(HttpFormLimit.MultipartHeadersLength);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Forms] - ReadFormAsync: every multipart section counts toward ValueCountLimit")]
    [InlineData("values")]
    [InlineData("files")]
    [InlineData("unnamed")]
    [InlineData("repeated")]
    public async Task ReadFormAsync_MultipartSectionCountOverLimit_ShouldNameSectionCount(string kind)
    {
        // Arrange
        using MemoryStream body = BodyOf(MultipartSections(kind, 3));
        var request = new BareHttpRequest { ContentType = "multipart/form-data; boundary=B", Body = body };
        var feature = new HttpFormFeature(request, new HttpFormOptions { ValueCountLimit = 2 });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(() => feature.ReadFormAsync(CancellationToken.None));

        // Assert
        exception.InnerException.ShouldBeOfType<HttpFormLimitExceededException>().Limit.ShouldBe(HttpFormLimit.MultipartSectionCount);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Forms] - ReadFormAsync: multipart sections exactly at ValueCountLimit still parse")]
    [InlineData("values", 2, 0)]
    [InlineData("files", 0, 2)]
    [InlineData("unnamed", 0, 0)]
    [InlineData("repeated", 1, 0)]
    public async Task ReadFormAsync_MultipartSectionCountAtLimit_ShouldParse(string kind, int valueCount, int fileCount)
    {
        // Arrange
        using MemoryStream body = BodyOf(MultipartSections(kind, 2));
        var request = new BareHttpRequest { ContentType = "multipart/form-data; boundary=B", Body = body };
        var feature = new HttpFormFeature(request, new HttpFormOptions { ValueCountLimit = 2 });

        // Act
        IHttpFormCollection form = await feature.ReadFormAsync(CancellationToken.None);

        // Assert
        form.Count.ShouldBe(valueCount);
        form.Files.Count.ShouldBe(fileCount);
        if (kind == "repeated")
        {
            form["same"].Count.ShouldBe(2);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Constructor: a legacy message-only exception has an unspecified limit")]
    public void Constructor_MessageOnly_ShouldKeepLegacyContract()
    {
        // Arrange
        const string message = "caller-provided diagnostic text";

        // Act
        var exception = new HttpFormLimitExceededException(message);

        // Assert
        exception.Limit.ShouldBe(HttpFormLimit.Unspecified);
        exception.Message.ShouldBe(message);
        exception.Code.ShouldBe(HttpErrorCode.ReadingError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forms] - Constructor: a typed limit is independent of message text")]
    public void Constructor_TypedLimit_ShouldPreserveLimitWithoutParsingMessage()
    {
        // Arrange
        const string message = "untrusted field text";

        // Act
        var exception = new HttpFormLimitExceededException(HttpFormLimit.KeyLength, message);

        // Assert
        exception.Limit.ShouldBe(HttpFormLimit.KeyLength);
        exception.Message.ShouldBe(message);
        exception.Code.ShouldBe(HttpErrorCode.ReadingError);
    }

    private static string MultipartSections(string kind, int count)
    {
        var body = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            string disposition = kind switch
            {
                "files" => $"form-data; name=\"file{i}\"; filename=\"file.txt\"",
                "unnamed" => "form-data",
                "repeated" => "form-data; name=\"same\"",
                _ => $"form-data; name=\"value{i}\"",
            };
            body.Append($"--B\r\nContent-Disposition: {disposition}\r\n\r\nv\r\n");
        }
        body.Append("--B--\r\n");
        return body.ToString();
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
