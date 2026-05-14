using System;
using System.IO;
using Assimalign.Cohesion.FileSystem.Internal;

namespace Assimalign.Cohesion.FileSystem.Isolated.Tests;

/// <summary>
/// Direct unit tests for the path translation helper. These verify that the public
/// <see cref="FileSystemPath"/> surface ('/' separated, rooted at "/") round-trips through the
/// store-side string form expected by <see cref="System.IO.IsolatedStorage.IsolatedStorageFile"/>.
/// </summary>
public class IsolatedPathHelperTests
{
    [Theory(DisplayName = "Cohesion Test [IsolatedPathHelper] - ToAbsolute: rooted and relative paths normalize")]
    [InlineData("/", "/")]
    [InlineData("/foo", "/foo")]
    [InlineData("foo", "/foo")]
    [InlineData("foo/bar", "/foo/bar")]
    [InlineData("/foo/bar", "/foo/bar")]
    public void ToAbsolute_Normalizes(string input, string expected)
    {
        FileSystemPath result = IsolatedPathHelper.ToAbsolute(input);
        Assert.Equal(expected, result.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedPathHelper] - ToAbsolute: empty path maps to root")]
    public void ToAbsolute_Empty_Root()
    {
        Assert.Equal("/", IsolatedPathHelper.ToAbsolute(FileSystemPath.Empty).ToString());
    }

    [Theory(DisplayName = "Cohesion Test [IsolatedPathHelper] - ToStorePath: strips leading and trailing slashes")]
    [InlineData("/", "")]
    [InlineData("/foo", "foo")]
    [InlineData("/foo/", "foo")]
    [InlineData("/foo/bar", "foo/bar")]
    [InlineData("/foo/bar/", "foo/bar")]
    public void ToStorePath_StripsSeparators(string input, string expected)
    {
        Assert.Equal(expected, IsolatedPathHelper.ToStorePath(input));
    }

    [Theory(DisplayName = "Cohesion Test [IsolatedPathHelper] - FromStorePath: round-trips back to absolute")]
    [InlineData("", "/")]
    [InlineData("foo", "/foo")]
    [InlineData("foo/bar", "/foo/bar")]
    [InlineData("foo\\bar", "/foo/bar")] // Windows-style separators are normalized
    public void FromStorePath_RoundTrips(string input, string expected)
    {
        Assert.Equal(expected, IsolatedPathHelper.FromStorePath(input).ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedPathHelper] - ChildSearchPattern: root produces '*'")]
    public void ChildSearchPattern_Root()
    {
        Assert.Equal("*", IsolatedPathHelper.ChildSearchPattern("/"));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedPathHelper] - ChildSearchPattern: nested directory appends '/*'")]
    public void ChildSearchPattern_Nested()
    {
        Assert.Equal("foo/bar/*", IsolatedPathHelper.ChildSearchPattern("/foo/bar"));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedPathHelper] - Join: relative segment appended to parent")]
    public void Join_AppendsRelative()
    {
        Assert.Equal("/foo/bar", IsolatedPathHelper.Join("/foo", "bar").ToString());
        Assert.Equal("/bar", IsolatedPathHelper.Join("/", "bar").ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedPathHelper] - Join: rejects null/empty child")]
    public void Join_RejectsEmptyChild()
    {
        Assert.Throws<ArgumentException>(() => IsolatedPathHelper.Join("/foo", ""));
        Assert.Throws<ArgumentNullException>(() => IsolatedPathHelper.Join("/foo", null!));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedPathHelper] - Round trip: absolute -> store -> absolute")]
    public void RoundTrip_AbsoluteThroughStore()
    {
        FileSystemPath original = "/path/to/file.txt";
        string store = IsolatedPathHelper.ToStorePath(original);
        FileSystemPath back = IsolatedPathHelper.FromStorePath(store);

        Assert.Equal(original.ToString(), back.ToString());
    }
}
