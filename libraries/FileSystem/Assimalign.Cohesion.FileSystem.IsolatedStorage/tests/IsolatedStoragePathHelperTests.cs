using System;
using System.IO;
using Assimalign.Cohesion.FileSystem.Internal;

namespace Assimalign.Cohesion.FileSystem.IsolatedStorage.Tests;

/// <summary>
/// Direct unit tests for the path translation helper. These verify that the public
/// <see cref="FileSystemPath"/> surface ('/' separated, rooted at "/") round-trips through the
/// store-side string form expected by <see cref="System.IO.IsolatedStorage.IsolatedStorageFile"/>.
/// </summary>
public class IsolatedStoragePathHelperTests
{
    [Theory(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - ToAbsolute: rooted and relative paths normalize")]
    [InlineData("/", "/")]
    [InlineData("/foo", "/foo")]
    [InlineData("foo", "/foo")]
    [InlineData("foo/bar", "/foo/bar")]
    [InlineData("/foo/bar", "/foo/bar")]
    public void ToAbsolute_Normalizes(string input, string expected)
    {
        FileSystemPath result = IsolatedStoragePathHelper.ToAbsolute(input);
        Assert.Equal(expected, result.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - ToAbsolute: empty path maps to root")]
    public void ToAbsolute_Empty_Root()
    {
        Assert.Equal("/", IsolatedStoragePathHelper.ToAbsolute(FileSystemPath.Empty).ToString());
    }

    [Theory(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - ToStorePath: strips leading and trailing slashes")]
    [InlineData("/", "")]
    [InlineData("/foo", "foo")]
    [InlineData("/foo/", "foo")]
    [InlineData("/foo/bar", "foo/bar")]
    [InlineData("/foo/bar/", "foo/bar")]
    public void ToStorePath_StripsSeparators(string input, string expected)
    {
        Assert.Equal(expected, IsolatedStoragePathHelper.ToStorePath(input));
    }

    [Theory(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - FromStorePath: round-trips back to absolute")]
    [InlineData("", "/")]
    [InlineData("foo", "/foo")]
    [InlineData("foo/bar", "/foo/bar")]
    [InlineData("foo\\bar", "/foo/bar")] // Windows-style separators are normalized
    public void FromStorePath_RoundTrips(string input, string expected)
    {
        Assert.Equal(expected, IsolatedStoragePathHelper.FromStorePath(input).ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - ChildSearchPattern: root produces '*'")]
    public void ChildSearchPattern_Root()
    {
        Assert.Equal("*", IsolatedStoragePathHelper.ChildSearchPattern("/"));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - ChildSearchPattern: nested directory appends '/*'")]
    public void ChildSearchPattern_Nested()
    {
        Assert.Equal("foo/bar/*", IsolatedStoragePathHelper.ChildSearchPattern("/foo/bar"));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - Join: relative segment appended to parent")]
    public void Join_AppendsRelative()
    {
        Assert.Equal("/foo/bar", IsolatedStoragePathHelper.Join("/foo", "bar").ToString());
        Assert.Equal("/bar", IsolatedStoragePathHelper.Join("/", "bar").ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - Join: rejects null/empty child")]
    public void Join_RejectsEmptyChild()
    {
        Assert.Throws<ArgumentException>(() => IsolatedStoragePathHelper.Join("/foo", ""));
        Assert.Throws<ArgumentNullException>(() => IsolatedStoragePathHelper.Join("/foo", null!));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStoragePathHelper] - Round trip: absolute -> store -> absolute")]
    public void RoundTrip_AbsoluteThroughStore()
    {
        FileSystemPath original = "/path/to/file.txt";
        string store = IsolatedStoragePathHelper.ToStorePath(original);
        FileSystemPath back = IsolatedStoragePathHelper.FromStorePath(store);

        Assert.Equal(original.ToString(), back.ToString());
    }
}
