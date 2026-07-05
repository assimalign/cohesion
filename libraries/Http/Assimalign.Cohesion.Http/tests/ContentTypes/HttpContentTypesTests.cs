using System.Collections.Frozen;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// Tests for <see cref="HttpContentTypes"/>: default extension-to-content-type resolution,
/// case-insensitive multi-dot file-name handling, the fallback, and custom overlay tables.
/// </summary>
public class HttpContentTypesTests
{
    [Theory]
    [InlineData(".css", "text/css")]
    [InlineData("css", "text/css")]
    [InlineData("site.css", "text/css")]
    [InlineData("site.min.css", "text/css")]
    [InlineData("app.js", "text/javascript")]
    [InlineData("module.mjs", "text/javascript")]
    [InlineData("data.json", "application/json")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("app.wasm", "application/wasm")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("archive.tar.gz", "application/gzip")]
    [InlineData("STYLE.CSS", "text/css")]
    public void TryGetContentType_KnownExtensions_ShouldResolve(string input, string expected)
    {
        HttpContentTypes.TryGetContentType(input, out string contentType).ShouldBeTrue();

        contentType.ShouldBe(expected);
    }

    [Theory]
    [InlineData("file.unknownext")]
    [InlineData("README")]
    [InlineData("")]
    [InlineData("trailing.")]
    public void TryGetContentType_UnknownOrMissing_ShouldFail(string input)
    {
        HttpContentTypes.TryGetContentType(input, out string contentType).ShouldBeFalse();
        contentType.ShouldBe(string.Empty);
    }

    [Fact]
    public void GetContentType_Unknown_ShouldReturnFallback()
    {
        HttpContentTypes.GetContentType("file.unknownext").ShouldBe(HttpContentTypes.Fallback);
        HttpContentTypes.Fallback.ShouldBe("application/octet-stream");
    }

    [Fact]
    public void GetContentType_Known_ShouldReturnMapping()
    {
        HttpContentTypes.GetContentType("index.html").ShouldBe("text/html");
    }

    [Fact]
    public void CreateMap_ShouldOverlayAndOverrideDefaults()
    {
        FrozenDictionary<string, string> map = HttpContentTypes.CreateMap(new[]
        {
            new KeyValuePair<string, string>(".foo", "application/x-foo"),
            new KeyValuePair<string, string>("bar", "application/x-bar"),  // no leading dot
            new KeyValuePair<string, string>(".css", "text/x-custom-css"), // override a default
        });

        HttpContentTypes.TryGetContentType(map, "file.foo", out string foo).ShouldBeTrue();
        foo.ShouldBe("application/x-foo");

        HttpContentTypes.TryGetContentType(map, "file.bar", out string bar).ShouldBeTrue();
        bar.ShouldBe("application/x-bar");

        HttpContentTypes.TryGetContentType(map, "site.css", out string css).ShouldBeTrue();
        css.ShouldBe("text/x-custom-css");

        // A default not overridden is still present.
        HttpContentTypes.TryGetContentType(map, "index.html", out string html).ShouldBeTrue();
        html.ShouldBe("text/html");
    }

    [Fact]
    public void CreateMap_ShouldNotMutateDefaultTable()
    {
        HttpContentTypes.CreateMap(new[]
        {
            new KeyValuePair<string, string>(".css", "text/x-custom-css"),
        });

        HttpContentTypes.TryGetContentType("site.css", out string css).ShouldBeTrue();
        css.ShouldBe("text/css");
    }

    [Fact]
    public void CreateMap_NullOverrides_ShouldReturnDefaults()
    {
        FrozenDictionary<string, string> map = HttpContentTypes.CreateMap(null);

        HttpContentTypes.TryGetContentType(map, "app.js", out string js).ShouldBeTrue();
        js.ShouldBe("text/javascript");
    }
}
