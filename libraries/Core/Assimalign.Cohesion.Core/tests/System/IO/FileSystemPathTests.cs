using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Tests;


public class FileSystemPathTests
{
    [Theory()]
    [InlineData("C:\\users\\")]
    [InlineData("C:\\users/")]
    public void RemoveLeadingSlashTest(string path)
    {
        var fsPath = new FileSystemPath(path);

        Assert.False(fsPath.EndsWith("/"));
        Assert.False(fsPath.EndsWith("\\"));
    }


    [Theory]
    [InlineData("C:/users/dotnetcadet", "C:\\users\\dotnetcadet/")]
    [InlineData("C:/users/dotnetcadet", "C:\\users\\\\//dotnetcadet////")]
    [InlineData("users/dotnetcadet", "//\\users\\\\//dotnetcadet////")]
    public void NormalizeTests(string expected, string path)
    {
        Assert.Equal(expected, new FileSystemPath(path).Value);
    }
}
