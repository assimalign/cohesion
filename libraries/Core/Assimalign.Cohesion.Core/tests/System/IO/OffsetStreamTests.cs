using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Tests;

public class OffsetStreamTests
{
    [Fact]
    public void Test()
    {
        var encoding = Encoding.UTF8;
        var message = encoding.GetBytes("My name is John Doe");

        using var memory = new MemoryStream();

        memory.Write(message);
        memory.Position = 0;

        // Will offset the message to only get the name
        using var offset = new OffsetStream(memory, 11, 8);

        var buffer = new byte[4];
        var read = offset.Read(buffer);

        var name = encoding.GetString(buffer);

        Assert.Equal("John", name);
    }
}
