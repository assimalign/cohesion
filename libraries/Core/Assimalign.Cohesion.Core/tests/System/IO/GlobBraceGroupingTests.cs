using Shouldly;

using Xunit;

namespace System.IO.Tests;

/// <summary>
/// Pins the brace-group matching that <c>docs/Assembly/System.IO/Glob/OVERVIEW.md</c> documents.
/// </summary>
public class GlobBraceGroupingTests
{
    [Fact(DisplayName = "Cohesion Test [Core] - Glob.IsMatch: Should match either alternative of **/*.{md,cs}")]
    public void IsMatch_WhenBraceGroupFollowsDirectoryWildcard_ShouldMatchEitherAlternative()
    {
        // Arrange
        Glob glob = Glob.Parse("**/*.{md,cs}");

        // Act
        bool markdown = glob.IsMatch("a/b.md");
        bool csharp = glob.IsMatch("a/b.cs");
        bool text = glob.IsMatch("a/b.txt");

        // Assert
        markdown.ShouldBeTrue();
        csharp.ShouldBeTrue();
        text.ShouldBeFalse();
    }
}
