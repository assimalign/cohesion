using System;
using System.Globalization;

using Shouldly;

using Xunit;

namespace System.IO.Tests;

/// <summary>
/// Pins <see cref="FileSystemPath.Merge(FileSystemPath, FileSystemPath)"/> as a navigation helper
/// (#1180): its prefix match is segment-aligned, parent segments stop at the path root, and a merged
/// path keeps its root. Merge is deliberately not a containment primitive — it still navigates above
/// the left path — so file-system providers confine paths to their roots themselves.
/// </summary>
public class FileSystemPathMergeTests
{
    [Theory(DisplayName = "Cohesion Test [Core] - FileSystemPath.Merge: a rooted path that only shares text with the left path should not merge")]
    [InlineData("/srv/public", "/srv/public2/secret.txt")]
    [InlineData("/srv/public", "/srv/publicity")]
    [InlineData("C:/srv/public", "C:/srv/public2/secret.txt")]
    [InlineData("//server/share", "//server/share2/secret.txt")]
    public void Merge_RootedSiblingPrefix_ShouldThrow(string left, string right)
    {
        // Arrange
        FileSystemPath leftPath = FileSystemPath.Parse(left);
        FileSystemPath rightPath = FileSystemPath.Parse(right);

        // Act
        Func<FileSystemPath> merge = () => FileSystemPath.Merge(leftPath, rightPath);

        // Assert
        Should.Throw<ArgumentException>(() => merge());
    }

    [Theory(DisplayName = "Cohesion Test [Core] - FileSystemPath.Merge: a rooted path under the left path on a segment boundary should be returned")]
    [InlineData("/srv/public", "/srv/public/index.html", "/srv/public/index.html")]
    [InlineData("/srv/public", "/srv/public", "/srv/public")]
    [InlineData("/", "/etc/hosts", "/etc/hosts")]
    [InlineData("C:/", "C:/data/file.txt", "C:/data/file.txt")]
    [InlineData("C:/srv/public", "C:/srv/public/css/site.css", "C:/srv/public/css/site.css")]
    [InlineData("//server/share", "//server/share/file.txt", "//server/share/file.txt")]
    public void Merge_RootedPathUnderLeft_ShouldReturnRightPath(string left, string right, string expected)
    {
        // Arrange
        FileSystemPath leftPath = FileSystemPath.Parse(left);
        FileSystemPath rightPath = FileSystemPath.Parse(right);

        // Act
        FileSystemPath merged = FileSystemPath.Merge(leftPath, rightPath);

        // Assert
        merged.ToString().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Core] - FileSystemPath.Merge: a relative path that only shares text with the left path should be joined")]
    [InlineData("users", "users/johndoe", "users/johndoe")]
    [InlineData("users", "usersx/johndoe", "users/usersx/johndoe")]
    public void Merge_RelativePrefix_ShouldMatchOnSegmentBoundary(string left, string right, string expected)
    {
        // Arrange
        FileSystemPath leftPath = FileSystemPath.Parse(left);
        FileSystemPath rightPath = FileSystemPath.Parse(right);

        // Act
        FileSystemPath merged = FileSystemPath.Merge(leftPath, rightPath);

        // Assert
        merged.ToString().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Core] - FileSystemPath.Merge: the prefix match should honor ignoreCase")]
    public void Merge_DifferentlyCasedPrefix_ShouldMatchOnlyWhenIgnoringCase()
    {
        // Arrange
        FileSystemPath left = FileSystemPath.Parse("/srv/public");
        FileSystemPath right = FileSystemPath.Parse("/SRV/PUBLIC/index.html");

        // Act
        FileSystemPath merged = left.Merge(right, CultureInfo.InvariantCulture, ignoreCase: true);

        // Assert
        merged.ToString().ShouldBe("/SRV/PUBLIC/index.html");
        Should.Throw<ArgumentException>(() => left.Merge(right, CultureInfo.InvariantCulture, ignoreCase: false));
    }

    [Theory(DisplayName = "Cohesion Test [Core] - FileSystemPath.Merge: parent segments should navigate up and keep the path root")]
    [InlineData("/srv/public/css", "../img/logo.png", "/srv/public/img/logo.png")]
    [InlineData("/srv/public", "../secret.txt", "/srv/secret.txt")]
    [InlineData("/srv/public", "../../etc/hosts", "/etc/hosts")]
    [InlineData("/srv/public", "..", "/srv")]
    [InlineData("C:/srv/public", "../../file.txt", "C:/file.txt")]
    [InlineData("//server/share/dir", "../file.txt", "//server/share/file.txt")]
    [InlineData("users/path1/path2", "../../johndoe", "users/johndoe")]
    [InlineData("users", "../file.txt", "file.txt")]
    public void Merge_ParentSegments_ShouldNavigateWithinThePathRoot(string left, string right, string expected)
    {
        // Arrange
        FileSystemPath leftPath = FileSystemPath.Parse(left);
        FileSystemPath rightPath = FileSystemPath.Parse(right);

        // Act
        FileSystemPath merged = FileSystemPath.Merge(leftPath, rightPath);

        // Assert
        merged.ToString().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Core] - FileSystemPath.Merge: parent segments that climb above the path root should throw")]
    [InlineData("/srv/public", "../../../file.txt")]
    [InlineData("/", "../file.txt")]
    [InlineData("C:/srv/public", "../../../file.txt")]
    [InlineData("//server/share/dir", "../../file.txt")]
    [InlineData("users", "../../file.txt")]
    public void Merge_ParentSegmentsAboveRoot_ShouldThrow(string left, string right)
    {
        // Arrange
        FileSystemPath leftPath = FileSystemPath.Parse(left);
        FileSystemPath rightPath = FileSystemPath.Parse(right);

        // Act
        Func<FileSystemPath> merge = () => FileSystemPath.Merge(leftPath, rightPath);

        // Assert
        Should.Throw<ArgumentException>(() => merge());
    }

    [Theory(DisplayName = "Cohesion Test [Core] - FileSystemPath.Merge: a name that only begins with two dots should join as an ordinary segment")]
    [InlineData("/srv/public", "..config", "/srv/public/..config")]
    [InlineData("/", "..config/app.json", "/..config/app.json")]
    public void Merge_NameStartingWithTwoDots_ShouldJoinAsSegment(string left, string right, string expected)
    {
        // Arrange
        FileSystemPath leftPath = FileSystemPath.Parse(left);
        FileSystemPath rightPath = FileSystemPath.Parse(right);

        // Act
        FileSystemPath merged = FileSystemPath.Merge(leftPath, rightPath);

        // Assert
        merged.ToString().ShouldBe(expected);
    }
}
