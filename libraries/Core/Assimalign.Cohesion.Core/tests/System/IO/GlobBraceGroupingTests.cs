using System.Globalization;

using Shouldly;

using Xunit;

using Assimalign.Cohesion;

namespace System.IO.Tests;

/// <summary>
/// Pins the brace-group matching that <c>docs/Assembly/System.IO/Glob/OVERVIEW.md</c> documents,
/// and the end-of-input handling that brace groups exposed.
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

    [Theory(DisplayName = "Cohesion Test [Core] - Glob.IsMatch: Should not match a character after the brace group's match")]
    [InlineData("**/*.{js,ts}", "src/App.tsx")]
    [InlineData("**/*.{js,ts}", "src/App.jsx")]
    [InlineData("**/*.{md,cs}", "a/b.mdx")]
    [InlineData("file.{cs,txt}", "file.csx")]
    [InlineData("a{b,c}d", "abdx")]
    public void IsMatch_WhenOneCharacterFollowsBraceGroupMatch_ShouldNotMatch(string pattern, string value)
    {
        // Arrange
        Glob glob = Glob.Parse(pattern);

        // Act
        bool isMatch = glob.IsMatch(value);

        // Assert
        isMatch.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Core] - Glob.IsMatch: Should return false when a longer alternative leaves no input for the next token")]
    [InlineData("{ab,c}/", "ab", false)]
    [InlineData("{ab,c}?", "ab", false)]
    [InlineData("{ab,c}[xy]", "ab", false)]
    [InlineData("{ab,c}[a-z]", "ab", false)]
    [InlineData("{ab,c}[a-z]", "ab", true)]
    [InlineData("{ab,c}[0-9]", "ab", false)]
    public void IsMatch_WhenLongerAlternativeLeavesNoInput_ShouldReturnFalse(string pattern, string value, bool caseInSensitive)
    {
        // Arrange
        Glob glob = Glob.Parse(pattern);

        // Act
        bool isMatch = Should.NotThrow(() => glob.IsMatch(value, caseInSensitive));

        // Assert
        isMatch.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Core] - Glob.IsMatch: Should not let * re-match text that a longer alternative matched")]
    [InlineData("{ab,c}*b", "ab")]
    [InlineData("{abc,d}*c", "abc")]
    public void IsMatch_WhenWildcardFollowsLongerAlternative_ShouldNotRematchItsText(string pattern, string value)
    {
        // Arrange
        Glob glob = Glob.Parse(pattern);

        // Act
        bool isMatch = glob.IsMatch(value);

        // Assert
        isMatch.ShouldBeFalse();
    }

    // Unlike the tests above, this one passes on 10.0.0-preview.1 too: it guards a result the
    // end-of-input fix must leave alone. When the tokens after a * can match nothing, as /** can,
    // the * takes the rest of its segment. Until the * reported that it stopped at the end, the
    // corrected end-of-input check rejected "srcx" while "src", where the * takes nothing, matched.
    [Fact(DisplayName = "Cohesion Test [Core] - Glob.IsMatch: Should treat * before /** alike whether it matches none or all of the last segment")]
    public void IsMatch_WhenWildcardBeforeDirectoryWildcardMatchesLastSegment_ShouldMatchAsWhenItMatchesNothing()
    {
        // Arrange
        Glob glob = Glob.Parse("src*/**");

        // Act
        bool matchesNothing = glob.IsMatch("src");
        bool matchesSegment = glob.IsMatch("srcx");

        // Assert
        matchesSegment.ShouldBe(matchesNothing);
    }

    [Theory(DisplayName = "Cohesion Test [Core] - Glob.Parse: Should throw ArgumentException for a brace group with no alternatives")]
    [InlineData("a{}d", 1)]
    [InlineData("{}", 0)]
    [InlineData("{,}", 0)]
    [InlineData("src/{,}/*.cs", 4)]
    [InlineData("*.{}", 2)]
    [InlineData("a{", 1)]
    public void Parse_WhenBraceGroupHasNoAlternatives_ShouldThrowArgumentException(string pattern, int index)
    {
        // Arrange
        string message = string.Format(CultureInfo.CurrentCulture, ErrorMessages.GlobBraceGroupEmpty, index);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => Glob.Parse(pattern));

        // Assert
        exception.ParamName.ShouldBe("pattern");
        exception.Message.ShouldStartWith(message, Case.Sensitive);
    }
}
