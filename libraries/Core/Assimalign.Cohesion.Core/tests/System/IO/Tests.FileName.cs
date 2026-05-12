using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Tests;

public class FileNameTests
{
    [Theory]
    [InlineData("test.txt", "///test.txt")]
    public void FormatTest(string expected, string value)
    {
        FileName name = value;

        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("test/")]
    [InlineData("test?t")]
    public void BadFormatTest(string value)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            FileName name = value;
        });
    }
}
