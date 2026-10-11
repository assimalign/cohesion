using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace System.IO.Tests;

public class FileSystemPathTests
{

    [Fact]
    public void GetDirectoriesExtensionTest()
    {
        var path = FileSystemPath.Parse("C:/users/dotnetcadet/documents/projects");
        var dirs = path.GetDirectoryNames();
        Assert.Equal(5, dirs.Length);
        Assert.Equal("C:/", dirs[0]);
        Assert.Equal("users/", dirs[1]);
        Assert.Equal("dotnetcadet/", dirs[2]);
        Assert.Equal("documents/", dirs[3]);
        Assert.Equal("projects/", dirs[4]);
    }

    [Theory]
    [InlineData("//", "///\\")]
    [InlineData("/", "\\")]
    [InlineData("C:/users/dotnetcadet", "C:\\users\\dotnetcadet/")]
    [InlineData("C:/users/dotnetcadet", "C:\\users\\\\//dotnetcadet////")]
    [InlineData("/users/dotnetcadet", "/users\\\\//dotnetcadet////")]
    [InlineData("users/dotnetcadet", "./users\\\\//dotnetcadet////")]
    [InlineData("//users/dotnetcadet", "///users\\\\//dotnetcadet////")]
    [InlineData("a", "a/\\")]
    public void ParseFormatTests(string expected, string path)
    {
        Assert.Equal(expected, FileSystemPath.Parse(path));
    }

    [Theory]
    [InlineData("C:/users/../", typeof(ArgumentException))]
    [InlineData("/../users/../", typeof(ArgumentException))]
    [InlineData("C:/users/?test.cs", typeof(ArgumentException))]
    public void ParseExceptionTest(string value, Type exceptionType)
    {
        Assert.Throws(exceptionType, () =>
        {
            FileSystemPath.Parse(value);
        });
    }

    [Theory]
    [InlineData("..", true)]
    [InlineData("users/..", false)]
    [InlineData("../users", true)]
    [InlineData("../../..", true)]
    [InlineData("/../../..", false)]
    [InlineData("users/../documents", false)]
    [InlineData("..test/uses", true)] // Weird but file names with beginning '.'
    [InlineData("test/...uses", true)]
    public void ParentGlobingTest(string value, bool isValid)
    {
        if (isValid)
        {
            var path = FileSystemPath.Parse(value);
        }
        else
        {
            Assert.Throws<ArgumentException>(() =>
            {
                FileSystemPath.Parse(value);
            });
        }
    }

    [Theory]
    [InlineData("C:/users/", "C:/users/johndoe/", "C:/users/johndoe")]
    [InlineData("C:/users/path1/path2", "../../johndoe/", "C:/users/johndoe")]
    [InlineData("users/path1/path2", "../../johndoe/", "users/johndoe")]
    [InlineData("users/", "users/johndoe/", "users/johndoe")]
    [InlineData("/users/", "users/johndoe/", "/users/users/johndoe")]
    public void MergeTest(string value1, string value2, string expected)
    {
        var path1 = FileSystemPath.Parse(value1);
        var path2 = FileSystemPath.Parse(value2);

        var path = FileSystemPath.Merge(path1, path2);

        Assert.Equal(expected, path);
    }


    [Theory]
    [InlineData("C:/directory", true)]
    [InlineData("/directory", false)]
    public void DriveTest(string value, bool hasDrive)
    {
        Assert.Equal(hasDrive, FileSystemPath.Parse(value).HasDrive(out var c));
    }


    [Theory]
    [InlineData("C:/directory", "C:/")]
    [InlineData("/directory", "/")]
    [InlineData("//server/directory/some/path", "//server/directory")]
    public void RootTest(string value, string root)
    {
        Assert.True(FileSystemPath.Parse(value).HasRoot(out var r));
        Assert.Equal(root, r);
    }

    [Theory]
    [InlineData("//server/share/directory", 2, "//server/share")]
    [InlineData("C:/server/share/directory", 4, "C:")]
    public void SegmentTest(string value, int count, string firstSegment)
    {
        var path = FileSystemPath.Parse(value);
        var segments = path.GetSegments();

        Assert.Equal(count, segments.Length);
        Assert.Equal(firstSegment, segments[0]);
    }

    [Theory(DisplayName = "Cohesion Test [FileSystemPath] - Parse: Should strip a device prefix from a drive path")]
    [InlineData(@"\\?\C:\users\dotnetcadet", "C:/users/dotnetcadet")]
    [InlineData(@"\\.\C:\users\dotnetcadet", "C:/users/dotnetcadet")]
    [InlineData("//?/C:/users/dotnetcadet", "C:/users/dotnetcadet")]
    [InlineData(@"\\?\C:\users\dotnetcadet\", "C:/users/dotnetcadet")]
    [InlineData(@"\\?\C:", "C:")]
    public void Parse_DevicePrefixedDrivePath_ShouldStripPrefix(string value, string expected)
    {
        // Act
        var path = FileSystemPath.Parse(value);

        // Assert
        path.ToString().ShouldBe(expected);
        path.HasDrive(out var drive).ShouldBeTrue();
        drive.ShouldBe('C');
    }

    [Theory(DisplayName = "Cohesion Test [FileSystemPath] - Parse: Should re-root a device UNC path as a share")]
    [InlineData(@"\\?\UNC\server\share\directory", "//server/share/directory")]
    [InlineData(@"\\?\unc\server\share\directory", "//server/share/directory")]
    [InlineData(@"\\.\UNC\server\share\directory", "//server/share/directory")]
    [InlineData(@"\\?\UNC\server\share", "//server/share")]
    public void Parse_DevicePrefixedUncPath_ShouldReRootAsShare(string value, string expected)
    {
        // Act
        var path = FileSystemPath.Parse(value);

        // Assert
        path.ToString().ShouldBe(expected);
        path.HasShare(out var share).ShouldBeTrue();
        share.ShouldBe("//server/share");
    }

    [Fact(DisplayName = "Cohesion Test [FileSystemPath] - Parse: Should accept an extended-length path past MAX_PATH")]
    public void Parse_ExtendedPathPastMaxPath_ShouldEqualOrdinaryPath()
    {
        // Arrange
        var ordinary = @"C:\" + string.Join('\\', Enumerable.Repeat("directory-segment-0123456789", 12)) + @"\app.exe";

        // Act
        var extended = FileSystemPath.Parse(@"\\?\" + ordinary);
        var extendedUnc = FileSystemPath.Parse(@"\\?\UNC\server\share\" + ordinary.Substring(3));

        // Assert
        ordinary.Length.ShouldBeGreaterThan(260);
        extended.ShouldBe(FileSystemPath.Parse(ordinary));
        extended.Length.ShouldBe(ordinary.Length);
        extendedUnc.ToString().ShouldStartWith("//server/share/directory-segment-0123456789/");
        extendedUnc.Length.ShouldBeGreaterThan(260);
    }

    [Theory(DisplayName = "Cohesion Test [FileSystemPath] - Parse: Should reject '?' and unsupported device paths")]
    [InlineData("C:/users/?test.cs")]
    [InlineData(@"\\?\C:\users\?test.cs")]
    [InlineData(@"C:\?\users")]
    [InlineData(@"users\\?\C:\directory")]
    [InlineData(@"\??\C:\users")]
    [InlineData(@"\\?\Volume{b75e2c83-0000-0000-0000-602f00000000}\users")]
    [InlineData(@"\\.\pipe\cohesion")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\users")]
    [InlineData(@"\\?\C:users")]
    [InlineData(@"\\?\UNC\\share")]
    [InlineData(@"\\?\UNC")]
    public void Parse_QuestionMarkOrUnsupportedDevicePath_ShouldThrow(string value)
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => FileSystemPath.Parse(value));
    }

    [Theory(DisplayName = "Cohesion Test [FileSystemPath] - Parse: Should report the caller's index after a device prefix")]
    [InlineData(@"\\?\C:\a?b", 8)]
    [InlineData(@"\\?\UNC\s\h?", 11)]
    public void Parse_IllegalCharacterAfterDevicePrefix_ShouldReportCallerIndex(string value, int index)
    {
        // Act
        var exception = Should.Throw<ArgumentException>(() => FileSystemPath.Parse(value));

        // Assert
        exception.Message.ShouldContain($"'?' at index {index}.");
    }

    [Theory]
    [InlineData("C:/users/dotnetcadet", "C:/UseRs\\DoTNetCadET", true)]
    [InlineData("C:/users/dotnetcadet", "C:/UseRs\\DoTNetCadE", false)]
    public void EqualityTest(string value1, string value2, bool isEqual)
    {
        FileSystemPath path1 = value1;
        FileSystemPath path2 = value2;

        //if (isEqual)
        //{
        //    Assert.True(path1.Equals(path2, StringComparison.OrdinalIgnoreCase));
        //    Assert.False(!path1.Equals(path2, StringComparison.OrdinalIgnoreCase));
        //}
        //else
        //{
        //    Assert.False(path1.Equals(path2, StringComparison.OrdinalIgnoreCase));
        //    Assert.True(!path1.Equals(path2, StringComparison.OrdinalIgnoreCase));
        //}
    }
}
