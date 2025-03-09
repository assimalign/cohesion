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
}
