using System.Collections.Frozen;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// Tests for <see cref="HttpContentTypes"/>: the file-name lookup (final extension, case-insensitive,
/// and no type for a name without an extension), the extension lookup (leading dot required), the
/// fallback, and custom overlay tables.
/// </summary>
public class HttpContentTypesTests
{
    // ---------------------------------------------------------------- file-name lookup

    [Theory(DisplayName = "Cohesion Test [Http] - HttpContentTypes: a file name resolves by its final extension")]
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
    [InlineData(".config.json", "application/json")]
    [InlineData("assets/site.css", "text/css")]
    [InlineData("assets\\site.css", "text/css")]
    [InlineData("assets.v2/site.css", "text/css")]
    public void TryGetFromFileName_MappedExtension_ShouldResolve(string fileName, string expected)
    {
        // Arrange — nothing beyond the inputs.

        // Act
        bool resolved = HttpContentTypes.TryGetFromFileName(fileName, out string contentType);

        // Assert
        resolved.ShouldBeTrue();
        contentType.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpContentTypes: a file name without an extension maps to nothing")]
    [InlineData("html")]
    [InlineData("json")]
    [InlineData("css")]
    [InlineData("js")]
    [InlineData("README")]
    [InlineData(".json")]
    [InlineData(".html")]
    [InlineData("..json")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("trailing.")]
    [InlineData("index.html.")]
    [InlineData("")]
    [InlineData("assets/html")]
    [InlineData("assets\\json")]
    [InlineData("assets/.json")]
    [InlineData("site.css/html")]
    public void TryGetFromFileName_NoExtension_ShouldMapToNothing(string fileName)
    {
        // Arrange — nothing beyond the inputs.

        // Act
        bool resolved = HttpContentTypes.TryGetFromFileName(fileName, out string contentType);

        // Assert
        resolved.ShouldBeFalse();
        contentType.ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpContentTypes: a file name with an unmapped extension maps to nothing")]
    public void TryGetFromFileName_UnmappedExtension_ShouldMapToNothing()
    {
        // Arrange — nothing beyond the input.

        // Act
        bool resolved = HttpContentTypes.TryGetFromFileName("file.unknownext", out string contentType);

        // Assert
        resolved.ShouldBeFalse();
        contentType.ShouldBe(string.Empty);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpContentTypes: GetFromFileName falls back for a name without a mapped extension")]
    [InlineData("file.unknownext")]
    [InlineData("html")]
    [InlineData(".json")]
    public void GetFromFileName_NoMappedExtension_ShouldReturnFallback(string fileName)
    {
        // Arrange — nothing beyond the input.

        // Act
        string contentType = HttpContentTypes.GetFromFileName(fileName);

        // Assert
        contentType.ShouldBe(HttpContentTypes.Fallback);
        HttpContentTypes.Fallback.ShouldBe("application/octet-stream");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpContentTypes: GetFromFileName returns the mapping for a known extension")]
    public void GetFromFileName_MappedExtension_ShouldReturnMapping()
    {
        // Arrange — nothing beyond the input.

        // Act
        string contentType = HttpContentTypes.GetFromFileName("index.html");

        // Assert
        contentType.ShouldBe("text/html");
    }

    // ---------------------------------------------------------------- extension lookup

    [Theory(DisplayName = "Cohesion Test [Http] - HttpContentTypes: an extension with its leading dot resolves")]
    [InlineData(".css", "text/css")]
    [InlineData(".CSS", "text/css")]
    [InlineData(".json", "application/json")]
    [InlineData(".gz", "application/gzip")]
    public void TryGetFromExtension_DottedExtension_ShouldResolve(string extension, string expected)
    {
        // Arrange — nothing beyond the inputs.

        // Act
        bool resolved = HttpContentTypes.TryGetFromExtension(extension, out string contentType);

        // Assert
        resolved.ShouldBeTrue();
        contentType.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpContentTypes: a value that is not a dotted extension maps to nothing")]
    [InlineData("css")]
    [InlineData("json")]
    [InlineData("site.css")]
    [InlineData(".tar.gz")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("./css")]
    [InlineData(".css/")]
    [InlineData("")]
    [InlineData(".unknownext")]
    public void TryGetFromExtension_NotADottedExtension_ShouldMapToNothing(string extension)
    {
        // Arrange — nothing beyond the inputs.

        // Act
        bool resolved = HttpContentTypes.TryGetFromExtension(extension, out string contentType);

        // Assert
        resolved.ShouldBeFalse();
        contentType.ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpContentTypes: GetFromExtension requires the leading dot")]
    public void GetFromExtension_BareToken_ShouldReturnFallback()
    {
        // Arrange — nothing beyond the inputs.

        // Act
        string dotted = HttpContentTypes.GetFromExtension(".css");
        string bare = HttpContentTypes.GetFromExtension("css");

        // Assert
        dotted.ShouldBe("text/css");
        bare.ShouldBe(HttpContentTypes.Fallback);
    }

    // ---------------------------------------------------------------- overlay tables

    [Fact(DisplayName = "Cohesion Test [Http] - HttpContentTypes: CreateMap overlays and overrides the defaults")]
    public void CreateMap_Overrides_ShouldOverlayAndOverrideDefaults()
    {
        // Arrange
        FrozenDictionary<string, string> map = HttpContentTypes.CreateMap(new[]
        {
            new KeyValuePair<string, string>(".foo", "application/x-foo"),
            new KeyValuePair<string, string>("bar", "application/x-bar"),  // no leading dot
            new KeyValuePair<string, string>(".css", "text/x-custom-css"), // override a default
        });

        // Act
        bool foundFoo = HttpContentTypes.TryGetFromFileName(map, "file.foo", out string foo);
        bool foundBar = HttpContentTypes.TryGetFromFileName(map, "file.bar", out string bar);
        bool foundBarExtension = HttpContentTypes.TryGetFromExtension(map, ".bar", out string barExtension);
        bool foundCss = HttpContentTypes.TryGetFromFileName(map, "site.css", out string css);
        bool foundHtml = HttpContentTypes.TryGetFromFileName(map, "index.html", out string html);

        // Assert — a new mapping lights up, a key without its dot still maps the extension, an
        // existing default is replaceable, and a default not overridden is still present.
        foundFoo.ShouldBeTrue();
        foo.ShouldBe("application/x-foo");
        foundBar.ShouldBeTrue();
        bar.ShouldBe("application/x-bar");
        foundBarExtension.ShouldBeTrue();
        barExtension.ShouldBe("application/x-bar");
        foundCss.ShouldBeTrue();
        css.ShouldBe("text/x-custom-css");
        foundHtml.ShouldBeTrue();
        html.ShouldBe("text/html");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpContentTypes: a key without its dot does not map a file of that bare name")]
    public void CreateMap_KeyWithoutDot_ShouldNotMapDotlessFileName()
    {
        // Arrange
        FrozenDictionary<string, string> map = HttpContentTypes.CreateMap(new[]
        {
            new KeyValuePair<string, string>("gltf", "model/gltf+json"),
        });

        // Act
        bool fileName = HttpContentTypes.TryGetFromFileName(map, "gltf", out string fromFileName);
        bool bareExtension = HttpContentTypes.TryGetFromExtension(map, "gltf", out string fromBareExtension);

        // Assert
        fileName.ShouldBeFalse();
        fromFileName.ShouldBe(string.Empty);
        bareExtension.ShouldBeFalse();
        fromBareExtension.ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpContentTypes: CreateMap does not mutate the default table")]
    public void CreateMap_Overrides_ShouldNotMutateDefaultTable()
    {
        // Arrange
        HttpContentTypes.CreateMap(new[]
        {
            new KeyValuePair<string, string>(".css", "text/x-custom-css"),
        });

        // Act
        bool resolved = HttpContentTypes.TryGetFromFileName("site.css", out string css);

        // Assert
        resolved.ShouldBeTrue();
        css.ShouldBe("text/css");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpContentTypes: CreateMap with no overrides returns the defaults")]
    public void CreateMap_NullOverrides_ShouldReturnDefaults()
    {
        // Arrange
        FrozenDictionary<string, string> map = HttpContentTypes.CreateMap(null);

        // Act
        bool resolved = HttpContentTypes.TryGetFromFileName(map, "app.js", out string js);

        // Assert
        resolved.ShouldBeTrue();
        js.ShouldBe("text/javascript");
    }
}
