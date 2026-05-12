using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Tests;

public class DirectoryNameTests
{
    [Theory]
    [InlineData("directory/", "/directory//")]
    public void FormatTest(string expected, string value)
    {
        DirectoryName name = value;

        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("../../users/chase", new string[] { "users", "chase" })]
    public void TestGetDirectories(string item, string[] segments)
    {
        FileSystemPath path = item;

        var directories = path.GetDirectoryNames();

        Assert.Equal(segments.Length, directories.Length);

        for (int i = 0; i < segments.Length; i++)
        {
            Assert.Equal(segments[i] + "/", directories[i]);
        }

    }
}
