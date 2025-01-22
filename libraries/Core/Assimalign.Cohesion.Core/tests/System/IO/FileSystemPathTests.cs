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
    [InlineData("")]
    public void ConvertTest(string value)
    {
        
    }

    [Theory()]
    [InlineData("C:\\users\\")]
    [InlineData("C:\\users/")]
    public void RemoveLeadingSlashTest(string path)
    {
        var fsPath = new FileSystemPath(path);

        Assert.False(fsPath.EndsWith("\\"));

        var fileName = fsPath.GetFileName();
    }


}
