using Assimalign.Cohesion.Web.StaticFiles.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// Focused coverage of the prefix-matching and traversal-gate helpers.
/// </summary>
public class StaticFilePathTests
{
    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - TryGetRelativePath: should match segment-aligned prefixes")]
    [InlineData("/static/app.css", "/static", true, "/app.css")]
    [InlineData("/static", "/static", true, "")]
    [InlineData("/static/", "/static", true, "/")]
    [InlineData("/static/a/b.txt", "/static", true, "/a/b.txt")]
    [InlineData("/staticfiles/app.css", "/static", false, "")]
    [InlineData("/other/app.css", "/static", false, "")]
    [InlineData("/Static/app.css", "/static", false, "")]
    [InlineData("/anything", "", true, "/anything")]
    public void TryGetRelativePath_ShouldMatchSegmentAligned(string path, string prefix, bool expected, string expectedRemainder)
    {
        // Act
        bool matched = StaticFilePath.TryGetRelativePath(path, prefix, out string remainder);

        // Assert
        matched.ShouldBe(expected);
        if (expected)
        {
            remainder.ShouldBe(expectedRemainder);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - HasUnsafeSegments: should flag traversal and reserved shapes")]
    [InlineData("/../x", true)]
    [InlineData("/a/../x", true)]
    [InlineData("/..", true)]
    [InlineData("/.", true)]
    [InlineData("/./x", true)]
    [InlineData("/a\\..\\x", true)]
    [InlineData("/..\\x", true)]
    [InlineData("/a:b", true)]
    [InlineData("/file.txt::$DATA", true)]
    [InlineData("/a\0b", true)]
    [InlineData("/a/b.txt", false)]
    [InlineData("/.well-known/x", false)]
    [InlineData("/a.b/c.d.txt", false)]
    [InlineData("/...x", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    public void HasUnsafeSegments_ShouldFlagTraversalShapes(string remainder, bool expected)
    {
        // Act / Assert
        StaticFilePath.HasUnsafeSegments(remainder).ShouldBe(expected);
    }
}
