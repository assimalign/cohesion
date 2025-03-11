using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Tests;

public class FileSystemPathTests
{

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
    [InlineData("../users/../", typeof(ArgumentException))]
    [InlineData("C:/users/?test.cs", typeof(ArgumentException))]
    public void ParseExceptionTest(string value, Type exceptionType)
    {
        Assert.Throws(exceptionType, () =>
        {
            FileSystemPath.Parse(value);
        });
    }

    [Theory]
    [InlineData("..", false)]
    [InlineData("users/..", false)]
    [InlineData("../users", false)]
    [InlineData("users/../documents", false)]
    [InlineData("..test/uses", true)] // Weird but file names can have
    [InlineData("test/...uses", true)]
    public void ParentGlobingTest(string value, bool isValid)
    {
        if (isValid)
        {
            FileSystemPath.Parse(value);
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
    [InlineData("users/", "users/johndoe/", "users/johndoe")]
    [InlineData("users/", "/users/johndoe/", "/users/johndoe/users")]
    public void CombineTest(string value1, string value2, string expected)
    {
        var path1 = FileSystemPath.Parse(value1);
        var path2 = FileSystemPath.Parse(value2);

        var path = FileSystemPath.Combine(path1, path2);

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
    [InlineData("//server/share/directory", 1, "directory")]
    [InlineData("C:/server/share/directory", 3, "server")]
    public void SegmentTest(string value, int count, string firstSegment)
    {
        var path = FileSystemPath.Parse(value);
        var segments = path.GetSegments();

        Assert.Equal(count, segments.Length);
        Assert.Equal(firstSegment, segments[0]);
    }

    [Theory]
    [InlineData("C:/users/dotnetcadet", "C:/UseRs\\DoTNetCadET", true)]
    [InlineData("C:/users/dotnetcadet", "C:/UseRs\\DoTNetCadE", true)]
    public void EqualityTest(string value1, string value2, bool isEqual)
    {
        FileSystemPath path1 = value1;
        FileSystemPath path2 = value2;

        if (isEqual)
        {
            Assert.True(path1 == path2);
            Assert.False(path1 != path2);
        }
        else
        {
            Assert.False(path1 == path2);
            Assert.True(path1 != path2);
        }
    }
}
